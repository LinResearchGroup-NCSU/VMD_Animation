using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Shared playback controller. Play, Pause, Step and Seek from every input end up here: the
// OnGUI panel below, VMDKeyboardController, VMDVRController and the XR VMDStatusPanel. It
// also provides the two-line status text and decides which display route draws the molecule.
//
// Routes (see the Route enum):
// - Coordinate route ("Unity live"): TRAJ_OPEN returns a snapshot of the visible reps. This
//   component reads coordinates in small batches (TRAJ_READ) into a rolling buffer and passes
//   each displayed frame to Unity's own renderers: VMDCoordinateScene (VDW, CPK), VMDCartoonScene
//   (NewCartoon) and VMDQuickSurfScene (QuickSurf). Reading does not change the frame VMD
//   displays; after a pause, step or seek the displayed frame is sent to VMD (TRAJ_SYNC).
// - VMD mesh route: for scenes the bridge reports as unsupported or that fail QuickSurf's
//   graphics support check. RepresentationController shows VMD's exported mesh, and playback
//   sends STEP 1 to VMD once per step interval.
//
// Collaborators:
// - VMDClient: sends the commands. RequestTrajectory matches each TRAJ_* reply by request ID.
// - RepresentationController: finds or adds this component on the VMDClient's object and
//   calls InitializeCoordinates. It reads OwnsDisplay and is told when the coordinate route
//   takes over or returns the display (SetCoordinateDisplay). It calls
//   ChangeRepresentation(s) for drawing-method changes, RefreshCoordinates when VMD's scene
//   changed, and UseExplicitStaticView for an explicit export, local OBJ or atom view.
// - VMDCameraController: calls CapturesPointer so mouse input over the OnGUI panel is ignored.
public class TrajectoryController : MonoBehaviour
{
    // When not assigned, OnEnable looks on this object and then anywhere in the scene.
    [SerializeField] private VMDClient client;
    // Seconds between frames during playback, on both routes.
    [SerializeField, Min(0.01f)] private float stepInterval = 0.1f;
    // Largest number of frames kept in the rolling coordinate buffer; Open may lower it (capacity).
    [SerializeField, Range(2, 64)] private int prefetchFrames = 24;
    // Which route draws the molecule and handles playback:
    // - Pending: not decided yet. Update sends TRAJ_OPEN once VMD is connected and
    //   its first coordinates have arrived. Every reset returns to this state.
    // - Coordinates: the coordinate route ("Unity live") is active.
    // - Existing: RepresentationController draws the VMD mesh after the live route is ruled out.
    // - Explicit: the user asked for an export, a local OBJ or the atom view
    //   (UseExplicitStaticView). Handled like Existing, with the status label "Static view".
    // - Failed: a request or a renderer threw. Updates stop until Play, a
    //   drawing-method change or a reconnect opens the trajectory again.
    private enum Route { Pending, Coordinates, Existing, Explicit, Failed }
    private Route route = Route.Pending;
    // The client whose CoordinatesReceived event this component handles; null while disabled.
    private VMDClient subscribedClient;
    // Set by InitializeCoordinates. 'molecule' is the parent of the
    // coordinate route's renderers and supplies the coordinate scale.
    private RepresentationController representationController;
    private MoleculeRenderer molecule;
    // Renderers of the coordinate route, all on one child object of the molecule. 'scene' exists
    // whenever the route is Coordinates; the other two only when the snapshot has such reps.
    private VMDCoordinateScene scene;
    private VMDCartoonScene cartoonScene;
    private VMDQuickSurfScene quickSurfScene;
    // TRAJ_OPEN reply of the current snapshot (token, atom and
    // frame counts, reps); null when there is no snapshot.
    private VMDTrajectoryReply metadata;
    // Rolling buffer: frame index to positions (x, y, z per atom), for the 'capacity' frames from
    // targetFrame onward. 'eviction' is a scratch list for removing frames that left that window.
    private readonly Dictionary<int, float[]> frames = new Dictionary<int, float[]>();
    private readonly List<int> eviction = new List<int>();
    // Cancels requests in flight when the trajectory is reset or this component is disabled.
    private CancellationTokenSource lifetime;
    // busy: a request started by Run is in flight; only one runs at a time.
    // waitingForCoordinates: the mesh route sent STEP 1 and awaits VMD's coordinates.
    // wasConnected: VMD was connected in the previous Update, to notice a disconnect.
    private bool busy, isPlaying, waitingForCoordinates, wasConnected;
    // generation: incremented by every reset. A request remembers the value
    //   it started with and drops its result when the value has changed.
    // currentFrame: frame the coordinate route displays, -1 before the first one.
    // targetFrame: frame it should display; shown as soon as it is in the buffer.
    // syncFrame: frame to send to VMD with TRAJ_SYNC, -1 when none is due.
    // lastVmdFrame: VMD's displayed frame, as last reported by VMD or set by a TRAJ_SYNC.
    // capacity: number of frames the buffer may hold.
    private int generation, currentFrame, targetFrame, syncFrame = -1, lastVmdFrame, capacity;
    // A seek (absolute frame, -1 for none) and steps (relative) requested while
    // the route was Pending or Failed. Open applies them to the starting frame.
    private int requestedSeek = -1, pendingStep;
    // VMD redraws on every TRAJ_SYNC, which blocks further reads; while the user keeps stepping,
    // sync only once the displayed frame has settled for this long.
    private const float SyncDelay = 0.3f;
    // Earliest time at which syncFrame may be sent.
    private float syncNotBefore;
    // Molecule ID and displayed frame saved by RefreshCoordinates, so that reopening
    // the same molecule continues at that frame; -1 when nothing is retained.
    private int retainedMolecule = -1, retainedFrame = -1;
    // nextStepTime: earliest time of the next playback step.
    // requestTime: when the mesh route's STEP 1 was sent, for its 10 s timeout.
    // nextCheck: earliest time of the next TRAJ_CHECK.
    private float nextStepTime, requestTime, nextCheck;
    // note: reason for using the mesh route, or the last error.
    // repSummary: the reps the coordinate route draws, e.g. "rep1 QuickSurf, rep3 CPK".
    // Both appear on the second line of the status text.
    private string note = "", repSummary = "";

