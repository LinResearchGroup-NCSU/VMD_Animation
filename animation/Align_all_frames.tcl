#Align all frames of a molecule to a reference region in VMD

#Command example:
#Step 1: source Align_all_frames.tcl
#Step 2: align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0
    #This command will align all frames of molecule 0 to the reference region defined by residues 331 to 508 and 863 to 1040 in frame 0.

proc align_all_frames {ref_sel molecule_ID ref_frame} {
    
    set ref [atomselect $molecule_ID $ref_sel frame $ref_frame]

    set num_frames [molinfo $molecule_ID get numframes]

    # Loop through all frames and align them to the reference region
    for {set i 0} {$i < $num_frames} {incr i} {
    
        set sel [atomselect $molecule_ID $ref_sel frame $i]
        
        set transformation_matrix [measure fit $sel $ref]
        
        set move_sel [atomselect $molecule_ID "all" frame $i]
        
        $move_sel move $transformation_matrix
        
        $sel delete
        $move_sel delete
    }

    $ref delete

    puts "All frames have been aligned to frame $ref_frame"
}
