# Optional helper for large systems in the Unity live route.
#
# WHY IT CAN BE NEEDED
#   Unity rebuilds some representations itself from the atom coordinates. QuickSurf is one of
#   them: for every visible QuickSurf rep, Unity fills one density grid that covers the whole
#   bounding box of the rep's atoms (plus the Gaussian cutoff), at VMD's grid spacing. That
#   grid is capped (MaxCells in Unity_VR_Client/Assets/Scripts/Trajectory/VMDQuickSurfScene.cs,
#   about 4.2 million grid points). When a rep needs more, Unity coarsens the grid spacing, and
#   the surface in Unity (desktop window and headset) looks blocky although VMD looks fine. The
#   Unity status line then says "QuickSurf grid coarsened to ... Å".
#   What matters is the volume the rep spans, not its atom count: a dense system with many atoms
#   is fine, while a sparse system that spans a large box (for example 100 nucleosomes spread
#   over about 1100 Å) wastes most of the grid on empty space and gets coarsened.
#   Splitting one such rep into several reps, each covering a compact group of molecules, gives
#   every group its own small grid, so each one stays under the cap at VMD's spacing.
#
# WHEN TO USE IT
#   Only when a large or sparse system looks coarse in Unity. Normal systems (such as the MutLα
#   example of render.tcl) do not need it; for them the script reports that nothing needs to be
#   split and changes nothing. VDW, CPK and NewCartoon are drawn per atom without a grid, so
#   they are never split.
#
# COSTS OF SPLITTING
#   - More surfaces: Unity sizes two triangle buffers per surface from its box (at least 11 MB,
#     often tens of MB; the script prints an estimate), and rebuilds every surface for every
#     frame, so GPU memory use grows and playback can get slower.
#   - Surfaces of different reps do not merge: where two groups touch, two surfaces intersect
#     instead of fusing into one.
#   - Ray highlighting works per rep, so it highlights one group at a time.
#   - VMD's representation list gets long. Pressing B in the headset changes every rep.
#
# USAGE (VMD Tk Console, after the molecule and its representations are set up)
#   source animation/split_large_reps.tcl
#   split_large_reps ?molid? ?-unit fragment|chain|block N? ?-margin 0.8? ?-check?
#   unsplit_large_reps ?molid?
#   Then source start_vr.tcl, or, if the bridge already runs, Unity reloads the scene by itself.
#
#   molid    Molecule to work on; default "top".
#   -unit    What is never cut apart; every group is made of whole units:
#              fragment  (default) VMD's bonded fragments. Good for all-atom models.
#              chain     Chain IDs. Only when every chain ID occurs once in the system.
#              block N   N consecutive atom indices of the molecule, for example one copy of a
#                        repeated complex. Use this for coarse-grained models without bonds,
#                        where every bead is its own fragment (the script warns in that case).
#                        Example, the nucleosome test system: -unit {block 1268}
#                        (or -unit block 1268).
#   -margin  Fraction of the Unity cap each group of a split rep may use in any frame; the rest
#            is headroom. A rep is split only when it exceeds the cap itself.
#   -check   Only report what would be split; change nothing.
#
#   Every frame of the molecule is checked, because molecules move during the trajectory.
#   Split reps are hidden, not deleted, and unsplit_large_reps deletes the added reps and shows
#   the original ones again. Run unsplit_large_reps before changing a split rep in VMD.
#
# ADDING A REPRESENTATION
#   A drawing method is split only if it has an entry in ::splitreps::methods (see below): a
#   usage procedure that estimates the Unity cost of one group and the Unity limit for it.
#   Add an entry only for a method that Unity builds over a whole box (like QuickSurf), and
#   copy the constants from the Unity code that draws it.

namespace eval ::splitreps {
    # Drawing method (lower case) -> usage procedure and Unity limit.
    # The usage procedure takes the rep style, the largest atom radius of the group and the
    # group's bounding box ({min} {max}, from measure minmax) and returns {usage bytes}:
    # usage is compared with the limit, bytes is a rough GPU memory estimate for the report.
    variable methods [dict create \
        quicksurf [dict create usage ::splitreps::quicksurf_usage limit [expr {1 << 22}]]]
    # molid -> list of {originalRepName {addedRepNames ...}}, for unsplit_large_reps.
    variable splits [dict create]
}

