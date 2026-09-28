# Self-contained tcltest regression tests for vmd_vr_bridge.tcl.
#
# Run from PowerShell (VMD 1.9.4 for Windows):
#   & 'C:\Program Files\VMD\vmd.exe' -dispdev text -e .\_test_bridge.tcl -eofexit
#
# Each test creates and deletes a tiny temporary PDB. It never loads, edits,
# or deletes the MutLalpha files used by the research workflow.

# Tests currently cover:
# - Command handling: PING, STATE, PLAY, PAUSE, FRAME, STEP, HIGHLIGHT, HELP, and unknown commands
# - Frame control: valid frame seeking, stepping, boundary clamping, and invalid arguments
# - Highlighting: adding/clearing domain representations and rejecting invalid selections
# - TCP connection: client connection, READY/STATE handshake, command responses, and client disconnection
# - State synchronization: polling broadcasts a STATE update when the VMD frame changes
# - All current regression tests pass

package require tcltest 2.2

namespace eval ::vrbridge_test {
    variable fixture ""
    variable molid -1
    variable client ""
    variable port 45455
    variable awaited_line ""
}

proc ::vrbridge_test::make_fixture {} {
    variable fixture
    variable molid

    set fixture [file normalize [file join [pwd] ".vrbridge_test_[pid].pdb"]]
    set handle [open $fixture w]
    puts $handle "ATOM      1  CA  ALA A   1       0.000   0.000   0.000  1.00  0.00           C"
    puts $handle "ATOM      2  CA  GLY A 331       4.000   0.000   0.000  1.00  0.00           C"
    puts $handle "TER"
    puts $handle "END"
    close $handle

    set molid [mol new $fixture type pdb waitfor all]
    # Add two frames to test normal and boundary behaviour.
    animate dup $molid
    animate dup $molid
    animate goto 0
}

proc ::vrbridge_test::cleanup {} {
    variable fixture
    variable molid
    variable client

    catch {animate pause}
    if {$client ne ""} {
        catch {close $client}
        set client ""
    }
    catch {::vrbridge::stop}
    if {$molid >= 0} {
        catch {mol delete $molid}
        set molid -1
    }
    if {$fixture ne ""} {
        catch {file delete -force $fixture}
        set fixture ""
    }
}

proc ::vrbridge_test::socket_readable {channel} {
    variable awaited_line
    if {[gets $channel line] >= 0} {
        set awaited_line $line
    } elseif {[eof $channel]} {
        set awaited_line "__EOF__"
    }
}

proc ::vrbridge_test::await_line {channel {timeout_ms 1500}} {
    variable awaited_line
    set awaited_line ""
    fileevent $channel readable [list ::vrbridge_test::socket_readable $channel]
    set timer [after $timeout_ms [list set ::vrbridge_test::awaited_line "__TIMEOUT__"]]
    vwait ::vrbridge_test::awaited_line
    after cancel $timer
    fileevent $channel readable {}
    return $awaited_line
}

proc ::vrbridge_test::read_until {channel expected_first_word} {
    for {set attempt 0} {$attempt < 6} {incr attempt} {
        set line [::vrbridge_test::await_line $channel]
        if {$line eq "__TIMEOUT__" || $line eq "__EOF__"} {
            error "Timed out waiting for $expected_first_word (got $line)"
        }
        if {[lindex $line 0] eq $expected_first_word} {
            return $line
        }
    }
    error "Did not receive $expected_first_word within 6 bridge messages"
}

proc ::vrbridge_test::connect_client {} {
    variable client
    variable port
    ::vrbridge_test::make_fixture
    ::vrbridge::start $port
    set client [socket 127.0.0.1 $port]
    fconfigure $client -blocking 0 -buffering line -translation lf -encoding utf-8
}

proc ::vrbridge_test::send_command {command expected_first_word} {
    variable client
    puts $client $command
    flush $client
    return [::vrbridge_test::read_until $client $expected_first_word]
}

proc ::vrbridge_test::await_client_count {expected {timeout_ms 1500}} {
    set deadline [expr {[clock milliseconds] + $timeout_ms}]
    while {[llength $::vrbridge::clients] != $expected} {
        if {[clock milliseconds] >= $deadline} {
            return 0
        }
        set ::vrbridge_test::wait_tick 0
        after 10 [list set ::vrbridge_test::wait_tick 1]
        vwait ::vrbridge_test::wait_tick
    }
    return 1
}

proc ::vrbridge_test::read_state_frame {channel expected_frame} {
    for {set attempt 0} {$attempt < 10} {incr attempt} {
        set line [::vrbridge_test::await_line $channel]
        if {$line eq "__TIMEOUT__" || $line eq "__EOF__"} {
            error "Timed out waiting for STATE frame $expected_frame (got $line)"
        }
        if {[lindex $line 0] eq "STATE" && [lindex $line 2] == $expected_frame} {
            return $line
        }
    }
    error "Did not receive STATE frame $expected_frame within 10 bridge messages"
}

set ::vrbridge_test::bridge_path [file normalize \
    [file join [file dirname [info script]] vmd_vr_bridge.tcl]]
set ::vrbridge_test::report_path [file normalize \
    [file join [file dirname [info script]] [format "_test_bridge_result_%d.txt" [pid]]]]

::tcltest::configure -verbose {pass error} -outfile $::vrbridge_test::report_path

if {![file exists $::vrbridge_test::bridge_path]} {
    error "Bridge script was not found next to this test: $::vrbridge_test::bridge_path"
}
source $::vrbridge_test::bridge_path

