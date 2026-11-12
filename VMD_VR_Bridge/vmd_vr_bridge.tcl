# VMD-Unity bridge: a line-based TCP server that runs inside VMD.
#
# A client (the Unity viewer, see Unity_VR_Client/Assets/Scripts/VMD/VMDClient.cs) connects to
# 127.0.0.1 on the same computer, sends one command per line and receives one reply line for each.
# The two processes share exported meshes through local files. The bridge also
# polls VMD every poll_ms milliseconds and sends a STATE line to every client when the top
# molecule, its frame, its frame count or its view matrices have changed.
#
# Loading: normally through start_vr.tcl in the repository root. By hand, from that directory:
#   source VMD_VR_Bridge/vmd_vr_bridge.tcl
#   ::vrbridge::start
# and ::vrbridge::stop to shut the bridge down. Sourcing this file resets the server and
# client variables without closing their sockets, so call ::vrbridge::stop before sourcing it
# again (start_vr.tcl does).
#
# This file implements the server, the STATE broadcast and the commands PING, HELLO, STATE,
# PLAY, PAUSE, TOGGLE, FRAME, STEP, HIGHLIGHT, SELECT, GET_COORDS and HELP. The files sourced
# at the end implement the others:
#   static_scene.tcl  GET_SCENE_STATE, EXPORT_SCENE, SET_REPRESENTATION, RELEASE_SCENE
#   trajectory.tcl    TRAJ_OPEN, TRAJ_READ, TRAJ_CHECK, TRAJ_SYNC, TRAJ_STYLE
#                     (it sources cartoon.tcl and quicksurf.tcl)
# README.md, section "VMD bridge", has the command table.
#
# Port: ::vrbridge::start <port> listens on the given port. Without a port it uses the
# VMD_VR_PORT environment variable, or 45454. In VMD: set env(VMD_VR_PORT) 45455 before
# sourcing start_vr.tcl. The Unity client must be given the same port.
#
# Protocol handshake: a client sends HELLO <version> after connecting.
# Increase protocol_version only for changes that break existing clients
# (command arguments, reply formats, export formats); VMDClient.ProtocolVersion must match.

namespace eval vrbridge {
    # Sent in the READY greeting and compared with the version in a client's HELLO.
    variable protocol_version 1
    # default_port applies when neither ::vrbridge::start nor VMD_VR_PORT names a port.
    # port is the port the server was last started on.
    variable default_port 45454
    variable port 45454
    # Listening socket, or "" while the bridge is stopped.
    variable server ""
    # Channels of the connected clients.
    variable clients {}
    # Last STATE line sent by poll; the next one is sent only when it differs.
    variable last_state ""
    # Polling interval in milliseconds, and the "after" ID of the next scheduled poll.
    variable poll_ms 100
    variable timer ""
    # Index of the representation added by HIGHLIGHT, or -1 when there is none.
    variable highlight_rep -1
    # Array that maps a selection name (upper case) to VMD selection text, for SELECT and
    # HIGHLIGHT. ::vrbridge::define_selection adds entries.
    variable selections

    # These labels map to MutLalpha domains from the existing VMD project.
    # N_DOMAIN, C_DOMAIN and LINKER use hard-coded resid ranges for this dataset;
    # they do not automatically identify domains in other proteins or numbering schemes.
    # resid is the residue identifier, not the atom index. These expressions have no
    # chain/segname filter, so repeated residue IDs across chains can select extra atoms.
    # Before using HIGHLIGHT with another structure, verify its domain boundaries,
    # residue numbering and chain/segment IDs, then adapt this mapping as needed.
    # Unadapted ranges may select incorrect atoms or nothing; ALL clears the highlight.
    array set selections {
        N_DOMAIN {resid 1 to 315 or resid 533 to 848}
        C_DOMAIN {resid 331 to 508 or resid 863 to 1040}
        LINKER {resid 316 to 330 or resid 509 to 532 or resid 849 to 862 or resid 1041 to 1064}
        ALL {all}
    }
}

