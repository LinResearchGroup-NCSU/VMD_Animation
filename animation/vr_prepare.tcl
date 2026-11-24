# Optional helper: adapt the current VMD scene to the Unity live route, without editing the
# scene's own script.
#
# WHY IT CAN BE NEEDED
#   Unity draws a scene itself ("Unity live": smooth playback, ray highlighting, B to switch the
#   drawing method) only when every visible rep of the top molecule uses VDW, CPK, NewCartoon or
#   QuickSurf, with a fixed selection and coloring, ColorID/Name/Element/ResName/Chain/SegName
#   coloring (NewCartoon also Structure), no smoothing, no periodic images and an opaque
#   material, and no other molecule is drawn. Any other scene falls back to "VMD mesh": VMD
#   exports the current frame as meshes, which is close to VMD's look but static, drops lines
#   and points, and is refused by Unity above 500,000 triangles or 128 MB. Visualization
#   scripts written for VMD (for example data/EGFR_water/view_unwrapped.vmd) usually use Lines,
#   Licorice, Points, smoothing or transparent materials, so each example needed its own
#   VR script. vr_prepare converts such a scene in place instead, and vr_unprepare undoes it.
#
# WHEN TO USE IT
#   After a scene's own script, when Unity shows "[VMD mesh]" or nothing. A scene that already
#   qualifies (such as animation/render.tcl or the nucleosome example) is left unchanged.
#   Run "vr_prepare -check" first to see what would change.
#
# WHAT IT CHANGES (only visible reps of the molecule, each change printed)
#   Lines, Bonds, DynamicBonds, Licorice   -> CPK, at a similar thickness
#   Points, Dotted, Solvent                -> small VDW spheres
#   Tube, Trace, Cartoon, Ribbons, ...     -> NewCartoon (QuickSurf when the selection has
#                                             nucleic acid, which NewCartoon cannot draw here)
#   Surf, MSMS, NanoShaper                 -> QuickSurf
#   other drawing methods (HBonds, Isosurface, Beads, ...) -> hidden
#   smoothing -> 0; periodic images -> off; automatic selection/color updates -> off;
#   a transparent material -> an opaque copy named VRPrep_<material>;
#   unsupported coloring -> Name (NewCartoon: Structure); other drawn molecules -> hidden.
#   Water reps (every selected atom matches VMD's "water") follow -water. A converted Lines or
#   Points rep with more atoms than -maxatoms is hidden, because Unity would stream and draw
#   every one of them each frame.
#   Unity's own renderers then draw the scene, so it looks close to, not identical to, VMD.
#
# USAGE (VMD Tk Console)
#   source animation/vr_prepare.tcl
#   vr_prepare ?molid? ?-check? ?-water hide|spheres? ?-maxatoms 60000?
#   source start_vr.tcl        (if the bridge already runs, Unity reloads by itself)
#   vr_unprepare ?molid?
#   Run vr_prepare BEFORE start_vr.tcl: start_vr.tcl hides Lines reps, and vr_prepare leaves
#   hidden reps alone, so a membrane drawn as Lines would otherwise stay hidden.
#
#   molid      Molecule to adapt; default "top".
#   -check     Print what would change and change nothing.
#   -water     hide (default): hide water reps; Unity has no point renderer and 100,000 water
#              spheres stream slowly. spheres: draw water as small VDW spheres.
#   -maxatoms  Largest converted Lines/Points rep kept (default 60000); larger ones are hidden.
#
#   Every original setting is recorded and vr_unprepare restores it. Run vr_unprepare before
#   editing the adapted reps in VMD. A QuickSurf rep that is too large for Unity's grid is
#   reported with the split_large_reps command to run (see animation/split_large_reps.tcl).

namespace eval ::vrprep {
    # molid -> list of records {repName setting value ...} and {molecule m drawn}, for undo.
    variable state [dict create]
    # Drawing methods the Unity live route draws.
    variable live {vdw cpk newcartoon quicksurf}
    variable lineMethods {lines bonds dynamicbonds licorice}
    variable pointMethods {points dotted solvent}
    variable ribbonMethods {tube trace cartoon ribbons newribbons}
    variable surfaceMethods {surf msms nanoshaper}
    variable atomColorings {ColorID Name Element ResName Chain SegName}
}

