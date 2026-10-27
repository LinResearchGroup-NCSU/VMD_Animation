// Uses real VMD metadata/frame fixtures from _test_cartoon.tcl in an isolated Editor project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CartoonPlaybackChecks
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
            if (DateTime.UtcNow > end) throw new TimeoutException("Cartoon timeout: " + player.Status);
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
                        reply.frames = frames.frames.Skip(reply.start).Take(int.Parse(words[4])).ToArray(); break;
                    case "TRAJ_SYNC": remoteFrame = int.Parse(words[3]); break;
                    case "TRAJ_STYLE":
                        if (words[3] == "QuickSurf") metadata = SurfaceFrom(metadata);
                        break;
                }
                var receive = (Action<string>)typeof(VMDClient).GetField("TrajectoryResponseReceived",Private).GetValue(client);
                receive?.Invoke("TRAJJSON " + JsonUtility.ToJson(reply));
            }
        }
        catch (Exception error) { tickError = error; }
    }
    // What the bridge reports after TRAJ_STYLE switches the cartoon rep to QuickSurf.
    private static VMDTrajectoryReply SurfaceFrom(VMDTrajectoryReply source)
    {
        var copy = JsonUtility.FromJson<VMDTrajectoryReply>(JsonUtility.ToJson(source));
        int[] atoms = source.cartoons[0].chains.SelectMany(c=>c.indices).ToArray();
        copy.reps = new VMDAtomRepresentation[0];
        copy.cartoons = new VMDCartoonRepresentation[0];
        copy.quicksurfs = new[] { new VMDQuickSurfRepresentation { id = 0, quality = 1, radiusScale = 1, isovalue = 0.5f, gridSpacing = 1,
            indices = atoms, radii = atoms.Select(_=>1.7f).ToArray(), colors = atoms.SelectMany(_=>new[]{0.2f,0.6f,1f}).ToArray() } };
        return copy;
    }
    private static async Task GeometryChecks()
    {
        var test = new GameObject("Cartoon geometry fixture");
        Mesh[] meshes = null;
        try
        {
            var scene = test.AddComponent<VMDCartoonScene>();
            scene.Initialize(metadata,1);
            scene.Show(frames.frames[0].positions);
            meshes = test.GetComponentsInChildren<MeshFilter>().Select(x=>x.sharedMesh).ToArray();
            Check(meshes.Length == 2 && scene.StripCount == 2,"separate protein chains have separate meshes");
            Vector3[] original = meshes[0].vertices, normals = meshes[0].normals;
            int[] triangles = meshes[0].triangles;
            scene.Show(frames.frames[1].positions);
            Check(test.GetComponentInChildren<MeshFilter>().sharedMesh == meshes[0],"reuses NewCartoon mesh between frames");
            Check(!meshes[0].vertices.SequenceEqual(original) && !meshes[0].normals.SequenceEqual(normals),"rebuilds positions and surface normals from current backbone");
            Check(meshes[0].triangles.SequenceEqual(triangles),"retains topology for connected trajectory");
            Check(meshes.All(m=>m.vertices.All(Finite) && m.normals.All(Finite)),"finite ribbon geometry and normals");
            scene.Show(frames.frames[0].positions);
            Check(meshes[0].vertices.SequenceEqual(original),"seeking back reconstructs identical geometry without accumulated deformation");
            // Let native MeshRenderers register before an offscreen Editor camera renders.
            await Task.Delay(100);
            Draw(test);
            float[] jumped = (float[])frames.frames[0].positions.Clone();
            jumped[metadata.cartoons[0].chains[0].indices[5]*3] += 100;
            scene.Show(jumped);
            Check(!meshes[0].triangles.SequenceEqual(triangles),"periodic/backbone jump disables cross-gap faces");
            scene.Show(frames.frames[0].positions);
            Check(meshes[0].triangles.SequenceEqual(triangles),"backbone recovery restores topology");
            scene.Show(new float[metadata.atomCount*3]);
            Check(meshes.All(m=>m.vertices.All(Finite) && m.normals.All(Finite)),"degenerate backbone does not produce NaN");
        }
        finally { test.GetComponent<VMDCartoonScene>()?.Clear(); Object.DestroyImmediate(test); }
        Check(meshes.All(m=>m == null),"teardown releases native cartoon meshes");
        foreach (string file in new[]{"cartoon-selection.json","cartoon-bspline.json","cartoon-mixed.json"})
        {
            var input = Read(file);
            test = new GameObject(file);
            try
            {
                var cartoon = test.AddComponent<VMDCartoonScene>(); cartoon.Initialize(input,1); cartoon.Show(frames.frames[0].positions);
                Check(cartoon.StripCount == 2,"selection/BSpline/mixed scene: " + file);
                if (file.Contains("mixed"))
                {
                    var atoms = test.AddComponent<VMDCoordinateScene>(); atoms.Initialize(input,1); atoms.Show(frames.frames[0].positions);
                    Check(atoms.AtomInstanceCount == 2 && atoms.BondInstanceCount == 0,"existing VDW renderer coexists with cartoon");
                }
            }
            finally { test.GetComponent<VMDCartoonScene>()?.Clear(); Object.DestroyImmediate(test); }
        }
    }
    private static bool Finite(Vector3 v) => !(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)||float.IsInfinity(v.x)||float.IsInfinity(v.y)||float.IsInfinity(v.z));
    private static void Draw(GameObject fixture)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        var renderers = fixture.GetComponentsInChildren<MeshRenderer>();
        Bounds bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        var cameraObject = new GameObject("Cartoon render camera");
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(1024,512,24);
        var image = new Texture2D(1024,512,TextureFormat.RGB24,false);
        var previous = RenderTexture.active;
        try
        {
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.06f,0.07f,0.09f);
            camera.orthographic = true; camera.aspect = 2;
            camera.orthographicSize = Mathf.Max(bounds.extents.y,bounds.extents.x/2)*1.2f;
            camera.transform.position = bounds.center-Vector3.forward*(bounds.extents.magnitude*3+1);
            camera.farClipPlane = 1000; camera.nearClipPlane = 0.01f; camera.targetTexture = target;
            camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0,0,1024,512),0,0); image.Apply();
            var pixels = image.GetPixels32();
            File.WriteAllBytes("cartoon-preview.png",image.EncodeToPNG());
            Debug.Log("Cartoon pixels: " + pixels.Count(c=>c.r > 80 || c.g > 80 || c.b > 80) + "; bounds " + bounds + "; shader " + renderers[0].sharedMaterial.shader.isSupported);
            Check(pixels.Count(c=>c.r > 80 || c.g > 80 || c.b > 80) > 500,"GPU draws continuous cartoon geometry");
            Check(!pixels.Any(c=>c.r>240 && c.b>240 && c.g<20),"cartoon shader compiles without magenta error pixels");
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
            metadata = Read("cartoon.json"); frames = Read("cartoon-frames.json");
            Check(metadata.supported && metadata.reps.Length == 0 && metadata.cartoons.Length == 1,"real VMD cartoon-only metadata parses");
            await GeometryChecks();
            root = new GameObject("Cartoon playback fixture"); client = root.AddComponent<VMDClient>();
            wire = new MemoryStream();
            typeof(VMDClient).GetField("writer",Private).SetValue(client,new StreamWriter(wire,new UTF8Encoding(false)){AutoFlush=true,NewLine="\n"});
            var coordinates = (List<Vector3>)typeof(VMDClient).GetField("coordinates",Private).GetValue(client);
            for (int i = 0; i < metadata.atomCount; i++) coordinates.Add(Vector3.zero);
            var owner = root.AddComponent<MoleculeRenderer>(); Invoke(owner,"OnEnable");
            player = root.GetComponent<TrajectoryController>(); Invoke(player,"OnEnable");
            EditorApplication.update += Tick;
            player.Play();
            await Until(()=>root.GetComponentInChildren<VMDCartoonScene>() != null && !player.IsWaitingForCoordinates);
            Check(player.OwnsDisplay && player.IsPlaying,"Play automatically prepares NewCartoon on the shared path");
            player.Pause(); player.Seek(0);
            await Until(()=>player.CurrentFrame==0 && !player.IsWaitingForCoordinates);
            player.NextFrame(); await Until(()=>player.CurrentFrame==1 && !player.IsWaitingForCoordinates);
            var mesh = root.GetComponentInChildren<MeshFilter>().sharedMesh;
            var held = mesh.vertices;
            await Task.Delay(150);
            Check(mesh.vertices.SequenceEqual(held),"pause retains exactly the same quality and geometry");
            player.PreviousFrame(); await Until(()=>player.CurrentFrame==0);
            accelerate = true; player.Play(); await Until(()=>player.CurrentFrame==3 && !player.IsPlaying); accelerate=false;
            Check(player.CurrentFrame==3,"continuous playback updates to final frame and pauses");
            player.Seek(1); await Until(()=>player.CurrentFrame==1);
            Check(!commands.Any(x=>x.StartsWith("EXPORT_") || x.StartsWith("STEP ") || x.StartsWith("FRAME ")),"cartoon play/step/seek/pause never exports OBJ or enters old mesh deformation");
            metadata = Read("cartoon-mixed.json");
            player.RefreshCoordinates();
            await Until(()=>root.GetComponentInChildren<VMDCoordinateScene>()?.AtomInstanceCount == 2 && !player.IsWaitingForCoordinates);
            Check(root.GetComponent<RepresentationController>().CurrentDrawingMethod == "NewCartoon","mixed scene retains the selected NewCartoon method for representation cycling");
            player.ChangeRepresentation(0,"QuickSurf");
            if (VMDQuickSurfScene.IsSupported)
            {
                await Until(()=>root.GetComponentInChildren<VMDQuickSurfScene>() != null && !player.IsWaitingForCoordinates);
                Check(player.OwnsDisplay && root.GetComponentInChildren<VMDCartoonScene>()==null,"QuickSurf joins the coordinate route and releases cartoon geometry");
            }
            else
            {
                await Until(()=>!player.OwnsDisplay);
                Check(root.GetComponentInChildren<VMDCartoonScene>()==null,"without compute shaders QuickSurf keeps the VMD mesh route and releases cartoon geometry");
            }
            Debug.Log("CARTOON PLAYBACK CHECKS PASSED"); exit = 0;
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
