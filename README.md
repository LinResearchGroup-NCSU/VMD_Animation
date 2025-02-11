# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!
## Start
Open VMD and start TkConsole
Ensure your coordinate file and trajectory file are in directory VMD_Animation, otherwise you need to specifiy their paths within render.tcl

`cd VMD_Animation`

## Render: 
Modify the render.tcl
### Step 1: Provide the path to the PDB and XTC files with frames to be imported in the script
    example:
        mol new mutl_open_CA.pdb
        mol addfile output_NVT_timecorrected.xtc first 12800 last 16069
### Step 2: Change the parameters in the script to customize the rendering
### Step 3: source file
        source render.tcl
