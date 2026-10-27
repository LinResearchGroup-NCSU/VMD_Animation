using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable] public sealed class VMDQuickSurfRepresentation
{
    public int id = -1, quality;
    public float radiusScale, isovalue, gridSpacing;
    public int[] indices;
    public float[] radii, colors;
}

// QuickSurf is rebuilt from each frame's atoms on the GPU (Gaussian density grid, then
// marching tetrahedra), never deformed from an exported mesh. VMD supplies the rep
// parameters, radii and colors. Each rep is a separate surface, as in VMD.
public sealed class VMDQuickSurfScene : MonoBehaviour
{
    // Gaussian cutoff, in units of the scaled atom radius, for VMD surface quality 0..3.
    private static readonly float[] GaussianLimit = { 2.0f, 2.5f, 3.0f, 3.5f };
    // The grid is shared by all surfaces; spacing grows automatically above this size.
    private const int MaxCells = 1 << 22;
    private const int MaxTriangles = 1 << 21;
    private const int AtomStride = 32, VertexStride = 28;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuAtom { public Vector3 position; public float invTwoSigma2; public Vector3 color; public float cutoff; }

    // Two vertex buffers per surface: the GPU fills "back" while "front" is drawn, and they
    // swap once the triangle count has been read back. A newer frame waits in "queued".
    private sealed class Surface
    {
        public VMDQuickSurfRepresentation rep;
        public GpuAtom[] atoms;
        public GraphicsBuffer atomBuffer, counter, front, back;
        public int frontTriangles, triangleCapacity;
        public Bounds frontBox, pendingBox;
        public bool hasFront, hasPending, inFlight, truncated;
        public float[] building, queued;
        public float spacing;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    }

    private readonly List<Surface> surfaces = new List<Surface>();
    private ComputeShader compute;
    private Material material;
    private GraphicsBuffer density, colorSum;
    private int gridCapacity, clearKernel, splatKernel, buildKernel;
    private Matrix4x4 view = Matrix4x4.identity, rawToLocal = Matrix4x4.identity;
    private float scale;
    private int atomCount;
    private bool centered, released;

    public int SurfaceCount => surfaces.Count;
    public int TriangleCount { get { int n = 0; foreach (var s in surfaces) if (s.hasFront) n += s.frontTriangles; return n; } }
    public bool IsBuilding { get { foreach (var s in surfaces) if (s.inFlight) return true; return false; } }
    public bool HasBounds { get { foreach (var s in surfaces) if (s.hasFront || s.hasPending) return true; return false; } }
    public Bounds WorldBounds
    {
        get
        {
            Matrix4x4 rawToWorld = transform.localToWorldMatrix * rawToLocal;
            Bounds result = default;
            bool first = true;
            foreach (var s in surfaces)
            {
                if (!s.hasFront && !s.hasPending) continue;
                Bounds box = ToWorld(s.hasFront ? s.frontBox : s.pendingBox, rawToWorld);
                if (first) { result = box; first = false; } else result.Encapsulate(box);
            }
            return result;
        }
    }
    // One short phrase for the status bar; empty when the surfaces match VMD's settings.
    public string Note
    {
        get
        {
            foreach (var s in surfaces) if (s.truncated) return "QuickSurf triangle limit reached; increase VMD grid spacing";
            foreach (var s in surfaces)
                if (s.spacing > s.rep.gridSpacing * 1.001f) return $"QuickSurf grid coarsened to {s.spacing:0.0#} Å";
            return "";
        }
    }
    // Compute shaders and readback are required; otherwise QuickSurf keeps the VMD mesh route.
    public static bool IsSupported => SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback &&
        SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null &&
        Resources.Load<ComputeShader>("VMDQuickSurfGrid") != null && Resources.Load<Shader>("VMDQuickSurf") != null;