# Port used when start is called without one: VMD_VR_PORT if set, otherwise 45454.
# Raises an error when VMD_VR_PORT is not an integer between 1 and 65535.
proc ::vrbridge::configured_port {} {
    variable default_port
    if {[info exists ::env(VMD_VR_PORT)] && [string trim $::env(VMD_VR_PORT)] ne ""} {
        return [::vrbridge::integer_in_range [string trim $::env(VMD_VR_PORT)] 1 65535 VMD_VR_PORT]
    }
    return $default_port
}

# Start the server on 127.0.0.1 and start polling VMD's state.
# requested_port: port to listen on; when empty, configured_port chooses it.
# Returns the port. If the bridge is already running, nothing changes and its current port is
# returned. Called by start_vr.tcl, or by hand from the Tk Console.
proc ::vrbridge::start {{requested_port ""}} {
    variable server
    variable port

    if {$server ne ""} {
        puts "VR bridge is already listening on localhost:$port"
        return $port
    }

    if {$requested_port eq ""} {
        set requested_port [::vrbridge::configured_port]
    }
    set port [::vrbridge::integer_in_range $requested_port 1 65535 port]
    set server [socket -server ::vrbridge::accept -myaddr 127.0.0.1 $port]
    after 0 ::vrbridge::poll
    puts "VMD-VR bridge listening on localhost:$port"
    return $port
}

# Stop the bridge: close every client connection (dropping that client's trajectory snapshot
# and export lease), close the listening socket and cancel the poll timer.
# Called by start_vr.tcl before it sources this file again, or by hand.
proc ::vrbridge::stop {} {
    variable server
    variable clients
    variable timer

    foreach channel $clients {
        ::vrbridge::trajectory_disconnect $channel
        ::vrbridge::scene_disconnect $channel
        catch {close $channel}
    }
    set clients {}
    if {$server ne ""} {
        catch {close $server}
        set server ""
    }
    if {$timer ne ""} {
        after cancel $timer
        set timer ""
    }
    puts "VMD-VR bridge stopped"
}

# Server-socket callback for a new connection.
# Registers the channel (non-blocking, line-buffered, UTF-8, LF line endings) and its
# readable handler, then sends the greeting "READY VMD_VR_BRIDGE <protocol_version>" followed
# by the current STATE line, or by an ERROR line when no molecule is loaded.
proc ::vrbridge::accept {channel address remote_port} {
    variable clients
    variable protocol_version

    # The server is explicitly bound to loopback. Keep this check as defense
    # in depth if it is ever changed to listen on another interface.
    if {$address ne "127.0.0.1" && $address ne "::1"} {
        close $channel
        return
    }

    fconfigure $channel -blocking 0 -buffering line -translation lf -encoding utf-8
    lappend clients $channel
    fileevent $channel readable [list ::vrbridge::receive $channel]
    ::vrbridge::send $channel "READY VMD_VR_BRIDGE $protocol_version"
    ::vrbridge::send_state $channel
    puts "VR client connected from $address:$remote_port"
}

# Forget one client: drop its trajectory snapshot and export lease, remove it from the client
# list and close its channel. Called by receive at end of file and by send when a write fails.
proc ::vrbridge::disconnect {channel} {
    variable clients
    ::vrbridge::trajectory_disconnect $channel
    ::vrbridge::scene_disconnect $channel
    set index [lsearch -exact $clients $channel]
    if {$index >= 0} {
        set clients [lreplace $clients $index $index]
    }
    catch {close $channel}
}

# Write one line to a client. A failed write disconnects that client.
proc ::vrbridge::send {channel message} {
    if {[catch {
        puts $channel $message
        flush $channel
    }]} {
        ::vrbridge::disconnect $channel
    }
}

