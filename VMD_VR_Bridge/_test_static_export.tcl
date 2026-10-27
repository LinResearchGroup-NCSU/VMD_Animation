# Run in a separate VMD text process from the VMD_VR_Bridge directory.
source ./vmd_vr_bridge.tcl
# Keep this suite's six exports for later parser checks; production keeps three.
set ::vrbridge::scene_cache_keep 8
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
    set errorReply [scene_request "SET_REPRESENTATION error_1 99 VDW"]
    set handle [open [file join $out error.json] w]; puts $handle [string range $errorReply 11 end]; close $handle
    assert {[lindex [lindex [molinfo $molid get {{rep 0}}] 0] 0] eq "NewCartoon"} "Rejected command modified style"
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
    assert {[::vrbridge::handle_command "STEP 1"] eq "OK FRAME 0"} "STEP regression"
    assert {[string match "COORDS *" [::vrbridge::handle_command GET_COORDS]]} "Coordinates regression"
    mol off $molid
    puts $report "PASS invalid commands, scene preservation, coordinates and STEP"

    # A complete synthetic protein backbone proves cartoon export independently
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
