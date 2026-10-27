// Copy into an isolated Unity project's Assets/Editor alongside the runtime scripts.
// Run: -batchmode -nographics -executeMethod CoordinatePlaybackChecks.Run
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CoordinatePlaybackChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static GameObject root;
    private static VMDClient client;
    private static TrajectoryController player;
    private static MemoryStream output;
    private static long consumed;
    private static VMDTrajectoryReply metadata;
    private static VMDTrajectoryReply sourceFrames;
    private static readonly List<string> commands = new List<string>();
    private static bool accelerate;
    private static bool changed;
    private static int remoteFrame;
    private static Exception tickError;
    private static void Assert(bool value, string name)
    {
        if (!value) throw new Exception("Failed: " + name);
        Debug.Log("PASS " + name);
    }
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);
    private static void Tick()
    {
        try
        {
            if (accelerate) typeof(TrajectoryController).GetField("nextStepTime", Private).SetValue(player, -1f);
            Invoke(player, "Update");
            string written = Encoding.UTF8.GetString(output.ToArray());
            string fresh = written.Substring((int)consumed);
            consumed = written.Length;
            foreach (string command in fresh.Split(new[]{'\n'}, StringSplitOptions.RemoveEmptyEntries)) Respond(command.Trim());
        }
        catch (Exception error) { tickError = error; }
    }
    private static void Respond(string command)
    {
        commands.Add(command);
        string[] words = command.Split(' ');
        if (!words[0].StartsWith("TRAJ_")) return;
        var reply = new VMDTrajectoryReply { requestId = words[1], ok = true, frame = remoteFrame };
        switch (words[0])
        {
            case "TRAJ_OPEN":
                reply = JsonUtility.FromJson<VMDTrajectoryReply>(JsonUtility.ToJson(metadata));
                reply.requestId = words[1]; reply.frame = remoteFrame;
                break;
            case "TRAJ_READ":
                reply.start = int.Parse(words[3]);
                reply.frames = new VMDCoordinateFrame[int.Parse(words[4])];
                for (int i = 0; i < reply.frames.Length; i++)
                {
                    float[] data = (float[])sourceFrames.frames[(reply.start+i)%4].positions.Clone();
                    reply.frames[i] = new VMDCoordinateFrame { positions = data };
                }
                if (changed) { reply.error = "Trajectory settings changed"; changed = false; }
                break;
            case "TRAJ_SYNC": remoteFrame = int.Parse(words[3]); break;
            case "TRAJ_STYLE":
                metadata = JsonUtility.FromJson<VMDTrajectoryReply>(File.ReadAllText("cpk.json"));
                metadata.frameCount = 30;
                break;
        }
        // Production envelope always begins with requestId (JsonUtility field order is identical).
        var receive = (Action<string>)typeof(VMDClient).GetField("TrajectoryResponseReceived", Private).GetValue(client);
        receive?.Invoke("TRAJJSON " + JsonUtility.ToJson(reply));
    }
    private static async Task Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (tickError != null) throw tickError;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Playback timeout: " + player.Status);
            await Task.Delay(10);
        }
    }
    private static void CheckDrawing(VMDCoordinateScene scene)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        var template = Resources.Load<Material>("VMDCoordinateInstances");
        Assert(template != null && template.enableInstancing && template.shader.isSupported, "runtime instancing material supported");
        var cameraObject = new GameObject("Render test camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(256,256,24);
        var pixels = new Texture2D(256,256,TextureFormat.RGB24,false);
        RenderTexture previous = RenderTexture.active;
        try
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            camera.orthographicSize = scene.WorldBounds.extents.magnitude * 1.1f;
            camera.transform.position = scene.WorldBounds.center - Vector3.forward*10;
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 100;
            camera.targetTexture = target;
            Invoke(scene, "LateUpdate");
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0,0,256,256),0,0); pixels.Apply();
            int red = 0, cyan = 0, magenta = 0;
            foreach (Color32 color in pixels.GetPixels32())
            {
                if (color.r > 30 && color.r > color.g*1.5f && color.r > color.b*1.5f) red++;
                if (color.b > 30 && color.g > color.r*1.5f && color.b > color.r*1.5f) cyan++;
                if (color.r > 200 && color.b > 200 && color.g < 30) magenta++;
            }
            Assert(red > 20 && cyan > 20 && magenta == 0, "GPU renders separate atom colors without missing shader");
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(cameraObject);
        }
    }
    public static async void Run()
    {
        int exit = 1;
        try
        {
            metadata = JsonUtility.FromJson<VMDTrajectoryReply>(File.ReadAllText("vdw.json"));
            sourceFrames = JsonUtility.FromJson<VMDTrajectoryReply>(File.ReadAllText("frames.json"));
            Assert(metadata.supported && sourceFrames.frames.Length == 4, "real VMD metadata and coordinate batch parse");
            metadata.frameCount = 30;
            root = new GameObject("Coordinate test");
            client = root.AddComponent<VMDClient>();
            output = new MemoryStream();
            typeof(VMDClient).GetField("writer", Private).SetValue(client, new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
            var coordinates = (List<Vector3>)typeof(VMDClient).GetField("coordinates", Private).GetValue(client);
            coordinates.Add(Vector3.zero); coordinates.Add(Vector3.right);
            var owner = root.AddComponent<MoleculeRenderer>();
            Invoke(owner, "OnEnable"); // Editor tests do not enter Play mode.
            player = root.GetComponent<TrajectoryController>();
            Invoke(player, "OnEnable");
            typeof(TrajectoryController).GetField("prefetchFrames", Private).SetValue(player, 3);
            EditorApplication.update += Tick;
            await Until(() => root.GetComponentInChildren<VMDCoordinateScene>()?.HasFrame == true);
            Assert(player.CurrentFrame == 0, "automatic first frame without export or mode selection");
            Assert(player.Status == "[Unity live] Paused  0 / 29\nrep0 VDW", "unified status for VDW: " + player.Status.Replace("\n", " | "));
            var sphereScene = root.GetComponentInChildren<VMDCoordinateScene>();
            Assert(sphereScene.AtomInstanceCount == 2 && sphereScene.BondInstanceCount == 0, "VDW topology");
            CheckDrawing(sphereScene);
            Bounds initial = sphereScene.WorldBounds;
            player.NextFrame(); await Until(() => player.CurrentFrame == 1);
            Assert(root.GetComponentInChildren<VMDCoordinateScene>() == sphereScene && sphereScene.WorldBounds.center != initial.center, "next frame reuses geometry and preserves trajectory displacement");
            player.PreviousFrame(); await Until(() => player.CurrentFrame == 0);
            player.Seek(29); await Until(() => player.CurrentFrame == 29);
            Assert(player.BufferedFrameCount <= 3, "bounded buffer across seek");
            player.Play(); await Until(() => player.CurrentFrame == 0);
            Assert(player.IsPlaying, "Play at end restarts");
            player.Pause();
            player.Seek(999); await Until(() => player.CurrentFrame == 29);
            player.Seek(-20); await Until(() => player.CurrentFrame == 0);
            Assert(!player.IsPlaying, "seek clamps and pauses");
            accelerate = true; player.Play();
            await Until(() => player.CurrentFrame >= 8);
            player.Pause(); accelerate = false;
            int paused = player.CurrentFrame;
            await Task.Delay(150);
            Assert(player.CurrentFrame == paused && !player.IsPlaying, "pause holds exact displayed frame");
            Assert(player.BufferedFrameCount <= 3, "streaming buffer remains bounded");
            player.ChangeRepresentation(0, "CPK");
            await Until(() => root.GetComponentInChildren<VMDCoordinateScene>()?.BondInstanceCount == 2 && !player.IsWaitingForCoordinates);
            Assert(root.GetComponentInChildren<VMDCoordinateScene>().AtomInstanceCount == 2, "CPK two colored half-bonds and atoms");
            Assert(player.CurrentFrame == paused, "representation switch retains the displayed frame");
            changed = true; player.Seek(25);
            await Until(() => commands.FindAll(x => x.StartsWith("TRAJ_OPEN")).Count >= 3 && !player.IsWaitingForCoordinates);
            Assert(player.OwnsDisplay, "stale snapshot reopens automatically");
            Assert(!commands.Exists(x => x.StartsWith("EXPORT_") || x.StartsWith("STEP ") || x.StartsWith("FRAME ")), "VDW/CPK playback never requests geometry export or live stepping");
            EditorApplication.update -= Tick;
            metadata.supported = false; metadata.message = "Representation Licorice uses the existing renderer";
            player.RefreshCoordinates();
            EditorApplication.update += Tick;
            await Until(() => !player.OwnsDisplay);
            Assert(root.GetComponentInChildren<VMDCoordinateScene>() == null, "unsupported representation releases coordinate display");
            Assert(player.Status.StartsWith("[VMD mesh] ") && player.Status.Contains("Representation Licorice uses the existing renderer"), "unified status names the fallback reason");
            player.enabled = false;
            Assert(player.BufferedFrameCount == 0 && !player.IsPlaying, "disable cancels and releases buffers");
            Debug.Log("COORDINATE PLAYBACK CHECKS PASSED");
            exit = 0;
        }
        catch (Exception error) { Debug.LogException(error); }
        finally
        {
            EditorApplication.update -= Tick;
            if (root != null) Object.DestroyImmediate(root);
            output?.Dispose();
            EditorApplication.Exit(exit);
        }
    }
}
