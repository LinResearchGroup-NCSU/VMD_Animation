# VMD_Animation
Here is a step-by-step guide to generate scientific presentation-ready movies of biomolecular conformational changes from raw MD trajectory. Enjoy!

The repository also includes a local VMD → Unity viewer with coordinate playback, automatic representation synchronization, and VMD-generated geometry. See [VMD and Unity viewer](#vmd-and-unity-viewer) for setup and current limitations. The original movie-making workflow below remains available.
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

## VMD and Unity viewer

The repository contains an experimental local bridge between VMD and a Unity client. VMD supplies molecular data, representation settings and coordinate snapshots. Unity controls coordinate playback and its own camera; pause and seek synchronize the displayed frame back to VMD. Compatible scenes render directly in Unity, while other scenes use exported VMD meshes. Headset input is not implemented yet.

### Extension folders

- `VMD_VR_Bridge/` contains the Tcl TCP bridge, its regression tests, and bridge-specific documentation.
- `Unity_VR_Client/` contains the Unity 6 client project, including the scene, project settings, input configuration, and C# communication scripts.

### VMD bridge

`VMD_VR_Bridge/vmd_vr_bridge.tcl` opens a UTF-8, line-based TCP server on `127.0.0.1:45454`. It accepts local client connections, returns command results, and checks VMD state every 100 ms, broadcasting only when it changes. Because it is bound to the loopback interface and exports use shared local files, run Unity and VMD on the same computer.

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
| `PLAY` / `PAUSE` | Start or pause VMD-side playback; Unity uses its shared trajectory controller instead. |
| `TOGGLE` | Legacy VMD-side command; Unity Play/Space uses its own shared playback controller. |
| `FRAME <index>` | Move to an exact trajectory frame. |
| `STEP <delta>` | Move forward or backward by a number of frames, clamped to the valid range. |
| `HIGHLIGHT <selection>` | Highlight `N_DOMAIN`, `C_DOMAIN`, or `LINKER`; use `ALL` to clear the added highlight representation. |
| `GET_COORDS` | Return the current top molecule's atom coordinates. |
| `GET_SCENE_STATE` | Return the scene signature used to detect representation, visibility and frame changes. |
| `EXPORT_SCENE <requestId>` | Export the visible VMD scene to OBJ/MTL and return its local path. |
| `SET_REPRESENTATION <requestId> <repIndex> <method>` | Change the top molecule's specified representation in VMD, then export the scene. Supported methods: VDW, CPK, QuickSurf, NewCartoon. |
| `RELEASE_SCENE <requestId>` | Release an export's import lease after Unity has finished reading its files. |
| `TRAJ_OPEN <requestId>` | Check compatibility and return a coordinate snapshot's token, metadata and initial transform. |
| `TRAJ_READ <requestId> <token> <start> <count>` | Read a bounded batch of coordinates without changing VMD's displayed frame. |
| `TRAJ_CHECK <requestId> <token>` | Validate the snapshot and return VMD's current frame. |
| `TRAJ_SYNC <requestId> <token> <frame>` | Synchronize VMD's frame to Unity after pause or seek. |
| `TRAJ_STYLE <requestId> <repIndex> <method>` | Change drawing method and invalidate the previous trajectory snapshot. |
| `HELP` | Return the supported command names. |

**Domain-selection limitation:** In `VMD_VR_Bridge/vmd_vr_bridge.tcl`, the `selections` mapping hard-codes `N_DOMAIN`, `C_DOMAIN` and `LINKER` to the residue ranges of the current MutLα dataset. These labels do not automatically identify domains in other proteins or structures with different residue numbering. VMD's `resid` refers to residue identifiers, not atom indices; the current expressions do not restrict `chain` or `segname`, so repeated residue IDs across chains can include unintended atoms. Before using domain highlighting with another structure, verify its domain boundaries, residue numbering and chain/segment identifiers and adapt the mapping accordingly. Otherwise, highlighting may select the wrong atoms or an empty selection. `HIGHLIGHT ALL` clears the added highlight representation; it does not perform domain detection.

When a client connects, the bridge sends `READY VMD_VR_BRIDGE 1` followed by the current `STATE` (or an error if no molecule is loaded). State messages include the top molecule ID, current frame, total frame count, and VMD rotation, center, scale, and global matrices. Unity reads the frame and frame count from this message; it does not apply these broadcast matrices to its camera. Trajectory metadata and mesh bindings carry their own geometry transforms.

`VMD_VR_Bridge/_test_bridge.tcl` provides regression coverage for command validation, frame control and clamping, domain highlighting, TCP connection handling, and state broadcasts. Generated test reports are ignored by Git.

### Unity client

`Unity_VR_Client/` is a Unity `6000.6.2f1` Universal Render Pipeline project. Open it with Unity Hub, open `Assets/Scenes/SampleScene.unity`, start the VMD bridge after loading a molecule, then enter Play mode. The client connects automatically. If connection fails or the bridge restarts, restart Play mode to reconnect.

Current scripts under `Assets/Scripts/`:

| Script | Responsibility |
| --- | --- |
| `VMD/VMDClient.cs` | TCP connection, commands, coordinate/state parsing, static-scene forwarding and cancellable trajectory requests matched by request ID. |
| `VMD/VMDKeyboardController.cs` | Desktop frame and trajectory-playback key bindings. |
| `VMD/VMDVRController.cs` | Placeholder for future headset/controller input. |
| `Molecule/MoleculeRenderer.cs` | Original atom objects, coordinate updates, automatic centering and atom scale; initializes the representation controller. |
| `Molecule/RepresentationController.cs` | Static export requests, representation-switch commands, loading and switching between static and coordinate views. |
| `Representations/VMDObjLoader.cs` | Generic OBJ/MTL parsing, coordinate-handedness conversion and mesh chunking. |
| `Representations/VMDStaticScene.cs` | Upload imported geometry to Unity meshes and release resources. |
| `Representations/VMDMeshBinding.cs` | Bind exported mesh vertices to selected atoms for approximate coordinate-driven deformation. |
| `Trajectory/TrajectoryController.cs` | Shared Play/Pause/Step/Seek API and status bar; automatic bounded coordinate prefetch for VDW/CPK, protein NewCartoon and QuickSurf, with existing playback retained for other representations. |
| `Trajectory/VMDCoordinateScene.cs` | Reusable instanced atom spheres and CPK bond cylinders driven by coordinates. |
| `Trajectory/VMDCartoonScene.cs` | NewCartoon spline and ribbon mesh rebuilt from each frame's backbone. |
| `Trajectory/VMDQuickSurfScene.cs` | QuickSurf rebuilt on the GPU from each frame's atoms (`VMDQuickSurfGrid.compute`, `VMDQuickSurf.shader`). |
| `Camera/VMDCameraController.cs` | Orbit, pan, zoom and complete-molecule framing. |

`Assets/Resources/VMDStaticGeometry.shader` displays imported colors and material properties. VDW/CPK use reusable Unity spheres/bonds; protein NewCartoon reconstructs its spline and ribbon mesh from each coordinate frame; QuickSurf rebuilds its Gaussian density surface on the GPU from each coordinate frame. These paths share automatic coordinate prefetch and playback controls. Unsupported scenes, and QuickSurf on a device without compute shaders, retain VMD-generated geometry. See [coordinate playback and small-frame testing](Unity_VR_Client/COORDINATE_PLAYBACK.md).

### Show or hide NewCartoon (VMD Tk Console)

`mol showrep` only controls a representation's visibility; it does not change its drawing method to NewCartoon. In VMD's **Graphics → Representations** window, first confirm that the target representation uses **NewCartoon**, then check its index (starting at `0`). `top` means the current top molecule, and `0` means its first representation, not trajectory frame 0.

If NewCartoon is representation `0`:

```tcl
# Show representation 0
mol showrep top 0 on

# Hide representation 0 without deleting the representation or molecular data
mol showrep top 0 off

# Restore visibility
mol showrep top 0 on
```

If representation `0` is Lines and representation `1` is NewCartoon, use these commands to test NewCartoon:

```tcl
mol showrep top 0 off
mol showrep top 1 on
```

Replace the indices with those used by your molecule. If NewCartoon itself is representation `0`, running `mol showrep top 0 off` hides NewCartoon; it does not specifically disable Lines. Changing a representation's drawing method preserves its visibility, so a hidden representation remains hidden after switching to NewCartoon until you turn it `on` again.

To test NewCartoon coordinate playback in Unity, keep the target NewCartoon representation visible and hide representations such as Lines that would make the scene use the existing rendering path. Then enter Unity Play mode and use the existing Play / Previous / Next buttons. `on` only restores visibility; if the model remains blank, check the atom selection, backbone data and Unity Console as well.

### Desktop controls

Focus the Game view while in Play mode:

| Input | Action |
| --- | --- |
| Enter / Numpad Enter | Play through the shared trajectory controller. |
| Space | Toggle the same Play/Pause controller. |
| P | Pause the shared trajectory controller and synchronize the coordinate route's frame to VMD. |
| Left / Right arrow | Previous / next frame. |
| 1 / 2 / 3 | Seek to frame 100 / 500 / 1000; the coordinate route clamps to the loaded range. |
| Left mouse drag | Orbit the molecule. |
| Right or middle mouse drag | Pan. |
| Mouse wheel | Zoom. |
| F | Fit the complete visible molecule. |

The Game view also provides Play/Pause, Previous and Next buttons. Keyboard, UI and future XR controls share `TrajectoryController`. The existing H binding still sends `HIGHLIGHT` without its required selection argument and is not a working highlight shortcut.

### Static representations: VMD export → Unity display

```text
VMD representation settings → VMD Wavefront OBJ/MTL export
                           → local file path over TCP
                           → Unity mesh import and display
```

After initial coordinates arrive, `TrajectoryController` checks whether the scene supports coordinate playback. Compatible scenes prepare automatically without OBJ export. The coordinate route checks snapshot validity after filling its buffer, with checks scheduled at 0.75-second intervals; representation changes reopen the snapshot. The VMD mesh route polls the scene signature every 0.5 seconds while paused, coalesces changes, and refreshes exported geometry after the export cooldown. Camera-only changes do not trigger an export. For a manual export, pause playback and use **Export current VMD scene** or call `RefreshFromVMD()`.

For future keyboard, UI or headset bindings, call `SetVDW()`, `SetCPK()`, `SetQuickSurf()`, `SetNewCartoon()`, `NextRepresentation()` or `PreviousRepresentation()`. `SetRepresentation(int repIndex, string method)` targets a specific representation on VMD's top molecule. These calls change the drawing method in VMD and select coordinate playback for compatible VDW/CPK/protein NewCartoon/QuickSurf scenes; other scenes use the VMD mesh path. Style changes pause playback and retain the displayed frame for the same molecule. No XR input bindings are installed. Switching methods applies that method's default parameters while preserving selection, coloring, material and visibility.

To import a saved export, set `Local Obj Path` and use **Load local VMD OBJ**, keeping the referenced MTL file alongside the OBJ. Keep `scene.binding` alongside bridge exports if coordinate-driven mesh playback is needed; older OBJ/MTL pairs remain loadable as static geometry. **Return to coordinate view** restores the original atom view. Explicit export/import and atom-view actions leave automatic coordinate rendering until a style change.

Current behavior and limitations:

- The VMD mesh route retains imported meshes during playback and approximately deforms top-molecule representation vertices using `scene.binding`: VDW vertices follow their nearest selected atom; other methods blend four neighboring atom displacements. Topology stays fixed, and other molecules remain static. This is not an exact surface/cartoon rebuild. Playback waits for a usable mesh and binding; it sends no per-frame export requests. Pause or a settled manual step queues a fresh VMD export while keeping the previous mesh visible.
- Export includes the entire visible VMD scene, not just the top molecule. Unity recenters imported geometry and fits the camera; it does not apply VMD view matrices again.
- Files are shared locally under `Unity_VR_Client/Temp/VMDStaticExports/`. Each request gets a separate directory containing OBJ, MTL and binding data. An import lease protects files while Unity reads them; exports have a two-second cooldown, and completed bridge-owned exports are pruned to a bounded set. Copy OBJ, MTL and binding together elsewhere to retain a playable export.
- Static export raises visible QuickSurf representations to a minimum grid spacing of 2.0 to limit export memory. This setting persists in VMD; it is not restored after export.
- Lighting and transparency may differ from VMD. Texture maps and OBJ points/lines are not displayed. Current limits are 128 MB per OBJ, 16 MB per MTL, 2 million input vertices and 500,000 output triangles.
- The Unity NewCartoon coordinate renderer supports CA-only amino-acid models as smooth coils without STRIDE. VMD Wavefront export may still produce empty NewCartoon geometry for CA-only data; use the live route or a complete-backbone structure where appropriate.
- Compatible coordinate scenes require one visible top molecule, supported drawing/color methods, opaque materials and fixed selections. Unsupported visible reps, multiple visible molecules, dynamic selections/colors, periodic images and smoothing select the VMD mesh route. QuickSurf also requires compute shaders. In-place coordinate, radius or bond edits not reflected in scene settings require restarting Play mode.
- The coordinate buffer defaults to 24 frames and targets 64 MiB of positions, with a minimum of two frames (one for a single-frame molecule). This excludes metadata, rendering buffers and parsing allocations. Requests contain at most four frames, reduced for large molecules. QuickSurf uses a bounded GPU grid and may coarsen its spacing; its surface can lag the displayed coordinate frame.
- Headset input, OpenXR/Meta Quest integration, atom-picking metadata and cross-computer mesh transfer remain outside this phase.

### Validation

Run tests in separate VMD text processes from `VMD_VR_Bridge/`, not by sourcing them in an active research session. For example, in PowerShell with VMD installed at the default Windows path:

```powershell
cd VMD_VR_Bridge
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_bridge.tcl -eofexit
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_static_export.tcl -eofexit
# Choose a writable location for the coordinate fixtures used by Unity checks.
$env:VMD_COORDINATE_TEST_ROOT = Join-Path $env:TEMP 'VMDCoordinateTests'
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_trajectory.tcl -eofexit
& 'C:\Program Files\VMD\vmd.exe' -dispdev text -e ./_test_cartoon.tcl -eofexit
```

The static-export test checks the four drawing methods, complete-backbone NewCartoon, invalid requests, preservation of scene settings, coordinates and frame stepping. Its fixtures, manifests and results are written to `Unity_VR_Client/Temp/static-export-tests/`.

Unity checks under `Unity_VR_Client/Tests/` cover coordinate playback, NewCartoon reconstruction and QuickSurf GPU rendering/fallback in isolated Editor projects. See [coordinate playback verification](Unity_VR_Client/COORDINATE_PLAYBACK.md#verification) for fixture and graphics requirements. These checks do not establish production frame rates or headset readiness. Rerun the applicable tests against the current working tree; historical results are not a current validation guarantee. Game-view comparison on the intended dataset and headset validation remain necessary.

### Generated files and version control

`.gitignore` excludes Unity caches/builds, IDE-generated projects, crash reports, temporary bridge fixtures and generated test reports. The existing `Temp/` rule also covers static OBJ/MTL exports and export-test output. Source scripts, shaders, Unity `.meta` files, project settings and deliberately saved molecular assets remain eligible for version control; there is no blanket exclusion for OBJ, MTL, PDB or trajectory files.



    
