# VMD owns representation geometry. Unity receives a completed OBJ/MTL export.

# File path for Unity to import the exported OBJ/MTL files.
namespace eval ::vrbridge {
    variable static_export_root [file normalize [file join [file dirname [info script]] .. Unity_VR_Client Temp VMDStaticExports]]
    # Keep these across script reloads; an active importer still owns its export lease.
    foreach {name value} {scene_export_busy 0 scene_export_pending {} scene_export_owner {} scene_export_expires 0 scene_export_next 0 scene_cache_keep 3 quicksurf_min_grid_spacing 2.0} {
        variable $name
        if {![info exists $name]} { set $name $value }
    }
}

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

# Increase lattice spacing to reduce QuickSurf density-grid and mesh memory.
# This intentionally persists the coarser setting in VMD, avoiding a costly restore.
proc ::vrbridge::scene_limit_quicksurf {} {
    variable quicksurf_min_grid_spacing
    foreach molid [molinfo list] {
        if {![molinfo $molid get drawn]} { continue }
        for {set i 0} {$i < [molinfo $molid get numreps]} {incr i} {
            if {![mol showrep $molid $i]} { continue }
            set style [lindex [molinfo $molid get [list [list rep $i]]] 0]
            if {[lindex $style 0] eq "QuickSurf" && [lindex $style 3] < $quicksurf_min_grid_spacing} {
                mol modstyle $i $molid {*}[lreplace $style 3 3 $quicksurf_min_grid_spacing]
            }
        }
    }
}

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
        set scene_export_busy 0
        set scene_export_next [expr {[clock milliseconds] + 2000}]
        # Failed/partial exports are also bounded; there is no reader lease on them.
        catch {::vrbridge::scene_prune_cache}
        return -options $options $result
    }
    set scene_export_pending $request_id
    set scene_export_expires [expr {[clock milliseconds] + 300000}]
    if {$owner ne "" && $scene_export_owner eq ""} { ::vrbridge::scene_release $request_id }
    return $result
}

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

proc ::vrbridge::scene_disconnect {owner} {
    variable scene_export_owner
    variable scene_export_pending
    if {$scene_export_owner eq $owner && $owner ne ""} {
        set scene_export_owner ""
        if {$scene_export_pending ne ""} { ::vrbridge::scene_release $scene_export_pending }
    }
}

# Escape a string for use in JSON.
proc ::vrbridge::scene_json_string {value} {
    set escaped [string map [list \\ \\\\ \" \\\" \n \\n \r \\r \t \\t] $value]
    return "\"$escaped\""
}

# Validate that a request ID is a safe string to include in a file path and JSON.
proc ::vrbridge::scene_request_id {value} {
    if {![regexp {^[A-Za-z0-9_-]{1,64}$} $value]} { error "Invalid request ID" }
    return $value
}

# A scene snapshot including the frame, but deliberately excluding view transforms.
# OBJ exports include all visible molecules, so watch every molecule's reps.
# All drawing methods and their parameters are compared, not just QuickSurf/CPK.
# New GUI drawing methods need no case here: retain the complete rep value so
# transitions between any methods trigger synchronization automatically.
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

# Always answer the lightweight query, including when no molecule is loaded.
proc ::vrbridge::scene_state {} {
    set signature ""
    catch {set signature [::vrbridge::scene_signature]}
    return "SCENESTATE [format {{"signature":%s}} [::vrbridge::scene_json_string $signature]]"
}

# Export the current top molecule and its active VMD representations
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

# Export the current top molecule and its active VMD representations
# as Wavefront OBJ/MTL files for Unity to display.
# Each export is stored in a unique directory to prevent later requests
# from overwriting files that Unity may still be importing.
proc ::vrbridge::scene_write_export {request_id} {
    variable static_export_root
    ::vrbridge::scene_request_id $request_id
    set molid [::vrbridge::top_molecule]
    if {[lsearch -exact [render list] Wavefront] < 0} { error "This VMD build has no Wavefront exporter" }

    # Each response refers to its own files; a later request cannot overwrite an import.
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

# Set the drawing method for a representation of the top molecule.
# For now: VDW, CPK, QuickSurf, NewCartoon.  (More may be added later.)
proc ::vrbridge::scene_set_representation {rep_index method} {
    set molid [::vrbridge::top_molecule]
    set maximum [expr {[molinfo $molid get numreps] - 1}]
    set rep_index [::vrbridge::integer_in_range $rep_index 0 $maximum representation]
    # Whitelist commands: no eval or arbitrary Tcl received from Unity.
    # Extension point for Unity-initiated representation changes: add a case below
    # with the new method's VMD style/default parameters, and register its name in
    # RepresentationController.SupportedMethods (plus both validation messages).
    # This whitelist only governs SET_REPRESENTATION, not GUI-change detection.
    # VDW, CPK, QuickSurf and NewCartoon defaults are defined in their cases below.
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

# Handle a scene command from Unity.  Return either a JSON manifest or an error message.
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
