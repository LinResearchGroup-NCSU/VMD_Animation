#This is a rendering script
#usage: 
#Step 1: Provide the path to the PDB and XTC files with frames to be imported in the script
    #example:
        #mol new mutl_open_CA.pdb
        #mol addfile output_NVT_timecorrected.xtc first 12800 last 16069
#Step 2: Change the parameters in the script to customize the rendering
#Step 3: source render.tcl


#################################################Load file###############################################################
mol new your_coordinate_file.pdb; #user-defined, see the example above
mol addfile your_trajectory_file.xtc first first_frame last last_frame; #user-defined, see the example above


#########################################Set the default display parameters, user-defined#############################################
display resize 2560 1440 
color Display Background white
display depthcue off
display projection Orthographic
axes location off
display ambientocclusion on; #turn on for better rendering, but slower
display shadows on; #turn on for better rendering, but slower

#########################################Set the lighting, user-defined#########################################
light 0 on; #Light effect changes with different rendering methods, recommended to try a few rendering methods.
light 1 on
light 2 off
light 3 off


##########################################Create your own material, user-defined#########################################
set your_material "your_material"

material add $your_material

material change ambient $your_material 0.0 
material change specular $your_material 0.49  
material change diffuse $your_material 0.90 
material change shininess $your_material 0.80
material change opacity $your_material 1.00
material change outline $your_material 1.70
material change outlinewidth $your_material 0.70 

##########################################Render the molecule, use MutLα as an example#########################################

#Render C domains(Blue)
mol representation Quicksurf
mol material $your_material
mol selection "resid 331 to 508 or resid 863 to 1040"
color change rgb 1 0.129 0.620 1.000 ;#change color index 1 to a certain color(written in RGB 0.129 0.620 1.000)
mol color ColorID 1 ;#color the molecule to color 1
mol addrep top ;#apply the representation to the top molecule

#Render N domains(Green)
mol representation Quicksurf
mol material $your_material
mol selection "resid 1 to 315 or resid 533 to 848"
color change rgb 2 0.588 0.980 0.592 
mol color ColorID 2 
mol addrep top 

#Render linker domain(White)
mol representation Quicksurf
mol material $your_material
mol selection "resid 316 to 330 or resid 509 to 532 or resid 849 to 862 or resid 1041 to 1064"
color change rgb 3 1 1 1 
mol color ColorID 3 
mol addrep top 
 