    // True while the display is not up to date: the mesh route awaits VMD's coordinates,
    // or the coordinate route owns the display but does not show targetFrame yet.
    public bool IsWaitingForCoordinates => waitingForCoordinates || (OwnsDisplay && (scene == null || !scene.HasFrame || targetFrame != currentFrame));
    public bool IsPlaying => isPlaying;
    public bool IsConnected => subscribedClient != null && subscribedClient.IsConnected;
    // Frame the coordinate route displays, -1 before the first. Not updated on the mesh route.
    public int CurrentFrame => currentFrame;
    // Frames in the current snapshot; 0 without one, which includes the mesh route.
    public int FrameCount => metadata?.frameCount ?? 0;
    public int BufferedFrameCount => frames.Count;
    // Two-line status text shown by the OnGUI panel and VMDStatusPanel; see BuildStatus.
    public string Status => BuildStatus();
    // The coordinate route is drawing, is still being prepared, or has failed. While this is
    // true RepresentationController leaves the display alone (its CoordinateOwnsDisplay).
    public bool OwnsDisplay => isActiveAndEnabled && molecule != null &&
        (route == Route.Pending || route == Route.Coordinates || route == Route.Failed);

    // Called by RepresentationController.Initialize with itself and
    // the MoleculeRenderer. No trajectory is opened before this call.
    public void InitializeCoordinates(RepresentationController representation, MoleculeRenderer owner)
    {
        representationController = representation;
        molecule = owner;
    }

    // Find the VMDClient, subscribe to its coordinate frames and start with an undecided route.
    private void OnEnable()
    {
        if (client == null) client = GetComponent<VMDClient>();
        if (client == null) client = FindAnyObjectByType<VMDClient>();
        subscribedClient = client;
        if (subscribedClient != null) subscribedClient.CoordinatesReceived += OnCoordinatesReceived;
        lifetime = new CancellationTokenSource();
        route = Route.Pending;
    }