# Estimate Unity's QuickSurf density grid for one group, as VMDQuickSurfScene.Build does.
# Keep these constants in sync with Unity_VR_Client/Assets/Scripts/Trajectory/VMDQuickSurfScene.cs
# (MaxCells, GaussianLimit, MaxTriangles, VertexStride) and the parameter defaults with
# VMD_VR_Bridge/quicksurf.tcl.
proc ::splitreps::quicksurf_usage {style maxRadius minmax} {
    lassign [lrange $style 1 4] radiusScale isovalue spacing quality
    if {$radiusScale eq ""} { set radiusScale 1.0 }
    if {$spacing eq ""} { set spacing 1.0 }
    if {$quality eq ""} { set quality 0 }
    set quality [expr {max(0, min(3, int(round($quality))))}]
    # Gaussian cutoff in units of sigma for quality 0..3 (GaussianLimit).
    set cutoff [expr {[lindex {2.0 2.5 3.0 3.5} $quality] * max($maxRadius * $radiusScale, 0.1)}]
    lassign $minmax lo hi
    set cells 1
    set sizes {}
    foreach a $lo b $hi {
        set size [expr {$b - $a + 2 * ($cutoff + $spacing)}]
        lappend sizes $size
        set cells [expr {$cells * (int(ceil($size / $spacing)) + 1)}]
    }
    # Initial triangle buffer: from the box surface area in grid cells, a power of two between
    # 65536 and MaxTriangles; Unity keeps two such buffers (front and back) of 28-byte vertices.
    lassign $sizes x y z
    set area [expr {2 * ($x*$y + $y*$z + $x*$z) / ($spacing * $spacing)}]
    set triangles 65536
    while {$triangles < min($area * 4, 1 << 21)} { set triangles [expr {$triangles * 2}] }
    set bytes [expr {2 * $triangles * 3 * 28}]
    return [list $cells $bytes]
}

# Resolve "top" (or an empty argument) to a molecule ID and check that it exists.
proc ::splitreps::molid {m} {
    if {$m eq "" || $m eq "top"} { set m [molinfo top] }
    if {$m < 0 || [lsearch -exact [molinfo list] $m] < 0} { error "No such molecule: $m" }
    return $m
}

# Return VMD selection text for a list of atom indices, as compact "index a to b c ..." ranges.
proc ::splitreps::index_text {indices} {
    set indices [lsort -integer -unique $indices]
    set parts {}
    set start [lindex $indices 0]
    set prev $start
    foreach i [lrange $indices 1 end] {
        if {$i == $prev + 1} { set prev $i; continue }
        lappend parts [expr {$start == $prev ? $start : "$start to $prev"}]
        set start $i
        set prev $i
    }
    lappend parts [expr {$start == $prev ? $start : "$start to $prev"}]
    return "index [join $parts { }]"
}

# Largest usage of one group over all frames, and the memory estimate at that frame.
# group: the unit keys; units: unit key -> {indices center maxRadius}.
proc ::splitreps::group_usage {m style usageProc units group} {
    set indices {}
    set radius 0.0
    foreach key $group {
        lassign [dict get $units $key] unitIndices center unitRadius
        lappend indices {*}$unitIndices
        set radius [expr {max($radius, $unitRadius)}]
    }
    set sel [atomselect $m [index_text $indices]]
    set worst {0 0}
    for {set f 0} {$f < [molinfo $m get numframes]} {incr f} {
        $sel frame $f
        set usage [$usageProc $style $radius [measure minmax $sel]]
        if {[lindex $usage 0] > [lindex $worst 0]} { set worst $usage }
    }
    $sel delete
    return $worst
}

