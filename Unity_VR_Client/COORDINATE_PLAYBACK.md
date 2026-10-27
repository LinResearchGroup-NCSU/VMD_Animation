# VDW / CPK / NewCartoon / QuickSurf coordinate playback

## Quick test (10–30 trajectory frames)

1. In VMD, load a structure and a small range of trajectory frames. Select **VDW**, **CPK**, protein **NewCartoon** or **QuickSurf** in Graphical Representations. Use one visible molecule, an opaque material, and static selections for the first test.
2. Reload the updated bridge in VMD's Tk Console (stop the old bridge first if it is running):

   ```tcl
   catch {::vrbridge::stop}
   # Replace C:/path/to/VMD_Animation with your repository location.
   source C:/path/to/VMD_Animation/VMD_VR_Bridge/vmd_vr_bridge.tcl
   ::vrbridge::start 45454
   ```

3. Open `Assets/Scenes/SampleScene.unity` and enter Unity Play mode. The client connects and automatically prepares the first frame and a small window of subsequent coordinates.
4. Click **Play** in the Game view. **Pause**, **Previous**, and **Next** operate on the same controller. Enter starts, P pauses, Space toggles, and the arrow keys step. No cache export, file path, Undo, or playback-mode choice is required.
5. Change VDW to CPK using the existing representation controls. Atom colors/selections/radii and the CPK bond topology come from VMD. Check that the displayed frame is retained, the bonds follow their atoms, and Play still advances. F refits the camera.

The status bar has the same two lines for every route: `[route] state  frame / last` and a detail line. `[Unity live]` lists the reps Unity draws (for example `rep1 QuickSurf, rep3 CPK`); `[VMD mesh]` shows why the scene uses the existing path (for example `Representation Lines uses the existing renderer`) and what the export path is doing; `[Static view]`, `[Preparing]`, `[Error]` and `[Offline]` cover the remaining states.

Frames are zero based. At the last frame playback pauses; Play restarts at frame 0. Seeking pauses and clamps to the available range. The original startup behavior (VMD frame 0 on entering Unity Play mode) is retained. Restart Play mode after reconnecting/reloading the bridge.

## Implementation and scope