    // Per-frame dispatch by connection state and route.
    private void Update()
    {
        if (subscribedClient == null || !subscribedClient.IsConnected)
        {
            // Disconnected: drop the snapshot once and stop playback until VMD is connected again.
            if (wasConnected) ResetCoordinates();
            wasConnected = false;
            isPlaying = waitingForCoordinates = false;
            note = "";
            SetRepresentationPlayback(false);
            return;
        }
        wasConnected = true;
        // Open the trajectory after VMD's first coordinates arrive, when no request is in flight.
        if (route == Route.Pending && !busy && molecule != null && subscribedClient.Coordinates.Count > 0)
        {
            Run(Open);
            return;
        }
        if (route == Route.Coordinates) { UpdateCoordinates(); return; }
        // Pending (waiting or opening) or Failed: nothing to update. The rest is the mesh route.
        if (route == Route.Pending || route == Route.Failed) return;
        UpdateExistingPlayback();
    }

    // Send TRAJ_OPEN and choose the route from the reply. Run from Update while
    // the route is Pending, and from ChangeRepresentations after the style change.
    // - Unsupported scene or QuickSurf fails VMDQuickSurfScene.IsSupported: route Existing.
    //   The reason goes to 'note' and RepresentationController gets the display back.
    // - Supported: size the buffer, pick the starting frame, create the renderers under the
    //   molecule and switch to route Coordinates. UpdateCoordinates then shows the first frame.
    // Invalid metadata throws, which Run turns into route Failed. 'version' is the
    // generation the request started with; a reply that arrives after a reset is dropped.
    private async Task Open(CancellationToken token, int version)
    {
        note = "";
        var reply = await subscribedClient.RequestTrajectory("TRAJ_OPEN", token);
        if (version != generation) return;
        bool hasCartoons = reply.cartoons != null && reply.cartoons.Length > 0;
        bool hasSurfaces = reply.quicksurfs != null && reply.quicksurfs.Length > 0;
        if (!reply.supported || (hasSurfaces && !VMDQuickSurfScene.IsSupported))
        {
            route = Route.Existing;
            note = reply.supported ? "QuickSurf needs GPU compute shaders; using the VMD mesh" : reply.message ?? "";
            ReleaseScene();
            representationController?.SetCoordinateDisplay(false);
            return;
        }
        if (reply.atomCount < 1 || reply.frameCount < 1 || reply.reps == null ||
            (reply.reps.Length == 0 && !hasCartoons && !hasSurfaces) || string.IsNullOrEmpty(reply.token))
            throw new FormatException("Invalid trajectory metadata.");
        // Buffer size in frames: at most prefetchFrames and 64 MiB of positions (12 bytes per
        // atom), at least the two frames needed to advance, and no more than the trajectory has.
        capacity = Math.Max(2, Math.Min(prefetchFrames, (int)Math.Min(int.MaxValue, 64L*1024*1024 / (reply.atomCount*12L))));
        capacity = Math.Min(capacity, reply.frameCount);
        metadata = reply;
        lastVmdFrame = reply.frame;
        currentFrame = -1;
        // Start at the frame retained by RefreshCoordinates if this is the same molecule, otherwise
        // at the frame VMD displays. A seek requested meanwhile replaces it; steps are added to it.
        int startFrame = retainedMolecule == reply.molecule && retainedFrame >= 0 ? retainedFrame : reply.frame;
        targetFrame = Mathf.Clamp(requestedSeek >= 0 ? requestedSeek : startFrame + pendingStep, 0, reply.frameCount-1);
        retainedMolecule = retainedFrame = -1;
        requestedSeek = -1; pendingStep = 0;
        frames.Clear();
        ReleaseScene();
        var root = new GameObject("VMD coordinate scene");
        root.transform.SetParent(molecule.transform, false);
        scene = root.AddComponent<VMDCoordinateScene>();
        scene.Initialize(reply, molecule.CoordinateScale);
        if (hasCartoons)
        {
            cartoonScene = root.AddComponent<VMDCartoonScene>();
            cartoonScene.Initialize(reply, molecule.CoordinateScale);
        }
        if (hasSurfaces)
        {
            quickSurfScene = root.AddComponent<VMDQuickSurfScene>();
            quickSurfScene.Initialize(reply, molecule.CoordinateScale);
        }
        repSummary = Describe(reply);
        route = Route.Coordinates;
        nextCheck = Time.unscaledTime + 0.75f;
    }

