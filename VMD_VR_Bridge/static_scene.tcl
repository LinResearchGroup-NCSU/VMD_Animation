# Static scene export for the "VMD mesh" display route. Sourced by vmd_vr_bridge.tcl.
#
# VMD owns the representation geometry: it writes the visible scene as Wavefront OBJ/MTL plus
# a scene.binding file, and Unity imports the files from the absolute path in the reply.
#
# Commands implemented here (dispatched by ::vrbridge::handle_command):
#   GET_SCENE_STATE                 Reply SCENESTATE with the scene signature.
#   EXPORT_SCENE <requestId>        Export; reply SCENEJSON with a manifest, or SCENEERROR.
#   SET_REPRESENTATION <requestId> <repIndex> <method>
#                                   Change one rep's drawing method, then export as above.
#   RELEASE_SCENE <requestId>       End the export's lease; reply OK RELEASE_SCENE <requestId>.
#
# Export lease: from the start of an export until the client sends RELEASE_SCENE (it has read
# the files) or disconnects, further exports are refused with code BUSY. A lease that is never
# released expires after five minutes. Every release and every failed export starts a
# two-second cooldown. The Unity side is Assets/Scripts/Molecule/RepresentationController.cs.
#
# scene_request_id and scene_json_string are also used by SELECT and the TRAJ_ commands, and
# scene_set_representation by TRAJ_STYLE.

namespace eval ::vrbridge {
    # Directory that receives the exports; it is set again on every reload.
    # Independent of the repository layout: users may only have VMD and these scripts.
    # Unity imports the absolute OBJ path returned in each reply.
    if {[info exists ::env(LOCALAPPDATA)] && $::env(LOCALAPPDATA) ne ""} {
        variable static_export_root [file normalize [file join $::env(LOCALAPPDATA) VMD_VR exports]]
    } else {
        variable static_export_root [file normalize [file join ~ .vmd_vr exports]]
    }
    # Export state and limits. A default is assigned only while the variable does not exist.
    # Keep these across script reloads; an active importer still owns its export lease.
    #   scene_export_busy     1 while an export is running or its lease is held.
    #   scene_export_pending  Request ID of the finished export whose lease is held, else "".
    #   scene_export_owner    Channel of the client that requested the current export, or "".
    #   scene_export_expires  Time (clock milliseconds) at which a held lease counts as abandoned.
    #   scene_export_next     Time (clock milliseconds) before which new exports are refused.
    #   scene_cache_keep      Number of newest export directories that scene_prune_cache keeps.
    #   quicksurf_min_grid_spacing  Smallest QuickSurf grid spacing allowed in an export.
    foreach {name value} {scene_export_busy 0 scene_export_pending {} scene_export_owner {} scene_export_expires 0 scene_export_next 0 scene_cache_keep 3 quicksurf_min_grid_spacing 2.0} {
        variable $name
        if {![info exists $name]} { set $name $value }
    }
}

# Delete old export directories under static_export_root, keeping the scene_cache_keep newest
# (at least one). Called by scene_release when a lease ends and by scene_export after a failed
# export. A directory is considered only when it is named export_<pid>_<clicks>_<requestId>
# and holds the bridge-export.cache marker written by scene_write_export; the time stored in
# the marker decides which directories are newest.
# Only reclaim bridge-marked cache directories. Never recursively delete a path,
# follow links, remove a user OBJ, or delete a directory containing unfamiliar files.
proc ::vrbridge::scene_prune_cache {} {
    variable static_export_root
    variable scene_cache_keep
    set root [file normalize $static_export_root]
    if {![file isdirectory $root] || [file type $root] ne "directory"} { return }
    set candidates {}
    foreach folder [glob -nocomplain -directory $root export_*] {
        if {[file type $folder] ne "directory" || [file dirname [file normalize $folder]] ne $root} { continue }
        if {![regexp {^export_[0-9]+_[0-9]+_[A-Za-z0-9_-]+$} [file tail $folder]]} { continue }
        set marker [file join $folder bridge-export.cache]
        if {![file exists $marker] || [file type $marker] ne "file"} { continue }
        set handle [open $marker r]
        set stamp [read $handle 128]
        close $handle
        if {![regexp {^VMD_STATIC_EXPORT_V1\n([0-9]+)\n$} $stamp -> created]} { continue }
        lappend candidates [list $created $folder]
    }
    set keep [expr {max(1, $scene_cache_keep)}]
    # An older directory is deleted only when it holds nothing but the bridge's own files.
    foreach entry [lrange [lsort -integer -decreasing -index 0 $candidates] $keep end] {
        set folder [lindex $entry 1]
        set files {}
        set safe 1
        foreach path [glob -nocomplain -directory $folder * .*] {
            set name [file tail $path]
            if {$name eq "." || $name eq ".."} { continue }
            if {$name ni {scene.obj scene.mtl scene.binding bridge-export.cache} || [file type $path] ne "file"} {
                set safe 0
                break
            }
            lappend files $path
        }
        if {$safe} {
            foreach path $files { file delete -- $path }
            file delete -- $folder
        }
    }
}