- `TrajectoryController.Play/Pause/Toggle/NextFrame/PreviousFrame/Step/Seek` are the shared public API. Future XR button events should call these methods; there is no separate VR playback loop or editor-only cache builder. Headset input bindings are not installed by this change.
- VMD sends selected atom indices, radii, colors, fixed bonds, and the initial molecule transform once per snapshot. Coordinates are read with `atomselect ... frame`, without changing VMD's displayed frame or exporting geometry. Pause/seek synchronize the displayed frame back to VMD.
- All live renderers share `Resources/VMDLighting.cginc`, the same lighting a VMD OBJ export gets in `VMDStaticGeometry.shader`. VMD's Wavefront exporter ignores the material and writes `Ka 0`, `Kd` = 0.65 × color, `Ks` = 0.5 × color and `Ns 40` for every rep, so a scene looks the same on the live and existing routes, and VMD material edits (other than transparency, which selects the existing route) do not change Unity's lighting on either route.
- Unity reuses a sphere mesh and cylinder mesh with GPU instancing. CPK bonds are two half-cylinders colored by their endpoints. Mesh detail is fixed at a modest resolution for this first version; VMD's own on-screen lighting is not reproduced pixel for pixel.
- NewCartoon has a separate `VMDCartoonScene` renderer. It reconstructs the backbone spline, ribbon orientation, beta arrows, coil tubes and surface normals at each source frame, reusing the mesh and arrays. Play, Pause and Seek use the same resolution: pausing does not replace the model with an OBJ. The VDW/CPK sphere/bond renderer and its material are unchanged.
- Protein NewCartoon preserves VMD's selected CA atoms, chain/segment/fragment boundaries, initial secondary-structure assignments, colors and thickness/aspect/spline parameters. Resolution is bounded to 4–24 samples per residue and 8–24 vertices per cross section. It supports Catmull–Rom and B-spline curves, plus Structure coloring. CA-only amino-acid models are drawn as smooth coils with a stable transported orientation; they do not invoke STRIDE to infer unavailable secondary structure. Secondary structure is not recomputed with STRIDE each frame; changes to VMD's assignments invalidate the snapshot. This is a Unity cartoon reconstruction, not a pixel-identical copy of VMD's renderer.
- QuickSurf has a separate `VMDQuickSurfScene` renderer. Each frame, a compute shader splats every selected atom as a Gaussian (sigma = radius × VMD's radius scale, cut off by VMD's surface quality) onto a grid at VMD's grid spacing, then extracts the isosurface at VMD's density isovalue with marching tetrahedra. Colors are density-weighted per-atom colors, normals come from the density gradient, and each VMD rep is its own surface, as in VMD. The GPU fills a back buffer while the previous surface is drawn, so a frame appears a few rendered frames after it is shown; buffers grow automatically, and grids above about 4 million points use a coarser spacing (noted on the status bar). This is a Unity reconstruction, not a pixel-identical copy of VMD's QuickSurf. Devices without compute shaders keep the existing VMD mesh route.
- The rolling buffer defaults to 24 frames. Its coordinate arrays target at most 64 MiB (with a minimum of two frames, or one for a single-frame molecule). The currently displayed frame, metadata, instance matrices, incoming JSON and temporary parsing allocations are additional memory. Each response contains at most four frames; large molecules reduce that to one. This is not a full-trajectory preload and creates no cache files.
- Preparation and buffer misses are automatic and visible as `Preparing` / `Buffering`. A slow connection/large frame can still require waiting. This removes per-frame OBJ export and parsing; NewCartoon still updates its geometry in Unity. It does not guarantee a particular frame rate or eliminate VMD/GPU limits.
- Supports visible VDW/CPK, protein NewCartoon and QuickSurf representations of one top molecule, fixed selections/bonds, and ColorID / Name / Element / ResName / Chain / SegName coloring with opaque materials. NewCartoon additionally supports Structure coloring. Multiple visible representations may be combined when all are compatible.
- Other drawing/color methods, nucleic-acid NewCartoon, multiple visible molecules, transparent materials, periodic images, and dynamic selection/color updates or smoothing retain the existing representation path. Explicit OBJ import/export, camera controls, and the original bridge commands remain available. These fallback paths retain their previous performance characteristics. A visible unsupported representation (such as VMD's default Lines) still makes the whole scene use that existing path.
- Representation/selection/color/material changes invalidate the snapshot. In-place edits to atom coordinates, radii or bond topology that do not change scene settings require restarting Unity Play mode; they are not continuously hashed during playback.
- The obsolete offline geometry cache generator/player, its bridge commands and manual path/Undo panel were removed. Existing generated files outside the source tree are untouched.

## Verification

`VMD_VR_Bridge/_test_trajectory.tcl` uses an eight-frame, two-atom real VMD fixture, including CPK connectivity, QuickSurf parameters (explicit and VMD defaults), unsupported-color fallback, selection invalidation, frame synchronization, limits and cleanup. Set `VMD_COORDINATE_TEST_ROOT` to a writable test directory before running it. The existing `_test_bridge.tcl` covers the original TCP/command behavior.

`Tests/CoordinatePlaybackChecks.cs` runs in an isolated Unity Editor project with the runtime scripts, resource shader/material (including their `.meta` files), and the JSON fixtures produced by the VMD test. It tests automatic preparation, geometry reuse, stepping, seeking, pause/restart, bounded buffering, style switching, snapshot invalidation and teardown. With a graphics device enabled, it also renders a small fixture and checks the distinct atom colors. These checks do not measure production GPU or VR performance.

`VMD_VR_Bridge/_test_cartoon.tcl` supplies a small multi-frame protein fixture with helix, sheet, coil and two independent chains. `Tests/CartoonPlaybackChecks.cs` verifies reconstruction, mesh reuse, updated normals, selection gaps, discontinuities, B-spline, mixed VDW/cartoon scenes, exact geometry on return to a frame, the shared playback controls, and switching from NewCartoon to QuickSurf. With graphics enabled it writes `cartoon-preview.png` for inspection; the isolated project also needs `com.unity.modules.imageconversion` for that test image.

To test NewCartoon, reload the bridge and restart Unity Play mode, select NewCartoon, then use the existing Play/Previous/Next buttons. At a chosen frame, pause and compare: geometry should remain identical. Jump elsewhere and return to the same frame: its shape should match again. Keep other unsupported visible representations hidden for this test, and check that the status shows `Playing`/`Paused`, not `uses the existing renderer`.

`Tests/QuickSurfPlaybackChecks.cs` uses the QuickSurf fixture from `_test_trajectory.tcl`. With graphics enabled (`-force-d3d11`) it checks that the GPU builds and draws a surface without shader errors, rebuilds it at moved atoms, returns to identical geometry, resolves queued frames to the newest one, joins the shared playback route with the unified status, and never exports OBJ or steps VMD; it writes `quicksurf-preview.png`. With `-nographics` it checks the fallback to the VMD mesh route.

CPK scaling follows [VMD's drawing implementation](https://www.ks.uiuc.edu/Research/vmd/doxygen/DrawMolItem_8C-source.html); the renderer uses Unity's [RenderMeshInstanced API](https://docs.unity3d.com/ScriptReference/Graphics.RenderMeshInstanced.html).
NewCartoon's style parameters are described in the [VMD NewCartoon documentation](https://www.ks.uiuc.edu/Research/vmd/current/ug/node70.html).
