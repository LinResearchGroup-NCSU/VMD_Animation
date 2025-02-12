source visualization.tcl
source la.tcl
source orient.tcl

##################################################################User-defined Variables##################################################
set start_frame 70 ;
set end_frame 3269
set key_frames {83 2957 3104 3268}
set frames_per_section 150
set default_speed 1.0
set key_frame_distance 20 ;#To adjust the animation speed
set rotation_picture 25; #Take 25 pictures while rotating
set rotation_speed 300
set pause_time 3500 ;#Pause time in milliseconds
set take_picture_interval 50

###############################################################End of User-defined Variables###############################################
# Initialize global variables
set current_frame start_frame

# Linear effect: Calculate the animation speed, currently under development
proc calculate_speed {current_frame key_frames} {
    global key_frame_distance
    set speed 1.0
    foreach key_frame $key_frames {
        set distance [expr $current_frame - $key_frame]
        if {$distance >= -($key_frame_distance) && $distance <= 0} {
            # Linear change between speeds 1 and 0.x before the key frame
            set speed [expr 1.0 - 0.01 * ($key_frame_distance + $distance)]
            break
        } elseif {$distance > 0 && $distance <= $key_frame_distance} {
            # Linear change between speeds 0.x and 1 after the key frame
            set speed [expr 1 - 0.01 * ($key_frame_distance - $distance)]
            break
        }
    }
    return $speed
}

# Stop animation at key frame or end frame
proc stop_animation {} {
    global start_frame end_frame key_frames current_frame pause_time rotation_picture rotation_speed
    set current_frame [set ::vmd_frame(0)] ;# Get the current frame

    # Calculate speed
    #set speed [calculate_speed $current_frame $key_frames]
    #animate speed $speed

    # Pause at the end frame
    if {$current_frame >= $end_frame} {
        animate pause
        puts "Animation paused at end frame $current_frame"

    #Pause at the key frames
    } else {
        set key_frame_index [lsearch $key_frames $current_frame]

        if {$key_frame_index != -1} {
            animate pause
            puts "Paused at key frame $current_frame"

            # Rotate the view
            set from_viewpoint $key_frame_index
            set to_viewpoint [expr {$key_frame_index + 1}]

            #Rotating
            #move_vp_render $from_viewpoint $to_viewpoint . $rotation_picture smooth
            move_vp $from_viewpoint $to_viewpoint $rotation_speed

            #Take picture during pause
            #take_picture_loop

            # Pause for n seconds and then continue 
            after $pause_time "animate forward; after 10 stop_animation"
        } else {
            
            # Continue in other frames and take pictures every n steps
            process_frames

            after 10 stop_animation; # Wait for 100ms and check the frame number
        }
    }
}

proc process_frames {} {
    global key_frames start_frame end_frame current_frame frames_per_section
    set last_frame $end_frame
    set num_keys [llength $key_frames]
    
    # Handle the first segment before the first key frame
    set first_key [lindex $key_frames 0]
    if {$current_frame < $first_key} {
        #increment $current_frame [expr ($first_key - 1)] 1  ; # set the increment value to 1
        increment_np $current_frame [expr ($first_key - 1)] 1
        animate forward
    }
    
    for {set i 0} {$i < $num_keys} {incr i} {
        set current_key [lindex $key_frames $i]
        set next_key [expr {$i < ($num_keys - 1) ? [lindex $key_frames [expr {$i + 1}]] : $last_frame}]
        
        set step_size [expr int(($next_key - $current_key) / ($frames_per_section - 1))]
        if {$step_size < 1} {
            set step_size 1
        }

        if {$current_frame == [expr {$current_key + 1}]} {
            #increment [expr {$current_key + 1}] [expr {$next_key - 1}] $step_size
            increment_np [expr {$current_key + 1}] [expr {$next_key - 1}] $step_size
            animate forward
        }
    }
}

# Take picture
proc take_picture_loop {} {
    global take_picture key_frames current_frame take_picture_interval
    
    set key_frame_i [lsearch $key_frames $current_frame]
    
    # If key_frame_i is -1, stop the loop
    if {$key_frame_i == -1} {
        return
    }

    # Otherwise, take a picture
    take_picture

    
    # Schedule the next frame capture
    after $take_picture_interval take_picture_loop
}

# Initialize the animation
proc init_animation {} {
    global start_frame default_speed

    # Get the number of representations for the top molecule
    set num_representations [molinfo top get numreps]

    # Smooth all representations for the top molecule
    for {set i 0} {$i < $num_representations} {incr i} {
        mol smoothrep top $i 5
    }

    retrieve_vp 0
    animate speed $default_speed
    animate style once

    animate goto $start_frame

    after 10 stop_animation

    
}

# Add a trace to check the frame number continuously and stop when reaching the end frame or key frames
trace variable ::vmd_frame(0) w stop_animation
