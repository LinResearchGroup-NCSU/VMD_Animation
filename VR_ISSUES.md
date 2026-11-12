# VR headset testing: launch procedure and open issues

First real-headset test: 2026-11-10, NCSU D. H. Hill Jr. Library Gaming & VR Studio.
Hardware: Valve Index with SteamVR (SteamVR is the active OpenXR runtime), NVIDIA GeForce RTX 5080, plus an Intel integrated GPU in the same PC.
Build: `VMD_VR_Lab_v3` (Single Pass Instanced). VMD scene set up by `render.tcl` (three QuickSurf domain representations).

**Device basis of this file.** Every observation, fix and number below comes from the Valve Index with SteamVR. Nothing has been run on another headset. The next tests will use a Meta Quest 3S: its launch procedure is in [Launch procedure with a Meta Quest 3S](#launch-procedure-with-a-meta-quest-3s-untested), and steps or notes that apply only to the Index are marked **(Index only)**.

## Launch procedure on the lab PC (Valve Index with SteamVR, tested)

### Before going
1. Open `Unity_VR_Client` with Unity `6000.6.2f1`. In **File → Build Profiles**, select **Windows / Intel 64-bit** and include `Assets/Scenes/SampleScene.unity`.
2. In the PC settings under **Project Settings → XR Plug-in Management → OpenXR**, set **Render Mode** to **Single Pass Instanced** and build into `VMD_VR_Lab/SPI/`. Switch to **Multi-pass** and build into `VMD_VR_Lab/MultiPass/`. Keep the complete player output in each directory, including the executable, data folder, and runtime files.
3. Assemble and zip the entire `VMD_VR_Lab/` folder as `Unity_VR_Client/Builds/VMD_VR_Lab_vN.zip`. The folder contains:
   - `SPI/` and `MultiPass/`: the Unity players
   - `start_vr.tcl` and `VMD_VR_Bridge/`: the VMD bridge
   - `render.tcl`, `mutl_open_CA.pdb`, `Example_Trajectory.xtc`: scene setup and sample data
   - `INSTRUCTIONS.txt`: short instructions
   - `VR_ISSUES.md`: this file
4. Upload the zip to Google Drive. The lab PC needs no Unity, Git or repository checkout.
5. Studio access requires a Wolfpack One Card, the VR orientation and the signed user agreement.

### At the lab
1. Log in to the PC with your Unity ID. Download the zip from Google Drive. Right-click it and choose **Extract All**; do not run the player from inside the zip.
2. Install or open **VMD 1.9.4a53** (Windows 64-bit, from the UIUC site, installed to a user folder if there is no admin right).
3. **(Index only)** Prepare the Index: check that the link box power light is on, then press each controller's system button (below the trackpad) to turn it on. The base stations normally stay powered.
4. **(Index only)** **Close SteamVR** (Steam → Library → SteamVR → **STOP**). In Task Manager (Ctrl+Shift+Esc), end every `Unity_VR_Client` process and any leftover `vrserver`, `vrmonitor` or `vrcompositor`. A player stuck from an earlier attempt keeps SteamVR from giving the headset to a new one.
5. In VMD, open **Extensions → Tk Console** and run (use forward slashes, and go down to the folder that contains `start_vr.tcl`):
   ```tcl
   cd {C:/path/to/extracted/VMD_VR_Lab}
   source render.tcl
   source start_vr.tcl
   ```
   Wait for `VMD-VR bridge listening on localhost:45454`. For the bundled example, `render.tcl` loads the data and creates the visible QuickSurf representations before `start_vr.tcl` hides Lines.
6. **(Index only)** Double-click `SPI\Unity_VR_Client.exe`. A normal window opens first; about a second later the player starts SteamVR by itself. Wait about 30 s, then put on the headset.
7. Success looks like this: the molecule appears in the headset, and Player.log contains `[VMDXRBootstrap] XR started.` and `[VMDClient] Connected`.

### Checks and fallbacks
- **Active OpenXR runtime.** In PowerShell, run:
  ```powershell
  Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1' | Select-Object ActiveRuntime
  ```
  **(Index only)** It must end in `SteamVR\steamxr_win64.json`. If it does not, set it in SteamVR → Settings → show Advanced Settings → Developer → Set SteamVR as OpenXR Runtime. For a Quest 3S over Link the expected value is different; see the Quest section.
- **Desktop only, no headset:** run `Unity_VR_Client.exe -desktop`.
- **Geometry appears in only one eye:** use `MultiPass\Unity_VR_Client.exe`.
- **Player log:** `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Unity_VR_Client\Player.log`. Open it with `notepad` from Win+R while the player is still running.
- **Unity shows "[Offline] Connecting to VMD":** run `source start_vr.tcl` again in VMD; Unity reconnects by itself.

### Before leaving
1. Delete the extracted folder and the zip, then empty the Recycle Bin.
2. Sign out of Google in the browser, and of any personal Steam or Meta account.
3. Sign out of Windows.

## Launch procedure with a Meta Quest 3S (untested)

This procedure has not been run. It is written from how Quest Link works and from the project's OpenXR settings, so expect to adjust it at the first test. "Before going", steps 1, 2, 5 and 7 of "At the lab", and "Before leaving" are the same as for the Index. Steps 3, 4 and 6 are replaced by the steps below.

### What differs from the Index

| | Valve Index (tested) | Meta Quest 3S (untested) |
| --- | --- | --- |
| PC software | SteamVR, already on the lab PC | Meta Horizon Link PC app; installing it may need admin rights |
| OpenXR runtime | SteamVR | Meta Horizon Link |
| Account | None | A Meta account; the headset is set up with the Meta Horizon phone app |
| Connection | Cable to the link box | USB-C data cable (Link), or Wi-Fi (Air Link) |
| Tracking | Base stations in the room | Cameras in the headset; no base stations |
| Start order | Start the player; it launches SteamVR | Start Link in the headset first, then the player |
| Grab | Squeeze the grip firmly (`VMDIndexGrip`) | Hold the grip button; release it to let go |
| Face buttons | A / B on both controllers | A / B on the right, X / Y on the left |
| Controller profile | Valve Index Controller Profile | Meta Quest Touch Plus, or Oculus Touch as the fallback; both are enabled in the project |

The player is the same build for both headsets. It still has to run on the PC that runs VMD: a standalone app inside the Quest cannot reach the bridge on `127.0.0.1`.

### Steps

1. On the PC, open **Meta Horizon Link** and sign in with the Meta account used on the headset.
2. In Meta Horizon Link, open **Settings → General**:
   - Turn on **Unknown Sources**, so an app from outside the Meta store may run.
   - Under **OpenXR Runtime**, set Meta Horizon Link as the active runtime.
   - To check, run the PowerShell command from "Checks and fallbacks". The value is expected to end in `oculus_openxr_64.json`.
3. Close SteamVR if it is running, and end any leftover `Unity_VR_Client` process in Task Manager.
4. Connect the headset to the PC with the USB-C cable. In the headset, open **Quick Settings → Link** and launch it. Wait until the Link home environment appears.
5. Run the VMD commands (step 5 of the Index procedure).
6. Double-click `SPI\Unity_VR_Client.exe`, then put on the headset.
7. Before leaving, also sign out of Meta Horizon Link, and set the OpenXR runtime back to SteamVR for the next Index user (SteamVR → Settings → Developer → Set SteamVR as OpenXR Runtime).

**Alternative without the Meta PC app.** Install the **Steam Link** app on the headset and keep SteamVR as the OpenXR runtime. The headset and the PC must then be on the same local network, and campus Wi-Fi often blocks the discovery this needs.

### What to check on the first Quest 3S run

- Player.log shows `[VMDXRBootstrap] XR started.`, not `reloading in desktop mode`.
- `[VMDIndexGrip]` does **not** appear in the log. That line is for Index controllers only.
- Holding the grip button grabs the molecule and releasing it lets go.
- Right A / B and left X / Y behave as listed under "Controls" in `INSTRUCTIONS.txt`.
- The status panel text is readable, and the molecule appears in front of the user at a comfortable size.

## Fixed

- **Startup error "Couldn't switch to requested monitor resolution", or the player hung (Not Responding) with the headset stuck in the SteamVR home.**
  - Cause: `VMDXRBootstrap` started XR in `Awake`, before the player had applied its startup window resolution. On this hybrid-GPU PC that resolution switch then failed with a fatal DX11 error (v1 build) or hung (v2 build). Command-line window options, `-force-d3d12`, the Windows "High performance" GPU preference and disabling the flip-model swapchain did not help.
  - Fix: Headset mode is still chosen in `Awake`, but XR now starts in `Start`, after three frames and a further 0.5 s. If XR does not start, the scene reloads in Desktop mode. See `Unity_VR_Client/Assets/Scripts/XR/VMDXRBootstrap.cs`.

## Open issues

### 1. The molecule looks flat (resolved: a size and distance effect, not a rendering bug)
- Observation: at its default size and distance the molecule looked flat; brought close, it is clearly 3D.
- Conclusion: stereo rendering works in SPI. The molecule was simply too small and far away for stereo depth to show.
- Follow-up, immersive scale (v6, to be verified on the headset):
  - Two-handed scaling was clamped to XRI's default 0.25×–2× per grab; it is now 0.05×–50×.
  - The right thumbstick click toggles an immersive size: a 4 m radius, centered at head height and 0.3 × radius ahead, so the user stands inside the molecule. These are the `immersiveRadius` and `immersiveCenterAhead` fields on `VMDXRBootstrap`.
  - The molecule shaders use `Cull Off`, so inside a closed surface (QuickSurf) the user sees the surface's inner side. Which drawing method reads best from inside is still to be judged on the headset.
  - The right thumbstick is also the XR Origin's snap-turn and teleport stick, and its left/right steps frames. Check that the click does not also trigger locomotion.

### 2. The molecule is small and not in front of the user (you have to turn your head left)
- Code: `VMDXRBootstrap.FitMolecule`.
- **Position: fix in v4, to be verified on the headset.**
  - Cause: the molecule was placed at `xrOrigin.transform.TransformPoint(moleculeOffset)`, which follows the XR Origin's orientation rather than where the head faces. When SteamVR's play-area forward differs from the direction the user faces, the molecule ends up to one side.
  - Fix: the offset is now measured from the floor point below the head, along the head's horizontal heading.
  - The first fit waits up to 5 s for a head pose and turns the molecule's front toward the user.
  - Recenter (left thumbstick click) places it in front of wherever the user is looking.
- **Size: cause found in the v4 log; fix in v5.**
  - The v4 log shows the fit working as designed, for example `bounds size (0.16, 0.13, 0.20), radius 0.141 -> 0.4 m, scale x2.844`. The molecule really was scaled to the 0.4 m target radius; that target is simply desk-model size on the headset.
  - v5 doubles it: `moleculeRadius` 0.4 → 0.8 m and `moleculeOffset.z` 0.8 → 1.5 m, in both the code and `SampleScene.unity`.
- **Placement reset on every drawing-method switch (seen in the v4 log; fix in v5).**
  - Each QuickSurf / NewCartoon / VDW / CPK switch rebuilt the geometry and triggered a new fit, which undid the user's grabs and scaling.
  - From v5 the fit runs once, when the molecule first appears. After that only Recenter fits it again.
- **Molecule stays in the hand and cannot be put down (Index; fix in v5).**
  - Cause: XRI's Select is bound to `{GripButton}`. On Index controllers that is `/input/squeeze/value`, which follows finger curl, so just holding the controller counts as a pressed grip.
  - Fix: the new `VMDIndexGrip` runs only when an Index controller is present. It switches the grab interactors' select input to `gripForce`. Squeeze firmly (force ≈ 0.3) to grab, and relax the hand to let go. Teleport and other interactors are not touched.

### 3. The status panel is small, tilted, and stuck to the hand
- Code: `VMDStatusPanel`.
- **Size (v4).** It was 0.5 mm per GUI pixel (24 × 5 cm); it is now 0.8 mm (38 × 8 cm).
- **Tilt (v4).** `LateUpdate` copied the head's rotation, including pitch and roll. The panel now stays upright and faces the head.
- **Stuck to the hand (v5).** The panel followed the left controller, so it was always in the hand. It now stays put in the room: 0.7 m ahead, 0.45 m to the left and 0.25 m below eye level, placed once the head is tracked. Recenter (left thumbstick click) places it in view again, together with the molecule.
- **Button hints (after v5, not built yet).** Nothing in the headset said which button does what. The panel now lists the controller bindings below the status lines.
- All of the above are still to be verified on the headset.

### 4. VMD's own display window looks different on the lab PC than on the laptop
- Observation: the same molecule looks different in the VMD OpenGL window on the lab PC and on the laptop. Exactly how it differs (background, colors, lighting, outlines, projection, size) is still to be written down, ideally with a screenshot from each machine.
- Likely causes, most likely first:
  1. **The display settings were never applied at the lab.** The first lab session used a pasted block that only built the three QuickSurf representations and the material. It left out the rest of `render.tcl`:
     - `color Display Background white`
     - `display projection Orthographic`
     - `display depthcue off`
     - `axes location off`
     - `display ambientocclusion on`, `display shadows on`
     - the light settings
     - the material's `outline` and `outlinewidth`

     From v3 on, running `source render.tcl` applies all of these, so check again with that.
  2. **Different render mode or OpenGL support.** Material outlines, ambient occlusion and shadows need VMD's GLSL render mode and a driver that supports it. Run `display get rendermode` on both machines and compare; set it with `display rendermode GLSL` if needed.
  3. **Different VMD version.** The laptop has 1.9.4a53; check the lab PC with `vmdinfo version`.
  4. **Window size and DPI.** `render.tcl` runs `display resize 2560 1440`. On a monitor of a different size, or with different Windows display scaling, the framing and apparent size change.
- The laptop's `vmd.rc` is VMD's default and it has no personal `vmd.rc` in the user folder. The lab PC has not been checked yet.
- Effect on Unity: the Unity client renders on its own and does not depend on VMD's OpenGL window. The visible representations, colors and materials do still decide which route Unity uses and what the `[VMD mesh]` export contains.

### 5. The background distracts from the molecule (fix after v5, not built yet)
- Observation (v4 screenshot): the head camera showed Unity's default skybox, with a blue sky, a horizon and a brown "ground". The ground is not at the real floor, the horizon competes with the molecule for depth cues, and the brown clashes with the green and white domains.
- Fix: in Simulator and Headset modes `VMDXRBootstrap` sets the XR cameras to a plain dark gray (`xrBackground`). White, as in `render.tcl`, would glare in a headset. The desktop camera keeps the skybox.
- The molecule itself rendered correctly in that screenshot: the three domain colors match `render.tcl`.

### 6. Highlighting could not be triggered from inside VR (fix after v5, not built yet)
- Before: the desktop H key cycles `N_DOMAIN` → `C_DOMAIN` → `LINKER` → off. The dimming works in the same renderers in VR, but no controller button or panel button triggered it. In Headset mode only the H key on the PC keyboard did, which the person wearing the headset cannot reach.
- Fix: the status panel has a **Highlight** button right of Next. Point a controller ray at it and pull the trigger; it calls the same `VMDHighlightController.Cycle()` as the H key. A yellow line under the playback status shows the highlight's status, or `Highlight: off`. The panel is now 38 × 17 cm.
- Replaced (2026-11-12, not yet tried in the simulator or on a headset): the right controller's ray now highlights the domain it points at (`VMDRayHighlight`), and the panel button, labelled **Pointing: On/Off**, switches that on and off. On the headset, check that the domain under the ray is the one that lights up, that the 4 Å pick radius feels right, and that the highlight does not flicker while the ray sweeps across the molecule.
- The button works the same way for Index and Quest controllers (ray and trigger). It has not been tried on any headset.
- Limits carried over from the desktop: only the live VDW, CPK, NewCartoon and QuickSurf renderers are highlighted, not the exported VMD mesh route; the button is disabled while VMD is not connected.