# Raise the grid spacing (style element 3) of every visible QuickSurf representation of every
# drawn molecule to at least quicksurf_min_grid_spacing, to reduce QuickSurf density-grid and
# mesh memory. The style name is matched in any letter case. Called by scene_export before
# each export.
# This intentionally persists the coarser setting in VMD; it is not restored after the export.
proc ::vrbridge::scene_limit_quicksurf {} {
    variable quicksurf_min_grid_spacing
    foreach molid [molinfo list] {
        if {![molinfo $molid get drawn]} { continue }
        for {set i 0} {$i < [molinfo $molid get numreps]} {incr i} {
            if {![mol showrep $molid $i]} { continue }
            set style [lindex [molinfo $molid get [list [list rep $i]]] 0]
            # VMD keeps the spelling the rep was created with ("Quicksurf" in render.tcl), and a
            # bare name without parameters when the defaults were used. Fill in the same
            # defaults as quicksurf_metadata, so every QuickSurf rep is limited.
            if {![string equal -nocase [lindex $style 0] QuickSurf]} { continue }
            lassign [lrange $style 1 4] radiusScale isovalue spacing quality
            if {$radiusScale eq ""} { set radiusScale 1.0 }
            if {$isovalue eq ""} { set isovalue 0.5 }
            if {$spacing eq ""} { set spacing 1.0 }
            if {$quality eq ""} { set quality 0 }
            if {[string is double -strict $spacing] && $spacing < $quicksurf_min_grid_spacing} {
                mol modstyle $i $molid QuickSurf $radiusScale $isovalue $quicksurf_min_grid_spacing $quality
            }
        }
    }
}

# Run one export under the export lock and return its JSON manifest (see scene_write_export).
# request_id: the client's ID for this request. rep_index, method: when method is not empty,
# that representation's drawing method is changed first (SET_REPRESENTATION).
# owner: the client's channel, or "" when there is no client (Tk Console, tests).
# Raises an error with errorcode VRBRIDGE BUSY while another export is running, its lease is
# held or the cooldown has not passed. Called by scene_command.
#
# The lock covers export AND Unity's file read. A plain render-only busy flag cannot
# reject TCP requests that accumulated while synchronous render blocked the Tcl loop.
proc ::vrbridge::scene_export {request_id {rep_index ""} {method ""} {owner ""}} {
    variable scene_export_busy
    variable scene_export_pending
    variable scene_export_expires
    variable scene_export_next
    variable scene_export_owner
    ::vrbridge::scene_request_id $request_id
    set now [clock milliseconds]
    # Recover abandoned import leases after five minutes, never unlock an active render.
    if {$scene_export_busy && $scene_export_pending ne "" && $now >= $scene_export_expires} {
        set scene_export_busy 0
        set scene_export_pending ""
    }
    if {$scene_export_busy || $now < $scene_export_next} {
        return -code error -errorcode {VRBRIDGE BUSY} "Export busy or cooling down; retry after the current import finishes"
    }
    set scene_export_busy 1
    set scene_export_pending ""
    set scene_export_owner $owner
    set code [catch {
        if {$method ne ""} { ::vrbridge::scene_set_representation $rep_index $method }
        ::vrbridge::scene_limit_quicksurf
        ::vrbridge::scene_write_export $request_id
    } result options]
    if {$code} {
        # A failed export frees the lock at once, but still starts the two-second cooldown.
        set scene_export_busy 0
        set scene_export_next [expr {[clock milliseconds] + 2000}]
        # Failed/partial exports are also bounded; there is no reader lease on them.
        catch {::vrbridge::scene_prune_cache}
        return -options $options $result
    }
    # The files exist: the lease starts here and lasts until scene_release. Once it is five
    # minutes old, the next export request treats it as abandoned (see above).
    set scene_export_pending $request_id
    set scene_export_expires [expr {[clock milliseconds] + 300000}]
    # scene_disconnect clears scene_export_owner. If it did so for this owner before the
    # lease started, it had nothing to release yet and no RELEASE_SCENE will come: release now.
    if {$owner ne "" && $scene_export_owner eq ""} { ::vrbridge::scene_release $request_id }
    return $result
}