# Plan the split of one rep. Returns a dict with the groups (lists of unit keys), the worst
# usage of the whole rep and of the largest group, and the memory estimate; groups has a single
# element when the rep already fits.
proc ::splitreps::plan_rep {m r unitMode blockSize margin} {
    variable methods
    lassign [molinfo $m get [list [list rep $r] [list selection $r]]] style selection
    set entry [dict get $methods [string tolower [lindex $style 0]]]
    set usageProc [dict get $entry usage]
    # A rep is split only when it exceeds the Unity limit itself (Unity would coarsen it);
    # the groups of a split rep then aim for the limit times -margin, leaving headroom.
    set limit [dict get $entry limit]
    set budget [expr {$limit * $margin}]

    # Collect the units at frame 0: unit key -> atom indices, coordinate sums, largest radius.
    set sel [atomselect $m $selection frame 0]
    set fields [list index x y z radius]
    switch -- $unitMode {
        fragment { lappend fields fragment }
        chain    { lappend fields chain }
    }
    set rows [$sel get $fields]
    $sel delete
    if {[llength $rows] == 0} { return [dict create groups {} whole {0 0} largest {0 0} bytes 0 units 0 atoms 0] }
    set acc [dict create]
    foreach row $rows {
        lassign $row index x y z radius key
        if {$unitMode eq "block"} { set key [expr {$index / $blockSize}] }
        if {[dict exists $acc $key]} {
            lassign [dict get $acc $key] ids sx sy sz rmax
            dict set acc $key [list [lappend ids $index] [expr {$sx+$x}] [expr {$sy+$y}] [expr {$sz+$z}] [expr {max($rmax,$radius)}]]
        } else {
            dict set acc $key [list [list $index] $x $y $z $radius]
        }
    }
    set units [dict create]
    dict for {key value} $acc {
        lassign $value ids sx sy sz rmax
        set n [llength $ids]
        dict set units $key [list $ids [list [expr {$sx/$n}] [expr {$sy/$n}] [expr {$sz/$n}]] $rmax]
    }

    # Recursive bisection: a group over budget is split at the median unit center along the
    # axis where the unit centers spread most, until every group fits.
    set whole [group_usage $m $style $usageProc $units [dict keys $units]]
    if {[lindex $whole 0] <= $limit} {
        return [dict create groups [list [dict keys $units]] units_dict $units whole $whole largest $whole \
            bytes [lindex $whole 1] units [dict size $units] atoms [llength $rows]]
    }
    set pending [list [list [dict keys $units] $whole]]
    set done {}
    while {[llength $pending] > 0} {
        set pending [lassign $pending item]
        lassign $item group usage
        if {[lindex $usage 0] <= $budget} { lappend done $item; continue }
        if {[llength $group] == 1} {
            error "One unit ($unitMode $group) alone needs [lindex $usage 0] grid points, more than [expr {int($budget)}]; use a smaller -unit or a larger QuickSurf grid spacing."
        }
        set best 0
        set bestSpread -1.0
        foreach axis {0 1 2} {
            set values {}
            foreach key $group { lappend values [lindex [dict get $units $key] 1 $axis] }
            set values [lsort -real $values]
            set spread [expr {[lindex $values end] - [lindex $values 0]}]
            if {$spread > $bestSpread} { set best $axis; set bestSpread $spread }
        }
        set sorted {}
        foreach key $group { lappend sorted [list $key [lindex [dict get $units $key] 1 $best]] }
        set sorted [lsort -real -index 1 $sorted]
        set half [expr {[llength $sorted] / 2}]
        foreach part [list [lrange $sorted 0 [expr {$half-1}]] [lrange $sorted $half end]] {
            set keys {}
            foreach pair $part { lappend keys [lindex $pair 0] }
            lappend pending [list $keys [group_usage $m $style $usageProc $units $keys]]
        }
    }

    set groups {}
    set largest {0 0}
    set bytes 0
    foreach item $done {
        lassign $item group usage
        lappend groups $group
        if {[lindex $usage 0] > [lindex $largest 0]} { set largest $usage }
        set bytes [expr {$bytes + [lindex $usage 1]}]
    }
    return [dict create groups $groups units_dict $units whole $whole largest $largest bytes $bytes \
        units [dict size $units] atoms [llength $rows]]
}

