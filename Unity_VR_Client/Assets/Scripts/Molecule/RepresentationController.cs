using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// The "VMD mesh" route: shows the molecule as geometry exported by VMD, and owns the
// drawing-method commands (VDW, CPK, QuickSurf, NewCartoon) for both display routes.
//
// Two routes can draw the molecule; TrajectoryController decides which one is active:
// - Coordinate route ("Unity live"): TrajectoryController reads coordinates and Unity's own
//   renderers draw them. While it owns the display (CoordinateOwnsDisplay), this component only
//   forwards drawing-method changes to it and sends no exports.
// - VMD mesh route (this component): for scenes the coordinate route cannot draw. VMD writes the
//   visible scene to OBJ/MTL plus scene.binding, and Unity imports it as a static mesh.
//
// What the mesh route does:
// - Export: EXPORT_SCENE (or SET_REPRESENTATION, which changes one rep first) makes VMD write the
//   files and reply SCENEJSON with their path, or SCENEERROR. One request is outstanding at a time.
// - Import: the OBJ is parsed on a background thread and replaces the previous mesh once ready.
//   RELEASE_SCENE then tells the bridge that the files have been read.
// - Synchronization: while paused, GET_SCENE_STATE is polled every 0.5 s. A changed signature
//   (representations, visibility, displayed frame or top molecule changed in VMD) triggers a new
//   export, so edits in VMD's GUI appear in Unity without any command from Unity.
// - Playback: no exports. The retained mesh is deformed from incoming coordinates through
//   scene.binding (an approximation); Pause exports the displayed frame again.
//
// Callers:
// - MoleculeRenderer: Initialize when it is enabled, OnCoordinatesUpdated for every coordinate frame.
// - TrajectoryController: SetCoordinateDisplay, TryStartMeshPlayback, SetPlaybackActive, FitView,
//   and it reads RepresentationIndex and Activity.
// - VMDVRController (right B button): NextRepresentation.
// - Inspector context menus: export, load a local OBJ, return to the atom view.
// No input bindings live here; keyboard and XR components call the public methods.
[DisallowMultipleComponent]
public class RepresentationController : MonoBehaviour
{
    // Request an export automatically once VMD is connected and the first coordinates have arrived.
    [SerializeField] private bool exportOnConnect = true;
    // VMD representation index that SetRepresentation(method) changes and whose drawing method is
    // reported as CurrentDrawingMethod. TrajectoryController reads it through RepresentationIndex.
    [SerializeField, Min(0)] private int representationIndex;
    // OBJ file for the "Load local VMD OBJ" context menu; not used when talking to VMD.
    [SerializeField] private string localObjPath = "";
    // Drawing method on display, "" when unknown. Serialized only to show it in the Inspector.
    [SerializeField] private string currentDrawingMethod = "";
    private VMDClient client;
    private MoleculeRenderer molecule;
    // Root object of the imported static scene (a child of the molecule), and its component.
    // drivenScene is the one deformed during mesh playback.
    private GameObject preview;
    private VMDStaticScene drivenScene;
    // The "playback needs scene.binding" warning is logged once per imported scene.
    private bool warnedMissingBinding;
    // Cancels the OBJ import in progress; null when none is running.
    private CancellationTokenSource loading;
    // ID of the export awaiting VMD's reply, null when none; sentAt is when it was sent (timeout).
    private string requestId;
    private float sentAt;
    // Incremented by every import and every cancel, so an import that finishes late can tell that
    // it has been superseded and drop its result.
    private int revision;
    // initialRequested: the first export of this connection has been requested, or deliberately
    // skipped. isLoading: an OBJ import is running.
    private bool initialRequested, isLoading;
    // Scene signature from VMD's last SCENESTATE or export reply; null until the first one.
    private string observedSignature;
    // statePending: a GET_SCENE_STATE awaits its reply.
    // refreshPending: VMD's scene settings changed, so a new export is due.
    // frameRefreshPending: the displayed frame changed while paused, or playback just ended.
    // coordinateViewRequested: the user chose the atom view (ShowAtomView); automatic exports
    //   stay off until an export is requested explicitly.
    private bool statePending, refreshPending, frameRefreshPending, coordinateViewRequested;
    // coordinateVersion counts coordinate frames received. An export remembers the value it was
    // requested at, and its reply is discarded if newer coordinates have arrived since.
    private int coordinateVersion, requestedCoordinateVersion;
    // Earliest times for the next GET_SCENE_STATE poll and for a pending refresh export; the
    // latter debounces bursts of frame steps and GUI edits.
    private float nextStatePoll, refreshAfter;
    // Mesh-route playback: the retained mesh follows incoming coordinates, and nothing is exported.
    private bool playbackActive;
    // Client-side cooldown between exports (2 s), mirroring the bridge's own cooldown.
    private float nextExportAllowed;
    // Extension point for Unity-initiated commands and next/previous cycling:
    // add a method here AND its default style in static_scene.tcl's
    // scene_set_representation switch; update both validation messages as well.
    // This list does NOT restrict automatic synchronization of VMD GUI changes.
    private static readonly string[] SupportedMethods = { "VDW", "CPK", "QuickSurf", "NewCartoon" };

