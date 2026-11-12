# Coordinate playback for the "Unity live" display route. Sourced by vmd_vr_bridge.tcl; this
# file sources cartoon.tcl and quicksurf.tcl for the NewCartoon and QuickSurf metadata.
#
# Commands implemented here (::vrbridge::handle_command passes them to trajectory_command):
#   TRAJ_OPEN <requestId>                           Check compatibility and open a snapshot.
#   TRAJ_READ <requestId> <token> <start> <count>   Read a bounded batch of coordinate frames.
#   TRAJ_CHECK <requestId> <token>                  Validate the snapshot; report VMD's frame.
#   TRAJ_SYNC <requestId> <token> <frame>           Set VMD's displayed frame.
#   TRAJ_STYLE <requestId> <repIndex> <method>      Change a drawing method; drop the snapshot.
# Every reply is one "TRAJJSON" line: a JSON object that starts with the request ID.
#
# A snapshot is what TRAJ_OPEN describes: the atoms, radii, colors and bonds that Unity draws
# for each visible representation. Unity then only reads coordinates.
# Each client owns its snapshot; changes to representation settings invalidate it. The next
# TRAJ_READ, TRAJ_CHECK or TRAJ_SYNC then fails and the client opens a new snapshot. The
# Unity side is Assets/Scripts/Trajectory/TrajectoryController.cs.
#
# Bounded coordinate reads. No render, temporary files, animate goto, or display update.

# trajectory_sessions maps a client channel to its snapshot: a list of the token, the
# molecule ID and the trajectory_signature taken at TRAJ_OPEN.
namespace eval ::vrbridge { variable trajectory_sessions; array set trajectory_sessions {} }
source [file join [file dirname [info script]] cartoon.tcl]
source [file join [file dirname [info script]] quicksurf.tcl]

# Drop a client's snapshot. Called by ::vrbridge::disconnect and ::vrbridge::stop.
proc ::vrbridge::trajectory_disconnect {channel} {
    variable trajectory_sessions
    catch {unset trajectory_sessions($channel)}
}

# Return a Tcl list of every VMD setting a snapshot depends on; a snapshot is invalid as soon
# as this list differs from the one stored at TRAJ_OPEN. It covers: the top molecule; for each
# molecule whether it is drawn and its atom, frame and rep counts; for each rep its
# visibility, style, selection, coloring, material, automatic selection and color updates,
# periodic images and smoothing; for each visible NewCartoon rep the backbone's secondary
# structure and the Structure colors; and the color and material definitions.
# Unlike scene_signature in static_scene.tcl, the displayed frame is not part of it.
# Called by trajectory_metadata, and by trajectory_command for every TRAJ_READ, TRAJ_CHECK
# and TRAJ_SYNC.
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
# sel: an atom selection. coloring: the rep's coloring string, such as "ColorID 1" or "Name".
# Returns a flat list of r g b values, three per selected atom. ColorID, Name, Element,
# ResName, Chain and SegName are supported; any other method raises an error, which makes
# TRAJ_OPEN report the scene as unsupported.
# Called by trajectory_metadata and by quicksurf_metadata in quicksurf.tcl.
proc ::vrbridge::trajectory_atom_colors {sel coloring} {
    set category [lindex $coloring 0]
    # Choose the atom property to read and the color category to look it up in. The category
    # names Resname and Segname differ in case from the coloring methods ResName and SegName.
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
            # The Name category is looked up by the first character of the atom name.
            if {$category eq "Name"} { set value [string index $value 0] }
            set cname [color $category $value]
        }
        lappend colors {*}[colorinfo rgb $cname]
    }
    return $colors
}

