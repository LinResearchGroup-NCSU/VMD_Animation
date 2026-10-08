# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!

The repository also includes a local VMD → Unity viewer with coordinate playback and VMD-generated static geometry. See [VR Extension](#vr-extension-phase-1) for setup and current limitations. The original movie-making workflow below remains available.
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

Run these commands from the repository root in VMD's Tk Console. When reloading an already-running bridge, first run `catch {::vrbridge::stop}`, then source the script again and restart Unity Play mode.

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
| `TOGGLE` | Legacy VMD-side playback toggle; use Unity's Enter/P controls for coordinate-synchronized playback. |
| `FRAME <index>` | Move to an exact trajectory frame. |
| `STEP <delta>` | Move forward or backward by a number of frames, clamped to the valid range. |
| `HIGHLIGHT <selection>` | Highlight `N_DOMAIN`, `C_DOMAIN`, or `LINKER`; use `ALL` to clear the added highlight representation. |
| `GET_COORDS` | Return the current top molecule's atom coordinates. |
| `EXPORT_SCENE <requestId>` | Export the visible VMD scene to OBJ/MTL and return its local path. |
| `SET_REPRESENTATION <requestId> <repIndex> <method>` | Change the top molecule's specified representation in VMD, then export the scene. Supported methods: VDW, CPK, QuickSurf, NewCartoon. |
| `HELP` | Return the supported command names. |

When a client connects, the bridge sends `READY VMD_VR_BRIDGE 1` followed by the current `STATE`. State messages include the top molecule ID, current frame, total frame count, and VMD rotation, center, scale, and global matrices.

`VMD_VR_Bridge/_test_bridge.tcl` provides regression coverage for command validation, frame control and clamping, domain highlighting, TCP connection handling, and state broadcasts. The latest run passed all 12 tests; generated test reports are ignored by Git.

### Unity client

`Unity_VR_Client/` is a Unity `6000.6.2f1` Universal Render Pipeline project. Open it with Unity Hub, open `Assets/Scenes/SampleScene.unity`, start the VMD bridge after loading a molecule, then enter Play mode. The client connects automatically. If connection fails or the bridge restarts, restart Play mode to reconnect.

Current scripts under `Assets/Scripts/`:

| Script | Responsibility |
| --- | --- |
| `VMD/VMDClient.cs` | TCP connection, commands, coordinate parsing, static-scene response forwarding. |
| `VMD/VMDKeyboardController.cs` | Desktop frame and trajectory-playback key bindings. |
| `VMD/VMDVRController.cs` | Placeholder for future headset/controller input. |
| `Molecule/MoleculeRenderer.cs` | Original atom objects, coordinate updates, automatic centering and atom scale; initializes the representation controller. |
| `Molecule/RepresentationController.cs` | Static export requests, representation-switch commands, loading and switching between static and coordinate views. |
| `Representations/VMDObjLoader.cs` | Generic OBJ/MTL parsing, coordinate-handedness conversion and mesh chunking. |
| `Representations/VMDStaticScene.cs` | Upload imported geometry to Unity meshes and release resources. |
| `Trajectory/TrajectoryController.cs` | Repeated `STEP 1` requests, waiting for coordinates before advancing. |
| `Camera/VMDCameraController.cs` | Orbit, pan, zoom and complete-molecule framing. |

`Assets/Resources/VMDStaticGeometry.shader` displays imported colors and material properties. Unity no longer contains separate VDW/CPK/QuickSurf/NewCartoon geometry algorithms; VMD generates those shapes. The original atom-coordinate renderer remains available for trajectory playback.

### Desktop controls

Focus the Game view while in Play mode:

| Input | Action |
| --- | --- |
| Enter / Numpad Enter | Start coordinate playback by repeatedly requesting `STEP 1`. |
| P | Pause Unity's step requests. |
| Left / Right arrow | Previous / next frame. |
| 1 / 2 / 3 | Request frame 100 / 500 / 1000 (the frame must exist). |
| Left mouse drag | Orbit the molecule. |
| Right or middle mouse drag | Pan. |
| Mouse wheel | Zoom. |
| F | Fit the complete visible molecule. |

The existing Space binding sends the legacy `TOGGLE` command; it is not the Enter/P coordinate-playback loop. The existing H binding sends `HIGHLIGHT` without its required selection argument, so it is not a working highlight shortcut. Neither binding was changed by the static-export work.

### Static representations: VMD export → Unity display

```text
VMD representation settings → VMD Wavefront OBJ/MTL export
                           → local file path over TCP
                           → Unity mesh import and display
```

After initial coordinates arrive, `RepresentationController` exports the current VMD scene once by default (`Export On Connect`). To refresh after changing VMD's Graphics → Representations settings, select the molecule object in Play mode and use the component's **Export current VMD scene** context menu, or call `RefreshFromVMD()`. GUI changes are not automatically monitored in this phase.

For future keyboard, UI or headset bindings, call `SetVDW()`, `SetCPK()`, `SetQuickSurf()`, `SetNewCartoon()`, `NextRepresentation()` or `PreviousRepresentation()`. `SetRepresentation(int repIndex, string method)` targets a specific representation on VMD's top molecule. These calls send a command to VMD, which changes the drawing method and exports the result; no new keyboard or XR bindings are installed. Switching methods applies that method's default parameters while preserving selection, coloring, material and visibility.

To import a saved export, set `Local Obj Path` and use **Load local VMD OBJ**, keeping the referenced MTL file alongside the OBJ. **Return to coordinate view** restores the original atom view.

Important phase-1 boundaries:

- Static export is not a live mesh stream. New coordinates from stepping or playback dismiss the static preview and restore the original atom view. Pause playback before refreshing a static representation.
- Export includes the entire visible VMD scene, not just the top molecule. Unity recenters imported geometry and fits the camera; it does not apply VMD view matrices again.
- Files are shared locally on the same computer, under `Unity_VR_Client/Temp/VMDStaticExports/`. Each request gets a separate directory; no automatic export-cache cleanup is implemented. Copy OBJ and MTL together elsewhere to retain them.
- Lighting and transparency may differ from VMD. Texture maps and OBJ points/lines are not displayed. Current limits are 128 MB per OBJ, 16 MB per MTL, 2 million input vertices and 500,000 output triangles.
- The supplied CA-only molecule currently exports empty NewCartoon geometry; a complete-backbone test produces geometry. Unity does not synthesize a replacement cartoon.
- Headset input, OpenXR/Meta Quest integration, atom-picking metadata and cross-computer mesh transfer remain outside this phase.

See [the detailed representation guide](VMD_VR_Bridge/REPRESENTATIONS.md) for APIs, protocol responses, import behavior and limitations.

### Validation

Run tests in separate VMD text processes from `VMD_VR_Bridge/`, not by sourcing them in an active research session. For example, in PowerShell with VMD installed at the default Windows path:

```powershell
cd VMD_VR_Bridge
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_bridge.tcl -eofexit
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_static_export.tcl -eofexit
```

The static-export test checks the four drawing methods, complete-backbone NewCartoon, invalid requests, preservation of scene settings, coordinates and frame stepping. Its fixtures, manifests and results are written to `Unity_VR_Client/Temp/static-export-tests/`.

Current validation: Unity C# compilation passed; the original bridge passed 12 tests; exported-file parsing and the 60,000-vertex mesh chunk boundary passed separate parser checks. The static VMD run completed its assertions but emitted STRIDE warnings and exited nonzero. Game-view visual acceptance and headset validation are still pending.

### Generated files and version control

`.gitignore` excludes Unity caches/builds, IDE-generated projects, crash reports, temporary bridge fixtures and generated test reports. The existing `Temp/` rule also covers static OBJ/MTL exports and export-test output. Source scripts, shaders, Unity `.meta` files, project settings and deliberately saved molecular assets remain eligible for version control; there is no blanket exclusion for OBJ, MTL, PDB or trajectory files.



    