    // JSON payload of SCENEJSON, SCENEERROR and SCENESTATE lines; each line fills only some fields.
    // 'code' is "BUSY" when the bridge is still exporting or cooling down.
    [Serializable] private class ExportReply
    {
        public string requestId = "", path = "", message = "";
        public string signature = "";
        public string code = "";
        public RepInfo[] reps = Array.Empty<RepInfo>();
    }
    // One VMD representation in an export reply: its index and drawing method.
    [Serializable] private class RepInfo { public int id = -1; public string method = ""; }

    // An imported VMD mesh exists (it may be hidden while the coordinate route draws).
    public bool HasStaticScene => preview != null;
    // An export is awaiting VMD's reply, or an OBJ import is running.
    public bool IsBusy => requestId != null || isLoading;
    private TrajectoryController trajectory;
    // The coordinate route is drawing the molecule, is still starting, or has failed
    // (TrajectoryController.OwnsDisplay). In all three cases this route stays idle.
    private bool CoordinateOwnsDisplay => trajectory != null && trajectory.OwnsDisplay;
    // Called by TrajectoryController when the coordinate route takes over (active, with the method
    // it draws) or gives the display back (inactive). It takes over only once a complete coordinate
    // frame is on screen, so the static preview is hidden no earlier than that.
    public void SetCoordinateDisplay(bool active, string method = "")
    {
        if (active)
        {
            CancelOperation();
            ResetSynchronization();
            playbackActive = false;
            molecule?.HideLegacyAtoms();
            currentDrawingMethod = method;
        }
        else
        {
            // Let Update request a fresh export for the mesh route.
            initialRequested = false;
            ResetSynchronization();
        }
        if (preview != null) preview.SetActive(!active);
    }
    // The mesh route should answer new coordinates with an export instead of fallback atoms.
    // True when this component is enabled, the coordinate route does not own the display, there
    // is neither mesh playback nor the atom view, and an export exists, is in progress, or will
    // be requested on connect.
    private bool UsesVMDRepresentation => isActiveAndEnabled && !CoordinateOwnsDisplay && !playbackActive && !coordinateViewRequested &&
        (exportOnConnect || initialRequested || HasStaticScene || IsBusy);
    // Drawing method on display: the coordinate route's method, or that of rep
    // 'representationIndex' in the last export; "" when unknown.
    public string CurrentDrawingMethod => currentDrawingMethod;
    // What the VMD mesh route is doing, as one short phrase for the shared status bar.
    public string Activity =>
        requestId != null ? "Exporting VMD geometry" :
        isLoading ? "Importing VMD geometry" :
        playbackActive && drivenScene != null && drivenScene.CanDeform ? "Mesh follows atoms until Pause (approximate)" :
        HasStaticScene ? (currentDrawingMethod.Length > 0 ? currentDrawingMethod + " from VMD export" : "VMD export") : "";
    public int RepresentationIndex => representationIndex;

