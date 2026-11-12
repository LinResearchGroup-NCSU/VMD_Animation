# Start the local VMD-to-Unity bridge from VMD's Tk Console:
#   source start_vr.tcl
# From another directory, source /path/to/VMD_Animation/start_vr.tcl instead.
# Load your molecule and trajectory in VMD first. Unity may already be in Play mode;
# it connects automatically.
# On the top molecule, Lines representations are hidden and existing NewCartoon
# representations are shown by drawing method, regardless of their rep indices.
# Other representations keep their visibility; no representations are created.
# Running this script again reloads the bridge and disconnects existing clients;
# Unity reconnects by itself. Molecular data is left unchanged.
# The bridge listens on port 45454; to use another port, run
#   set env(VMD_VR_PORT) 45455
# before sourcing, and give Unity the same port (see VMDClient).

# The body runs in an anonymous procedure, so its variables stay local. Its one argument is
# the directory of this script, from which the bridge script is located.
apply {{script_directory} {
    if {[llength [info commands ::molinfo]] == 0} {
        error "Run start_vr.tcl inside VMD."
    }

    set bridge [file join $script_directory VMD_VR_Bridge vmd_vr_bridge.tcl]
    if {![file isfile $bridge]} {
        error "Bridge script not found: $bridge"
    }

    # Representation indices vary between molecules. Read each drawing method
    # instead of assuming rep 0 is Lines or rep 1 is NewCartoon.
    # This changes visibility only, preserving style parameters and molecular data.
    set molid [molinfo top]
    if {$molid >= 0} {
        for {set rep 0} {$rep < [molinfo $molid get numreps]} {incr rep} {
            set style [lindex [molinfo $molid get [list [list rep $rep]]] 0]
            switch -nocase -- [lindex $style 0] {
                Lines { mol showrep $molid $rep off }
                NewCartoon { mol showrep $molid $rep on }
            }
        }
        # Lines are still hidden when no NewCartoon exists; configure a supported
        # representation in VMD if no visible representation remains.
    }

    # Stop before sourcing: the bridge source initializes its socket variables.
    if {[llength [info commands ::vrbridge::stop]] != 0} {
        ::vrbridge::stop
    }
    uplevel #0 [list source $bridge]
    set port [::vrbridge::start]

    if {[molinfo top] < 0} {
        puts "Load a molecule and trajectory in VMD, then source this script again."
    }
    puts "Bridge ready on port $port. Unity's SampleScene connects in Play mode, now or within 2 s if already running."
}} [file dirname [file normalize [info script]]]
