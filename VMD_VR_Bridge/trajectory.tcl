# Bounded coordinate reads. No render, temporary files, animate goto, or display update.
# Each client owns its snapshot; changes to representation settings invalidate it.
namespace eval ::vrbridge { variable trajectory_sessions; array set trajectory_sessions {} }
source [file join [file dirname [info script]] cartoon.tcl]
source [file join [file dirname [info script]] quicksurf.tcl]

proc ::vrbridge::trajectory_disconnect {channel} {
    variable trajectory_sessions
    catch {unset trajectory_sessions($channel)}
}

proc ::vrbridge::trajectory_signature {} {
    set result [list [molinfo top]]
    foreach m [molinfo list] {
        lappend result $m [molinfo $m get {drawn numatoms numframes numreps}]
        for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
            lappend result [mol showrep $m $r] [molinfo $m get [list [list rep $r] [list selection $r] [list color $r] [list material $r]]]
            lappend result [mol selupdate $r $m] [mol colupdate $r $m]
            lappend result [mol showperiodic $m $r] [mol smoothrep $m $r]
            if {[mol showrep $m $r] && [lindex [lindex [molinfo $m get [list [list rep $r]]] 0] 0] eq "NewCartoon"} {
                lappend result [::vrbridge::cartoon_structure_signature $m]
                foreach name [colorinfo category Structure] { lappend result $name [color Structure $name] }
            }
        }
    }
    # Color edits should also invalidate a running snapshot.
    foreach category {Name Element Resname Chain Segname} {
        foreach name [colorinfo category $category] { lappend result $category $name [color $category $name] }
    }
    foreach name [colorinfo colors] { lappend result [colorinfo rgb $name] }
    foreach name [material list] { lappend result $name [material settings $name] }
    return $result
}

# Per-atom RGB for the static color methods shared by VDW, CPK and QuickSurf.
proc ::vrbridge::trajectory_atom_colors {sel coloring} {
    set category [lindex $coloring 0]
    switch -- $category {
        ColorID { set property index }
        Name { set property name }
        Element { set property element }
        ResName { set property resname; set category Resname }
        Chain { set property chain }
        SegName { set property segname; set category Segname }
        default { error "Color method $category uses the existing renderer" }
    }
    set colors {}
    foreach value [$sel get $property] {
        if {$category eq "ColorID"} { set cname [lindex $coloring 1] } else {
            if {$category eq "Name"} { set value [string index $value 0] }
            set cname [color $category $value]
        }
        lappend colors {*}[colorinfo rgb $cname]
    }
    return $colors
}

proc ::vrbridge::trajectory_metadata {owner} {
    variable trajectory_sessions
    set m [::vrbridge::top_molecule]
    foreach other [molinfo list] {
        if {$other != $m && [molinfo $other get drawn]} { error "Multiple visible molecules use the existing representation renderer" }
    }
    if {![molinfo $m get drawn]} { error "Top molecule is hidden" }
    set n [molinfo $m get numatoms]
    set total [molinfo $m get numframes]
    if {$n < 1 || $total < 1} { error "No trajectory coordinates loaded" }
    set reps {}
    set cartoons {}
    set surfaces {}
    for {set r 0} {$r < [molinfo $m get numreps]} {incr r} {
        if {![mol showrep $m $r]} { continue }
        lassign [molinfo $m get [list [list rep $r] [list selection $r] [list color $r] [list material $r]]] style selection coloring material
        set method [lindex $style 0]
        # render.tcl's "mol representation Quicksurf" keeps VMD's alternate spelling.
        if {[string equal -nocase $method QuickSurf]} { set method QuickSurf }
        if {$method ni {VDW CPK NewCartoon QuickSurf}} { error "Representation $method uses the existing renderer" }
        if {[mol selupdate $r $m] || [mol colupdate $r $m]} { error "Dynamic selections/colors use the existing renderer" }
        if {[mol showperiodic $m $r] ni {{} none} || [mol smoothrep $m $r] != 0} { error "Periodic images/smoothing use the existing renderer" }
        set settings [material settings $material]
        if {[lindex $settings 5] < 0.999} { error "Transparent materials use the existing renderer" }
        if {$method eq "NewCartoon"} {
            lappend cartoons [::vrbridge::cartoon_metadata $m $selection $coloring $style $r]
            continue
        }
        if {$method eq "QuickSurf"} {
            lappend surfaces [::vrbridge::quicksurf_metadata $m $selection $coloring $style $r]
            continue
        }
        set sel [atomselect $m $selection]
        set code [catch {
            set indices [$sel get index]
            set radii [$sel get radius]
            set colors [::vrbridge::trajectory_atom_colors $sel $coloring]
            set bonds {}
            if {$method eq "CPK"} {
                set selected [dict create]
                foreach i $indices { dict set selected $i 1 }
                foreach i $indices neighbors [$sel getbonds] {
                    foreach j $neighbors {
                        if {$j > $i && [dict exists $selected $j]} { lappend bonds $i $j }
                    }
                }
            }
            set sphere [lindex $style 1]
            set bond 0
            if {$method eq "CPK"} {
                set sphere [expr {$sphere * 0.25}]
                set bond [expr {[lindex $style 2] * 0.25}]
            }
            lappend reps [format {{"id":%d,"method":"%s","indices":[%s],"radii":[%s],"colors":[%s],"bonds":[%s],"sphereScale":%s,"bondRadius":%s}} $r $method [join $indices ,] [join $radii ,] [join $colors ,] [join $bonds ,] $sphere $bond]
        } message options]
        $sel delete
        if {$code} { return -options $options $message }
    }
    if {![llength $reps] && ![llength $cartoons] && ![llength $surfaces]} { error "No visible coordinate representations" }
    set token [clock clicks]
    set trajectory_sessions($owner) [list $token $m [::vrbridge::trajectory_signature]]
    set matrix [transmult [lindex [molinfo $m get global_matrix] 0] [lindex [molinfo $m get scale_matrix] 0] [lindex [molinfo $m get rotate_matrix] 0] [lindex [molinfo $m get center_matrix] 0]]
    return [format {"supported":true,"token":"%s","molecule":%d,"atomCount":%d,"frameCount":%d,"frame":%d,"matrix":[%s],"reps":[%s],"cartoons":[%s],"quicksurfs":[%s]} $token $m $n $total [molinfo $m get frame] [join [concat {*}$matrix] ,] [join $reps ,] [join $cartoons ,] [join $surfaces ,]]
}

