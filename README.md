# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!
## Start
Open VMD and start TkConsole, and ensure your coordinate file and trajectory file are in directory VMD_Animation, otherwise you need to specifiy their paths within render.tcl

        'cd VMD_Animation'

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
        align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0
Example: This command will align all frames of molecule 0 to the reference region defined by residues 331 to 508 and 863 to 1040 in frame 0.

## Select the best viewpoint for your key conformations
### Step 1: Source file
Take picture while animation is playing
        source with_capture.tcl
or
        source without_capture.tcl 
Play animation only
Skip step 2-6 if you already saved your viewpoints to a file, then
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
#### Parameter (Example)
Your animation will start at frame 70.
        set start_frame 70
Your animation will end at frame 3269.
        set end_frame 3269
Your key conformations are located at frame 83, 2957, 3104, 3268.
        set key_frames {83 2957 3104 3268} 
The number of frames to be captured between each key frames.
        set frames_per_section 150
To make the time intervel between each key frames the same, here we do a simple mathemathics to skip frames. 
![image](https://github.com/user-attachments/assets/304e6a7e-97fe-4403-80b9-43ffcacca55d)
Animation speed: You can set the speed between 0-1.
        set default_speed 1.0 
Take 25 pictures while rotating, available in with_capture.tcl
        set rotation_picture 25
Rotation speed, higher the value, lower the rotation speed, available in without_capture.tcl
        set rotation_speed 300 
Pause time for key conformations in milliseconds
        set pause_time 35000 
Here the author set the value of 35000 in with_capture.tcl, as the render method TachyonInternal takes certain time for rendering. If you are using a simpler render method like snapshot, you can set the value of 3500, just like what the author set in without_capture.tcl.
Time interval for capturing while the animation is pausing
        set take_picture_interval 50 #Values are under exploration
    






    