::tcltest::test command-1.1 {PING, empty input, HELP and unknown command} \
    -setup {::vrbridge_test::make_fixture} \
    -body {
        list \
            [::vrbridge::handle_command PING] \
            [::vrbridge::handle_command ""] \
            [expr {[lsearch -exact [::vrbridge::handle_command HELP] FRAME] >= 0}] \
            [lindex [::vrbridge::handle_command UNKNOWN] 0]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {PONG {} 1 ERROR}

::tcltest::test command-1.2 {STATE reports the test molecule and three frames} \
    -setup {::vrbridge_test::make_fixture} \
    -body {
        set state [::vrbridge::handle_command STATE]
        list [lindex $state 0] \
            [expr {[lindex $state 1] == $::vrbridge_test::molid}] \
            [lindex $state 3]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {STATE 1 3}

::tcltest::test command-1.3 {FRAME and STEP seek and clamp to valid frames} \
    -setup {::vrbridge_test::make_fixture} \
    -body {
        list \
            [::vrbridge::handle_command "FRAME 1"] \
            [molinfo $::vrbridge_test::molid get frame] \
            [::vrbridge::handle_command "STEP 1"] \
            [::vrbridge::handle_command "STEP 99"] \
            [::vrbridge::handle_command "STEP -99"]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {{OK FRAME 1} 1 {OK FRAME 2} {OK FRAME 2} {OK FRAME 0}}

::tcltest::test command-1.4 {FRAME and STEP reject invalid arguments} \
    -setup {::vrbridge_test::make_fixture} \
    -body {
        list \
            [lindex [::vrbridge::handle_command "FRAME 3"] 0] \
            [lindex [::vrbridge::handle_command "STEP one"] 0]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {ERROR ERROR}

::tcltest::test command-1.5 {highlight adds and clears one representation} \
    -setup {::vrbridge_test::make_fixture} \
    -body {
        set initial [molinfo $::vrbridge_test::molid get numreps]
        set added [::vrbridge::handle_command "HIGHLIGHT C_DOMAIN"]
        set after_add [molinfo $::vrbridge_test::molid get numreps]
        set cleared [::vrbridge::handle_command "HIGHLIGHT ALL"]
        set after_clear [molinfo $::vrbridge_test::molid get numreps]
        list $added [expr {$after_add == $initial + 1}] \
            $cleared [expr {$after_clear == $initial}]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {{OK HIGHLIGHT C_DOMAIN} 1 {OK HIGHLIGHT ALL} 1}

::tcltest::test command-1.6 {unknown highlight is rejected} \
    -setup {::vrbridge_test::make_fixture} \
    -body {lindex [::vrbridge::handle_command "HIGHLIGHT UNKNOWN"] 0} \
    -cleanup {::vrbridge_test::cleanup} \
    -result ERROR

::tcltest::test command-1.7 {PLAY starts forward animation} \
    -setup {::vrbridge_test::make_fixture} \
    -body {::vrbridge::handle_command PLAY} \
    -cleanup {::vrbridge_test::cleanup} \
    -result {OK PLAY}

::tcltest::test command-1.8 {PAUSE stops animation} \
    -setup {
        ::vrbridge_test::make_fixture
        animate forward
    } \
    -body {::vrbridge::handle_command PAUSE} \
    -cleanup {::vrbridge_test::cleanup} \
    -result {OK PAUSE}

::tcltest::test tcp-1.1 {client receives greeting and initial state} \
    -setup {::vrbridge_test::connect_client} \
    -body {
        set ready [::vrbridge_test::read_until $::vrbridge_test::client READY]
        set state [::vrbridge_test::read_until $::vrbridge_test::client STATE]
        list $ready [lindex $state 0]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {{READY VMD_VR_BRIDGE 1} STATE}

::tcltest::test tcp-1.2 {client can send PING and FRAME} \
    -setup {::vrbridge_test::connect_client} \
    -body {
        list \
            [::vrbridge_test::send_command PING PONG] \
            [::vrbridge_test::send_command "FRAME 1" OK]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {PONG {OK FRAME 1}}

::tcltest::test tcp-1.3 {closing a client disconnects it from the bridge} \
    -setup {::vrbridge_test::connect_client} \
    -body {
        ::vrbridge_test::read_until $::vrbridge_test::client READY
        close $::vrbridge_test::client
        set ::vrbridge_test::client ""
        ::vrbridge_test::await_client_count 0
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result 1

::tcltest::test tcp-1.4 {poll broadcasts STATE after the frame changes} \
    -setup {::vrbridge_test::connect_client} \
    -body {
        ::vrbridge_test::read_until $::vrbridge_test::client READY
        ::vrbridge_test::read_state_frame $::vrbridge_test::client 0
        animate goto 1
        display update
        set state [::vrbridge_test::read_state_frame $::vrbridge_test::client 1]
        list [lindex $state 0] [lindex $state 2] \
            [expr {$state eq $::vrbridge::last_state}]
    } \
    -cleanup {::vrbridge_test::cleanup} \
    -result {STATE 1 1}

set ::vrbridge_test::passed $::tcltest::numTests(Passed)
set ::vrbridge_test::failed $::tcltest::numTests(Failed)
::tcltest::cleanupTests
puts "VMD-VR bridge test summary: $::vrbridge_test::passed passed, $::vrbridge_test::failed failed"
puts "Test report: $::vrbridge_test::report_path"
quit