# Resolve "top" (or an empty argument) to a molecule ID and check that it exists.
proc ::vrprep::molid {m} {
    if {$m eq "" || $m eq "top"} { set m [molinfo top] }
    if {$m < 0 || [lsearch -exact [molinfo list] $m] < 0} { error "No such molecule: $m" }
    return $m
}

# Number of atoms of molecule m that match selection text.
proc ::vrprep::count {m text} {
    set sel [atomselect $m $text]
    set n [$sel num]
    $sel delete
    return $n
}

# Plan the changes for rep r of molecule m. Returns a list of {setting value description}
# triples; empty when the rep already qualifies. setting is show, style, color, material,
# smooth, periodic, selupdate or colupdate.
proc ::vrprep::plan_rep {m r water maxAtoms} {
    variable live; variable lineMethods; variable pointMethods; variable ribbonMethods
    variable surfaceMethods; variable atomColorings
    lassign [molinfo $m get [list [list rep $r] [list selection $r] [list color $r] [list material $r]]] style selection coloring material
    set method [string tolower [lindex $style 0]]
    set atoms [::vrprep::count $m $selection]
    set changes {}
    # Water: every selected atom is water.
    if {$atoms > 0 && [::vrprep::count $m "($selection) and not water"] == 0} {
        if {$water eq "hide"} { return [list [list show 0 "water rep ($atoms atoms) hidden (-water spheres draws it)"]] }
        if {$method ni {vdw quicksurf}} { lappend changes [list style {VDW 0.3 8} "$style -> VDW 0.3 8 (water as small spheres)"] }
        set method vdw
    }
    set newMethod $method
    if {[llength $changes] == 0} {
        if {$method in $lineMethods} {
            # Licorice draws spheres and bonds of its first parameter's radius; the bridge passes
            # CPK's sphere scale and bond radius to Unity times 0.25, so these give a similar
            # thickness. Lines and Bonds become thin sticks.
            set radius 0.3
            if {$method eq "licorice" && [string is double -strict [lindex $style 1]]} { set radius [lindex $style 1] }
            if {$method in {lines dynamicbonds}} { set radius 0.1 }
            set bond [format %.2f [expr {4.0 * $radius}]]
            set sphere [format %.2f [expr {2.4 * $radius}]]
            if {$method ne "licorice" && $atoms > $maxAtoms} { return [list [list show 0 "$style ($atoms atoms) hidden: more than -maxatoms $maxAtoms"]] }
            lappend changes [list style [list CPK $sphere $bond 12 12] "$style -> CPK $sphere $bond 12 12"]
            set newMethod cpk
        } elseif {$method in $pointMethods} {
            if {$atoms > $maxAtoms} { return [list [list show 0 "$style ($atoms atoms) hidden: more than -maxatoms $maxAtoms"]] }
            lappend changes [list style {VDW 0.3 8} "$style -> VDW 0.3 8"]
            set newMethod vdw
        } elseif {$method in $ribbonMethods} {
            if {[::vrprep::count $m "($selection) and nucleic"] > 0} {
                lappend changes [list style {QuickSurf 1.0 0.5 1.0 1} "$style -> QuickSurf (the selection has nucleic acid)"]
                set newMethod quicksurf
            } else {
                lappend changes [list style {NewCartoon 0.3 10 4.1 0} "$style -> NewCartoon 0.3 10 4.1 0"]
                set newMethod newcartoon
            }
        } elseif {$method in $surfaceMethods} {
            lappend changes [list style {QuickSurf 1.0 0.5 1.0 1} "$style -> QuickSurf 1.0 0.5 1.0 1"]
            set newMethod quicksurf
        } elseif {$method ni $live} {
            return [list [list show 0 "$style hidden: Unity has no renderer for it"]]
        }
    }
    # NewCartoon with nucleic acid uses VMD's mesh route.
    if {$newMethod eq "newcartoon" && [::vrprep::count $m "($selection) and nucleic"] > 0} {
        lappend changes [list style {QuickSurf 1.0 0.5 1.0 1} "NewCartoon -> QuickSurf (the selection has nucleic acid)"]
        set newMethod quicksurf
    }
    set category [lindex $coloring 0]
    set allowed $atomColorings
    if {$newMethod eq "newcartoon"} { lappend allowed Structure }
    if {$category ni $allowed} {
        set replacement [expr {$newMethod eq "newcartoon" ? "Structure" : "Name"}]
        lappend changes [list color $replacement "coloring $coloring -> $replacement"]
    }
    if {[mol smoothrep $m $r] != 0} { lappend changes [list smooth 0 "smoothing [mol smoothrep $m $r] -> 0"] }
    if {[mol showperiodic $m $r] ni {{} none}} { lappend changes [list periodic {} "periodic images off"] }
    if {[mol selupdate $r $m]} { lappend changes [list selupdate off "automatic selection update off"] }
    if {[mol colupdate $r $m]} { lappend changes [list colupdate off "automatic color update off"] }
    if {[lindex [material settings $material] 5] < 0.999} {
        lappend changes [list material VRPrep_$material "material $material (transparent) -> VRPrep_$material (opaque copy)"]
    }
    return $changes
}

