using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Desktop UI, keyboard and future XR UnityEvents share this controller.
// VDW/CPK, NewCartoon and QuickSurf share the rolling coordinate buffer and playback controls.
public class TrajectoryController : MonoBehaviour
{
    [SerializeField] private VMDClient client;
    [SerializeField, Min(0.01f)] private float stepInterval = 0.1f;
    [SerializeField, Range(2, 64)] private int prefetchFrames = 24;
    private enum Route { Pending, Coordinates, Existing, Explicit, Failed }
    private Route route = Route.Pending;
    private VMDClient subscribedClient;
    private RepresentationController representationController;
    private MoleculeRenderer molecule;
    private VMDCoordinateScene scene;
    private VMDCartoonScene cartoonScene;
    private VMDQuickSurfScene quickSurfScene;
    private VMDTrajectoryReply metadata;
    private readonly Dictionary<int, float[]> frames = new Dictionary<int, float[]>();
    private readonly List<int> eviction = new List<int>();
    private CancellationTokenSource lifetime;
    private bool busy, isPlaying, waitingForCoordinates, wasConnected;
    private int generation, currentFrame, targetFrame, syncFrame = -1, lastVmdFrame, capacity;
    private int requestedSeek = -1, pendingStep;
    private int retainedMolecule = -1, retainedFrame = -1;
    private float nextStepTime, requestTime, nextCheck;
    // Fallback reason or last error, shown on the status bar's second line.
    private string note = "", repSummary = "";
    public bool IsWaitingForCoordinates => waitingForCoordinates || (OwnsDisplay && (scene == null || !scene.HasFrame || targetFrame != currentFrame));
    public bool IsPlaying => isPlaying;
    public int CurrentFrame => currentFrame;
    public int FrameCount => metadata?.frameCount ?? 0;
    public int BufferedFrameCount => frames.Count;
    public string Status => BuildStatus();
    public bool OwnsDisplay => isActiveAndEnabled && molecule != null &&
        (route == Route.Pending || route == Route.Coordinates || route == Route.Failed);

