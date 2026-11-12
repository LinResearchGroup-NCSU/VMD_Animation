# Regression test for static scene export (static_scene.tcl): SET_REPRESENTATION,
# EXPORT_SCENE and RELEASE_SCENE through ::vrbridge::handle_command, without a TCP client.
#
# Run in a separate VMD text process from the VMD_VR_Bridge directory; the paths below are
# relative to it. From PowerShell:
#   & 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_static_export.tcl -eofexit
# It needs ../mutl_open_CA.pdb and a VMD build with the Wavefront renderer. Every request
# waits 2.1 s for the export cooldown. The script ends with quit.
#
# It checks that:
# - each of the four drawing methods is applied and exported, while selection, coloring and
#   material stay unchanged;
# - an unknown method and an invalid rep index are answered with SCENEERROR and leave the
#   style unchanged;
# - EXPORT_SCENE leaves the reps, selections, visibility and view matrices unchanged;
# - PING, STEP and GET_COORDS still work;
# - NewCartoon exports for a synthetic molecule with a complete backbone.
#
# Output goes to ../Unity_VR_Client/Temp/static-export-tests/ (ignored by Git): results.txt
# with PASS lines, or a FAIL line with the error; one <name>.json manifest per successful
# export; error.json; backbone.pdb; and the exported files under exports_<pid>/.
source ./vmd_vr_bridge.tcl
# Keep this suite's six exports for later parser checks; production keeps three.
set ::vrbridge::scene_cache_keep 8
# Send one scene command and return its reply. After a successful export the lease is
# released, as the Unity client does once it has read the files.
proc scene_request {command} {
    set response [::vrbridge::handle_command $command]
    if {[string match "SCENEJSON *" $response]} {
        ::vrbridge::handle_command "RELEASE_SCENE [lindex $command 1]"
    }
    # Respect the production cooldown instead of bypassing its queue protection.
    after 2100
    return $response
}
set out [file normalize ../Unity_VR_Client/Temp/static-export-tests]
file mkdir $out
# Export into a directory of this test's own.
# Do not rotate cache files belonging to a live interactive VMD process.
set ::vrbridge::static_export_root [file join $out exports_[pid]]
# Evaluate condition in the caller's scope and raise 'message' as an error if it is false.
proc assert {condition message} { if {![uplevel 1 [list expr $condition]]} { error $message } }
# Check that a reply is a SCENEJSON manifest and save its JSON payload to <out>/<name>.json.
proc save_reply {name response} {
    global out
    assert {[string match "SCENEJSON *" $response]} $response
    set file [open [file join $out $name.json] w]
    puts $file [string range $response 10 end]
    close $file
}
set report [open [file join $out results.txt] w]
fconfigure $report -buffering line
if {[catch {
    assert {[::vrbridge::handle_command PING] eq "PONG"} "PING regression"
    # Part 1: switch rep 0 (the first 32 atoms, ColorID 1) through the four drawing methods.
    set molid [mol new ../mutl_open_CA.pdb waitfor all]
    axes location Off
    mol modcolor 0 $molid ColorID 1
    mol modselect 0 $molid {index 0 to 31}
    set previous [molinfo $molid get {{selection 0} {color 0} {material 0}}]
    foreach method {VDW CPK QuickSurf NewCartoon} {
        save_reply $method [scene_request "SET_REPRESENTATION test_$method 0 $method"]
        assert {[lindex [lindex [molinfo $molid get {{rep 0}}] 0] 0] eq $method} "Style was not applied"
        assert {[molinfo $molid get {{selection 0} {color 0} {material 0}}] eq $previous} "Other representation settings changed"
        puts $report "PASS $method export and VMD style change"
    }
    assert {[string match "SCENEERROR *" [scene_request "SET_REPRESENTATION invalid 0 Unknown"]]} "Unsupported method accepted"
    assert {[string match "SCENEERROR *" [scene_request "SET_REPRESENTATION invalid 99 VDW"]]} "Invalid rep accepted"
    # Save one SCENEERROR payload (the text after the 11-character prefix) as error.json.
    set errorReply [scene_request "SET_REPRESENTATION error_1 99 VDW"]
    set handle [open [file join $out error.json] w]; puts $handle [string range $errorReply 11 end]; close $handle
    assert {[lindex [lindex [molinfo $molid get {{rep 0}}] 0] 0] eq "NewCartoon"} "Rejected command modified style"
    # Part 2: a plain EXPORT_SCENE with one visible and one hidden rep must not alter the scene.
    mol modstyle 0 $molid VDW
    mol modselect 0 $molid {index 0 1}
    mol representation CPK
    mol selection {index 2 3}
    mol addrep $molid
    mol showrep $molid 1 off
    set before [molinfo $molid get {{rep 0} {rep 1} {selection 0} {selection 1} {rotate_matrix} {scale_matrix}}]
    save_reply multi [scene_request "EXPORT_SCENE multi"]
    assert {[molinfo $molid get {{rep 0} {rep 1} {selection 0} {selection 1} {rotate_matrix} {scale_matrix}}] eq $before} "Export mutated the scene"
    assert {[mol showrep $molid 1] == 0} "Export changed visibility"
    # The molecule has a single frame, so STEP 1 is clamped to frame 0.
    assert {[::vrbridge::handle_command "STEP 1"] eq "OK FRAME 0"} "STEP regression"
    assert {[string match "COORDS *" [::vrbridge::handle_command GET_COORDS]]} "Coordinates regression"
    mol off $molid
    puts $report "PASS invalid commands, scene preservation, coordinates and STEP"

    # Part 3: a complete synthetic protein backbone proves cartoon export independently
    # of the user's CA-only molecule, which VMD does not classify as a protein.
    set fixture [file join $out backbone.pdb]
    set handle [open $fixture w]
    set serial 0
    for {set resid 1} {$resid <= 8} {incr resid} {
        foreach {name element dx dy} {N N -1.2 0 CA C 0 0 C C 1.3 0 O O 1.6 1.2} {
            incr serial
            puts $handle [format "ATOM  %5d %-4s ALA A%4d    %8.3f%8.3f%8.3f  1.00  0.00          %2s" \
                $serial $name $resid [expr {$resid * 3.8 + $dx}] $dy 0.0 $element]
        }
    }
    puts $handle "END"; close $handle
    set molid [mol new $fixture waitfor all]
    save_reply CartoonBackbone [scene_request "SET_REPRESENTATION backbone 0 NewCartoon"]
    puts $report "PASS complete-backbone NewCartoon export"
} error options]} {
    puts $report "FAIL $error"
    puts $report [dict get $options -errorinfo]
}
close $report
quit
