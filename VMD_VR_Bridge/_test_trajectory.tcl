# Regression test for the coordinate commands (trajectory.tcl and quicksurf.tcl): TRAJ_OPEN,
# TRAJ_READ, TRAJ_CHECK, TRAJ_SYNC and TRAJ_STYLE through ::vrbridge::handle_command, without
# a TCP client. Small real-VMD coordinate regression fixture. Never invokes Wavefront.
#
# Run in a separate VMD text process, with VMD_COORDINATE_TEST_ROOT naming a writable output
# directory; it is created when missing. From PowerShell, in VMD_VR_Bridge:
#   $env:VMD_COORDINATE_TEST_ROOT = Join-Path $env:TEMP 'VMDCoordinateTests'
#   & 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_trajectory.tcl -eofexit
# The script ends with quit.
#
# Fixture: a molecule of two bonded atoms with eight frames, written to that directory and
# deleted at the end.
# It checks: VDW, CPK and QuickSurf metadata (explicit and default QuickSurf parameters);
# batched reads that leave VMD's frame unchanged; the per-request frame limit; snapshots
# being private to a client; frame synchronization; invalidation after a selection change;
# the "supported":false reply for an unsupported coloring, drawing method and dynamic
# selection; and removal of the snapshot on disconnect.
#
# Output: PASS and FAIL lines on the VMD console, and in that directory vdw.json, cpk.json,
# quicksurf.json (TRAJ_OPEN replies), frames.json (a TRAJ_READ reply) and bridge-result.txt
# with the passed and failed counts. The Unity checks CoordinatePlaybackChecks.cs and
# QuickSurfPlaybackChecks.cs under Unity_VR_Client/Tests/ read the JSON files.
source [file join [file dirname [info script]] vmd_vr_bridge.tcl]
set out $::env(VMD_COORDINATE_TEST_ROOT)
file mkdir $out
set pdb [file join $out coordinate-test.pdb]
set f [open $pdb w]
puts $f "ATOM      1  C   ALA A   1       0.000   0.000   0.000  1.00  0.00           C"
puts $f "ATOM      2  O   ALA A   1       1.300   0.000   0.000  1.00  0.00           O"
puts $f END
close $f
set m [mol new $pdb type pdb waitfor all]
for {set i 1} {$i < 8} {incr i} { animate dup $m }
# Bond the two atoms (for the CPK check) and give every frame its own coordinates.
set sel [atomselect $m all]
$sel setbonds {{1} {0}}
for {set i 0} {$i < 8} {incr i} {
    $sel frame $i
    $sel set {x y z} [list [list $i 0 0] [list [expr {$i+1.3}] 0 $i]]
}
$sel delete
molinfo $m set frame 0
mol modstyle 0 $m VDW 1.0 12
set passed 0
set failed 0
# Record one check; value is its boolean result. A failed check is counted and the run goes on.
proc check {name value} {
    if {$value} { incr ::passed; puts "PASS $name" } else { incr ::failed; puts "FAIL $name" }
}
# Save the JSON payload of a TRAJJSON reply (the text after the 9-character prefix) as
# <out>/<name>.
proc save_json {name line} {
    set f [open [file join $::out $name] w]; puts $f [string range $line 9 end]; close $f
}
if {[catch {
    # The second argument of handle_command stands in for the client's channel: "client"
    # owns the snapshots below, and "other" is a second client without one.
    set open [::vrbridge::handle_command "TRAJ_OPEN open" client]
    puts $open
    check vdw-supported [string match {*"supported":true*} $open]
    save_json vdw.json $open
    set token [lindex $::vrbridge::trajectory_sessions(client) 0]
    set read [::vrbridge::handle_command "TRAJ_READ read $token 0 4" client]
    save_json frames.json $read
    check batch-success [string match {*"ok":true*} $read]
    check frame-unchanged [expr {[molinfo $m get frame] == 0}]
    # A count above the per-request limit of four frames is refused.
    check bounds-rejected [string match {*"error":*} [::vrbridge::handle_command "TRAJ_READ bad $token 0 100" client]]
    check wrong-owner-rejected [string match {*"error":*} [::vrbridge::handle_command "TRAJ_READ bad $token 0 1" other]]
    ::vrbridge::handle_command "TRAJ_SYNC sync $token 2" client
    check pause-sync [expr {[molinfo $m get frame] == 2}]
    # TRAJ_STYLE drops the snapshot; the new CPK snapshot must contain the bond.
    ::vrbridge::handle_command "TRAJ_STYLE style 0 CPK" client
    set open [::vrbridge::handle_command "TRAJ_OPEN cpk" client]
    puts $open
    save_json cpk.json $open
    check cpk-supported [string match {*"supported":true*"bonds":\[0,1\]*} $open]
    set token [lindex $::vrbridge::trajectory_sessions(client) 0]
    # A selection change in VMD invalidates the token; a new snapshot has the new selection.
    mol modselect 0 $m "index 0"
    check selection-invalidates [string match {*"error":*} [::vrbridge::handle_command "TRAJ_CHECK changed $token" client]]
    set selected [::vrbridge::handle_command "TRAJ_OPEN selected" client]
    check selection-preserved [string match {*"indices":\[0\]*"bonds":\[\]*} $selected]
    # QuickSurf with explicit parameters, then with the alternate spelling and no parameters.
    mol modselect 0 $m all
    mol modstyle 0 $m QuickSurf 1.0 0.5 1.0 1
    set surface [::vrbridge::handle_command "TRAJ_OPEN surface" client]
    puts $surface
    save_json quicksurf.json $surface
    check quicksurf-supported [string match {*"supported":true*"reps":\[\]*"cartoons":\[\]*"quicksurfs":\[\{"id":0,"indices":\[0,1\]*"radiusScale":1.0,"isovalue":0.5,"gridSpacing":1.0,"quality":1*} $surface]
    set token [lindex $::vrbridge::trajectory_sessions(client) 0]
    check quicksurf-reads-coordinates [string match {*"ok":true*} [::vrbridge::handle_command "TRAJ_READ surfaceread $token 0 4" client]]
    mol modstyle 0 $m Quicksurf
    check quicksurf-defaults [string match {*"radiusScale":1.0,"isovalue":0.5,"gridSpacing":1.0,"quality":0*} [::vrbridge::handle_command "TRAJ_OPEN defaults" client]]
    # Scenes that the coordinate route cannot draw are reported as unsupported.
    mol modcolor 0 $m Beta
    check quicksurf-color-fallback [string match {*"supported":false*Color method Beta*} [::vrbridge::handle_command "TRAJ_OPEN beta" client]]
    mol modcolor 0 $m Name
    mol modstyle 0 $m Licorice
    check other-representation-fallback [string match {*"supported":false*Representation Licorice*} [::vrbridge::handle_command "TRAJ_OPEN fallback" client]]
    mol modstyle 0 $m VDW
    mol selupdate 0 $m on
    check dynamic-selection-fallback [string match {*"supported":false*} [::vrbridge::handle_command "TRAJ_OPEN dynamic" client]]
    mol selupdate 0 $m off
    # Disconnecting a client removes its snapshot.
    ::vrbridge::handle_command "TRAJ_OPEN owner" client
    ::vrbridge::trajectory_disconnect client
    check disconnect-releases [expr {![info exists ::vrbridge::trajectory_sessions(client)]}]
} error]} { incr failed; puts "FAIL exception $error\n$::errorInfo" }
puts "COORDINATE TESTS: $passed passed, $failed failed"
set f [open [file join $out bridge-result.txt] w]; puts $f "$passed $failed"; close $f
mol delete $m
file delete $pdb
quit