# End the lease of export request_id, start the two-second cooldown and prune old export
# directories. Always returns "OK RELEASE_SCENE <request_id>", also when nothing is released:
# when owner is given and is not the lease's owner, or when request_id is not the leased export.
# Called for RELEASE_SCENE with the client's channel as owner, and without an owner by
# scene_export and scene_disconnect.
proc ::vrbridge::scene_release {request_id {owner ""}} {
    variable scene_export_busy
    variable scene_export_pending
    variable scene_export_next
    variable scene_export_owner
    ::vrbridge::scene_request_id $request_id
    if {$owner ne "" && $scene_export_owner ne $owner} { return "OK RELEASE_SCENE $request_id" }
    if {$scene_export_pending eq $request_id} {
        set scene_export_pending ""
        set scene_export_owner ""
        set scene_export_busy 0
        set scene_export_next [expr {[clock milliseconds] + 2000}]
        if {[catch {::vrbridge::scene_prune_cache} problem]} {
            puts "VMD export cache cleanup warning: $problem"
        }
    }
    return "OK RELEASE_SCENE $request_id"
}

# A client is gone: clear its ownership of the current export and release the lease it holds,
# because it will not send RELEASE_SCENE any more.
# Called by ::vrbridge::disconnect and ::vrbridge::stop with the client's channel.
proc ::vrbridge::scene_disconnect {owner} {
    variable scene_export_owner
    variable scene_export_pending
    if {$scene_export_owner eq $owner && $owner ne ""} {
        set scene_export_owner ""
        if {$scene_export_pending ne ""} { ::vrbridge::scene_release $scene_export_pending }
    }
}

