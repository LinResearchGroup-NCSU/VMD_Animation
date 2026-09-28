# # VMD-VR Bridge Script for VMD 1.9.4 Windows 64-bit

# This script provides a TCP bridge between VMD and a VR application.
# It allows the VR application to send commands to VMD and receive
# VMD state updates for synchronization.

# Basic Usage:
# 1. Start or stop TCP server for communication with VR applications.
# 2. Receive commands from Unity to control VMD, such as changing the
#    trajectory frame, playing/pausing the trajectory, and highlighting regions.
# 3. Handle VMD control commands sent from Unity, enabling real-time interaction with
#    molecular structures in VR.
# 4. Read current status of VMD and send it back to Unity for synchronization.

# start / stop / accept / disconnect / send / receive / handle_command / send_state / poll

# ::vrbridge::start port in VMD console to start the bridge server on port.

# TODO: Currently limited to localhost (127.0.0.1) for local development.
# For future remote VR clients, allow network connections and update
# the client address and connection/security settings.

# Namespace for VR Bridge
namespace eval vrbridge {
    variable port 45454
    variable server ""
    variable clients {}
    variable last_state ""
    variable poll_ms 100
    variable timer ""
    variable highlight_rep -1
    variable selections

    # These labels map to MutLalpha domains from the existing VMD project.
    array set selections {
        N_DOMAIN {resid 1 to 315 or resid 533 to 848}
        C_DOMAIN {resid 331 to 508 or resid 863 to 1040}
        LINKER {resid 316 to 330 or resid 509 to 532 or resid 849 to 862 or resid 1041 to 1064}
        ALL {all}
    }
}

# Start the VR bridge server
# If the server is already running, it will return the current port. 
# Otherwise, it will start a new server on the specified port.
proc ::vrbridge::start {{requested_port 45454}} {
    variable server
    variable port

    if {$server ne ""} {
        puts "VR bridge is already listening on localhost:$port"
        return $port
    }

    set port $requested_port
    set server [socket -server ::vrbridge::accept -myaddr 127.0.0.1 $port]
    after 0 ::vrbridge::poll
    puts "VMD-VR bridge listening on localhost:$port"
    return $port
}

# Stop the VR bridge server and close all client connections
# This function will close the server socket and all connected client sockets,
# effectively stopping the VR bridge.
proc ::vrbridge::stop {} {
    variable server
    variable clients
    variable timer

    foreach channel $clients {
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

# Accept a new client connection
# This function is called when a new client connects to the VR bridge server.
# It checks the client's address to ensure it is from localhost,
# configures the channel for non-blocking I/O,
# and sets up a file event to handle incoming messages from the client.
proc ::vrbridge::accept {channel address remote_port} {
    variable clients

    # The server is explicitly bound to loopback. Keep this check as defense
    # in depth if it is ever changed to listen on another interface.
    if {$address ne "127.0.0.1" && $address ne "::1"} {
        close $channel
        return
    }

    fconfigure $channel -blocking 0 -buffering line -translation lf -encoding utf-8
    lappend clients $channel
    fileevent $channel readable [list ::vrbridge::receive $channel]
    ::vrbridge::send $channel "READY VMD_VR_BRIDGE 1"
    ::vrbridge::send_state $channel
    puts "VR client connected from $address:$remote_port"
}

# Disconnect a client connection
# This function is called when a client disconnects from the VR bridge server.
# It removes the client from the list of connected clients and closes the channel.
proc ::vrbridge::disconnect {channel} {
    variable clients
    set index [lsearch -exact $clients $channel]
    if {$index >= 0} {
        set clients [lreplace $clients $index $index]
    }
    catch {close $channel}
}

# Send a message to a client
# This function sends a message to the specified client channel.
# If an error occurs while sending the message, it will disconnect the client.
proc ::vrbridge::send {channel message} {
    if {[catch {
        puts $channel $message
        flush $channel
    }]} {
        ::vrbridge::disconnect $channel
    }
}

# Receive and handle commands from a client
# This function reads incoming messages from the client channel,
# processes each command, and sends back a response if necessary.
proc ::vrbridge::receive {channel} {
    if {[eof $channel]} {
        ::vrbridge::disconnect $channel
        return
    }

    while {[gets $channel line] >= 0} {
        set response [::vrbridge::handle_command [string trim $line]]
        if {$response ne ""} {
            ::vrbridge::send $channel $response
        }
    }
}

# Send a message to all connected clients
# This function iterates through all connected client channels and sends the specified
proc ::vrbridge::broadcast {message} {
    variable clients
    foreach channel $clients {
        ::vrbridge::send $channel $message
    }
}

# This function returns the ID of the top molecule in VMD.
# If no molecule is loaded, it raises an error.
proc ::vrbridge::top_molecule {} {
    set molid [molinfo top]
    if {$molid < 0} {
        error "No molecule is loaded in VMD"
    }
    return $molid
}

# Read the current state of VMD and return it as a formatted message.
# This function retrieves the current frame, number of frames, and transformation matrices
proc ::vrbridge::state_message {} {
    set molid [::vrbridge::top_molecule]
    set frame [molinfo $molid get frame]
    set frame_count [molinfo $molid get numframes]
    set rotation [lindex [molinfo $molid get rotate_matrix] 0]
    set center [lindex [molinfo $molid get center_matrix] 0]
    set scale [lindex [molinfo $molid get scale_matrix] 0]
    set global [lindex [molinfo $molid get global_matrix] 0]

    # Braces preserve each VMD 4x4 matrix as one Tcl list.  The PCVR client
    # converts these row-major matrices to its own coordinate convention.
    return [list STATE $molid $frame $frame_count $rotation $center $scale $global]
}

# Send the current state of VMD to a client channel.
# This function retrieves the current state of VMD and sends it to the specified client channel.
proc ::vrbridge::send_state {channel} {
    if {[catch {set message [::vrbridge::state_message]} error]} {
        ::vrbridge::send $channel [list ERROR $error]
        return
    }
    ::vrbridge::send $channel $message
}

# Polling function to check for state changes in VMD
# This function periodically checks the current state of VMD and broadcasts it to all
# connected clients if there are any changes. It uses a timer to schedule the next poll.
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

# Validate that a value is an integer within a specified range.
proc ::vrbridge::integer_in_range {value minimum maximum name} {
    if {![string is integer -strict $value] || $value < $minimum || $value > $maximum} {
        error "$name must be an integer between $minimum and $maximum"
    }
    return $value
}

# Highlight a specific selection in VMD based on the provided label.
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

# Handle a command received from a client.
# This function processes a command string, executes the corresponding action in VMD,
# and returns a response message. It supports commands like PING, STATE, PLAY, PAUSE,
# TOGGLE, FRAME, STEP, HIGHLIGHT, and HELP.
proc ::vrbridge::handle_command {line} {
    if {$line eq ""} {
        return ""
    }

    set words [split $line]
    set command [string toupper [lindex $words 0]]
    set args [lrange $words 1 end]

    if {[catch {
        switch -- $command {
            PING { set result "PONG" }
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
            # Not implemented yet:TODO
            TOGGLE {
                if ([animate info] eq "paused") {
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
            HELP {
                set result [list COMMANDS PING STATE PLAY PAUSE TOGGLE FRAME STEP HIGHLIGHT HELP]
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