    public void Initialize(VMDTrajectoryReply metadata, float coordinateScale)
    {
        atomCount = metadata.atomCount;
        scale = coordinateScale;
        if (metadata.matrix?.Length == 16)
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) view[r,c] = metadata.matrix[r*4+c];
        compute = Resources.Load<ComputeShader>("VMDQuickSurfGrid");
        var shader = Resources.Load<Shader>("VMDQuickSurf");
        if (compute == null || shader == null) throw new InvalidOperationException("QuickSurf GPU shaders are missing.");
        material = new Material(shader) { name = "QuickSurf" };
        clearKernel = compute.FindKernel("ClearGrid");
        splatKernel = compute.FindKernel("SplatAtoms");
        buildKernel = compute.FindKernel("BuildSurface");
        foreach (var rep in metadata.quicksurfs)
        {
            Validate(rep);
            if (rep.indices.Length == 0) continue;
            float limit = GaussianLimit[Mathf.Clamp(rep.quality, 0, GaussianLimit.Length - 1)];
            var surface = new Surface { rep = rep, atoms = new GpuAtom[rep.indices.Length] };
            for (int i = 0; i < rep.indices.Length; i++)
            {
                float sigma = Mathf.Max(rep.radii[i] * rep.radiusScale, 0.1f);
                surface.atoms[i].invTwoSigma2 = 1f / (2f * sigma * sigma);
                surface.atoms[i].cutoff = limit * sigma;
                surface.atoms[i].color = new Vector3(rep.colors[i*3], rep.colors[i*3+1], rep.colors[i*3+2]);
            }
            surface.atomBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rep.indices.Length, AtomStride);
            surface.counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            surfaces.Add(surface);
        }
    }

    private void Validate(VMDQuickSurfRepresentation rep)
    {
        int n = rep?.indices?.Length ?? -1;
        if (n < 0 || rep.radii?.Length != n || rep.colors?.Length != n*3 || !Positive(rep.radiusScale) ||
            !Positive(rep.isovalue) || !Positive(rep.gridSpacing)) throw new FormatException("Invalid QuickSurf metadata.");
        foreach (int atom in rep.indices)
            if (atom < 0 || atom >= atomCount) throw new FormatException("Invalid QuickSurf atom index.");
    }
    private static bool Positive(float value) => value > 0 && !float.IsInfinity(value);

    // Queue a frame. Surfaces appear a few rendered frames later, once the GPU has built them.
    public void Show(float[] positions)
    {
        if (released) return;
        if (positions == null || positions.Length != atomCount*3) throw new FormatException("Wrong QuickSurf coordinate count.");
        if (!centered)
        {
            // Same fixed reference center/handedness as the atom and cartoon renderers.
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < positions.Length; i += 3) { x += positions[i]; y += positions[i+1]; z += positions[i+2]; }
            Vector3 p = view.MultiplyPoint3x4(new Vector3((float)(x/atomCount),(float)(y/atomCount),(float)(z/atomCount)));
            Vector3 center = new Vector3(p.x,p.y,-p.z);
            rawToLocal = Matrix4x4.Scale(Vector3.one*scale) * Matrix4x4.Translate(-center) * Matrix4x4.Scale(new Vector3(1,1,-1)) * view;
            centered = true;
        }
        foreach (var surface in surfaces)
        {
            if (surface.inFlight) surface.queued = positions;
            else Build(surface, positions);
        }
    }

    private void Build(Surface s, float[] positions)
    {
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        float reach = 0;
        for (int i = 0; i < s.atoms.Length; i++)
        {
            int atom = s.rep.indices[i];
            var p = new Vector3(positions[atom*3], positions[atom*3+1], positions[atom*3+2]);
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) throw new FormatException("Non-finite QuickSurf coordinate.");
            s.atoms[i].position = p;
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            reach = Mathf.Max(reach, s.atoms[i].cutoff);
        }
        float spacing = s.rep.gridSpacing;
        Vector3 origin = min - Vector3.one*(reach + spacing);
        Vector3 size = max - min + Vector3.one*(2*(reach + spacing));
        Vector3Int dims = Dims(size, spacing);
        while ((long)dims.x*dims.y*dims.z > MaxCells)
        {
            spacing *= Mathf.Max(1.01f, Mathf.Pow((float)((long)dims.x*dims.y*dims.z) / MaxCells, 1f/3f));
            dims = Dims(size, spacing);
        }
        int cells = dims.x*dims.y*dims.z;
        s.spacing = spacing;
        if (s.triangleCapacity == 0)
        {
            float area = 2*(size.x*size.y + size.y*size.z + size.x*size.z) / (spacing*spacing);
            s.triangleCapacity = Mathf.Clamp(Mathf.NextPowerOfTwo((int)Mathf.Min(area*4, MaxTriangles)), 1 << 16, MaxTriangles);
        }
        EnsureGrid(cells);
        if (s.back == null || s.back.count < s.triangleCapacity*3)
        {
            s.back?.Release();
            s.back = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s.triangleCapacity*3, VertexStride);
        }
        s.atomBuffer.SetData(s.atoms);

        compute.SetInt("_AtomCount", s.atoms.Length);
        compute.SetInt("_CellCount", cells);
        compute.SetInt("_DimX", dims.x); compute.SetInt("_DimY", dims.y); compute.SetInt("_DimZ", dims.z);
        compute.SetFloat("_Spacing", spacing);
        compute.SetFloat("_Isovalue", s.rep.isovalue);
        compute.SetVector("_Origin", origin);
        compute.SetInt("_Capacity", s.back.count / 3);
        int clearGroups = Mathf.Clamp(CeilDiv(cells, 256), 1, 4096);
        compute.SetInt("_ThreadCount", clearGroups*256);
        compute.SetBuffer(clearKernel, "_Density", density);
        compute.SetBuffer(clearKernel, "_ColorSum", colorSum);
        compute.SetBuffer(clearKernel, "_Counter", s.counter);
        compute.Dispatch(clearKernel, clearGroups, 1, 1);
        compute.SetBuffer(splatKernel, "_Atoms", s.atomBuffer);
        compute.SetBuffer(splatKernel, "_Density", density);
        compute.SetBuffer(splatKernel, "_ColorSum", colorSum);
        compute.Dispatch(splatKernel, CeilDiv(s.atoms.Length, 64), 1, 1);
        compute.SetBuffer(buildKernel, "_Density", density);
        compute.SetBuffer(buildKernel, "_ColorSum", colorSum);
        compute.SetBuffer(buildKernel, "_Counter", s.counter);
        compute.SetBuffer(buildKernel, "_Vertices", s.back);
        compute.Dispatch(buildKernel, CeilDiv(dims.x-1, 4), CeilDiv(dims.y-1, 4), CeilDiv(dims.z-1, 4));

        s.pendingBox = new Bounds();
        s.pendingBox.SetMinMax(origin, origin + (Vector3)(dims - Vector3Int.one)*spacing);
        s.hasPending = true;
        s.inFlight = true;
        s.building = positions;
        AsyncGPUReadback.Request(s.counter, request => Completed(s, request));
    }

    private void Completed(Surface s, AsyncGPUReadbackRequest request)
    {
        if (released) return;
        s.inFlight = false;
        if (!request.hasError)
        {
            int count = (int)Math.Min(request.GetData<uint>()[0], int.MaxValue);
            int capacity = s.back.count / 3;
            if (count > capacity && capacity < MaxTriangles)
            {
                // Rebuild the same (or a newer queued) frame into a larger buffer.
                s.triangleCapacity = Mathf.Min(MaxTriangles, Mathf.NextPowerOfTwo(count + count/4));
                float[] again = s.queued ?? s.building;
                s.queued = null;
                Build(s, again);
                return;
            }
            (s.front, s.back) = (s.back, s.front);
            s.frontTriangles = Mathf.Min(count, capacity);
            s.truncated = count > capacity;
            s.frontBox = s.pendingBox;
            s.hasFront = true;
        }
        else Debug.LogWarning("[QuickSurf] GPU readback failed; keeping the previous surface.", this);
        s.building = null;
        if (s.queued != null)
        {
            float[] next = s.queued;
            s.queued = null;
            Build(s, next);
        }
    }

    private void EnsureGrid(int cells)
    {
        if (density != null && cells <= gridCapacity) return;
        density?.Release(); colorSum?.Release();
        gridCapacity = Math.Max(cells, Math.Min(cells + cells/4, MaxCells));
        density = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gridCapacity, 4);
        colorSum = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gridCapacity*3, 4);
    }

    private void LateUpdate()
    {
        if (released || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Matrix4x4 rawToWorld = transform.localToWorldMatrix * rawToLocal;
        Matrix4x4 normalMatrix = rawToWorld.inverse.transpose;
        foreach (var s in surfaces)
        {
            if (!s.hasFront || s.frontTriangles == 0) continue;
            s.properties.SetBuffer("_Vertices", s.front);
            s.properties.SetMatrix("_RawToWorld", rawToWorld);
            s.properties.SetMatrix("_RawToWorldNormal", normalMatrix);
            var parameters = new RenderParams(material) { worldBounds = ToWorld(s.frontBox, rawToWorld), matProps = s.properties,
                layer = gameObject.layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            Graphics.RenderPrimitives(parameters, MeshTopology.Triangles, s.frontTriangles*3);
        }
    }

    private static Vector3Int Dims(Vector3 size, float spacing) => new Vector3Int(
        Mathf.CeilToInt(size.x/spacing)+1, Mathf.CeilToInt(size.y/spacing)+1, Mathf.CeilToInt(size.z/spacing)+1);
    private static int CeilDiv(int value, int divisor) => Math.Max(1, (value + divisor - 1) / divisor);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static Bounds ToWorld(Bounds box, Matrix4x4 matrix)
    {
        Vector3 c = matrix.MultiplyPoint3x4(box.center), e = box.extents;
        Vector3 x = matrix.MultiplyVector(new Vector3(e.x,0,0)), y = matrix.MultiplyVector(new Vector3(0,e.y,0)), z = matrix.MultiplyVector(new Vector3(0,0,e.z));
        return new Bounds(c, 2*new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),
            Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y), Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z)));
    }

    // Explicit release also works when the controller is exercised outside Play mode.
    public void Clear()
    {
        released = true;
        foreach (var s in surfaces) { s.atomBuffer?.Release(); s.counter?.Release(); s.front?.Release(); s.back?.Release(); }
        surfaces.Clear();
        density?.Release(); colorSum?.Release();
        density = colorSum = null;
        if (material != null) { if (Application.isPlaying) Destroy(material); else DestroyImmediate(material); }
        material = null;
    }
    private void OnDestroy() => Clear();
}
