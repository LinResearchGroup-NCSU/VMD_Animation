# Run in a separate VMD text process from the VMD_VR_Bridge directory.
source ./vmd_vr_bridge.tcl
set out [file normalize ../Unity_VR_Client/Temp/static-export-tests]
file mkdir $out
proc assert {condition message} { if {![uplevel 1 [list expr $condition]]} { error $message } }
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
        save_reply $method [::vrbridge::handle_command "SET_REPRESENTATION test_$method 0 $method"]
        assert {[lindex [lindex [molinfo $molid get {{rep 0}}] 0] 0] eq $method} "Style was not applied"
        assert {[molinfo $molid get {{selection 0} {color 0} {material 0}}] eq $previous} "Other representation settings changed"
        puts $report "PASS $method export and VMD style change"
    }
    assert {[string match "SCENEERROR *" [::vrbridge::handle_command "SET_REPRESENTATION invalid 0 Unknown"]]} "Unsupported method accepted"
    assert {[string match "SCENEERROR *" [::vrbridge::handle_command "SET_REPRESENTATION invalid 99 VDW"]]} "Invalid rep accepted"
    set errorReply [::vrbridge::handle_command "SET_REPRESENTATION error_1 99 VDW"]
    set handle [open [file join $out error.json] w]; puts $handle [string range $errorReply 11 end]; close $handle
    assert {[lindex [lindex [molinfo $molid get {{rep 0}}] 0] 0] eq "NewCartoon"} "Rejected command modified style"
    mol modstyle 0 $molid VDW
    mol modselect 0 $molid {index 0 1}
    mol representation CPK
    mol selection {index 2 3}
    mol addrep $molid
    mol showrep $molid 1 off
    set before [molinfo $molid get {{rep 0} {rep 1} {selection 0} {selection 1} {rotate_matrix} {scale_matrix}}]
    save_reply multi [::vrbridge::handle_command "EXPORT_SCENE multi"]
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
    save_reply CartoonBackbone [::vrbridge::handle_command "SET_REPRESENTATION backbone 0 NewCartoon"]
    puts $report "PASS complete-backbone NewCartoon export"
} error options]} {
    puts $report "FAIL $error"
    puts $report [dict get $options -errorinfo]
}
close $report
quit