# Build the body of the TRAJ_OPEN reply for client owner and store its new snapshot in
# trajectory_sessions.
# Returns the JSON fields, without the enclosing braces: supported, token, molecule,
# atomCount, frameCount, frame (VMD's displayed frame), matrix (the molecule's
# global * scale * rotate * center matrix as 16 numbers), reps (the VDW and CPK reps),
# cartoons (see cartoon.tcl) and quicksurfs (see quicksurf.tcl).
# The scene qualifies when the top molecule is the only drawn molecule and has atoms and
# frames, and every visible rep uses VDW, CPK, NewCartoon or QuickSurf with a fixed selection
# and coloring, no periodic images, no smoothing and an opaque material. Otherwise an error is
# raised; trajectory_command reports it as "supported":false with the message, and Unity then
# shows the scene through the VMD mesh route.
# Called by trajectory_command for TRAJ_OPEN.
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
        # Element 5 of the material settings is read as the opacity.
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
        # VDW and CPK: the selected atoms' indices, radii and colors. CPK adds the bonds
        # between selected atoms as index pairs, each bond once (lower index first).
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
            # Style element 1 is the sphere scale; for CPK, element 2 is the bond radius.
            # The 0.25 factors for CPK follow VMD's own CPK drawing code (see the links at
            # the end of Unity_VR_Client/COORDINATE_PLAYBACK.md).
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
    # Every snapshot gets a new token; TRAJ_READ, TRAJ_CHECK and TRAJ_SYNC must present it.
    set token [clock clicks]
    set trajectory_sessions($owner) [list $token $m [::vrbridge::trajectory_signature]]
    set matrix [transmult [lindex [molinfo $m get global_matrix] 0] [lindex [molinfo $m get scale_matrix] 0] [lindex [molinfo $m get rotate_matrix] 0] [lindex [molinfo $m get center_matrix] 0]]
    return [format {"supported":true,"token":"%s","molecule":%d,"atomCount":%d,"frameCount":%d,"frame":%d,"matrix":[%s],"reps":[%s],"cartoons":[%s],"quicksurfs":[%s]} $token $m $n $total [molinfo $m get frame] [join [concat {*}$matrix] ,] [join $reps ,] [join $cartoons ,] [join $surfaces ,]]
}

# Handle the TRAJ_ commands.
# command: the command name in upper case. args: the words after it, starting with the
# request ID (an ordinary parameter here, because it is not the last one). owner: the
# client's channel, the key into trajectory_sessions.
# Returns "TRAJJSON" followed by a JSON object with requestId and either the command's result
# fields or "error". VMDClient matches a reply to its request by that leading requestId.
# TrajectoryController compares the error texts "Trajectory snapshot expired" and
# "Trajectory settings changed" literally and opens a new snapshot when it receives one.
# Called by ::vrbridge::handle_command.
proc ::vrbridge::trajectory_command {command args owner} {
    variable trajectory_sessions
    set id [lindex $args 0]
    if {[catch {
        ::vrbridge::scene_request_id $id
        switch -- $command {
            TRAJ_OPEN {
                if {[llength $args] != 1} { error "Usage: TRAJ_OPEN requestId" }
                # A scene that cannot be drawn from coordinates is a normal reply
                # ("supported":false), not an "error" reply.
                if {[catch {::vrbridge::trajectory_metadata $owner} data]} {
                    catch {unset trajectory_sessions($owner)}
                    set data "\"supported\":false,\"message\":[::vrbridge::scene_json_string $data]"
                }
            }
            TRAJ_STYLE {
                if {[llength $args] != 3} { error "Usage: TRAJ_STYLE requestId repIndex method" }
                ::vrbridge::scene_set_representation [lindex $args 1] [lindex $args 2]
                # The old snapshot no longer matches; the client sends TRAJ_OPEN again.
                catch {unset trajectory_sessions($owner)}
                set data {"ok":true}
            }
            TRAJ_READ - TRAJ_CHECK - TRAJ_SYNC {
                # All three require the client's current token and an unchanged signature.
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
                    # TrajectoryController applies the same limit when it chooses count.
                    set maximum [expr {max(1,min(4,1000000 / (3 * [molinfo $m get numatoms])))}]
                    set count [::vrbridge::integer_in_range [lindex $args 3] 1 $maximum count]
                    set count [expr {min($count,[molinfo $m get numframes]-$start)}]
                    # Read through an atom selection that is moved from frame to frame, so
                    # VMD's displayed frame does not change.
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
