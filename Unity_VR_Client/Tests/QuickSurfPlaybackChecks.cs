// Uses real VMD QuickSurf metadata/frame fixtures from _test_trajectory.tcl in an isolated Editor project.
// Run: -batchmode -force-d3d11 -executeMethod QuickSurfPlaybackChecks.Run (GPU path),
// or -nographics to check the fallback to the VMD mesh route.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public static class QuickSurfPlaybackChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static GameObject root;
    private static VMDClient client;
    private static TrajectoryController player;
    private static VMDTrajectoryReply metadata, frames;
    private static MemoryStream wire;
    private static int consumed, remoteFrame;
    private static bool accelerate;
    private static Exception tickError;
    private static readonly List<string> commands = new List<string>();
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Failed: " + name);
        Debug.Log("PASS " + name);
    }
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method,Private).Invoke(target,null);
    private static VMDTrajectoryReply Read(string path) => JsonUtility.FromJson<VMDTrajectoryReply>(File.ReadAllText(path));
    private static async Task Until(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (tickError != null) throw tickError;
            if (DateTime.UtcNow > end) throw new TimeoutException("QuickSurf timeout: " + player?.Status);
            await Task.Delay(10);
        }
    }
    private static void Tick()
    {
        try
        {
            if (accelerate) typeof(TrajectoryController).GetField("nextStepTime",Private).SetValue(player,-1f);
            Invoke(player,"Update");
            string text = Encoding.UTF8.GetString(wire.ToArray());
            string incoming = text.Substring(consumed); consumed = text.Length;
            foreach (string line in incoming.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                commands.Add(line);
                string[] words = line.Trim().Split(' ');
                if (!words[0].StartsWith("TRAJ_")) continue;
                var reply = new VMDTrajectoryReply { requestId = words[1], ok = true, frame = remoteFrame };
                switch (words[0])
                {
                    case "TRAJ_OPEN":
                        reply = JsonUtility.FromJson<VMDTrajectoryReply>(JsonUtility.ToJson(metadata));
                        reply.requestId = words[1]; reply.frame = remoteFrame; break;
                    case "TRAJ_READ":
                        reply.start = int.Parse(words[3]);
                        reply.frames = new VMDCoordinateFrame[int.Parse(words[4])];
                        for (int i = 0; i < reply.frames.Length; i++)
                            reply.frames[i] = new VMDCoordinateFrame { positions = (float[])frames.frames[(reply.start+i)%frames.frames.Length].positions.Clone() };
                        break;
                    case "TRAJ_SYNC": remoteFrame = int.Parse(words[3]); break;
                }
                var receive = (Action<string>)typeof(VMDClient).GetField("TrajectoryResponseReceived",Private).GetValue(client);
                receive?.Invoke("TRAJJSON " + JsonUtility.ToJson(reply));
            }
        }
        catch (Exception error) { tickError = error; }
    }
    private static async Task Built(VMDQuickSurfScene surface)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (surface.IsBuilding)
        {
            AsyncGPUReadback.WaitAllRequests();
            if (DateTime.UtcNow > end) throw new TimeoutException("QuickSurf GPU readback did not complete");
            await Task.Delay(10);
        }
    }
    private static async Task GeometryChecks()
    {
        var test = new GameObject("QuickSurf geometry fixture");
        var surface = test.AddComponent<VMDQuickSurfScene>();
        try
        {
            surface.Initialize(metadata,1);
            surface.Show(frames.frames[0].positions);
            await Built(surface);
            int first = surface.TriangleCount;
            Bounds start = surface.WorldBounds;
            Check(surface.SurfaceCount == 1 && first > 50,"GPU builds a QuickSurf around the atoms (" + first + " triangles)");
            Check(start.size.x > 3 && start.size.x < 20,"surface bounds follow the Gaussian atom size");
            Draw(test, surface);
            surface.Show(frames.frames[3].positions);
            await Built(surface);
            Check(surface.TriangleCount > 50 && surface.WorldBounds.center != start.center,"next frame rebuilds the surface at the moved atoms");
            surface.Show(frames.frames[0].positions);
            await Built(surface);
            Check(surface.TriangleCount == first && surface.WorldBounds.center == start.center,"seeking back rebuilds identical geometry without accumulated deformation");
            for (int i = 0; i < 3; i++) surface.Show(frames.frames[i].positions);
            await Built(surface);
            Check(surface.WorldBounds.center != start.center,"frames queued during a build resolve to the newest frame");
        }
        finally { surface.Clear(); Object.DestroyImmediate(test); }
        Check(surface.SurfaceCount == 0,"teardown releases GPU buffers");
    }
    private static void Draw(GameObject fixture, VMDQuickSurfScene surface)
    {
        Bounds bounds = surface.WorldBounds;
        var cameraObject = new GameObject("QuickSurf render camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(256,256,24);
        var image = new Texture2D(256,256,TextureFormat.RGB24,false);
        var previous = RenderTexture.active;
        try
        {
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.orthographic = true; camera.orthographicSize = bounds.extents.magnitude*1.1f;
            camera.transform.position = bounds.center-Vector3.forward*(bounds.extents.magnitude*3+1);
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 1000; camera.targetTexture = target;
            Invoke(surface,"LateUpdate");
            camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0,0,256,256),0,0); image.Apply();
            File.WriteAllBytes("quicksurf-preview.png",image.EncodeToPNG());
            var pixels = image.GetPixels32();
            int lit = pixels.Count(c=>c.r > 40 || c.g > 40 || c.b > 40);
            Debug.Log("QuickSurf pixels: " + lit);
            Check(lit > 500,"GPU draws the QuickSurf with the VMD/QuickSurf shader");
            Check(!pixels.Any(c=>c.r>240 && c.b>240 && c.g<20),"QuickSurf shader compiles without magenta error pixels");
        }
        finally
        {
            RenderTexture.active = previous; camera.targetTexture = null; target.Release();
            Object.DestroyImmediate(target); Object.DestroyImmediate(image); Object.DestroyImmediate(cameraObject);
        }
    }
    public static async void Run()
    {
        int exit = 1;
        try
        {
            metadata = Read("quicksurf.json"); frames = Read("frames.json");
            Check(metadata.supported && metadata.reps.Length == 0 && metadata.quicksurfs.Length == 1 &&
                metadata.quicksurfs[0].indices.Length == 2 && metadata.quicksurfs[0].quality == 1,"real VMD QuickSurf metadata parses");
            metadata.frameCount = 30;
            bool gpu = VMDQuickSurfScene.IsSupported;
            if (gpu) await GeometryChecks();
            else Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null,"GPU QuickSurf is unavailable only without a graphics device");
            root = new GameObject("QuickSurf playback fixture"); client = root.AddComponent<VMDClient>();
            wire = new MemoryStream();
            typeof(VMDClient).GetField("writer",Private).SetValue(client,new StreamWriter(wire,new UTF8Encoding(false)){AutoFlush=true,NewLine="\n"});
            var coordinates = (List<Vector3>)typeof(VMDClient).GetField("coordinates",Private).GetValue(client);
            for (int i = 0; i < metadata.atomCount; i++) coordinates.Add(Vector3.zero);
            var owner = root.AddComponent<MoleculeRenderer>(); Invoke(owner,"OnEnable");
            player = root.GetComponent<TrajectoryController>(); Invoke(player,"OnEnable");
            EditorApplication.update += Tick;
            if (!gpu)
            {
                await Until(()=>!player.OwnsDisplay);
                Check(root.GetComponentInChildren<VMDQuickSurfScene>() == null && player.Status.StartsWith("[VMD mesh]") &&
                    player.Status.Contains("compute shaders"),"without compute shaders QuickSurf keeps the VMD mesh route and says why");
            }
            else
            {
                await Until(()=>root.GetComponentInChildren<VMDQuickSurfScene>() != null && !player.IsWaitingForCoordinates);
                var surface = root.GetComponentInChildren<VMDQuickSurfScene>();
                await Built(surface);
                Check(player.OwnsDisplay && surface.TriangleCount > 50,"QuickSurf joins the shared coordinate route");
                Check(player.Status == "[Unity live] Paused  0 / 29\nrep0 QuickSurf","unified status: " + player.Status.Replace("\n"," | "));
                Check(root.GetComponent<RepresentationController>().CurrentDrawingMethod == "QuickSurf","representation controls see QuickSurf as the displayed method");
                player.NextFrame(); await Until(()=>player.CurrentFrame==1 && !player.IsWaitingForCoordinates);
                accelerate = true; player.Play(); await Until(()=>player.CurrentFrame >= 6); player.Pause(); accelerate = false;
                await Built(surface);
                Check(player.Status.StartsWith("[Unity live] Paused  " + player.CurrentFrame + " / 29"),"status follows playback");
                Check(!commands.Any(x=>x.StartsWith("EXPORT_") || x.StartsWith("STEP ") || x.StartsWith("FRAME ")),"QuickSurf play/step/pause never exports OBJ or steps VMD");
                Invoke(player,"OnDisable"); // Editor tests do not enter Play mode, so call it directly.
                Check(root.GetComponentInChildren<VMDQuickSurfScene>() == null,"disable releases the QuickSurf scene");
            }
            Debug.Log("QUICKSURF PLAYBACK CHECKS PASSED"); exit = 0;
        }
        catch (Exception error) { Debug.LogException(error); }
        finally
        {
            EditorApplication.update -= Tick;
            if (root != null) Object.DestroyImmediate(root);
            wire?.Dispose(); EditorApplication.Exit(exit);
        }
    }
}