# Readable-event callback for a client channel: pass every complete line to handle_command
# and send back each non-empty reply. At end of file the client is disconnected.
proc ::vrbridge::receive {channel} {
    if {[eof $channel]} {
        ::vrbridge::disconnect $channel
        return
    }

    while {[gets $channel line] >= 0} {
        set response [::vrbridge::handle_command [string trim $line] $channel]
        if {$response ne ""} {
            ::vrbridge::send $channel $response
        }
    }
}

# Send one line to every connected client. poll uses it for STATE updates.
proc ::vrbridge::broadcast {message} {
    variable clients
    foreach channel $clients {
        ::vrbridge::send $channel $message
    }
}

# Return the ID of VMD's top molecule, or raise an error when no molecule is loaded.
proc ::vrbridge::top_molecule {} {
    set molid [molinfo top]
    if {$molid < 0} {
        error "No molecule is loaded in VMD"
    }
    return $molid
}

# Build the STATE line for the top molecule:
#   STATE <molid> <frame> <frameCount> <rotate> <center> <scale> <global>
# The last four are VMD's 4x4 view matrices. Raises an error when no molecule is loaded.
# Used for the STATE command, the connection greeting and the poll broadcast.
proc ::vrbridge::state_message {} {
    set molid [::vrbridge::top_molecule]
    set frame [molinfo $molid get frame]
    set frame_count [molinfo $molid get numframes]
    set rotation [lindex [molinfo $molid get rotate_matrix] 0]
    set center [lindex [molinfo $molid get center_matrix] 0]
    set scale [lindex [molinfo $molid get scale_matrix] 0]
    set global [lindex [molinfo $molid get global_matrix] 0]

    # The list command keeps each 4x4 matrix together as one braced element. Unity's
    # VMDClient reads only the frame and the frame count from this line.
    return [list STATE $molid $frame $frame_count $rotation $center $scale $global]
}

# Send the current STATE line to one client, or "ERROR <message>" when it cannot be built
# (no molecule loaded). Called by accept.
proc ::vrbridge::send_state {channel} {
    if {[catch {set message [::vrbridge::state_message]} error]} {
        ::vrbridge::send $channel [list ERROR $error]
        return
    }
    ::vrbridge::send $channel $message
}

# Timer callback: broadcast the STATE line when it differs from the last one sent, then
# schedule the next poll after poll_ms milliseconds. Nothing is sent while no molecule is
# loaded. start schedules the first poll and stop cancels the timer.
proc ::vrbridge::poll {} {
    variable poll_ms
    variable timer
    variable last_state

    if {![catch {set state [::vrbridge::state_message]}] && $state ne $last_state} {
        set last_state $state
        ::vrbridge::broadcast $state
    }
    set timer [after $poll_ms ::vrbridge::poll]
}

# Return value when it is an integer between minimum and maximum (inclusive); otherwise raise
# an error that starts with name, the caller's word for the argument.
proc ::vrbridge::integer_in_range {value minimum maximum name} {
    if {![string is integer -strict $value] || $value < $minimum || $value > $maximum} {
        error "$name must be an integer between $minimum and $maximum"
    }
    return $value
}

# HIGHLIGHT <label>: mark a named selection in VMD itself by adding a VDW representation
# (ColorID 0, Opaque) to the top molecule. The representation added by the previous call is
# deleted first, so at most one exists; the label ALL only deletes it.
# label: a key of the selections array, in any letter case; unknown names raise an error.
# Unity does not send this command: it uses SELECT and draws the highlight itself.
proc ::vrbridge::highlight {label} {
    variable selections
    variable highlight_rep

    set molid [::vrbridge::top_molecule]
    set label [string toupper $label]
    if {![info exists selections($label)]} {
        error "Unknown selection '$label'"
    }

    if {$highlight_rep >= 0} {
        catch {mol delrep $highlight_rep $molid}
        set highlight_rep -1
    }

    if {$label ne "ALL"} {
        mol representation VDW 1.2 12.0
        mol color ColorID 0
        mol selection $selections($label)
        mol material Opaque
        mol addrep $molid
        set highlight_rep [expr {[molinfo $molid get numreps] - 1}]
    }
    display update
}