    // Coordinate route, every Update:
    // 1. Display targetFrame as soon as it is in the buffer.
    // 2. While playing, advance targetFrame by one frame per stepInterval; pause at the last frame.
    // 3. Start at most one request, in this order: a TRAJ_SYNC that is due, a TRAJ_READ
    //    for the first frames missing from the buffer window, or the periodic TRAJ_CHECK.
    private void UpdateCoordinates()
    {
        if (targetFrame != currentFrame && frames.TryGetValue(targetFrame, out var frame))
        {
            try
            {
                bool first = !scene.HasFrame;
                scene.Show(frame);
                cartoonScene?.Show(frame);
                quickSurfScene?.Show(frame);
                currentFrame = targetFrame;
                // First frame: take over the display from the mesh route and fit the view.
                if (first) representationController?.SetCoordinateDisplay(true, DisplayedMethod());
                if (first) representationController?.FitView();
                // While paused, sync VMD to this frame once it has settled (see SyncDelay).
                if (!isPlaying) { syncFrame = currentFrame; syncNotBefore = Time.unscaledTime + SyncDelay; }
                nextStepTime = Time.unscaledTime + stepInterval;
            }
            catch (Exception error) { Fail(error); return; }
        }
        if (isPlaying && currentFrame == targetFrame && currentFrame >= 0 && Time.unscaledTime >= nextStepTime)
        {
            if (currentFrame >= metadata.frameCount-1) Pause();
            else targetFrame = currentFrame + 1;
        }
        // The requests below run one at a time.
        if (busy) return;
        if (syncFrame >= 0 && currentFrame >= 0 && Time.unscaledTime >= syncNotBefore)
        {
            int frameToSync = syncFrame;
            syncFrame = -1;
            Run(async (token, version) =>
            {
                await subscribedClient.RequestTrajectory("TRAJ_SYNC " + metadata.token + " " + frameToSync, token);
                if (version == generation) lastVmdFrame = frameToSync;
            });
            return;
        }
        // Buffer window: 'capacity' frames starting at targetFrame. Drop the
        // frames outside it, then request the first ones that are missing.
        int end = Math.Min(metadata.frameCount, targetFrame + capacity);
        eviction.Clear();
        foreach (int index in frames.Keys)
            if (index < targetFrame || index >= end) eviction.Add(index);
        foreach (int index in eviction) frames.Remove(index);
        int missing = targetFrame;
        while (missing < end && frames.ContainsKey(missing)) missing++;
        if (missing < end)
        {
            int start = missing;
            // At most 4 frames and about one million floats per request, which is also the
            // bridge's limit (trajectory.tcl); larger molecules are read one frame at a time.
            int count = Math.Min(end-start, Math.Max(1, Math.Min(4, 1000000 / (metadata.atomCount*3))));
            Run(async (token, version) =>
            {
                var reply = await subscribedClient.RequestTrajectory("TRAJ_READ " + metadata.token + " " + start + " " + count, token);
                if (version != generation) return;
                if (reply.start != start || reply.frames == null || reply.frames.Length != count) throw new FormatException("Invalid coordinate batch.");
                for (int i = 0; i < reply.frames.Length; i++)
                {
                    float[] positions = reply.frames[i]?.positions;
                    if (positions == null || positions.Length != metadata.atomCount*3) throw new FormatException("Invalid frame size.");
                    // A new seek may have overtaken this response.
                    if (start+i >= targetFrame && start+i < targetFrame+capacity) frames[start+i] = positions;
                }
            });
        }
        else if (Time.unscaledTime >= nextCheck)
        {
            // The window is full. Every 0.75 s TRAJ_CHECK verifies that the
            // snapshot is still valid (if not, Run opens the trajectory again)
            // and returns VMD's frame. If that frame was changed in VMD while
            // Unity is paused and has no sync of its own due, Unity follows it.
            nextCheck = Time.unscaledTime + 0.75f;
            Run(async (token, version) =>
            {
                var reply = await subscribedClient.RequestTrajectory("TRAJ_CHECK " + metadata.token, token);
                if (version == generation && !isPlaying && reply.frame != lastVmdFrame && syncFrame < 0)
                {
                    lastVmdFrame = reply.frame;
                    targetFrame = Mathf.Clamp(reply.frame, 0, metadata.frameCount-1);
                }
            });
        }
    }

