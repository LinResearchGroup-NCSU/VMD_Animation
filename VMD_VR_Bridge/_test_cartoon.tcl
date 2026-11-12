# Regression test for the NewCartoon metadata (cartoon.tcl) in TRAJ_OPEN replies, through
# ::vrbridge::handle_command, without a TCP client.
#
# Run in a separate VMD process. Only synthetic atoms are loaded or modified.
# VMD_COORDINATE_TEST_ROOT must name the output directory; it is created when missing.
# From PowerShell, in VMD_VR_Bridge:
#   $env:VMD_COORDINATE_TEST_ROOT = Join-Path $env:TEMP 'VMDCoordinateTests'
#   & 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_cartoon.tcl -eofexit
# The script ends with quit.
#
# Fixture: two ALA chains with complete backbones (N, CA, C, O), chain A with 12 residues and
# chain B with 6, in four frames with different coordinates. Secondary structure is assigned
# by hand: helix for resid 1 to 4, sheet for 5 to 8 and coil for 9 to 12.
# It checks: ribbon parameters, structure kinds and the split into two chains; coordinate
# reads; that VMD's rep settings and frame stay unchanged; invalidation after a structure
# change; a chain split at unselected residues; VDW, CPK and QuickSurf reps next to the
# cartoon; the "supported":false reply for Licorice; ColorID coloring; frame synchronization;
# and a CA-only copy of the molecule. A reply for the B-spline style is saved without a check.
#
# Output: PASS lines on the VMD console, and in the output directory cartoon.json,
# cartoon-frames.json, cartoon-selection.json, cartoon-bspline.json, cartoon-mixed.json and
# cartoon-result.txt with the passed and failed counts.
# Unity_VR_Client/Tests/CartoonPlaybackChecks.cs reads the JSON files.
source [file join [file dirname [info script]] vmd_vr_bridge.tcl]
set out $::env(VMD_COORDINATE_TEST_ROOT)
file mkdir $out
set passed 0; set failed 0
# Evaluate condition in the caller's scope. The first failed check raises an error, which ends
# the checks; it is counted once after the catch below.
proc check {name condition} {
    if {![uplevel 1 [list expr $condition]]} { error "FAIL $name" }
    incr ::passed; puts "PASS $name"
}
# Save the JSON payload of a TRAJJSON reply (the text after the 9-character prefix) as
# <out>/<name>.
proc save {name line} {
    set f [open [file join $::out $name] w]; puts $f [string range $line 9 end]; close $f
}
# Write the two-chain backbone fixture: residues 3.8 apart along x, chain B offset by 14 in y.
set pdb [file join $out cartoon-test.pdb]
set f [open $pdb w]
set serial 0
foreach {chain length offset} {A 12 0 B 6 14} {
    for {set r 1} {$r <= $length} {incr r} {
        foreach {name element dx dy} {N N -1.2 0 CA C 0 0 C C 1.3 0 O O 1.6 1.2} {
            incr serial
            puts $f [format "ATOM  %5d %-4s ALA %s%4d    %8.3f%8.3f%8.3f  1.00  0.00          %2s" $serial $name $chain $r [expr {$r*3.8+$dx}] [expr {$offset+$dy}] 0.0 $element]
        }
    }
    puts $f TER
}
puts $f END; close $f
set m [mol new $pdb type pdb waitfor all]
if {[catch {
    # Four frames, each displaced from the reference coordinates by a different wave.
    for {set i 1} {$i < 4} {incr i} { animate dup $m }
    set all [atomselect $m all]
    set reference [$all get {x y z}]
    for {set frame 0} {$frame < 4} {incr frame} {
        set moved {}
        foreach p $reference {
            lassign $p x y z
            lappend moved [list $x [expr {$y + 2*sin($x*0.22+$frame*0.7)}] [expr {1.2*cos($x*0.18+$frame*0.6)}]]
        }
        $all frame $frame; $all set {x y z} $moved
    }
    $all delete
    # Assign the secondary structure by hand.
    foreach {selection structure} {{resid 1 to 4} H {resid 5 to 8} E {resid 9 to 12} C} {
        set s [atomselect $m $selection]; $s set structure $structure; $s delete
    }
    molinfo $m set frame 0
    mol modstyle 0 $m NewCartoon 0.3 10 4.1 0
    mol modcolor 0 $m Structure
    set before [molinfo $m get {{rep 0} {selection 0} {color 0} {material 0} frame}]
    # "test" stands in for the client's channel in every handle_command call below.
    set open [::vrbridge::handle_command "TRAJ_OPEN cartoon" test]
    puts $open
    check supported {[string match {*"supported":true*} $open]}
    check ribbon-metadata {[string match {*"cartoons":*"radius":0.3*} $open]}
    check secondary-structure {[string match {*"structures":\[1,1,1,1,2,2,2,2,0,0,0,0\]*} $open]}
    check two-chains {[regexp -all {"indices":} $open] == 2}
    save cartoon.json $open
    set token [lindex $::vrbridge::trajectory_sessions(test) 0]
    set frames [::vrbridge::handle_command "TRAJ_READ coords $token 0 4" test]
    check read-frames {[string match {*"ok":true*} $frames]}
    save cartoon-frames.json $frames
    check preserves-vmd-scene {$before eq [molinfo $m get {{rep 0} {selection 0} {color 0} {material 0} frame}]}
    # A change of the secondary structure in VMD invalidates the snapshot.
    set s [atomselect $m {resid 1}]; $s set structure C; $s delete
    check structure-invalidates {[string match {*"error":*} [::vrbridge::handle_command "TRAJ_CHECK changed $token" test]]}
    set s [atomselect $m {resid 1}]; $s set structure H; $s delete
    # Unselected residues in the middle of chain A split it into two chains.
    mol modselect 0 $m {chain A and (resid 1 to 4 or resid 9 to 12)}
    set selected [::vrbridge::handle_command "TRAJ_OPEN selected" test]
    check selection-splits {[regexp -all {"indices":} $selected] == 2}
    save cartoon-selection.json $selected
    mol modselect 0 $m all
    mol modstyle 0 $m NewCartoon 0.3 10 4.1 1
    save cartoon-bspline.json [::vrbridge::handle_command "TRAJ_OPEN spline" test]
    mol modstyle 0 $m NewCartoon 0.3 10 4.1 0
    # A second rep next to the cartoon: VDW, CPK and QuickSurf are supported, Licorice is not.
    mol representation VDW 1 12
    mol color ColorID 1
    mol selection {index 1 5}
    mol addrep $m
    set mixed [::vrbridge::handle_command "TRAJ_OPEN mixed" test]
    check mixed-vdw-cartoon {[string match {*"supported":true*"method":"VDW"*"cartoons":*} $mixed]}
    save cartoon-mixed.json $mixed
    mol modstyle 1 $m CPK 1 0.3 12 12
    check mixed-cpk-cartoon {[string match {*"supported":true*"method":"CPK"*"cartoons":*} [::vrbridge::handle_command "TRAJ_OPEN mixedcpk" test]]}
    mol modstyle 1 $m QuickSurf
    check mixed-quicksurf-cartoon {[string match {*"supported":true*"cartoons":\[\{*"quicksurfs":\[\{"id":1,"indices":\[1,5\]*} [::vrbridge::handle_command "TRAJ_OPEN surf" test]]}
    mol modstyle 1 $m Licorice
    check other-representation-unchanged {[string match {*"supported":false*Representation Licorice*} [::vrbridge::handle_command "TRAJ_OPEN licorice" test]]}
    mol delrep 1 $m
    mol modcolor 0 $m ColorID 0
    check color-id {[string match {*"supported":true*} [::vrbridge::handle_command "TRAJ_OPEN color" test]]}
    set token [lindex $::vrbridge::trajectory_sessions(test) 0]
    ::vrbridge::handle_command "TRAJ_SYNC sync $token 2" test
    check pause-sync {[molinfo $m get frame] == 2}
    # A CA-only copy of the molecule: still two chains, with -1 for the missing C atoms.
    set caFile [file join $out cartoon-ca-only.pdb]
    set ca [atomselect $m {name CA}]; $ca writepdb $caFile; $ca delete
    mol off $m
    set caMolecule [mol new $caFile type pdb waitfor all]
    mol modstyle 0 $caMolecule NewCartoon
    set caReply [::vrbridge::handle_command "TRAJ_OPEN caonly" test]
    puts $caReply
    check ca-only-backbone {[string match {*"supported":true*"carbons":\[-1,*} $caReply]}
    check ca-only-chains {[regexp -all {"indices":} $caReply] == 2}
    mol delete $caMolecule
    file delete $caFile
} error]} { incr failed; puts "$error\n$::errorInfo" }
mol delete $m
file delete $pdb
puts "CARTOON TESTS: $passed passed, $failed failed"
set f [open [file join $out cartoon-result.txt] w]; puts $f "$passed $failed"; close $f
quit