# Add or replace a named selection for SELECT and HIGHLIGHT, e.g. for another protein's domains:
#   ::vrbridge::define_selection MY_DOMAIN {chain A and resid 10 to 120}
# Names are case-insensitive letters, digits and underscores; ALL is reserved.
# Definitions survive reloading the bridge, unless they redefine a built-in name.
# Returns the name in upper case. Meant to be called from VMD's Tk Console.
proc ::vrbridge::define_selection {name selection} {
    variable selections
    set name [string toupper $name]
    if {![regexp {^[A-Z0-9_]{1,64}$} $name]} {
        error "Selection names use letters, digits and underscores"
    }
    if {$name eq "ALL"} {
        error "ALL is reserved"
    }
    if {[string trim $selection] eq ""} {
        error "Empty selection"
    }
    set selections($name) $selection
    return $name
}

# SELECT <requestId> <name | VMD selection text>
# Resolve a selection on the top molecule to atom indices without changing anything in VMD;
# the client draws the highlight itself. Text that is not a defined name is used as a VMD
# atom selection and may contain spaces. Replies use their own SELJSON envelope, so clients
# can tell them apart from trajectory replies; errors for a valid request ID are SELJSON too.
# words: the words after SELECT; the first is the request ID. An invalid request ID raises an
# error, which handle_command returns as a plain ERROR line.
# The reply's JSON object holds requestId and either "error" or: ok, molecule, atomCount
# (atoms in the whole molecule, not in the selection), label (the matched selection name,
# empty for selection text) and indices.
proc ::vrbridge::select_command {words} {
    variable selections
    set id [lindex $words 0]
    ::vrbridge::scene_request_id $id
    if {[catch {
        set text [string trim [join [lrange $words 1 end] " "]]
        if {$text eq ""} { error "Usage: SELECT requestId <name|selection>" }
        set label [string toupper $text]
        if {[info exists selections($label)]} {
            set text $selections($label)
        } else {
            set label ""
        }
        set molid [::vrbridge::top_molecule]
        set sel [atomselect $molid $text]
        set code [catch {$sel get index} indices options]
        $sel delete
        if {$code} { return -options $options $indices }
        set data [format {"ok":true,"molecule":%d,"atomCount":%d,"label":%s,"indices":[%s]} \
            $molid [molinfo $molid get numatoms] [::vrbridge::scene_json_string $label] [join $indices ,]]
    } message]} {
        set data "\"error\":[::vrbridge::scene_json_string $message]"
    }
    return "SELJSON [format {{"requestId":%s,%s}} [::vrbridge::scene_json_string $id] $data]"
}