    // Start one asynchronous request and stay 'busy' until it ends. Cancellation is
    // silent. The bridge errors "Trajectory settings changed" and "Trajectory snapshot
    // expired" (trajectory.tcl) open the trajectory again; any other error ends in route
    // Failed. Errors and completion of a request from before the last reset are ignored.
    private async void Run(Func<CancellationToken, int, Task> action)
    {
        int version = generation;
        busy = true;
        try { await action(lifetime.Token, version); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version == generation)
            {
                if (error.Message == "Trajectory settings changed" || error.Message == "Trajectory snapshot expired") RefreshCoordinates();
                else Fail(error);
            }
        }
        finally { if (version == generation) busy = false; }
    }

    // Stop playback and enter route Failed; the error goes to the status text. Play retries.
    private void Fail(Exception error)
    {
        isPlaying = false;
        route = Route.Failed;
        note = error.Message;
        Debug.LogWarning("[TrajectoryController] " + note + " (Play to retry)", this);
    }

    // Start playback. Does nothing while disabled or disconnected. A Failed route is opened
    // again first, and the coordinate route restarts from frame 0 when it is on the last frame.
    public void Play()
    {
        if (!isActiveAndEnabled || subscribedClient == null || !subscribedClient.IsConnected) return;
        if (route == Route.Failed) RefreshCoordinates();
        if (route == Route.Coordinates && currentFrame == metadata.frameCount-1) targetFrame = 0;
        isPlaying = true;
        nextStepTime = Time.unscaledTime;
    }

    // Stop playback. The coordinate route stays on the displayed frame, giving up a frame it
    // was still heading for, and schedules a TRAJ_SYNC so that VMD shows the same frame. The
    // mesh route ends mesh playback, after which RepresentationController exports the paused
    // frame; while a STEP reply is still awaited, OnCoordinatesReceived ends it instead.
    public void Pause()
    {
        isPlaying = false;
        if (route == Route.Coordinates && currentFrame >= 0) { targetFrame = currentFrame; syncFrame = currentFrame; syncNotBefore = Time.unscaledTime + SyncDelay; }
        else if (!waitingForCoordinates) SetRepresentationPlayback(false);
    }

    // Entry points for the Play/Pause, Next and Previous buttons and keys.
    public void Toggle() { if (isPlaying) Pause(); else Play(); }
    public void NextFrame() => Step(1);
    public void PreviousFrame() => Step(-1);

    // Move by 'delta' frames (negative steps back) and pause. On the coordinate route the
    // step is relative to targetFrame, so quick repeated steps add up before the frame is
    // displayed. While the route is Pending or Failed the step is remembered for Open. On
    // the mesh route VMD performs the step (STEP) and clamps it to the trajectory.
    public void Step(int delta)
    {
        // Read the target before Pause resets targetFrame to the displayed frame.
        int target = targetFrame + delta;
        Pause();
        if (route == Route.Pending || route == Route.Failed)
        {
            if (requestedSeek >= 0) requestedSeek = Math.Max(0, requestedSeek + delta); else pendingStep += delta;
            return;
        }
        if (route == Route.Coordinates) Seek(target);
        else subscribedClient?.SendCommand("STEP " + delta);
    }

    // Jump to an absolute frame and pause. The coordinate route clamps it to the
    // snapshot's frames and cancels the sync that Pause scheduled, because a new one
    // follows once the frame is displayed. While the route is Pending or Failed the
    // frame is remembered for Open. On the mesh route VMD performs the seek (FRAME).
    public void Seek(int frame)
    {
        Pause();
        if (route == Route.Pending || route == Route.Failed) { requestedSeek = Math.Max(0, frame); return; }
        if (route == Route.Coordinates) { targetFrame = Mathf.Clamp(frame, 0, metadata.frameCount-1); syncFrame = -1; }
        else subscribedClient?.SendCommand("FRAME " + Math.Max(0, frame));
    }

    // Drop the snapshot and let Update open the trajectory again, continuing at the displayed
    // frame if VMD still serves the same molecule. Called when the snapshot is out of date:
    // by RepresentationController when VMD's scene signature changes, by Run when the bridge
    // rejects the snapshot, by Play after a failure, and before a drawing-method change.
    public void RefreshCoordinates()
    {
        int oldMolecule = metadata?.molecule ?? -1;
        int oldFrame = scene != null && scene.HasFrame ? currentFrame : -1;
        ResetCoordinates();
        // ResetCoordinates clears the retained values, so they are set afterwards.
        retainedMolecule = oldMolecule; retainedFrame = oldFrame;
    }

    // Change the drawing method of one VMD rep; see ChangeRepresentations.
    // Called by RepresentationController.SetRepresentation.
    public void ChangeRepresentation(int index, string method) => ChangeRepresentations(new[] { index }, method);

    // Apply one drawing method to several VMD reps, e.g. every visible one, then reload once:
    // pause, drop the snapshot, send one TRAJ_STYLE per rep and open the trajectory again, which
    // also chooses the route for the new method. Called by RepresentationController.Cycle.
    public void ChangeRepresentations(IReadOnlyList<int> indices, string method)
    {
        Pause();
        RefreshCoordinates();
        // Run sets 'busy' at once, so Update does not open the Pending route in the meantime.
        Run(async (token, version) =>
        {
            foreach (int index in indices)
            {
                await subscribedClient.RequestTrajectory("TRAJ_STYLE " + index + " " + method, token);
                if (version != generation) return;
            }
            await Open(token, version);
        });
    }

    // VMD rep indices drawn by the coordinate route, in VMD order; empty until its metadata is
    // loaded. RepresentationController.Cycle uses it to switch every displayed rep together.
    public List<int> DisplayedRepresentationIds()
    {
        var ids = new List<int>();
        if (metadata == null) return ids;
        foreach (var rep in metadata.reps) ids.Add(rep.id);
        if (metadata.cartoons != null) foreach (var cartoon in metadata.cartoons) ids.Add(cartoon.id);
        if (metadata.quicksurfs != null) foreach (var surface in metadata.quicksurfs) ids.Add(surface.id);
        ids.Sort();
        return ids;
    }

    // Called by RepresentationController when the user explicitly asks for a VMD export, a
    // local OBJ or the atom view while the coordinate route owns the display. Stops the
    // coordinate route and leaves the display to RepresentationController (route Explicit)
    // until the trajectory is opened again, for example by a drawing-method change.
    public void UseExplicitStaticView()
    {
        Pause(); ResetCoordinates(); route = Route.Explicit;
        representationController?.SetCoordinateDisplay(false);
    }

    // Invalidate everything tied to the current snapshot: requests in flight (new generation,
    // cancelled token), the buffer, the metadata, remembered seeks and steps, and the renderers.
    // The route returns to Pending, so Update opens the trajectory again when it can.
    private void ResetCoordinates()
    {
        generation++;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = new CancellationTokenSource();
        busy = false;
        frames.Clear(); metadata = null;
        syncFrame = -1; requestedSeek = -1; pendingStep = 0;
        retainedMolecule = retainedFrame = -1;
        route = Route.Pending;
        ReleaseScene();
    }

    // Destroy the coordinate route's renderers. The cartoon and QuickSurf
    // components release their meshes and GPU buffers at once through Clear.
    private void ReleaseScene()
    {
        cartoonScene?.Clear();
        cartoonScene = null; // Owned by the same root as the coordinate scene.
        quickSurfScene?.Clear();
        quickSurfScene = null;
        if (scene == null) return;
        scene.gameObject.SetActive(false);
        if (Application.isPlaying) Destroy(scene.gameObject); else DestroyImmediate(scene.gameObject);
        scene = null;
    }

    // Playback on the mesh route (Existing and Explicit): one STEP 1 per stepInterval,
    // and the next one only after VMD's coordinates for the previous step have
    // arrived. A reply that takes longer than 10 s pauses playback. Stepping starts
    // only once RepresentationController allows mesh playback (TryStartMeshPlayback).
    private void UpdateExistingPlayback()
    {
        if (waitingForCoordinates)
        {
            if (Time.unscaledTime-requestTime > 10f)
            {
                isPlaying = waitingForCoordinates = false;
                SetRepresentationPlayback(false);
                note = "Coordinate response timed out; paused";
            }
            return;
        }
        if (!isPlaying || Time.unscaledTime < nextStepTime || !SetRepresentationPlayback(true)) return;
        waitingForCoordinates = subscribedClient.SendCommand("STEP 1");
        // SendCommand returned false: the command was not sent, so stop playing.
        if (!waitingForCoordinates) { isPlaying = false; SetRepresentationPlayback(false); return; }
        requestTime = Time.unscaledTime;
        nextStepTime = requestTime + stepInterval;
    }

    // A coordinate frame arrived from VMD; on the mesh route this answers a STEP.
    // If playback was paused while waiting, mesh playback ends here (see Pause).
    private void OnCoordinatesReceived(IReadOnlyList<Vector3> coordinates)
    {
        waitingForCoordinates = false;
        if (!OwnsDisplay && !isPlaying) SetRepresentationPlayback(false);
    }

    // Start (true) or stop (false) RepresentationController's mesh playback. Returns
    // whether frames may be stepped now: TryStartMeshPlayback's answer when
    // starting, otherwise true. Also true when there is no RepresentationController.
    private bool SetRepresentationPlayback(bool active)
    {
        if (representationController == null) representationController = FindAnyObjectByType<RepresentationController>();
        if (representationController == null) return true;
        if (active) return representationController.TryStartMeshPlayback();
        representationController.SetPlaybackActive(false);
        return true;
    }

    // Unsubscribe, cancel requests, destroy the renderers and give the display to the mesh route.
    private void OnDisable()
    {
        if (subscribedClient != null) subscribedClient.CoordinatesReceived -= OnCoordinatesReceived;
        subscribedClient = null;
        isPlaying = waitingForCoordinates = false;
        ResetCoordinates(); lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        representationController?.SetCoordinateDisplay(false);
        SetRepresentationPlayback(false);
    }

    // Drawing method reported to RepresentationController when the coordinate route
    // takes over: that of the rep its controls target (RepresentationIndex) if the
    // snapshot draws that rep, otherwise that of the first rep in the snapshot.
    private string DisplayedMethod()
    {
        int wanted = representationController != null ? representationController.RepresentationIndex : 0;
        string first = null;
        foreach (var rep in metadata.reps) { if (rep.id == wanted) return rep.method; first ??= rep.method; }
        if (metadata.cartoons != null)
            foreach (var cartoon in metadata.cartoons) { if (cartoon.id == wanted) return "NewCartoon"; first ??= "NewCartoon"; }
        if (metadata.quicksurfs != null)
            foreach (var surface in metadata.quicksurfs) { if (surface.id == wanted) return "QuickSurf"; first ??= "QuickSurf"; }
        return first ?? "";
    }

    // Rep summary for the status text, e.g. "rep1 QuickSurf, rep3 CPK", in
    // VMD rep order. A rep without an id (-1) is listed by its method only.
    private static string Describe(VMDTrajectoryReply reply)
    {
        var reps = new List<KeyValuePair<int, string>>();
        foreach (var rep in reply.reps) reps.Add(new KeyValuePair<int, string>(rep.id, rep.method));
        if (reply.cartoons != null) foreach (var cartoon in reply.cartoons) reps.Add(new KeyValuePair<int, string>(cartoon.id, "NewCartoon"));
        if (reply.quicksurfs != null) foreach (var surface in reply.quicksurfs) reps.Add(new KeyValuePair<int, string>(surface.id, "QuickSurf"));
        reps.Sort((a, b) => a.Key.CompareTo(b.Key));
        return string.Join(", ", reps.ConvertAll(rep => rep.Key >= 0 ? "rep" + rep.Key + " " + rep.Value : rep.Value));
    }

    // One format for every route: "[route] state  frame / last" and a detail
    // line. The route labels are Offline, Unity live (Coordinates), VMD mesh
    // (Existing), Static view (Explicit), Error (Failed) and Preparing (Pending).
    private string BuildStatus()
    {
        if (subscribedClient != null && !subscribedClient.IsConnected && !string.IsNullOrEmpty(subscribedClient.ConnectionError))
            return "[Offline] Not connected\n" + subscribedClient.ConnectionError;
        if (subscribedClient == null || !subscribedClient.IsConnected)
            return "[Offline] Connecting to VMD\nRun source start_vr.tcl in VMD; Unity connects automatically";
        string label, state, frame, detail;
        switch (route)
        {
            case Route.Coordinates:
                bool buffering = currentFrame < 0 || targetFrame != currentFrame;
                label = "Unity live";
                state = buffering ? "Buffering" : isPlaying ? "Playing" : "Paused";
                frame = FrameText(buffering ? targetFrame : currentFrame, metadata?.frameCount ?? 0);
                detail = Join(repSummary, quickSurfScene != null ? quickSurfScene.Note : "");
                break;
            case Route.Existing:
            case Route.Explicit:
                label = route == Route.Existing ? "VMD mesh" : "Static view";
                state = waitingForCoordinates ? "Stepping" : isPlaying ? "Playing" : "Paused";
                frame = FrameText(subscribedClient.VmdFrame, subscribedClient.VmdFrameCount);
                detail = Join(note, representationController != null ? representationController.Activity : "");
                break;
            case Route.Failed:
                label = "Error";
                state = "Stopped";
                frame = FrameText(currentFrame, metadata?.frameCount ?? 0);
                detail = Join(note, "press Play to retry");
                break;
            default:
                label = "Preparing";
                state = "Reading VMD representations";
                frame = FrameText(-1, 0);
                detail = note;
                break;
        }
        return "[" + label + "] " + state + "  " + frame + "\n" + detail;
    }

    // "frame / last frame", or "- / -" when either is unknown.
    private static string FrameText(int frame, int count) => frame >= 0 && count > 0 ? frame + " / " + (count-1) : "- / -";
    // Join two status phrases with a separator, skipping an empty one.
    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    // Screen rectangle of the OnGUI panel, in GUI coordinates (origin at the top left).
    private static readonly Rect Controls = new Rect(12,12,480,100);
    // True when a pointer position with its origin at the bottom left, as VMDCameraController
    // reads it from the mouse, lies on the panel. The camera controller then ignores the mouse.
    public static bool CapturesPointer(Vector2 screenPoint) => Controls.Contains(new Vector2(screenPoint.x, Screen.height-screenPoint.y));

    // Desktop panel: Play/Pause, Previous and Next buttons above the two status lines. The
    // buttons are disabled while VMD is disconnected. VMDStatusPanel copies this layout for XR.
    private void OnGUI()
    {
        GUI.Box(Controls, "");
        bool previous = GUI.enabled;
        GUI.enabled = subscribedClient != null && subscribedClient.IsConnected;
        if (GUI.Button(new Rect(20,20,75,28), isPlaying ? "Pause" : "Play")) Toggle();
        if (GUI.Button(new Rect(103,20,90,28), "Previous")) PreviousFrame();
        if (GUI.Button(new Rect(201,20,90,28), "Next")) NextFrame();
        GUI.enabled = previous;
        GUI.Label(new Rect(20,55,460,50), Status);
    }
}
