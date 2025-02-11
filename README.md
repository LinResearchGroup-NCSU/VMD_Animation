# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!
## Start
Open VMD and start TkConsole, and ensure your coordinate file and trajectory file are in directory VMD_Animation, otherwise you need to specifiy their paths within render.tcl

`cd VMD_Animation`

## Render: 
### Step 1: Provide the path to the PDB and XTC files with frames to be imported in the script
    example:
        mol new mutl_open_CA.pdb
        mol addfile output_NVT_timecorrected.xtc first 12800 last 16069
### Step 2: Change the parameters in the script to customize the rendering
### Step 3: Source file
        source render.tcl

## Align all frames to a reference region
### Step 1: Source file
        source Align_all_frames.tcl
### Step 2: Specify the region to be aligned, molecule ID and reference frame number
    example:(This command will align all frames of molecule 0 to the reference region defined by residues 331 to 508 and 863 to 1040 in frame 0.)
        align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0
    
