using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Phase 1: request a VMD scene export and display its OBJ geometry.
// Keyboard/XR components call these public methods; no input bindings live here.
[DisallowMultipleComponent]
public class RepresentationController : MonoBehaviour
{
    [SerializeField] private bool exportOnConnect = true;
    [SerializeField, Min(0)] private int representationIndex;
    [SerializeField] private string localObjPath = "";
    [SerializeField] private string currentDrawingMethod = "";
    private VMDClient client;
    private MoleculeRenderer molecule;
    private GameObject preview;
    private VMDStaticScene drivenScene;
    private bool warnedMissingBinding;
    private CancellationTokenSource loading;
    private string requestId;
    private float sentAt;
    private int revision;
    private bool initialRequested, isLoading;
    private string observedSignature;
    private bool statePending, refreshPending, frameRefreshPending, coordinateViewRequested;
    private int coordinateVersion, requestedCoordinateVersion;
    private float nextStatePoll, refreshAfter;
    private bool playbackActive;
    private float nextExportAllowed;
    // Extension point for Unity-initiated commands and next/previous cycling:
    // add a method here AND its default style in static_scene.tcl's
    // scene_set_representation switch; update both validation messages as well.
    // This list does NOT restrict automatic synchronization of VMD GUI changes.
    private static readonly string[] SupportedMethods = { "VDW", "CPK", "QuickSurf", "NewCartoon" };

    [Serializable] private class ExportReply
    {
        public string requestId = "", path = "", message = "";
        public string signature = "";
        public string code = "";
        public RepInfo[] reps = Array.Empty<RepInfo>();
    }
    [Serializable] private class RepInfo { public int id = -1; public string method = ""; }

    public bool HasStaticScene => preview != null;
    public bool IsBusy => requestId != null || isLoading;
    private TrajectoryController trajectory;
    private bool CoordinateOwnsDisplay => trajectory != null && trajectory.OwnsDisplay;
    // Hide the static preview only after a complete coordinate frame is available.
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
            initialRequested = false;
            ResetSynchronization();
        }
        if (preview != null) preview.SetActive(!active);
    }
    private bool UsesVMDRepresentation => isActiveAndEnabled && !CoordinateOwnsDisplay && !playbackActive && !coordinateViewRequested &&
        (exportOnConnect || initialRequested || HasStaticScene || IsBusy);
    public string CurrentDrawingMethod => currentDrawingMethod;
    // What the VMD mesh route is doing, as one short phrase for the shared status bar.
    public string Activity =>
        requestId != null ? "Exporting VMD geometry" :
        isLoading ? "Importing VMD geometry" :
        playbackActive && drivenScene != null && drivenScene.CanDeform ? "Mesh follows atoms until Pause (approximate)" :
        HasStaticScene ? (currentDrawingMethod.Length > 0 ? currentDrawingMethod + " from VMD export" : "VMD export") : "";
    public int RepresentationIndex => representationIndex;

    // Bind this controller to a VMDClient and its owning MoleculeRenderer, and (re)subscribe
    // to static-scene responses. Called by MoleculeRenderer.OnEnable.
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

    // Main loop: handle disconnects and export timeouts, trigger the initial export after the
    // first coordinates arrive, refresh settled paused frames, and poll VMD's
    // scene signature every 0.5 s while not playing. Playback sends no mesh requests.
    private void Update()
    {
        if (CoordinateOwnsDisplay) return;
        if (client == null || molecule == null) return;
        if (!client.IsConnected)
        {
            initialRequested = false;
            ResetSynchronization();
            if (IsBusy) CancelOperation();
            return;
        }
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

    // Called by trajectory playback, not by input bindings. Wait for the last coordinate
    // reply before passing false, so Pause exports the frame actually displayed.
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
            frameRefreshPending = true;
            refreshAfter = Time.unscaledTime + 0.35f;
        }
    }

    // Defer STEP until the first export and binding have finished. This also handles
    // Enter pressed immediately after entering Unity Play without exporting every frame.
    public bool TryStartMeshPlayback()
    {
        if (CoordinateOwnsDisplay) return false;
        if (playbackActive) return true;
        if (coordinateViewRequested) { SetPlaybackActive(true); return true; }
        if (IsBusy) return false;
        if (drivenScene != null && drivenScene.CanDeform)
        {
            SetPlaybackActive(true);
            return true;
        }
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
    public bool RequestStaticExport()
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        return SendExportCommand("EXPORT_SCENE");
    }

    // These wrappers can be wired directly to UnityEvents / future XR buttons.
    // To expose a new method as a button action, add a SetXxx wrapper here after
    // registering it in SupportedMethods and the VMD-side style switch.
    public void SetVDW() => SetRepresentation("VDW");
    public void SetCPK() => SetRepresentation("CPK");
    public void SetQuickSurf() => SetRepresentation("QuickSurf");
    public void SetNewCartoon() => SetRepresentation("NewCartoon");

    // Change the drawing method of the configured representation index.
    public void SetRepresentation(string method) => SetRepresentation(representationIndex, method);

    // Validate the method against SupportedMethods and send SET_REPRESENTATION for the given
    // VMD representation index; VMD applies it and replies with a new export. Returns true if sent.
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
        SetRepresentation(SupportedMethods[next]);
    }

    // Send an export-producing command tagged with a new unique request ID so its reply can be
    // matched. Refuses while disconnected or while another export/import is still in progress.
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
    // and a successful reply starts loading the exported OBJ file.
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
                if (observedSignature != null && observedSignature != state.signature)
                {
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
            if (requestId == null || reply.requestId != requestId)
            {
                if (!failed) ReleaseExport(reply.requestId);
                return;
            }
            requestId = null;
            if (failed)
            {
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
    public void LoadStaticScene(string path)
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        if (!isActiveAndEnabled || molecule == null) return;
        CancelOperation();
        initialRequested = true;
        coordinateViewRequested = false;
        LoadScene(path, "");
    }

    // Parse the OBJ on a background thread, then build the mesh hierarchy on the main thread and
    // swap it in for the current preview. Results from cancelled or superseded loads are discarded.
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
    // from the latest coordinates. Also available as an Inspector context-menu action.
    [ContextMenu("Return to coordinate view")]
    public void ShowAtomView()
    {
        if (CoordinateOwnsDisplay) trajectory.UseExplicitStaticView();
        coordinateViewRequested = true;
        frameRefreshPending = refreshPending = false;
        CancelOperation();
        ClearPreview();
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