# Split every visible rep of molecule m whose drawing method has an entry in
# ::splitreps::methods and that needs more than the Unity limit (times -margin) in some frame.
proc split_large_reps {args} {
    set m top
    if {[llength $args] > 0 && [string index [lindex $args 0] 0] ne "-"} { set args [lassign $args m] }
    set unitMode fragment
    set blockSize 0
    set margin 0.8
    set checkOnly 0
    while {[llength $args] > 0} {
        set args [lassign $args option]
        switch -- $option {
            -unit {
                set args [lassign $args unitMode]
                # Accept both "-unit block 1268" and "-unit {block 1268}".
                if {[llength $unitMode] == 2} { lassign $unitMode unitMode blockSize }
                if {$unitMode eq "block" && $blockSize == 0} { set args [lassign $args blockSize] }
                if {$unitMode ni {fragment chain block}} { error "-unit must be fragment, chain or block N" }
                if {$unitMode eq "block" && (![string is integer -strict $blockSize] || $blockSize < 1)} {
                    error "-unit block needs a positive atom count, for example -unit block 1268"
                }
            }
            -margin {
                set args [lassign $args margin]
                if {![string is double -strict $margin] || $margin <= 0 || $margin > 1} { error "-margin must be in (0, 1]" }
            }
            -check { set checkOnly 1 }
            default { error "Unknown option $option. Usage: split_large_reps ?molid? ?-unit fragment|chain|block N? ?-margin 0.8? ?-check?" }
        }
    }
    set m [::splitreps::molid $m]
    if {!$checkOnly && [dict exists $::splitreps::splits $m]} {
        error "Molecule $m is already split; run unsplit_large_reps $m first."
    }

    # Plan every eligible rep before changing anything, so an error leaves the scene untouched.
    set plans {}
    for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
        if {![mol showrep $m $r]} { continue }
        set method [string tolower [lindex [lindex [molinfo $m get [list [list rep $r]]] 0] 0]]
        if {![dict exists $::splitreps::methods $method]} { continue }
        set plan [::splitreps::plan_rep $m $r $unitMode $blockSize $margin]
        set limit [dict get $::splitreps::methods $method limit]
        set groups [llength [dict get $plan groups]]
        puts [format "split_large_reps: rep %d (%s, %s): %d atoms in %d %s units; needs %.0f%% of the Unity limit." \
            $r [lindex [molinfo $m get [list [list rep $r]]] 0 0] [lindex [molinfo $m get [list [list selection $r]]] 0] \
            [dict get $plan atoms] [dict get $plan units] $unitMode [expr {100.0 * [lindex [dict get $plan whole] 0] / $limit}]]
        if {$groups <= 1} {
            puts "split_large_reps: rep $r fits; nothing to split."
            continue
        }
        if {$unitMode eq "fragment" && [dict get $plan units] > 0.5 * [dict get $plan atoms]} {
            puts "split_large_reps: warning: most atoms of rep $r are single-atom fragments (no bonds, as in coarse-grained models); groups may cut molecules apart. Consider -unit block N."
        }
        puts [format "split_large_reps: rep %d -> %d reps; largest group needs %.0f%% of the limit; about %.0f MB of Unity surface buffers." \
            $r $groups [expr {100.0 * [lindex [dict get $plan largest] 0] / $limit}] [expr {[dict get $plan bytes] / 1048576.0}]]
        if {$groups > 200} { puts "split_large_reps: warning: $groups reps is a lot; playback may be slow." }
        lappend plans [list [mol repname $m $r] $plan]
    }
    if {[llength $plans] == 0} {
        puts "split_large_reps: nothing to split for molecule $m."
        return
    }
    if {$checkOnly} {
        puts "split_large_reps: -check given; nothing changed."
        return
    }

    # Apply: add one rep per group with the original style, coloring and material, then hide
    # the original rep. Redrawing is paused meanwhile and restored afterwards.
    set updating [display update status]
    display update off
    set records {}
    foreach item $plans {
        lassign $item name plan
        set r [mol repindex $m $name]
        lassign [molinfo $m get [list [list rep $r] [list color $r] [list material $r]]] style color material
        set units [dict get $plan units_dict]
        set added {}
        foreach group [dict get $plan groups] {
            set indices {}
            foreach key $group { lappend indices {*}[lindex [dict get $units $key] 0] }
            mol representation $style
            mol color $color
            mol material $material
            mol selection [::splitreps::index_text $indices]
            mol addrep $m
            lappend added [mol repname $m [expr {[molinfo $m get numreps] - 1}]]
        }
        mol showrep $m $r off
        lappend records [list $name $added]
    }
    dict set ::splitreps::splits $m $records
    if {$updating eq "on"} { display update on }
    display update
    puts "split_large_reps: done; run unsplit_large_reps $m to restore the original reps."
}

# Delete the reps added by split_large_reps for molecule m and show the original reps again.
proc unsplit_large_reps {{m top}} {
    set m [::splitreps::molid $m]
    if {![dict exists $::splitreps::splits $m]} {
        puts "unsplit_large_reps: molecule $m has no split reps."
        return
    }
    foreach record [dict get $::splitreps::splits $m] {
        lassign $record name added
        foreach repName $added {
            set r [mol repindex $m $repName]
            if {$r >= 0} { mol delrep $r $m }
        }
        set r [mol repindex $m $name]
        if {$r >= 0} { mol showrep $m $r on }
    }
    dict unset ::splitreps::splits $m
    puts "unsplit_large_reps: original reps of molecule $m restored."
}
