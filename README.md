# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!
## Start
Open VMD and start TkConsole, and ensure your coordinate file and trajectory file are in directory VMD_Animation, otherwise you need to specifiy their paths within render.tcl

`cd VMD_Animation`

## Render 
### Step 1: Provide the path to the PDB and XTC files with frames to be imported in the script
    #example:
        mol new mutl_open_CA.pdb
        mol addfile output_NVT_timecorrected.xtc first 12800 last 16069
### Step 2: Change the parameters in the script to customize the rendering
### Step 3: Source file
        source render.tcl

## Align all frames to a reference region
### Step 1: Source file
        source Align_all_frames.tcl
### Step 2: Specify the region to be aligned, molecule ID and reference frame number
    #example:(This command will align all frames of molecule 0 to the reference region defined by residues 331 to 508 and 863 to 1040 in frame 0.)
        align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0

## Select the best viewpoint for your key conformations
### Step 1: Source file
        source with_capture.tcl #take picture while animation is playing
        #or
        source without_capture.tcl #Play animation only
        #Skip step 2-6 if you already saved your viewpoints to a file
        source your_viewpoints_file.tcl 
### Step 2: Identify the key frames which contain your key conformations
### Step 3: Swith to key frame 1, save current viewpoint to 0
        save_vp 0
### Step 4: Rotate the molecule to satified viewpoint, save the viewpoint to 1
        save_vp 1
### Step 5: Swith to key frame 2, rotate your molecule, save the virepoint to 2, continue to do this for the rest of key frames
        save_vp 2
### Step 6: Write all the viewpoints to a file 
        write_vps your_viewpoint_file.tcl

## Animation
### Step 1: Set user-defined parameter, please modified
#### Parameter 1(Example)
    set start_frame 70 #Your animation will start at frame 70
    set end_frame 3269 #Your animation will end at frame 3269
    set key_frames {83 2957 3104 3268} #Your key conformations located at frame 83, 2957, 3104, 3268
#### Parameter 2(Example)
    set frames_per_section 150 #The number of frames to be captured between each key frames
To make the time intervel between each key frames the same, here we do a simple mathemathics to skip frames. 

$$
\frac{\text{key\_frame}_{i+1} - \text{key\_frame}_i}{\text{frames\_per\_section} - 1}
$$




    