    public void InitializeCoordinates(RepresentationController representation, MoleculeRenderer owner)
    {
        representationController = representation;
        molecule = owner;
    }
    private void OnEnable()
    {
        if (client == null) client = GetComponent<VMDClient>();
        if (client == null) client = FindAnyObjectByType<VMDClient>();
        subscribedClient = client;
        if (subscribedClient != null) subscribedClient.CoordinatesReceived += OnCoordinatesReceived;
        lifetime = new CancellationTokenSource();
        route = Route.Pending;
    }
    private void Update()
    {
        if (subscribedClient == null || !subscribedClient.IsConnected)
        {
            if (wasConnected) ResetCoordinates();
            wasConnected = false;
            isPlaying = waitingForCoordinates = false;
            note = "";
            SetRepresentationPlayback(false);
            return;
        }
        wasConnected = true;
        if (route == Route.Pending && !busy && molecule != null && subscribedClient.Coordinates.Count > 0)
        {
            Run(Open);
            return;
        }
        if (route == Route.Coordinates) { UpdateCoordinates(); return; }
        if (route == Route.Pending || route == Route.Failed) return;
        UpdateExistingPlayback();
    }
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
        // At most 64 MiB of cached positions, except the two frames required to advance.
        capacity = Math.Max(2, Math.Min(prefetchFrames, (int)Math.Min(int.MaxValue, 64L*1024*1024 / (reply.atomCount*12L))));
        capacity = Math.Min(capacity, reply.frameCount);
        metadata = reply;
        lastVmdFrame = reply.frame;
        currentFrame = -1;
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
                if (first) representationController?.SetCoordinateDisplay(true, DisplayedMethod());
                if (first) representationController?.FitView();
                if (!isPlaying) syncFrame = currentFrame;
                nextStepTime = Time.unscaledTime + stepInterval;
            }
            catch (Exception error) { Fail(error); return; }
        }
        if (isPlaying && currentFrame == targetFrame && currentFrame >= 0 && Time.unscaledTime >= nextStepTime)
        {
            if (currentFrame >= metadata.frameCount-1) Pause();
            else targetFrame = currentFrame + 1;
        }
        if (busy) return;
        if (syncFrame >= 0 && currentFrame >= 0)
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
    private void Fail(Exception error)
    {
        isPlaying = false;
        route = Route.Failed;
        note = error.Message;
        Debug.LogWarning("[TrajectoryController] " + note + " (Play to retry)", this);
    }
    public void Play()
    {
        if (!isActiveAndEnabled || subscribedClient == null || !subscribedClient.IsConnected) return;
        if (route == Route.Failed) RefreshCoordinates();
        if (route == Route.Coordinates && currentFrame == metadata.frameCount-1) targetFrame = 0;
        isPlaying = true;
        nextStepTime = Time.unscaledTime;
    }
    public void Pause()
    {
        isPlaying = false;
        if (route == Route.Coordinates && currentFrame >= 0) { targetFrame = currentFrame; syncFrame = currentFrame; }
        else if (!waitingForCoordinates) SetRepresentationPlayback(false);
    }
    public void Toggle() { if (isPlaying) Pause(); else Play(); }
    public void NextFrame() => Step(1);
    public void PreviousFrame() => Step(-1);
    public void Step(int delta)
    {
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
    public void Seek(int frame)
    {
        Pause();
        if (route == Route.Pending || route == Route.Failed) { requestedSeek = Math.Max(0, frame); return; }
        if (route == Route.Coordinates) { targetFrame = Mathf.Clamp(frame, 0, metadata.frameCount-1); syncFrame = -1; }
        else subscribedClient?.SendCommand("FRAME " + Math.Max(0, frame));
    }
    public void RefreshCoordinates()
    {
        int oldMolecule = metadata?.molecule ?? -1;
        int oldFrame = scene != null && scene.HasFrame ? currentFrame : -1;
        ResetCoordinates();
        retainedMolecule = oldMolecule; retainedFrame = oldFrame;
    }
    public void ChangeRepresentation(int index, string method)
    {
        Pause();
        RefreshCoordinates();
        Run(async (token, version) =>
        {
            await subscribedClient.RequestTrajectory("TRAJ_STYLE " + index + " " + method, token);
            if (version == generation) await Open(token, version);
        });
    }
    // Explicit OBJ/atom-view menu actions retain their meaning until a style change.
    public void UseExplicitStaticView()
    {
        Pause(); ResetCoordinates(); route = Route.Explicit;
        representationController?.SetCoordinateDisplay(false);
    }
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
        if (!waitingForCoordinates) { isPlaying = false; SetRepresentationPlayback(false); return; }
        requestTime = Time.unscaledTime;
        nextStepTime = requestTime + stepInterval;
    }
    private void OnCoordinatesReceived(IReadOnlyList<Vector3> coordinates)
    {
        waitingForCoordinates = false;
        if (!OwnsDisplay && !isPlaying) SetRepresentationPlayback(false);
    }
    private bool SetRepresentationPlayback(bool active)
    {
        if (representationController == null) representationController = FindAnyObjectByType<RepresentationController>();
        if (representationController == null) return true;
        if (active) return representationController.TryStartMeshPlayback();
        representationController.SetPlaybackActive(false);
        return true;
    }
    private void OnDisable()
    {
        if (subscribedClient != null) subscribedClient.CoordinatesReceived -= OnCoordinatesReceived;
        subscribedClient = null;
        isPlaying = waitingForCoordinates = false;
        ResetCoordinates(); lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
        representationController?.SetCoordinateDisplay(false);
        SetRepresentationPlayback(false);
    }
    // The rep the representation controls target, else the first rep in the snapshot.
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
    // "rep1 QuickSurf, rep3 CPK" in VMD rep order.
    private static string Describe(VMDTrajectoryReply reply)
    {
        var reps = new List<KeyValuePair<int, string>>();
        foreach (var rep in reply.reps) reps.Add(new KeyValuePair<int, string>(rep.id, rep.method));
        if (reply.cartoons != null) foreach (var cartoon in reply.cartoons) reps.Add(new KeyValuePair<int, string>(cartoon.id, "NewCartoon"));
        if (reply.quicksurfs != null) foreach (var surface in reply.quicksurfs) reps.Add(new KeyValuePair<int, string>(surface.id, "QuickSurf"));
        reps.Sort((a, b) => a.Key.CompareTo(b.Key));
        return string.Join(", ", reps.ConvertAll(rep => rep.Key >= 0 ? "rep" + rep.Key + " " + rep.Value : rep.Value));
    }
    // One format for every route: "[route] state  frame / last" and a detail line.
    private string BuildStatus()
    {
        if (subscribedClient == null || !subscribedClient.IsConnected)
            return "[Offline] Connecting to VMD\nRun ::vrbridge::start in VMD, then enter Play mode";
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
    private static string FrameText(int frame, int count) => frame >= 0 && count > 0 ? frame + " / " + (count-1) : "- / -";
    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;
    private static readonly Rect Controls = new Rect(12,12,480,100);
    public static bool CapturesPointer(Vector2 screenPoint) => Controls.Contains(new Vector2(screenPoint.x, Screen.height-screenPoint.y));
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
