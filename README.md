# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!
## Start
Open VMD and launch TkConsole. Ensure that your coordinate and trajectory files are located in the `VMD_Animation` directory; otherwise, specify their paths in `render.tcl`.

        cd VMD_Animation

## Render 
### Step 1. Provide the path to the PDB and XTC files with frames to be imported in `render.tcl`
Example:

```tcl
mol new mutl_open_CA.pdb
mol addfile output_NVT_timecorrected.xtc first 12800 last 16069
```

### Step 2. Modify parameters in `render.tcl` to customize the rendering.
### Step 3. Source the script
        source render.tcl

## Align all frames to a reference region
### Step 1. Source the alignment script
        source Align_all_frames.tcl
        
### Step 2. Specify the region to be aligned, molecule ID and reference frame number
Example: This command will align all frames of molecule 0 to the reference region defined by residues 331 to 508 and 863 to 1040 in frame 0.

        align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0

## Select the best viewpoint for your key conformations
### Step 1. Source the script
Take pictures while the animation is playing:

        source with_capture.tcl

Or, just play the animation without capturing pictures:

        source without_capture.tcl
        
Skip steps 2-6 if you have already saved your viewpoints to a file, then:

        source your_viewpoints_file.tcl 
        
### Step 2. Identify the key frames containing your key conformations.
### Step 3. Switch to key frame 1, save the current viewpoint as viewpoint 0:

        save_vp 0
        
### Step 4. Rotate the molecule to the desired viewpoint, then save it as viewpoint 1:

        save_vp 1
        
### Step 5. Switch to key frame 2, rotate the molecule, and save the viewpoint as viewpoint 2. Continue this process for the remaining key frames:

        save_vp 2
        
### Step 6. Write all the viewpoints to a file 

        write_vps your_viewpoint_file.tcl

## Animation
### Step 1. Set user-defined parameters. Please modify them as needed.
#### Parameter (Example)
Your animation will start at frame 70.

        set start_frame 70
        
Your animation will end at frame 3269.

        set end_frame 3269
        
Your key conformations are located at frame 83, 2957, 3104, 3268.

        set key_frames {83 2957 3104 3268} 
        
The number of frames to be captured between each key frames.

        set frames_per_section 150
        
To ensure the time interval between each key frame is the same, we do a simple calculation to skip frames: 
![image](https://github.com/user-attachments/assets/304e6a7e-97fe-4403-80b9-43ffcacca55d)

Animation speed: You can set the speed between 0 and 1:

        set default_speed 1.0 
        
Take 25 pictures while rotating (available in `with_capture.tcl`):

        set rotation_picture 25
        
Rotation speed: The higher the value, the slower the rotation speed (available in `without_capture.tcl`)

        set rotation_speed 300 
        
Pause time for key conformations in milliseconds

        set pause_time 35000 
        
Here, the author set the value to 35000 in `with_capture.tcl` and captured 8 frames while pausing, since the rendering method `TachyonInternal` takes some time. If using a simpler render method like `snapshot`, you can set a lower value (e.g., 3500), as shown in `without_capture.tcl`.

Time interval for capturing while the animation is paused:

        set take_picture_interval 50 #Values are under exploration
    
### Step 2. Capture
Skip step 2, if you are using `without_capture.tcl`
Create an output directory

        mkdir your_output_dir

Nevigate to your output directory

        cd your_output_dir

### Step 3. Start animation with or without capture

        init_animation

*Note: If you're using the TachyonInternal renderer with all the cool effects enabled and displaying the molecule at high resolution, the execution time is likely to be longer. However, this will depend on the performance of your computer.*

## Movie Making
The author used **VideoMatch**, a movie-making software recommended by VMD, to concatenate the output images into a complete movie.

You can download **VideoMatch** here: [VideoMatch Download](https://gromada.com/videomach/)

With **VideoMatch**, you can easily add annotations and export the final movie.

## Example: Visulize four key comformational changes of MutLα
The author used the script above to generate a movie visualizing four key conformational changes of MutLα, starting from a raw MD trajectory.

You can see the example movie here: [MutLα](https://youtu.be/FPb0yXllI84)

## VR Extension (Phase 1)

The repository now also contains an experimental local bridge between VMD and a Unity client. VMD remains the source of truth for the loaded molecule, trajectory frame, representations, selections, and view transforms. The Unity project is the starting point for a separate immersive viewer and controller interface.

### Extension folders

- `VMD_VR_Bridge/` contains the Tcl TCP bridge, its regression tests, and bridge-specific documentation.
- `Unity_VR_Client/` contains the Unity 6 client project, including the scene, project settings, input configuration, and C# communication scripts.

### VMD bridge

`VMD_VR_Bridge/vmd_vr_bridge.tcl` opens a UTF-8, line-based TCP server on `127.0.0.1:45454`. It accepts local client connections, returns command results, and broadcasts VMD state changes at 100 ms intervals. Because it is bound to the loopback interface, it is currently intended for a Unity client running on the same computer as VMD.

After loading a molecule and trajectory in VMD, the bridge can be started from the Tk Console:

```tcl
source VMD_VR_Bridge/vmd_vr_bridge.tcl
::vrbridge::start
```

To stop it:

```tcl
::vrbridge::stop
```

The current command protocol supports:

| Command | Purpose |
| --- | --- |
| `PING` | Check whether the bridge is responding. |
| `STATE` | Request the current molecule, frame count, frame index, and VMD transformation matrices. |
| `PLAY` / `PAUSE` | Start or pause trajectory playback. |
| `TOGGLE` | Switch between playback and pause; this path is still marked for further implementation work. |
| `FRAME <index>` | Move to an exact trajectory frame. |
| `STEP <delta>` | Move forward or backward by a number of frames, clamped to the valid range. |
| `HIGHLIGHT <selection>` | Highlight `N_DOMAIN`, `C_DOMAIN`, or `LINKER`; use `ALL` to clear the added highlight representation. |
| `HELP` | Return the supported command names. |

When a client connects, the bridge sends `READY VMD_VR_BRIDGE 1` followed by the current `STATE`. State messages include the top molecule ID, current frame, total frame count, and VMD rotation, center, scale, and global matrices.

`VMD_VR_Bridge/_test_bridge.tcl` provides regression coverage for command validation, frame control and clamping, domain highlighting, TCP connection handling, and state broadcasts. The included test-result files record 12 passing tests.

### Unity client

`Unity_VR_Client/` is a Unity `6000.6.2f1` Universal Render Pipeline project. Its current extension-specific scripts are:

- `VMDClient.cs`: implements a local TCP client for `127.0.0.1:45454`, sends one command per line, and logs bridge responses.
- `VMDKeyboardController.cs`: provides desktop test inputs for selecting frames, stepping through the trajectory, and sending the playback toggle command.
- `VMDVRController.cs`: is currently a placeholder for future VR-controller integration.

The sample scene contains a `VMDManager` object with the client component. At this phase, automatic connection startup, parsing and applying `STATE` data to a molecular renderer, OpenXR/Meta Quest configuration, headset-native rendering, and completed VR controller bindings are not yet implemented. The extension therefore provides the tested VMD-side control bridge and an initial Unity communication client, rather than a finished VR application.



    