proc ::vrbridge::trajectory_command {command args owner} {
    variable trajectory_sessions
    set id [lindex $args 0]
    if {[catch {
        ::vrbridge::scene_request_id $id
        switch -- $command {
            TRAJ_OPEN {
                if {[llength $args] != 1} { error "Usage: TRAJ_OPEN requestId" }
                if {[catch {::vrbridge::trajectory_metadata $owner} data]} {
                    catch {unset trajectory_sessions($owner)}
                    set data "\"supported\":false,\"message\":[::vrbridge::scene_json_string $data]"
                }
            }
            TRAJ_STYLE {
                if {[llength $args] != 3} { error "Usage: TRAJ_STYLE requestId repIndex method" }
                ::vrbridge::scene_set_representation [lindex $args 1] [lindex $args 2]
                catch {unset trajectory_sessions($owner)}
                set data {"ok":true}
            }
            TRAJ_READ - TRAJ_CHECK - TRAJ_SYNC {
                if {![info exists trajectory_sessions($owner)]} { error "Trajectory snapshot expired" }
                lassign $trajectory_sessions($owner) token m signature
                if {[lindex $args 1] ne $token || $signature ne [::vrbridge::trajectory_signature]} { error "Trajectory settings changed" }
                if {$command eq "TRAJ_CHECK"} {
                    if {[llength $args] != 2} { error "Usage: TRAJ_CHECK requestId token" }
                    set data [format {"ok":true,"frame":%d} [molinfo $m get frame]]
                } elseif {$command eq "TRAJ_SYNC"} {
                    if {[llength $args] != 3} { error "Usage: TRAJ_SYNC requestId token frame" }
                    set frame [::vrbridge::integer_in_range [lindex $args 2] 0 [expr {[molinfo $m get numframes]-1}] frame]
                    molinfo $m set frame $frame
                    set data {"ok":true}
                } else {
                    if {[llength $args] != 4} { error "Usage: TRAJ_READ requestId token start count" }
                    set start [::vrbridge::integer_in_range [lindex $args 2] 0 [expr {[molinfo $m get numframes]-1}] start]
                    # At most 4 frames, about 1M floats, per response. A large single frame is allowed.
                    set maximum [expr {max(1,min(4,1000000 / (3 * [molinfo $m get numatoms])))}]
                    set count [::vrbridge::integer_in_range [lindex $args 3] 1 $maximum count]
                    set count [expr {min($count,[molinfo $m get numframes]-$start)}]
                    set sel [atomselect $m all frame $start]
                    set code [catch {
                        set frames {}
                        for {set f $start} {$f < $start+$count} {incr f} {
                            $sel frame $f
                            set flat [concat {*}[$sel get {x y z}]]
                            lappend frames [format {{"positions":[%s]}} [join $flat ,]]
                        }
                        set data [format {"ok":true,"start":%d,"frames":[%s]} $start [join $frames ,]]
                    } message options]
                    $sel delete
                    if {$code} { return -options $options $message }
                }
            }
        }
    } message]} { set data "\"error\":[::vrbridge::scene_json_string $message]" }
    return "TRAJJSON [format {{"requestId":%s,%s}} [::vrbridge::scene_json_string $id] $data]"
}