    // Bind this controller to a VMDClient and its owning MoleculeRenderer, and (re)subscribe
    // to static-scene responses. Also finds or adds the TrajectoryController on the client's
    // object and hands it this controller. Called by MoleculeRenderer.OnEnable.
    public void Initialize(VMDClient source, MoleculeRenderer owner)
    {
        if (client != null) client.StaticSceneResponseReceived -= Receive;
        client = source;
        molecule = owner;
        trajectory = source != null ? source.GetComponent<TrajectoryController>() : null;
        if (source != null && trajectory == null) trajectory = source.gameObject.AddComponent<TrajectoryController>();
        trajectory?.InitializeCoordinates(this, owner);
        if (client != null && isActiveAndEnabled) client.StaticSceneResponseReceived += Receive;
    }

    // Re-subscribe to static-scene responses without registering a duplicate handler,
    // and reset the initial-export and synchronization state.
    private void OnEnable()
    {
        if (client != null)
        {
            client.StaticSceneResponseReceived -= Receive;
            client.StaticSceneResponseReceived += Receive;
        }
        initialRequested = false;
        coordinateViewRequested = false;
        playbackActive = false;
        nextExportAllowed = 0f;
        ResetSynchronization();
    }

    // Main loop of the mesh route; idle while the coordinate route owns the display.
    // In order: handle a disconnect, time out an unanswered export, request the initial export
    // once the first coordinates have arrived, poll VMD's scene signature every 0.5 s, and send
    // the refresh export that a frame step or a VMD GUI edit has queued. During playback nothing
    // is polled or exported.
    private void Update()
    {
        if (CoordinateOwnsDisplay) return;
        if (client == null || molecule == null) return;
        if (!client.IsConnected)
        {
            // The next connection starts again from the initial export.
            initialRequested = false;
            ResetSynchronization();
            if (IsBusy) CancelOperation();
            return;
        }
        // A bridge that never answers (for example one older than EXPORT_SCENE) must not block
        // every later export.
        if (requestId != null && Time.unscaledTime - sentAt > 120f)
        {
            CancelOperation();
            Debug.LogWarning("[RepresentationController] Export timed out. Reload the updated VMD bridge.", this);
        }
        // Playback deforms the retained mesh. Do not poll/export changing frame signatures.
        if (playbackActive || CoordinateOwnsDisplay) return;
        // Wait for startup coordinates so they cannot overwrite the first static preview.
        if (exportOnConnect && !initialRequested && client.Coordinates.Count > 0 &&
            !IsBusy && Time.unscaledTime >= nextExportAllowed)
        {
            initialRequested = true;
            RequestStaticExport();
        }
        // One outstanding query at a time; camera-only changes do not trigger exports.
        if (!statePending && Time.unscaledTime >= nextStatePoll)
        {
            statePending = client.SendCommand("GET_SCENE_STATE");
            nextStatePoll = Time.unscaledTime + 0.5f;
        }
        // Coalesce manual steps and GUI edits, respecting the export cooldown.
        if (!IsBusy && Time.unscaledTime >= nextExportAllowed && Time.unscaledTime >= refreshAfter &&
            (frameRefreshPending || refreshPending))
        {
            // frameRefreshPending is cleared by SendExportCommand once the command is sent.
            if (RequestStaticExport())
            {
                refreshPending = false;
            }
        }
    }

    // Clear all scene-signature polling and automatic-refresh state.
    private void ResetSynchronization()
    {
        observedSignature = null;
        statePending = refreshPending = frameRefreshPending = false;
        nextStatePoll = 0f;
    }