# Run one command line and return its reply line ("" for an empty line).
# line: the trimmed command line; the command name is case-insensitive.
# channel: the client's channel, or "" when called from the Tk Console or a test. The
# trajectory and scene commands use it to tell clients apart (snapshots and export leases).
# An error raised by a command is returned as "ERROR <message>". SELECT, the scene commands
# and the TRAJ_ commands report most failures in their own SELJSON, SCENEERROR and TRAJJSON
# replies instead. PLAY, PAUSE and TOGGLE drive VMD's own animation.
# Called by receive for every line a client sends.
proc ::vrbridge::handle_command {line {channel ""}} {
    if {$line eq ""} {
        return ""
    }

    set words [split $line]
    set command [string toupper [lindex $words 0]]
    set args [lrange $words 1 end]

    if {[catch {
        switch -- $command {
            TRAJ_OPEN - TRAJ_READ - TRAJ_CHECK - TRAJ_SYNC - TRAJ_STYLE {
                set result [::vrbridge::trajectory_command $command $args $channel]
            }
            PING { set result "PONG" }
            HELLO {
                # Other commands do not require HELLO; the client stops on a mismatch.
                variable protocol_version
                if {[llength $args] != 1} { error "Usage: HELLO <protocolVersion>" }
                if {[lindex $args 0] eq $protocol_version} {
                    set result [list HELLO OK $protocol_version]
                } else {
                    set result [list HELLO MISMATCH $protocol_version]
                }
            }
            STATE {
                set result [::vrbridge::state_message]
            }
            PLAY {
                animate forward
                set result "OK PLAY"
            }
            PAUSE {
                animate pause
                set result "OK PAUSE"
            }
            TOGGLE {
                if {[animate info] eq "paused"} {
                    animate forward
                } else {
                    animate pause
                }
                set result "OK TOGGLE"
            }
            FRAME {
                if {[llength $args] != 1} { error "Usage: FRAME <index>" }
                set molid [::vrbridge::top_molecule]
                set maximum [expr {[molinfo $molid get numframes] - 1}]
                set frame [::vrbridge::integer_in_range [lindex $args 0] 0 $maximum frame]
                animate goto $frame
                display update
                set result [list OK FRAME $frame]
            }
            STEP {
                # Unlike FRAME, a target outside the trajectory is clamped, not rejected.
                if {[llength $args] != 1} { error "Usage: STEP <delta>" }
                if {![string is integer -strict [lindex $args 0]]} {
                    error "delta must be an integer"
                }
                set molid [::vrbridge::top_molecule]
                set current [molinfo $molid get frame]
                set maximum [expr {[molinfo $molid get numframes] - 1}]
                set target [expr {$current + [lindex $args 0]}]
                if {$target < 0} { set target 0 }
                if {$target > $maximum} { set target $maximum }
                animate goto $target
                display update
                set result [list OK FRAME $target]
            }
            HIGHLIGHT {
                if {[llength $args] != 1} {
                    error "Usage: HIGHLIGHT <N_DOMAIN|C_DOMAIN|LINKER|ALL>"
                }
                ::vrbridge::highlight [lindex $args 0]
                set result [list OK HIGHLIGHT [string toupper [lindex $args 0]]]
            }
            SELECT {
                set result [::vrbridge::select_command $args]
            }
            GET_COORDS {
                # Reply: COORDS and one list of x y z triples, for every atom of the top
                # molecule at its current frame.
                set molid [::vrbridge::top_molecule]
                set frame [molinfo $molid get frame]
                set coords [atomselect $molid "all" frame $frame]
                set coord_list [$coords get {x y z}]
                $coords delete
                set result [list COORDS $coord_list]
            }
            HELP {
                set result [list COMMANDS PING HELLO STATE PLAY PAUSE TOGGLE FRAME STEP HIGHLIGHT SELECT GET_COORDS GET_SCENE_STATE EXPORT_SCENE SET_REPRESENTATION RELEASE_SCENE TRAJ_OPEN TRAJ_READ TRAJ_CHECK TRAJ_SYNC TRAJ_STYLE HELP]
            }
            GET_SCENE_STATE {
                set result [::vrbridge::scene_state]
            }
            RELEASE_SCENE {
                if {[llength $args] != 1} { error "Usage: RELEASE_SCENE <requestId>" }
                set result [::vrbridge::scene_release [lindex $args 0] $channel]
            }
            EXPORT_SCENE -
            SET_REPRESENTATION {
                set result [::vrbridge::scene_command $command $args $channel]
            }
            default {
                error "Unknown command '$command'"
            }
        }
    } error]} {
        set result [list ERROR $error]
    }
    return $result
}

# The remaining commands live in separate files: scene export in static_scene.tcl and
# coordinate playback in trajectory.tcl. static_scene.tcl also defines scene_request_id and
# scene_json_string, which select_command above uses.
source [file join [file dirname [info script]] static_scene.tcl]
source [file join [file dirname [info script]] trajectory.tcl]
