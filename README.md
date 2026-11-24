# VMD_Animation

Step-by-step scripts that turn a raw MD trajectory into a presentation-ready movie of biomolecular conformational changes in VMD. The same VMD scene can also be viewed in VR through a local VMD → Unity viewer: set the scene up once, then render a movie or view it in a headset.

- Movie making: [Movie making](#movie-making)
- VR viewer: [VR Quick Start](#vr-quick-start)
- How the viewer works, the bridge protocol and tests: [docs/DEVELOPER.md](docs/DEVELOPER.md)
- Headset tests, launch procedures and open issues: [docs/VR_ISSUES.md](docs/VR_ISSUES.md)

## Repository layout

| Path | Contents |
| --- | --- |
| `animation/` | Movie-making Tcl scripts: `render.tcl` (also sets up the VR sample scene), `Align_all_frames.tcl`, `with_capture.tcl`, `without_capture.tcl`, `visualization.tcl`, `la.tcl`, `orient.tcl`, `viewpoint_example.tcl`. VR scene helpers: `vr_prepare.tcl` and `split_large_reps.tcl`. |
| `data/` | Sample MutLα structure and trajectory (`mutl_open_CA.pdb`, `Example_Trajectory.xtc`, frames 0–2000) and the example movie. |
| `start_vr.tcl`, `VMD_VR_Bridge/` | VMD side of the VR viewer: launcher and TCP bridge, with its regression tests. |
| `Unity_VR_Client/` | Unity 6 project of the VR viewer. Build output goes to the ignored `Unity_VR_Client/Builds/`. |
| `vr.cmd`, `tools/` | The `vr build / run / deploy / test` command line (`tools/vr.ps1`) and the lab bundle templates (`tools/bundle/`). See [Build, run, deploy](#build-run-deploy). |
| `docs/` | `DEVELOPER.md` (viewer internals), `COORDINATE_PLAYBACK.md` (live renderers and their tests), `VR_ISSUES.md` (headset tests and issues). |
| `local/` | Ignored; for private data and local exports. |

## Movie making

Open VMD, launch **Extensions → Tk Console**, and `cd` to the repository root. The scripts find their helper files and the sample data relative to their own folder, so they also work from another working directory.

```tcl
cd {C:/path/to/VMD_Animation}
```

### Render

**Step 1. Set the structure and trajectory in `animation/render.tcl`.** It loads the bundled sample:

```tcl
mol new [file join $render_data_dir mutl_open_CA.pdb]
mol addfile [file join $render_data_dir Example_Trajectory.xtc] first 0 last 2000 waitfor all
```

These are examples; replace them with your own files and frame range, for example:

```tcl
mol new your_structure.pdb
mol addfile your_trajectory.xtc first 12800 last 16069 waitfor all
```

**Step 2. Adjust the display, lighting, material and representations** in the marked user-defined sections of `animation/render.tcl`.

**Step 3. Source the script.**

```tcl
source animation/render.tcl
```

To view the same scene in VR, continue with [VR Quick Start](#vr-quick-start).

### Align all frames to a reference region

```tcl
source animation/Align_all_frames.tcl
align_all_frames "resid 331 to 508 or resid 863 to 1040" 0 0
```

The arguments are the reference region, the molecule ID and the reference frame. This example aligns all frames of molecule 0 to residues 331–508 and 863–1040 (a MutLα domain) in frame 0; use a region of your own molecule.

### Select the best viewpoint for your key conformations

**Step 1. Load the animation script.** Either take pictures while the animation plays:

```tcl
source animation/with_capture.tcl
```

or only play it, without pictures:

```tcl
source animation/without_capture.tcl
```

If you already saved viewpoints to a file, source that file and skip Steps 2–6 (`animation/viewpoint_example.tcl` is an example of such a file):

```tcl
source your_viewpoint_file.tcl
```

**Step 2.** Find the key frames that contain your key conformations.

**Step 3.** Go to key frame 1 and save the current viewpoint as viewpoint 0:

```tcl
save_vp 0
```

**Step 4.** Rotate the molecule to the view you want and save it as viewpoint 1:

```tcl
save_vp 1
```

**Step 5.** Go to key frame 2, rotate the molecule and save viewpoint 2. Continue for the remaining key frames:

```tcl
save_vp 2
```

**Step 6.** Write all viewpoints to a file:

```tcl
write_vps your_viewpoint_file.tcl
```

### Animation

**Step 1. Set the parameters.** Both animation scripts set these defaults; change them by running `set` after sourcing the script. The values are examples from the full MutLα trajectory used for the [example movie](#example-movie). The bundled `Example_Trajectory.xtc` has frames 0–2000 only, so with the sample keep every frame number in that range (for example `set end_frame 2000` and `set key_frames {83 700 1400 1999}`).

| Parameter | Example | Meaning |
| --- | --- | --- |
| `start_frame` | `70` | First frame of the animation. |
| `end_frame` | `3269` | Last frame of the animation. |
| `key_frames` | `{83 2957 3104 3268}` | Frames that contain the key conformations. |
| `frames_per_section` | `150` | Frames captured between two key frames. |
| `default_speed` | `1.0` | Animation speed, between 0 and 1. |
| `rotation_picture` | `25` | Pictures taken while rotating (`with_capture.tcl`). |
| `rotation_speed` | `300` | Higher is slower (`without_capture.tcl`). |
| `pause_time` | `35000` | Pause at each key conformation, in milliseconds. |
| `take_picture_interval` | `50` | Interval between pictures while paused (values still under exploration). |

To keep the time between key frames equal, frames are skipped as follows:

![Frame skipping between key frames](https://github.com/user-attachments/assets/304e6a7e-97fe-4403-80b9-43ffcacca55d)

`with_capture.tcl` uses `pause_time 35000` and captures 8 frames while paused, because the `TachyonInternal` renderer takes time. A faster renderer such as `snapshot` allows a lower value, such as the 3500 in `without_capture.tcl`.

**Step 2. Create an output directory** (only with `with_capture.tcl`):

```tcl
mkdir your_output_dir
cd your_output_dir
```

**Step 3. Start the animation.**

```tcl
init_animation
```

With `TachyonInternal`, all effects on and a high resolution, each picture takes longer; how long depends on the computer.

### Make the movie

The author joined the pictures into a movie with [VideoMach](https://gromada.com/videomach/), a tool recommended by VMD, which can also add annotations.

### Example movie

The scripts above produced a movie of four key conformational changes of MutLα from a raw MD trajectory: [MutLα on YouTube](https://youtu.be/FPb0yXllI84).

## VMD and Unity viewer

An experimental local viewer: VMD supplies the molecule, its representations and trajectory coordinates through a TCP bridge, and a Unity client draws them on the desktop, in the XR Interaction Simulator or in a PC VR headset on the same computer. Unity plays the trajectory itself; pausing and seeking set VMD to the same frame. Tested on a Valve Index (SteamVR) and on a Meta Quest over Meta Horizon Link with a USB cable; see [docs/VR_ISSUES.md](docs/VR_ISSUES.md).

### VR Quick Start

Every scene, the bundled MutLα sample or your own, goes through the same steps:

**Load a scene in VMD → adapt it for VR (only if needed) → start the bridge → start the viewer → check the status line.**

No VR-specific copy of a scene script is needed; the steps work on whatever VMD has loaded.

#### Step 0. Install (once)

Clone or download this repository and install VMD (tested with Windows x64 VMD `1.9.4a53`). To run the viewer from the Unity Editor or to build it, also install Unity `6000.6.2f1` through Unity Hub. VMD and the viewer must run on the same PC. On a PC without Unity, such as the lab PC, use a bundle made by [`vr deploy`](#build-run-deploy); its `INSTRUCTIONS.txt` repeats these steps.

#### Step 1. Load a scene in VMD

Open a fresh VMD session, open **Extensions → Tk Console**, `cd` to the repository root (or the extracted bundle folder) and load the scene:

| Scene | How to load it |
| --- | --- |
| Bundled MutLα sample | `source animation/render.tcl` |
| Your own data in the movie-making style | Copy `animation/render.tcl`, change its file paths and reps (see [Render](#render)), and source the copy |
| Any other scene | Source its own script or `.vmd` state file, or set it up in the VMD GUI |

```tcl
cd {C:/path/to/VMD_Animation}
source animation/render.tcl
```

#### Step 2. Adapt the scene for VR (only if needed)

Unity draws a scene itself (`[Unity live]`: smooth playback, ray highlighting, drawing-method switching) only when every visible rep uses VDW, CPK, NewCartoon or QuickSurf with a supported coloring and an opaque material. `animation/render.tcl` already qualifies, so the sample skips this step. For any other scene, check first:

```tcl
source animation/vr_prepare.tcl
vr_prepare -check     ;# prints what would change; changes nothing
vr_prepare            ;# converts the visible reps in place
```

`vr_prepare` turns Lines, Licorice and Bonds into CPK, Points into small VDW spheres, Tube, Cartoon and Ribbons into NewCartoon, and Surf or MSMS into QuickSurf. It hides water, other molecules and drawing methods Unity cannot draw (for example HBonds or Isosurface), and prints every change. A scene that already qualifies is left unchanged. The result looks close to, not identical to, VMD. Run it **before** Step 3: `start_vr.tcl` hides Lines reps, and `vr_prepare` leaves hidden reps alone. Options are listed at the top of [`animation/vr_prepare.tcl`](animation/vr_prepare.tcl).

If `vr_prepare` reports a QuickSurf rep that is too large, or a large or sparse system later looks blocky (the status line says the QuickSurf grid was coarsened), split it:

```tcl
source animation/split_large_reps.tcl
split_large_reps top -check
split_large_reps top
```

The default keeps VMD's bonded fragments together, which suits all-atom models. For coarse-grained models without bonds, give the size of one repeated unit, for example `-unit block 1268` for the nucleosome system. Options and costs are listed at the top of [`animation/split_large_reps.tcl`](animation/split_large_reps.tcl).

#### Step 3. Start the bridge

```tcl
source start_vr.tcl
```

Wait for `VMD-VR bridge listening on localhost:45454`. `start_vr.tcl` hides the top molecule's Lines reps and shows its NewCartoon reps; nothing else changes. If you change the scene later, you do not need to source it again: Unity reloads the scene by itself.

**Your own highlight parts (optional).** The highlight parts `N_DOMAIN`, `C_DOMAIN` and `LINKER` are examples: residue ranges of the bundled MutLα sample. For your own molecule, replace them with your own VMD selections now, before Step 4. See [Changing what is highlighted](#changing-what-is-highlighted).

#### Step 4. Start the viewer

| Where | How |
| --- | --- |
| Unity Editor, no headset | In Unity Hub, open the `Unity_VR_Client` folder as a project. Open `Assets/Scenes/SampleScene.unity` and enter **Play** mode. The Editor uses the XR Interaction Simulator by default (see [XR modes](docs/DEVELOPER.md#xr-modes)). |
| Built player on this PC | `vr build` once, then `vr run -Runtime desktop`, `xrsim` (Meta XR Simulator) or `steamvr`. See [Build, run, deploy](#build-run-deploy). |
| Lab bundle with a headset | Double-click `Start_Index_SteamVR.cmd`, or for a Quest start Link in the headset first and then double-click `Start_Quest_MetaLink.cmd`. See the bundle's `INSTRUCTIONS.txt` and [docs/VR_ISSUES.md](docs/VR_ISSUES.md). |

VMD and the viewer can start in either order; the viewer retries every 2 s. Start playback with **Enter** in the Game view or desktop window, or with the right **A** button in a headset. See [Controls](#controls).

#### Step 5. Check the status line

| Status | Meaning | What to do |
| --- | --- | --- |
| `[Unity live]` | Unity draws the scene itself. | Nothing. |
| `[VMD mesh]` | Some visible rep cannot be drawn by Unity, so VMD exports static meshes. A PC without live QuickSurf support also shows this. | Run Step 2 (`vr_prepare -check`). |
| `[Offline]` | The viewer cannot reach the bridge. | Check that VMD is still running, then `source start_vr.tcl` again. The viewer reconnects by itself. |
| QuickSurf grid coarsened | A QuickSurf rep spans too large a box. | Run `split_large_reps` (Step 2). |

#### Back to movie-making in the same VMD session

`vr_prepare` and `split_large_reps` change the reps in place. Before rendering movies from the same scene, restore it:

```tcl
vr_unprepare
unsplit_large_reps
```

### Controls

#### Desktop

Focus the Game view (or the player window):

| Input | Action |
| --- | --- |
| Enter / Numpad Enter | Play. |
| Space | Toggle Play/Pause. |
| P | Pause and set VMD to the displayed frame. |
| Left / Right arrow, or , / . | Previous / next frame. |
| 1 / 2 / 3 | Seek to frame 100 / 500 / 1000, clamped to the loaded range. |
| Left mouse drag | Orbit. |
| Right or middle mouse drag | Pan. |
| Mouse wheel | Zoom. |
| F | Fit the whole visible molecule. |
| H | Highlight the next part: `N_DOMAIN` → `C_DOMAIN` → `LINKER` → off. These are the MutLα sample's example parts; see [Changing what is highlighted](#changing-what-is-highlighted). |

The Game view also has Play/Pause, Previous, Next and **Speed** buttons. In Simulator mode the XR Interaction Simulator also reads Space, the arrow keys, digits, P and H, so only Enter / Numpad Enter (toggle Play/Pause) and , / . (previous / next frame) work there.

**Playback speed.** At 1x, one frame is shown every 0.1 s (10 frames per second). **Speed** steps through 1x, 0.5x, 0.25x and 2x and applies at once, also during playback. Slower speeds show each frame longer without interpolating, so 0.25x looks stepwise. On the VMD mesh route the speed is an upper limit, since each step waits for VMD's export. Every Play session starts at 1x.

#### Headset

| Input | Action |
| --- | --- |
| Grab: trigger on Valve Index controllers, grip on others | Hold on the molecule, up close or along the ray, to move and rotate it; with both hands, also scale it (0.05×–50× per grab). A molecule grabbed from a distance stays at that distance. |
| Right A | Play / Pause. |
| Right B | Next drawing method for every visible rep, keeping selections and colors: Original → VDW → CPK → QuickSurf → NewCartoon → Original. Original is the scene's own look; a method every rep already used at the start is left out. Presses during a change are ignored. |
| Right thumbstick left / right | Previous / next frame; repeats while held. Ignored while the molecule is grabbed. |
| Left X / Y (left A / B on Index controllers) | Back / forward 100 frames. |
| Left thumbstick click | Recenter: restore orientation and size in front of where you look. |
| Right thumbstick click | Toggle immersive size (stand inside the molecule, 4 m radius); click again for the in-front size. |
| Right ray + trigger | Press a status-panel button. |
| Right ray on the molecule | Highlight the part under the ray (see [Highlighting](#highlighting)). |

The ray only points; the face buttons and thumbsticks work wherever it points.

**Status panel.** A 38 × 17 cm panel stays in the room, ahead and to the left of where you looked when it was placed; Recenter brings it back into view. It has Play/Pause, Previous, Next and **Speed** buttons, the two status lines, a **Pointing: On/Off** button for ray highlighting with its own status line, and the controller bindings. To move it, grab the bar on top (Index: trigger, others: grip). Buttons work only while VMD is connected.

#### XR Interaction Simulator (Editor, no headset)

Controller buttons and the thumbstick go to the right controller; hold Shift to send them to the left controller. Release G or T before Shift. `]`, `[` and `H` choose which device W/A/S/D, Q/E, the arrow keys and the mouse move (right controller, left controller, head); they do not choose which hand receives button presses. With the head selected, W/S move forward/back, A/D left/right, Q/E down/up, and the arrow keys turn. In Simulator mode the ray highlight follows the mouse cursor.

| Controller input | Simulator keys | Action |
| --- | --- | --- |
| Right A | 1 | Play / Pause. |
| Right B | 2 | Next drawing method. |
| Right thumbstick left / right | J / L | Previous / next frame; repeats while held. |
| Right grip | G | Grab the molecule the ray points at. |
| Right trigger | T | Press the status-panel button the ray points at. |
| Right thumbstick click | 3 | Toggle immersive / in-front size. |
| Left X | Shift + 1 | Back 100 frames. |
| Left Y | Shift + 2 | Forward 100 frames. |
| Left thumbstick click | Shift + 3 | Recenter. |
| Left grip | Shift + G | Grab with the left hand; with both grips held, scale. |

These keys come from the simulator sample's `XR Interaction Controller Controls` input actions; its on-screen panel shows the current bindings.

### Highlighting

A highlight mutes the color of every atom outside one part of the molecule; the part keeps VMD's colors. Nothing changes in VMD, so playback continues undisturbed, and turning the highlight off restores VMD's exact colors.

- **Desktop:** H cycles through the parts, then off.
- **Headset and Simulator:** point the right ray at a part to highlight it; point away and it clears after about 0.25 s. The panel's **Pointing: On/Off** button switches this on and off. In Headset mode, H on the PC keyboard still cycles.
- Only the live VDW, CPK, NewCartoon and QuickSurf renderers (`[Unity live]`) are highlighted, not the `[VMD mesh]` route.
- The highlight follows rep changes made in VMD and is cleared when the connection to VMD drops.

How the ray picks atoms is described in [docs/DEVELOPER.md](docs/DEVELOPER.md#highlighting).

#### Changing what is highlighted

The default parts are examples. `N_DOMAIN`, `C_DOMAIN` and `LINKER` are defined in the bridge (`selections` in [`VMD_VR_Bridge/vmd_vr_bridge.tcl`](VMD_VR_Bridge/vmd_vr_bridge.tcl)) as residue ranges of the bundled MutLα sample, without a chain or segment filter. They do not detect domains: on another structure they select the wrong atoms or none, and the status line then shows, for example, `Highlight N_DOMAIN: no atoms selected`. Replace them with your own parts in one of two ways.

**A. Redefine the three names (no rebuild).** This works in the Unity Editor, a built player and a lab bundle. In VMD's Tk Console, after `source start_vr.tcl` and before starting the viewer, run one line per name. The selection text in braces is an example; replace it with any VMD selection for your molecule:

```tcl
::vrbridge::define_selection N_DOMAIN {chain A}
::vrbridge::define_selection C_DOMAIN {chain B}
::vrbridge::define_selection LINKER {resname ATP}
```

- Keep the names `N_DOMAIN`, `C_DOMAIN` and `LINKER`: a built player only asks for these three. They are labels only, so each may hold any part. For fewer than three parts, give the unused names `{none}`.
- Check a selection in VMD first: `[atomselect top {chain A}] num` prints how many atoms it selects.
- Ray highlighting resolves the names once per connection. If the viewer was already running, close it and start it again. The desktop H key asks VMD each time.
- Sourcing `start_vr.tcl` again restores the sample ranges of the three names. To avoid retyping, keep the lines in a file of your own, for example `my_highlight.tcl`, and source it right after `start_vr.tcl` each time.
- To change the defaults for every session of your copy, edit the `selections` array in `VMD_VR_Bridge/vmd_vr_bridge.tcl` instead.

**B. Use your own names or another number of parts (needs Unity and a new build).**

1. In Unity Hub, open the `Unity_VR_Client` project, then `Assets/Scenes/SampleScene.unity`.
2. In the Hierarchy, select **VMDManager** (the object with `VMDClient`) and choose **Add Component → VMD Highlight Controller**. Without this the component is only added at runtime, with the default list.
3. Edit **Selections**, in the order H cycles through them. An entry is either a name defined in the bridge or VMD selection text such as `chain A and resid 100 to 200` (an example), which needs no bridge definition. Selection text is fixed in the build, so it suits only the structure it was written for; names keep the build reusable for other molecules. **Dimming** sets how strongly the other atoms are muted.
4. Save the scene. Play mode in the Editor uses the new list at once; a built player needs `vr build`, and a lab bundle `vr deploy`.

Define each new name in VMD after `source start_vr.tcl`, as in A. `MY_DOMAIN` and the selection text are examples:

```tcl
::vrbridge::define_selection MY_DOMAIN {chain A and resid 10 to 120}
```

Names are case-insensitive letters, digits and underscores; `ALL` is reserved. Names you add survive sourcing `start_vr.tcl` again; redefinitions of `N_DOMAIN`, `C_DOMAIN` and `LINKER` do not. With a bridge older than `SELECT`, highlighting reports that the bridge must be reloaded and everything else keeps working.

### Build, run, deploy

One Windows OpenXR player serves every headset: the PC runs VMD and the viewer and renders, and the headset only displays the picture and sends head and controller input. The same build runs on a Valve Index (SteamVR), on a Meta Quest through Meta Horizon Link / Air Link or the Steam Link app (SteamVR), and in Meta XR Simulator. There is no Android (standalone Quest) build: a standalone app could not reach the bridge, which listens only on `127.0.0.1`.

The OpenXR runtime is chosen for each launch through the `XR_RUNTIME_JSON` environment variable, which the OpenXR loader reads. The system-wide runtime setting, which needs admin rights to change, is never touched.

Run the commands from the repository root in a Command Prompt or PowerShell (`vr help` shows every option):

| Command | What it does |
| --- | --- |
| `vr build [-Stereo SPI\|MultiPass\|All]` | Batch-mode Unity build of the Windows player into `Unity_VR_Client/Builds/Windows/<Stereo>/`, through `Assets/Editor/VMDBuild.cs`. Default: Single Pass Instanced. The Unity Editor must not have the project open. Project-settings files that Unity rewrites during the build are restored afterwards. Log: `Builds/logs/build_<Stereo>.log`. |
| `vr run [-Runtime xrsim\|steamvr\|metalink\|desktop\|<runtime .json>] [-Seconds 60] [-Stop]` | Starts the built player against one runtime (default: Meta XR Simulator), checks that the VMD bridge is listening, follows the player log, prints the mode, the XR start, the molecule placement (`[VMDView]` lines) and the VMD connection, and ends with PASS or FAIL. Arguments after `--` go to the player. Log: `Builds/logs/run_<runtime>_<time>.log`. |
| `vr deploy [-Target all\|index\|quest] [-SkipBuild] [-LocalExamples]` | Builds SPI and MultiPass (unless `-SkipBuild`) and writes the lab bundle `Builds/deploy/VMD_VR_Lab_<date>_<commit>.zip` (contents below). |
| `vr test` | Runs the four bridge regression tests in VMD text mode and summarizes them (see [Validation](docs/DEVELOPER.md#validation)). |

**Lab bundle.** The zip holds both players, `start_vr.tcl`, the bridge (without its tests), `animation/render.tcl`, `animation/vr_prepare.tcl`, `animation/split_large_reps.tcl`, the sample data, `docs/VR_ISSUES.md`, `INSTRUCTIONS.txt` (from `tools/bundle/`), `run_player.cmd` and the double-click launchers. The lab PC needs no Unity, Git or checkout.

- `-Target index` or `quest` packs only that headset's launchers (plus `Start_Desktop.cmd`) and adds `_index` or `_quest` to the name.
- `-LocalExamples` also packs this checkout's git-ignored `animation/*.tcl` scripts and `data/` files (not movies or subfolders), lists them in `EXTRA_EXAMPLES.txt` and adds `_examples` to the name.
- A checkout with uncommitted changes to tracked files gets `-dirty` after the commit.

The launchers pick the runtime for that launch only, from the registered OpenXR runtimes (readable without admin rights) or the usual install folders, and say so if none is found:

| Launcher | Headset and runtime |
| --- | --- |
| `Start_Index_SteamVR.cmd` | Valve Index, SteamVR. |
| `Start_Quest_MetaLink.cmd` | Meta Quest over Meta Horizon Link (cable) or Air Link; start Link in the headset first. |
| `Start_Quest_SteamLink.cmd` | Meta Quest over the Steam Link app (SteamVR); connect Steam Link first. |
| `Start_Desktop.cmd` | No headset (`-desktop`). |

The launchers start the Multi-pass player, because the Single Pass Instanced player showed one eye only on a Valve Index ([docs/VR_ISSUES.md](docs/VR_ISSUES.md), issue 9). Add `SPI` as the first argument (for example `Start_Index_SteamVR.cmd SPI`) to start the SPI player instead.

**Meta XR Simulator.** `vr run` (default `-Runtime xrsim`) needs Meta XR Simulator for Windows from Meta's download page, which asks you to accept Meta's license. It finds `meta_openxr_simulator.json` through the registered OpenXR runtimes, under `Program Files` or `AppData\Local`, or through the `VMD_XRSIM_JSON` environment variable. The simulator runs on Windows and macOS only; there is no Linux build, so it cannot run in WSL or a Linux container. Its "headless" mode means that Meta's `metavr` command-line tool drives it; the simulator still opens a window.

**Environment overrides:** `UNITY_EDITOR` (path to `Unity.exe`; by default the version in `ProjectSettings/ProjectVersion.txt` under Unity Hub), `VMD_EXE` (`vmd.exe`), `VMD_XRSIM_JSON`, `VMD_VR_PORT` (bridge port, see [Port](docs/DEVELOPER.md#port)).

### Known limitations

- VMD and the viewer must run on the same PC; a headset works only as a PC VR display of that PC.
- `[Unity live]` needs one visible top molecule whose reps all use VDW, CPK, protein NewCartoon or QuickSurf with a supported coloring, a fixed selection and an opaque material. Anything else uses the `[VMD mesh]` route, whose playback deforms exported meshes approximately and which is not highlighted.
- The viewer's lighting and transparency may differ from VMD's.
- Air Link and the Steam Link app are untested with the Quest, and some changes are not yet verified on a headset; see [docs/VR_ISSUES.md](docs/VR_ISSUES.md).

The full list, with memory and file limits, is in [docs/DEVELOPER.md](docs/DEVELOPER.md#current-behavior-and-limitations).