# Return value as a quoted JSON string. Backslash, double quote, newline, carriage return and
# tab are escaped; other characters are copied unchanged.
proc ::vrbridge::scene_json_string {value} {
    set escaped [string map [list \\ \\\\ \" \\\" \n \\n \r \\r \t \\t] $value]
    return "\"$escaped\""
}

# Validate a client request ID: 1 to 64 letters, digits, underscores or hyphens, so that it is
# safe to include in a file path and JSON. Returns the ID, or raises an error.
proc ::vrbridge::scene_request_id {value} {
    if {![regexp {^[A-Za-z0-9_-]{1,64}$} $value]} { error "Invalid request ID" }
    return $value
}

# Return a Tcl list of everything an export depends on: the top molecule and, for every
# molecule, whether it is drawn, its frame, and each rep's visibility, style, selection,
# coloring and material.
# A scene snapshot including the frame, but deliberately excluding view transforms.
# OBJ exports include all visible molecules, so watch every molecule's reps.
# The complete rep strings are compared, so a change of any drawing method or parameter is
# detected without a case per method.
# Unity polls this with GET_SCENE_STATE and exports again when it changes; each export
# manifest also carries it.
proc ::vrbridge::scene_signature {} {
    set signature [list [molinfo top]]
    foreach molid [molinfo list] {
        lappend signature $molid [molinfo $molid get drawn] [molinfo $molid get frame]
        for {set i 0} {$i < [molinfo $molid get numreps]} {incr i} {
            lappend signature $i [mol showrep $molid $i] \
                [molinfo $molid get [list [list rep $i] [list selection $i] [list color $i] [list material $i]]]
        }
    }
    return $signature
}

# GET_SCENE_STATE: return "SCENESTATE" followed by a JSON object whose "signature" field holds
# scene_signature as a string.
# Always answer the lightweight query, including when no molecule is loaded: if the signature
# cannot be read, it is sent as an empty string.
proc ::vrbridge::scene_state {} {
    set signature ""
    catch {set signature [::vrbridge::scene_signature]}
    return "SCENESTATE [format {{"signature":%s}} [::vrbridge::scene_json_string $signature]]"
}

# Write scene.binding, which Unity (Assets/Scripts/Representations/VMDMeshBinding.cs) uses to
# move the exported mesh with the atoms during playback.
# path: the file to write. molid: the top molecule. Called by scene_write_export.
# Lines written:
#   VMD_BINDING 1
#   molecule <molid>
#   matrix <16 numbers>      The molecule's global * scale * rotate * center matrix.
#   atom <x> <y> <z>         One line per atom, at the molecule's current frame.
#   rep <index> <method> <atom indices>    One line per visible rep of the molecule.
# Record the exact reference coordinates and OBJ-space transform once per export.
# Playback still sends coordinates only. Bindings use top-molecule rep groups; axes
# and other molecules remain static, rather than being driven by the wrong atom list.
proc ::vrbridge::scene_write_binding {path molid} {
    set matrix [transmult [lindex [molinfo $molid get global_matrix] 0] \
        [lindex [molinfo $molid get scale_matrix] 0] \
        [lindex [molinfo $molid get rotate_matrix] 0] \
        [lindex [molinfo $molid get center_matrix] 0]]
    set handle [open $path w]
    set selection ""
    set code [catch {
        puts $handle "VMD_BINDING 1"
        puts $handle "molecule $molid"
        puts $handle "matrix [join [concat {*}$matrix] { }]"
        set selection [atomselect $molid all frame [molinfo $molid get frame]]
        foreach position [$selection get {x y z}] { puts $handle "atom [join $position { }]" }
        $selection delete
        set selection ""
        for {set i 0} {$i < [molinfo $molid get numreps]} {incr i} {
            if {![mol showrep $molid $i]} { continue }
            set style [lindex [molinfo $molid get [list [list rep $i]]] 0]
            set expression [lindex [molinfo $molid get [list [list selection $i]]] 0]
            set selection [atomselect $molid $expression frame [molinfo $molid get frame]]
            puts $handle "rep $i [lindex $style 0] [join [$selection get index] { }]"
            $selection delete
            set selection ""
        }
    } result options]
    if {$selection ne ""} { catch {$selection delete} }
    close $handle
    if {$code} { return -options $options $result }
}

# Export the visible VMD scene as Wavefront OBJ/MTL files for Unity to display, and return
# the JSON manifest for the SCENEJSON reply: requestId, path (absolute path of the OBJ),
# molecule, frame, reps (id, method and visible flag of each rep of the top molecule) and
# signature (scene_signature after the export).
# Each export is stored in a unique directory, export_<pid>_<clicks>_<requestId>, to prevent
# later requests from overwriting files that Unity may still be importing. It holds
# scene.obj, scene.mtl, scene.binding and the bridge-export.cache marker that
# scene_prune_cache requires.
# Raises an error without a top molecule or a Wavefront renderer. Called by scene_export.
proc ::vrbridge::scene_write_export {request_id} {
    variable static_export_root
    ::vrbridge::scene_request_id $request_id
    set molid [::vrbridge::top_molecule]
    if {[lsearch -exact [render list] Wavefront] < 0} { error "This VMD build has no Wavefront exporter" }

    set folder [file join $static_export_root "export_[pid]_[clock clicks]_$request_id"]
    file mkdir $folder
    set marker [open [file join $folder bridge-export.cache] w]
    puts $marker "VMD_STATIC_EXPORT_V1\n[clock milliseconds]"
    close $marker
    set obj [file join $folder scene.obj]
    display update
    ::vrbridge::scene_write_binding [file join $folder scene.binding] $molid
    render Wavefront $obj
    if {![file exists $obj] || ![file exists [file rootname $obj].mtl]} {
        error "VMD did not produce both OBJ and MTL files"
    }
    set reps {}
    for {set i 0} {$i < [molinfo $molid get numreps]} {incr i} {
        set style [lindex [molinfo $molid get [list [list rep $i]]] 0]
        lappend reps [format {{"id":%d,"method":%s,"visible":%d}} $i \
            [::vrbridge::scene_json_string [lindex $style 0]] [mol showrep $molid $i]]
    }
    return [format {{"requestId":%s,"path":%s,"molecule":%d,"frame":%d,"reps":[%s],"signature":%s}} \
        [::vrbridge::scene_json_string $request_id] [::vrbridge::scene_json_string $obj] \
        $molid [molinfo $molid get frame] [join $reps ,] \
        [::vrbridge::scene_json_string [::vrbridge::scene_signature]]]
}

# Set the drawing method of representation rep_index of the top molecule to method: VDW, CPK,
# QuickSurf or NewCartoon (in any letter case), with the fixed parameters listed below.
# Raises an error for an invalid index or any other method.
# Called by scene_export for SET_REPRESENTATION and by trajectory_command for TRAJ_STYLE.
proc ::vrbridge::scene_set_representation {rep_index method} {
    set molid [::vrbridge::top_molecule]
    set maximum [expr {[molinfo $molid get numreps] - 1}]
    set rep_index [::vrbridge::integer_in_range $rep_index 0 $maximum representation]
    # Whitelist commands: no eval or arbitrary Tcl received from Unity.
    # Extension point for Unity-initiated representation changes: add a case below
    # with the new method's VMD style/default parameters, and register its name in
    # RepresentationController.SupportedMethods (plus both validation messages).
    # This whitelist only governs SET_REPRESENTATION and TRAJ_STYLE, not GUI-change detection.
    switch -nocase -- $method {
        VDW { set style {VDW 1.0 12} }
        CPK { set style {CPK 1.0 0.3 12 12} }
        QuickSurf { set style {QuickSurf 1.0 0.5 2.0 1} }
        NewCartoon { set style {NewCartoon 0.3 10 4.1 0} }
        default { error "Supported methods: VDW CPK QuickSurf NewCartoon" }
    }
    # Changing only the drawing method preserves selection, coloring and material.
    mol modstyle $rep_index $molid {*}$style
}

# Handle EXPORT_SCENE and SET_REPRESENTATION.
# command: one of those two names. arguments: the words after it, starting with the request
# ID. owner: the client's channel.
# Returns "SCENEJSON <manifest>" on success. Any failure, including an invalid request ID or a
# wrong number of arguments, returns "SCENEERROR" followed by a JSON object with requestId,
# message and code. code is BUSY when scene_export refused the request because of the lock or
# the cooldown, and ERROR otherwise.
# Called by ::vrbridge::handle_command.
proc ::vrbridge::scene_command {command arguments {owner ""}} {
    set request_id [lindex $arguments 0]
    if {[catch {
        ::vrbridge::scene_request_id $request_id
        if {$command eq "EXPORT_SCENE"} {
            if {[llength $arguments] != 1} { error "Usage: EXPORT_SCENE <requestId>" }
            set manifest [::vrbridge::scene_export $request_id "" "" $owner]
        } else {
            if {[llength $arguments] != 3} {
                error "Usage: SET_REPRESENTATION <requestId> <repIndex> <method>"
            }
            set manifest [::vrbridge::scene_export $request_id [lindex $arguments 1] [lindex $arguments 2] $owner]
        }
    } message options]} {
        set code "ERROR"
        if {[dict exists $options -errorcode] && [dict get $options -errorcode] eq "VRBRIDGE BUSY"} { set code "BUSY" }
        return "SCENEERROR [format {{"requestId":%s,"message":%s,"code":%s}} \
            [::vrbridge::scene_json_string $request_id] [::vrbridge::scene_json_string $message] \
            [::vrbridge::scene_json_string $code]]"
    }
    return "SCENEJSON $manifest"
}
