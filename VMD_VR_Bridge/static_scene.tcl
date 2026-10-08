# VMD owns representation geometry. Unity receives a completed OBJ/MTL export.
namespace eval ::vrbridge {
    variable static_export_root [file normalize [file join [file dirname [info script]] .. Unity_VR_Client Temp VMDStaticExports]]
}

proc ::vrbridge::scene_json_string {value} {
    set escaped [string map [list \\ \\\\ \" \\\" \n \\n \r \\r \t \\t] $value]
    return "\"$escaped\""
}

proc ::vrbridge::scene_request_id {value} {
    if {![regexp {^[A-Za-z0-9_-]{1,64}$} $value]} { error "Invalid request ID" }
    return $value
}

# Export the current top molecule and its active VMD representations
# as Wavefront OBJ/MTL files for Unity to display.
# Each export is stored in a unique directory to prevent later requests
# from overwriting files that Unity may still be importing.
proc ::vrbridge::scene_export {request_id} {
    variable static_export_root
    ::vrbridge::scene_request_id $request_id
    set molid [::vrbridge::top_molecule]
    if {[lsearch -exact [render list] Wavefront] < 0} { error "This VMD build has no Wavefront exporter" }

    # Each response refers to its own files; a later request cannot overwrite an import.
    set folder [file join $static_export_root "export_[pid]_[clock clicks]_$request_id"]
    file mkdir $folder
    set obj [file join $folder scene.obj]
    display update
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
    return [format {{"requestId":%s,"path":%s,"molecule":%d,"frame":%d,"reps":[%s]}} \
        [::vrbridge::scene_json_string $request_id] [::vrbridge::scene_json_string $obj] \
        $molid [molinfo $molid get frame] [join $reps ,]]
}

proc ::vrbridge::scene_set_representation {rep_index method} {
    set molid [::vrbridge::top_molecule]
    set maximum [expr {[molinfo $molid get numreps] - 1}]
    set rep_index [::vrbridge::integer_in_range $rep_index 0 $maximum representation]
    # Whitelist commands: no eval or arbitrary Tcl received from Unity.
    switch -nocase -- $method {
        VDW { set style {VDW 1.0 12} }
        CPK { set style {CPK 1.0 0.3 12 12} }
        QuickSurf { set style {QuickSurf 1.0 0.5 1.0 1} }
        NewCartoon { set style {NewCartoon 0.3 10 4.1 0} }
        default { error "Supported methods: VDW CPK QuickSurf NewCartoon" }
    }
    # Changing only the drawing method preserves selection, coloring and material.
    mol modstyle $rep_index $molid {*}$style
}

proc ::vrbridge::scene_command {command arguments} {
    set request_id [lindex $arguments 0]
    if {[catch {
        ::vrbridge::scene_request_id $request_id
        if {$command eq "EXPORT_SCENE"} {
            if {[llength $arguments] != 1} { error "Usage: EXPORT_SCENE <requestId>" }
        } else {
            if {[llength $arguments] != 3} {
                error "Usage: SET_REPRESENTATION <requestId> <repIndex> <method>"
            }
            ::vrbridge::scene_set_representation [lindex $arguments 1] [lindex $arguments 2]
        }
        set manifest [::vrbridge::scene_export $request_id]
    } message]} {
        return "SCENEERROR [format {{"requestId":%s,"message":%s}} \
            [::vrbridge::scene_json_string $request_id] [::vrbridge::scene_json_string $message]]"
    }
    return "SCENEJSON $manifest"
}