    // Start or stop mesh-route playback. Called by trajectory playback, not by input bindings.
    // Wait for the last coordinate reply before passing false, so Pause exports the frame
    // actually displayed.
    public void SetPlaybackActive(bool active)
    {
        if (playbackActive == active) return;
        playbackActive = active;
        frameRefreshPending = refreshPending = false;
        if (active)
        {
            // Keep imported meshes/materials/bindings. Only their vertex positions
            // change as coordinate replies arrive; Pause replaces the reference export.
            if (molecule != null && !coordinateViewRequested) molecule.HideLegacyAtoms();
        }
        else if (isActiveAndEnabled && !coordinateViewRequested)
        {
            // Replace the approximately deformed mesh by an exact export of the paused frame,
            // after a short delay in case more frame changes follow.
            frameRefreshPending = true;
            refreshAfter = Time.unscaledTime + 0.35f;
        }
    }

    // Ask to start mesh-route playback. Returns true when playback may step frames now, false
    // when the caller must wait and ask again. Stepping is deferred until the first export and
    // its binding have been imported; this also handles Enter pressed immediately after entering
    // Unity Play, without exporting every frame.
    public bool TryStartMeshPlayback()
    {
        if (CoordinateOwnsDisplay) return false;
        if (playbackActive) return true;
        // The atom view plays back without a mesh.
        if (coordinateViewRequested) { SetPlaybackActive(true); return true; }
        if (IsBusy) return false;
        if (drivenScene != null && drivenScene.CanDeform)
        {
            SetPlaybackActive(true);
            return true;
        }
        // No deformable mesh yet: queue the first export, or explain once why playback cannot start.
        if (!initialRequested) frameRefreshPending = true;
        else if (!warnedMissingBinding)
        {
            warnedMissingBinding = true;
            Debug.LogWarning("[RepresentationController] Playback needs a non-empty mesh and scene.binding. Reload the updated bridge and refresh the export.", this);
        }
        return false;
    }

    // Inspector context-menu action: manually request a fresh scene export from VMD.
    [ContextMenu("Export current VMD scene")]
    public void RefreshFromVMD() => RequestStaticExport();