# Adapt molecule m (see the header). Prints every planned change; applies them unless -check.
proc vr_prepare {args} {
    set m top
    if {[llength $args] > 0 && [string index [lindex $args 0] 0] ne "-"} { set args [lassign $args m] }
    set checkOnly 0
    set water hide
    set maxAtoms 60000
    while {[llength $args] > 0} {
        set args [lassign $args option]
        switch -- $option {
            -check { set checkOnly 1 }
            -water {
                set args [lassign $args water]
                if {$water ni {hide spheres}} { error "-water must be hide or spheres" }
            }
            -maxatoms {
                set args [lassign $args maxAtoms]
                if {![string is integer -strict $maxAtoms] || $maxAtoms < 1} { error "-maxatoms must be a positive integer" }
            }
            default { error "Unknown option $option. Usage: vr_prepare ?molid? ?-check? ?-water hide|spheres? ?-maxatoms N?" }
        }
    }
    set m [::vrprep::molid $m]
    if {!$checkOnly && [dict exists $::vrprep::state $m]} { error "Molecule $m is already prepared; run vr_unprepare $m first." }

    # Plan everything first, so that an error leaves the scene untouched.
    set plans {}
    for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
        if {![mol showrep $m $r]} { continue }
        set changes [::vrprep::plan_rep $m $r $water $maxAtoms]
        if {[llength $changes]} { lappend plans [list [mol repname $m $r] $r $changes] }
    }
    set others {}
    foreach other [molinfo list] {
        if {$other != $m && [molinfo $other get drawn]} { lappend others $other }
    }
    foreach item $plans {
        lassign $item name r changes
        foreach change $changes { puts "vr_prepare: rep $r: [lindex $change 2]" }
    }
    foreach other $others { puts "vr_prepare: molecule $other hidden (Unity draws one molecule)" }
    # start_vr.tcl hides Lines reps, so a Lines rep hidden now was probably meant to be seen.
    for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
        if {![mol showrep $m $r] && [string equal -nocase [lindex [lindex [molinfo $m get [list [list rep $r]]] 0] 0] Lines]} {
            puts "vr_prepare: note: rep $r (Lines) is hidden and left alone. If start_vr.tcl hid it, run \"mol showrep $m $r on\" and vr_prepare again (or run vr_prepare before start_vr.tcl)."
        }
    }
    if {![llength $plans] && ![llength $others]} {
        puts "vr_prepare: molecule $m already suits the Unity live route; nothing to change."
        return
    }
    if {$checkOnly} {
        puts "vr_prepare: -check given; nothing changed."
        return
    }

    # Apply, recording the original value of every setting that changes.
    set records {}
    foreach item $plans {
        lassign $item name r changes
        lassign [molinfo $m get [list [list rep $r] [list selection $r] [list color $r] [list material $r]]] style selection coloring material
        set record [list rep $name]
        foreach change $changes {
            lassign $change setting value
            switch -- $setting {
                show      { lappend record show 1; mol showrep $m $r off }
                style     { lappend record style $style; mol modstyle $r $m {*}$value }
                color     { lappend record color $coloring; mol modcolor $r $m {*}$value }
                smooth    { lappend record smooth [mol smoothrep $m $r]; mol smoothrep $m $r 0 }
                periodic  { lappend record periodic [mol showperiodic $m $r]; mol showperiodic $m $r {} }
                selupdate { lappend record selupdate on; mol selupdate $r $m off }
                colupdate { lappend record colupdate on; mol colupdate $r $m off }
                material {
                    if {[lsearch -exact [material list] $value] < 0} {
                        material add $value copy $material
                        material change opacity $value 1.0
                    }
                    lappend record material $material
                    mol modmaterial $r $m $value
                }
            }
        }
        lappend records $record
    }
    foreach other $others { lappend records [list molecule $other]; mol off $other }
    dict set ::vrprep::state $m $records
    puts "vr_prepare: done; run vr_unprepare $m to restore the original reps."

    # Report what the bridge will do, when it is loaded (start_vr.tcl).
    if {[llength [info commands ::vrbridge::trajectory_metadata]]} {
        if {[catch {::vrbridge::trajectory_metadata vr_prepare_check 1} reply]} {
            puts "vr_prepare: the bridge still uses the VMD mesh route: $reply"
        } else {
            set atoms [molinfo $m get numatoms]
            set sent $atoms
            if {[regexp {"streamed":\[([^\]]*)\]} $reply -> list]} { set sent [llength [split $list ,]] }
            puts "vr_prepare: the bridge will use the Unity live route, streaming $sent of $atoms atoms per frame."
        }
        catch {unset ::vrbridge::trajectory_sessions(vr_prepare_check)}
    }
    # QuickSurf reps over Unity's grid cap, when split_large_reps.tcl is loaded.
    if {[llength [info commands ::splitreps::plan_rep]]} {
        for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
            if {![mol showrep $m $r]} { continue }
            set method [string tolower [lindex [lindex [molinfo $m get [list [list rep $r]]] 0] 0]]
            if {$method ne "quicksurf"} { continue }
            set plan [::splitreps::plan_rep $m $r fragment 0 0.8]
            if {[llength [dict get $plan groups]] > 1} {
                puts "vr_prepare: rep $r (QuickSurf) exceeds Unity's grid; consider: split_large_reps $m -check"
            }
        }
    }
}

# Restore what vr_prepare changed on molecule m.
proc vr_unprepare {{m top}} {
    set m [::vrprep::molid $m]
    if {![dict exists $::vrprep::state $m]} {
        puts "vr_unprepare: molecule $m was not prepared."
        return
    }
    foreach record [dict get $::vrprep::state $m] {
        if {[lindex $record 0] eq "molecule"} {
            if {[lsearch -exact [molinfo list] [lindex $record 1]] >= 0} { mol on [lindex $record 1] }
            continue
        }
        set r [mol repindex $m [lindex $record 1]]
        if {$r < 0} { continue }
        foreach {setting value} [lrange $record 2 end] {
            switch -- $setting {
                show      { mol showrep $m $r on }
                style     { mol modstyle $r $m {*}$value }
                color     { mol modcolor $r $m {*}$value }
                smooth    { mol smoothrep $m $r $value }
                periodic  { mol showperiodic $m $r $value }
                selupdate { mol selupdate $r $m on }
                colupdate { mol colupdate $r $m on }
                material  { mol modmaterial $r $m $value }
            }
        }
    }
    dict unset ::vrprep::state $m
    puts "vr_unprepare: molecule $m restored."
}