    // Ask VMD to export the current scene as OBJ/MTL. Returns true if the command was sent.
    // If the coordinate route owns the display, it is switched to the explicit static view first.
    public bool RequestStaticExport()
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        return SendExportCommand("EXPORT_SCENE");
    }

    // These wrappers can be wired directly to UnityEvents, e.g. UI buttons. (The XR controller
    // uses NextRepresentation instead.) To expose a new method as a button action, add a SetXxx
    // wrapper here after registering it in SupportedMethods and the VMD-side style switch.
    public void SetVDW() => SetRepresentation("VDW");
    public void SetCPK() => SetRepresentation("CPK");
    public void SetQuickSurf() => SetRepresentation("QuickSurf");
    public void SetNewCartoon() => SetRepresentation("NewCartoon");

    // Change the drawing method of the configured representation index.
    public void SetRepresentation(string method) => SetRepresentation(representationIndex, method);

    // Change the drawing method of one VMD representation. The method must be in SupportedMethods
    // (case-insensitive). Returns true if the change was sent.
    // - Normally the TrajectoryController sends it (TRAJ_STYLE) and reopens the trajectory, which
    //   also decides again which route can draw the new method.
    // - Without a TrajectoryController it falls back to SET_REPRESENTATION: VMD changes the rep
    //   and replies with a new export.
    // - While an export or import is in progress, or VMD is disconnected, the change is refused
    //   with a warning (see SendExportCommand).
    public bool SetRepresentation(int repIndex, string method)
    {
        string canonical = Array.Find(SupportedMethods,
            value => string.Equals(value, method, StringComparison.OrdinalIgnoreCase));
        if (repIndex < 0 || canonical == null)
        {
            Debug.LogWarning("[RepresentationController] Choose VDW, CPK, QuickSurf or NewCartoon and a valid rep index.", this);
            return false;
        }
        if (trajectory != null && !IsBusy && client != null && client.IsConnected)
        {
            trajectory.ChangeRepresentation(repIndex, canonical);
            representationIndex = repIndex;
            return true;
        }
        bool sent = SendExportCommand("SET_REPRESENTATION", repIndex + " " + canonical);
        if (sent) representationIndex = repIndex;
        return sent;
    }

    // Switch to the next / previous drawing method in SupportedMethods.
    public void NextRepresentation() => Cycle(1);
    public void PreviousRepresentation() => Cycle(-1);

    // Step through SupportedMethods relative to the currently displayed method (wrapping around)
    // and apply the result; starts from the first method if the current one is not in the list.
    private void Cycle(int direction)
    {
        int current = Array.IndexOf(SupportedMethods, currentDrawingMethod);
        int next = current < 0 ? 0 : (current + direction + SupportedMethods.Length) % SupportedMethods.Length;
        // In the coordinate route switch every visible rep together, e.g. all three MutL domains;
        // the configured index may be a hidden rep such as the default Lines.
        if (CoordinateOwnsDisplay && !IsBusy && client != null && client.IsConnected)
        {
            List<int> visible = trajectory.DisplayedRepresentationIds();
            if (visible.Count > 0)
            {
                Debug.Log($"[RepresentationController] {currentDrawingMethod} -> {SupportedMethods[next]} for reps {string.Join(", ", visible)}.", this);
                trajectory.ChangeRepresentations(visible, SupportedMethods[next]);
                return;
            }
        }
        // Mesh route, or no visible rep known yet: change only the configured rep.
        if (!SetRepresentation(representationIndex, SupportedMethods[next]))
            Debug.LogWarning($"[RepresentationController] '{name}' cannot switch to {SupportedMethods[next]} now " +
                             $"(client: {(client != null ? client.name : "none")}, connected: {client != null && client.IsConnected}, " +
                             $"trajectory: {(trajectory != null ? trajectory.name : "none")}, busy: {IsBusy}, live route: {CoordinateOwnsDisplay}).", this);
    }

    // Send an export-producing command (EXPORT_SCENE or SET_REPRESENTATION) tagged with a new
    // request ID, so its reply can be matched. Returns true if it was sent. Refused, with a
    // warning, while disconnected, while another export or import is in progress, while the
    // coordinate route owns the display, during playback and during the export cooldown.
    private bool SendExportCommand(string command, string arguments = null)
    {
        if (!isActiveAndEnabled || client == null || !client.IsConnected || IsBusy ||
            CoordinateOwnsDisplay || playbackActive || Time.unscaledTime < nextExportAllowed)
        {
            Debug.LogWarning("[RepresentationController] Pause playback and wait for the current import/cooldown before exporting.", this);
            return false;
        }
        requestId = Guid.NewGuid().ToString("N");
        requestedCoordinateVersion = coordinateVersion;
        sentAt = Time.unscaledTime;
        string line = command + " " + requestId + (arguments == null ? "" : " " + arguments);
        if (client.SendCommand(line))
        {
            // An explicit export ends the atom view and counts as the connection's first export.
            initialRequested = true;
            coordinateViewRequested = false;
            frameRefreshPending = false;
            nextExportAllowed = Time.unscaledTime + 2f;
            return true;
        }
        requestId = null;
        return false;
    }

    // Handle SCENESTATE / SCENEJSON / SCENEERROR lines forwarded by VMDClient.
    // SCENESTATE feeds change detection; SCENEJSON / SCENEERROR complete the pending request,
    // and a successful reply starts loading the exported OBJ file. A reply that is no longer
    // wanted (unknown request ID, playback started, newer coordinates) is only released.
    private void Receive(string response)
    {
        try
        {
            if (response.StartsWith("SCENESTATE ", StringComparison.Ordinal))
            {
                statePending = false;
                if (playbackActive || CoordinateOwnsDisplay) return;
                var state = JsonUtility.FromJson<ExportReply>(response.Substring(11));
                if (state == null || string.IsNullOrEmpty(state.signature)) return;
                // Method-agnostic: every change of VMD's representation settings
                // refreshes the export, in either direction, without a per-method branch.
                // New GUI methods need no registration here; their geometry must still
                // be supported by VMD's Wavefront exporter and our OBJ/MTL importer.
                // The signature also covers the top molecule and each molecule's visibility
                // and displayed frame.
                if (observedSignature != null && observedSignature != state.signature)
                {
                    // The new settings may suit the coordinate route: let it try again, unless
                    // the user chose the atom view. Export after a short delay for further edits.
                    if (trajectory != null && !coordinateViewRequested)
                        trajectory.RefreshCoordinates();
                    refreshPending = true;
                    refreshAfter = Time.unscaledTime + 0.25f;
                }
                observedSignature = state.signature;
                return;
            }
            bool failed = response.StartsWith("SCENEERROR ", StringComparison.Ordinal);
            var reply = JsonUtility.FromJson<ExportReply>(response.Substring(failed ? 11 : 10));
            if (reply == null) return;
            // Not the request we are waiting for, e.g. one cancelled earlier: free its files.
            if (requestId == null || reply.requestId != requestId)
            {
                if (!failed) ReleaseExport(reply.requestId);
                return;
            }
            requestId = null;
            if (failed)
            {
                // The bridge is still exporting or cooling down: retry after the cooldown.
                if (reply.code == "BUSY" && !playbackActive)
                {
                    frameRefreshPending = true;
                    nextExportAllowed = Time.unscaledTime + 2f;
                }
                Debug.LogWarning("[RepresentationController] VMD: " + reply.message, this);
                return;
            }
            // A manual step can overtake an export. Never display its older coordinates.
            if (playbackActive || requestedCoordinateVersion != coordinateVersion)
            {
                ReleaseExport(reply.requestId);
                return;
            }
            if (string.IsNullOrWhiteSpace(reply.path))
            {
                ReleaseExport(reply.requestId);
                throw new FormatException("Export response has no OBJ path.");
            }
            // This export already includes these settings (also avoids duplicate
            // exports after a Unity-initiated SET_REPRESENTATION command).
            if (!string.IsNullOrEmpty(reply.signature))
            {
                observedSignature = reply.signature;
                refreshPending = false;
            }
            // Report the drawing method of the configured rep, if the export contains it.
            string method = "";
            if (reply.reps != null)
                foreach (var rep in reply.reps)
                    if (rep.id == representationIndex) method = rep.method;
            LoadScene(reply.path, method, reply.requestId);
        }
        catch (Exception error)
        {
            requestId = null;
            Debug.LogWarning("[RepresentationController] Invalid export response: " + error.Message, this);
        }
    }

    // Inspector context-menu action: load the OBJ at localObjPath without contacting VMD.
    [ContextMenu("Load local VMD OBJ")]
    public void LoadLocalFile() => LoadStaticScene(localObjPath);

    // Cancel any pending operation and load a static OBJ scene from the given path.
    // If the coordinate route owns the display, it is switched to the explicit static view first.
    public void LoadStaticScene(string path)
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        if (!isActiveAndEnabled || molecule == null) return;
        CancelOperation();
        initialRequested = true;
        coordinateViewRequested = false;
        LoadScene(path, "");
    }

    // Import an exported scene. The OBJ and its scene.binding are parsed on a background thread;
    // the mesh hierarchy is then built on the main thread in an inactive staging object and
    // swapped in for the current preview, so the old mesh stays visible until the new one is
    // ready. A load that was cancelled or superseded, or whose coordinates are out of date,
    // discards its result. 'exportId' is the bridge's request ID (null for a local file); the
    // export is released when the load ends, however it ends.
    private async void LoadScene(string path, string method, string exportId = null)
    {
        int version = ++revision;
        int frameVersion = coordinateVersion;
        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        isLoading = true;
        GameObject staging = null;
        try
        {
            var imported = await Task.Run(() =>
            {
                var data = VMDObjLoader.Load(path, cancellation.Token);
                return (data, binding: VMDMeshBinding.Load(path, data, cancellation.Token));
            }, cancellation.Token);
            if (version != revision || frameVersion != coordinateVersion || !isActiveAndEnabled || molecule == null) return;
            var data = imported.data;
            staging = new GameObject("VMD static scene");
            staging.SetActive(false);
            staging.transform.SetParent(molecule.transform, false);
            var scene = staging.AddComponent<VMDStaticScene>();
            scene.Load(data, molecule.CoordinateScale, imported.binding);
            ClearPreview();
            preview = staging;
            drivenScene = scene;
            warnedMissingBinding = false;
            // The staging object is now the preview; the finally block must not destroy it.
            staging = null;
            molecule.HideLegacyAtoms();
            preview.SetActive(true);
            currentDrawingMethod = method;
            foreach (string warning in data.warnings) Debug.LogWarning("[VMD OBJ] " + warning, this);
            Debug.Log($"[VMD OBJ] Loaded {data.triangleCount} triangles from {path}", this);
            if (data.triangleCount > 0) FitView();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version == revision)
                Debug.LogError("[RepresentationController] Static import failed: " + error.Message, this);
        }
        finally
        {
            if (staging != null) Destroy(staging);
            if (ReferenceEquals(loading, cancellation)) { loading = null; isLoading = false; }
            cancellation.Dispose();
            ReleaseExport(exportId);
        }
    }

    // Tell the bridge that Unity has finished with an export's files (RELEASE_SCENE), which ends
    // the bridge's import lease on them, and start the cooldown before the next export.
    // Does nothing for an empty ID or while disconnected.
    private void ReleaseExport(string id)
    {
        if (string.IsNullOrEmpty(id) || client == null || !client.IsConnected) return;
        // Only release bridge-issued safe IDs; local OBJ imports never request deletion.
        if (id.Length > 64) return;
        foreach (char c in id)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return;
        client.SendCommand("RELEASE_SCENE " + id);
        nextExportAllowed = Math.Max(nextExportAllowed, Time.unscaledTime + 2f);
    }

    // Called by MoleculeRenderer for every coordinate frame from VMD. Returns true when the
    // molecule is drawn from a VMD representation, so the renderer must not draw fallback atoms;
    // false when it should draw the atoms itself (atom view, or the mesh route is not in use).
    // While paused, queue VMD geometry and keep the previous mesh until ready.
    // During playback reuse the same mesh, with no export requests or atom fallback.
    public bool OnCoordinatesUpdated()
    {
        coordinateVersion++;
        if (CoordinateOwnsDisplay) return true;
        if (playbackActive && !coordinateViewRequested)
        {
            if (drivenScene != null) drivenScene.UpdateCoordinates(client.Coordinates);
            return true;
        }
        if (!UsesVMDRepresentation) return false;
        frameRefreshPending = true;
        refreshAfter = Time.unscaledTime + 0.35f;
        return true;
    }

    // Discard the static scene and any pending operation, and return to rendering atoms
    // from the latest coordinates. Automatic exports stay off until an export is requested
    // explicitly. Also available as an Inspector context-menu action, and called on disable.
    [ContextMenu("Return to coordinate view")]
    public void ShowAtomView()
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        coordinateViewRequested = true;
        frameRefreshPending = refreshPending = false;
        CancelOperation();
        ClearPreview();
        // Keep Update from requesting the initial export again.
        initialRequested = true;
        if (molecule != null && client != null && molecule.isActiveAndEnabled)
        {
            molecule.RenderMolecule(client.Coordinates);
            FitView();
        }
    }

    // Ask the camera controller, if present, to refit the view to the molecule.
    public void FitView()
    {
        var cameraController = FindAnyObjectByType<VMDCameraController>();
        if (cameraController != null) cameraController.FitMolecule();
    }

    // Abort any outstanding export request or OBJ import; incrementing 'revision' makes
    // in-flight loads ignore their results.
    private void CancelOperation()
    {
        revision++;
        loading?.Cancel();
        loading = null;
        isLoading = false;
        requestId = null;
    }

    // Destroy the current static scene preview, if any, and clear the displayed method name.
    private void ClearPreview()
    {
        if (preview != null) { preview.SetActive(false); Destroy(preview); }
        preview = null;
        drivenScene = null;
        currentDrawingMethod = "";
    }

    // Unsubscribe from static-scene responses and restore the atom view.
    private void OnDisable()
    {
        if (client != null) client.StaticSceneResponseReceived -= Receive;
        ShowAtomView();
    }
}
