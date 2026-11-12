using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

// One visible QuickSurf rep, as sent by VMD_VR_Bridge/quicksurf.tcl. 'id' is the VMD rep index.
// radiusScale, isovalue, gridSpacing and quality (0 to 3) are the parameters of VMD's QuickSurf
// style. indices are the selected atoms; radii and colors (r, g, b) are per selected atom.
[Serializable] public sealed class VMDQuickSurfRepresentation
{
    public int id = -1, quality;
    public float radiusScale, isovalue, gridSpacing;
    public int[] indices;
    public float[] radii, colors;
}

// Draws the QuickSurf reps of the coordinate route. QuickSurf is rebuilt from each frame's atoms on
// the GPU (Gaussian density grid, then marching tetrahedra), never deformed from an exported mesh.
// VMD supplies the rep parameters, radii and colors. Each rep is a separate surface, as in VMD.
//
// TrajectoryController adds this component next to VMDCoordinateScene when the snapshot has
// QuickSurf reps and IsSupported is true. It calls Initialize with the TRAJ_OPEN reply, Show
// for every frame it displays, and Clear before it destroys the scene object, and it shows Note
// in the status text. VMDHighlightController calls SetHighlight; VMDCameraController reads
// HasBounds and WorldBounds. The compute shader VMDQuickSurfGrid builds the triangles and the
// VMDQuickSurf shader draws them straight from a GPU buffer, without a Mesh or a Renderer.
public sealed class VMDQuickSurfScene : MonoBehaviour
{
    // Gaussian cutoff, in units of the scaled atom radius, for VMD surface quality 0..3.
    private static readonly float[] GaussianLimit = { 2.0f, 2.5f, 3.0f, 3.5f };
    // Largest density grid in grid points (about 4.2 million); a build that would
    // need more uses a coarser spacing. The grid buffers are shared by all surfaces.
    private const int MaxCells = 1 << 22;
    // Largest triangle buffer of one surface (about 2.1 million); a surface with more is cut off.
    private const int MaxTriangles = 1 << 21;
    // Sizes in bytes of GpuAtom and of the compute shader's SurfaceVertex (float3, float3, uint).
    private const int AtomStride = 32, VertexStride = 28;

    // Mirrors struct Atom in VMDQuickSurfGrid.compute. position is in VMD coordinates. The
    // atom's density at distance r is exp(-r * r * invTwoSigma2), and zero beyond 'cutoff'.
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuAtom { public Vector3 position; public float invTwoSigma2; public Vector3 color; public float cutoff; }

    // Two vertex buffers per surface: the GPU fills "back" while "front" is drawn, and they
    // swap once the triangle count has been read back. A newer frame waits in "queued".
    private sealed class Surface
    {
        public VMDQuickSurfRepresentation rep;
        // CPU copy of the atoms; every build writes the frame's
        // positions into it and uploads it to atomBuffer.
        public GpuAtom[] atoms;
        // counter: one uint, the number of triangles the last build
        // produced, which may exceed what the vertex buffer holds.
        public GraphicsBuffer atomBuffer, counter, front, back;
        // Triangles to draw from 'front', and the size in triangles that 'back' should have.
        public int frontTriangles, triangleCapacity;
        // Grid boxes, in VMD coordinates, of the surface on display and of the one being built.
        public Bounds frontBox, pendingBox;
        // hasFront: 'front' holds a finished surface. hasPending: a build has been
        // started, so pendingBox is valid. inFlight: a build awaits its readback.
        // truncated: the surface on display reached MaxTriangles and is incomplete.
        public bool hasFront, hasPending, inFlight, truncated;
        // The frame being built, and the newest frame that
        // arrived meanwhile (older ones are dropped).
        public float[] building, queued;
        // Grid spacing of the last build; larger than rep.gridSpacing when the grid was coarsened.
        public float spacing;
        public Vector3[] colors; // VMD's per-atom colors, restored when the highlight is cleared.
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    }

    private readonly List<Surface> surfaces = new List<Surface>();
    private ComputeShader compute;
    private Material material;
    // Shared density grid: one fixed-point density and three color sums per grid point.
    private GraphicsBuffer density, colorSum;
    // gridCapacity: grid points allocated. The others are the kernel indices in 'compute'.
    private int gridCapacity, clearKernel, splatKernel, buildKernel;
    // view: VMD's molecule transform from the TRAJ_OPEN reply. rawToLocal: from VMD
    // coordinates to this object's local space; set from the first frame (see Show).
    private Matrix4x4 view = Matrix4x4.identity, rawToLocal = Matrix4x4.identity;
    private float scale;
    private int atomCount;
    // centered: rawToLocal has been set. released: Clear
    // has run, so later calls and readbacks are ignored.
    private bool centered, released;
    // The last frame passed to Show; rebuilt when the highlight changes.
    private float[] lastPositions;

    public int SurfaceCount => surfaces.Count;
    public int AtomCount => atomCount;
    // Triangles on display, over all surfaces.
    public int TriangleCount { get { int n = 0; foreach (var s in surfaces) if (s.hasFront) n += s.frontTriangles; return n; } }
    // Some surface is waiting for the GPU to finish a build.
    public bool IsBuilding { get { foreach (var s in surfaces) if (s.inFlight) return true; return false; } }
    // WorldBounds is valid once a build has started. It encloses the grid box of every surface on
    // display, or being built while there is none yet, so it is somewhat larger than the surfaces.
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
    // Live QuickSurf requires a graphics device, compute shaders, asynchronous GPU readback,
    // and both shader resources. A failed check selects the VMD mesh route.
    public static bool IsSupported => SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback &&
        SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null &&
        Resources.Load<ComputeShader>("VMDQuickSurfGrid") != null && Resources.Load<Shader>("VMDQuickSurf") != null;

    // Read the snapshot once: load the shaders and precompute every atom's Gaussian for
    // each QuickSurf rep. Nothing is built before Show. Throws when a shader is missing
    // or the metadata is invalid; TrajectoryController reports that as a failure.
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
            // An empty selection has no surface.
            if (rep.indices.Length == 0) continue;
            float limit = GaussianLimit[Mathf.Clamp(rep.quality, 0, GaussianLimit.Length - 1)];
            var surface = new Surface { rep = rep, atoms = new GpuAtom[rep.indices.Length], colors = new Vector3[rep.indices.Length] };
            for (int i = 0; i < rep.indices.Length; i++)
            {
                // Gaussian width: the VMD radius times the rep's radius scale, at least
                // 0.1. The cutoff is that width times the limit for the rep's quality.
                float sigma = Mathf.Max(rep.radii[i] * rep.radiusScale, 0.1f);
                surface.atoms[i].invTwoSigma2 = 1f / (2f * sigma * sigma);
                surface.atoms[i].cutoff = limit * sigma;
                surface.colors[i] = new Vector3(rep.colors[i*3], rep.colors[i*3+1], rep.colors[i*3+2]);
                surface.atoms[i].color = surface.colors[i];
            }
            SurfaceAtoms ??= new bool[atomCount];
            foreach (int atom in rep.indices) SurfaceAtoms[atom] = true;
            surface.atomBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rep.indices.Length, AtomStride);
            surface.counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            surfaces.Add(surface);
        }
    }

    // Check one rep's array lengths, its parameters (positive and finite) and its atom indices.
    private void Validate(VMDQuickSurfRepresentation rep)
    {
        int n = rep?.indices?.Length ?? -1;
        if (n < 0 || rep.radii?.Length != n || rep.colors?.Length != n*3 || !Positive(rep.radiusScale) ||
            !Positive(rep.isovalue) || !Positive(rep.gridSpacing)) throw new FormatException("Invalid QuickSurf metadata.");
        foreach (int atom in rep.indices)
            if (atom < 0 || atom >= atomCount) throw new FormatException("Invalid QuickSurf atom index.");
    }
    private static bool Positive(float value) => value > 0 && !float.IsInfinity(value);

    // Queue a frame. Surfaces appear a few rendered frames later, once the GPU has
    // built them. A surface that is still building keeps only the newest waiting frame.
    // The first frame also fixes the mapping from VMD coordinates to local space.
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
            // Applied from right to left: the view matrix, the Z flip, the center and the scale.
            rawToLocal = Matrix4x4.Scale(Vector3.one*scale) * Matrix4x4.Translate(-center) * Matrix4x4.Scale(new Vector3(1,1,-1)) * view;
            centered = true;
        }
        lastPositions = positions;
        foreach (var surface in surfaces)
        {
            if (surface.inFlight) surface.queued = positions;
            else Build(surface, positions);
        }
    }

    // Mute atoms outside 'selected' in the surface colors (null restores VMD's colors), then rebuild
    // the current frame so the change also shows while playback is paused.
    public void SetHighlight(bool[] selected, float dimming)
    {
        if (released) return;
        foreach (var s in surfaces)
        {
            for (int i = 0; i < s.atoms.Length; i++)
            {
                Color original = new Color(s.colors[i].x, s.colors[i].y, s.colors[i].z, 1);
                Color color = VMDHighlightController.IsSelected(selected, s.rep.indices[i])
                    ? original : VMDHighlightController.Dim(original, dimming);
                s.atoms[i].color = new Vector3(color.r, color.g, color.b);
            }
            float[] current = s.queued ?? s.building ?? lastPositions;
            if (current == null) continue; // No frame yet; the first build uses the new colors.
            if (s.inFlight) s.queued = current;
            else Build(s, current);
        }
    }

    // True for every atom (by atom index) that some QuickSurf surface draws; null before
    // Initialize. VMDRayHighlight picks these atoms with TryRaycast instead of by distance.
    public bool[] SurfaceAtoms { get; private set; }

    // Where a world-space ray first enters one of the surfaces on display from outside, and the
    // atom that contributes the most density there. 'distance' is in world units from the ray
    // origin. A surface that contains the origin (the hand, typically at immersive size) is not
    // entered until the ray has left it; 'insideAtom' is then the atom contributing the most
    // density at the origin, or -1, so the caller can fall back to the surface the hand is in.
    // The ray is marched in VMD coordinates, as the GPU builds the surface: the density is the
    // sum of the atoms' Gaussians (only atoms within their cutoff of the ray take part), and the
    // surface is where it reaches the rep's isovalue. The step is half the grid spacing, and
    // the crossing is refined by bisection. Uses the newest frame passed to Show, which the
    // surface on display may trail by a few rendered frames. Called by VMDRayHighlight.
    public bool TryRaycast(Ray worldRay, out float distance, out int atom, out int insideAtom)
    {
        distance = float.PositiveInfinity;
        atom = insideAtom = -1;
        if (released || !centered || lastPositions == null)
            return false;
        Matrix4x4 worldToRaw = (transform.localToWorldMatrix * rawToLocal).inverse;
        Vector3 origin = worldToRaw.MultiplyPoint3x4(worldRay.origin);
        Vector3 direction = worldToRaw.MultiplyVector(worldRay.direction);
        // Raw length of one world unit along the ray; a raw distance t is t / rawPerWorld in world.
        float rawPerWorld = direction.magnitude;
        if (rawPerWorld < 1e-12f)
            return false;
        direction /= rawPerWorld;
        float nearest = float.PositiveInfinity;
        foreach (var s in surfaces)
        {
            if (!s.hasFront)
                continue;
            // Atoms whose cutoff sphere the ray crosses, and the stretch of the ray they cover.
            candidates.Clear();
            float start = float.PositiveInfinity, end = float.NegativeInfinity;
            for (int i = 0; i < s.atoms.Length; i++)
            {
                Vector3 offset = RawPoint(s.rep.indices[i]) - origin;
                float along = Vector3.Dot(offset, direction), cutoff = s.atoms[i].cutoff;
                if (along + cutoff <= 0f || offset.sqrMagnitude - along * along >= cutoff * cutoff)
                    continue;
                candidates.Add(i);
                start = Mathf.Min(start, along - cutoff);
                end = Mathf.Max(end, along + cutoff);
            }
            if (candidates.Count == 0)
                continue;
            start = Mathf.Max(start, 0f);
            end = Mathf.Min(end, nearest);
            float step = Mathf.Max(0.5f * (s.spacing > 0f ? s.spacing : s.rep.gridSpacing), (end - start) / 4000f, 1e-3f);
            float previous = start;
            // The origin lies inside this surface: skip ahead until the ray has left it.
            bool leaving = start <= 0f && Density(s, origin) >= s.rep.isovalue;
            if (leaving && insideAtom < 0)
                insideAtom = s.rep.indices[StrongestAtom(s, origin)];
            for (float t = start; t <= end; t += step)
            {
                if (Density(s, origin + direction * t) < s.rep.isovalue)
                {
                    leaving = false;
                    previous = t;
                    continue;
                }
                if (leaving)
                {
                    previous = t;
                    continue;
                }
                // The crossing lies between 'previous' (outside, or the start) and t (inside).
                float outside = previous, inside = t;
                for (int k = 0; k < 6; k++)
                {
                    float middle = 0.5f * (outside + inside);
                    if (Density(s, origin + direction * middle) >= s.rep.isovalue) inside = middle; else outside = middle;
                }
                if (inside < nearest)
                {
                    nearest = inside;
                    atom = s.rep.indices[StrongestAtom(s, origin + direction * inside)];
                }
                break;
            }
        }
        if (atom < 0)
            return false;
        distance = nearest / rawPerWorld;
        return true;
    }

    // Reusable list of the candidate atoms of TryRaycast, as indices into Surface.atoms.
    private readonly List<int> candidates = new List<int>();

    // Atom 'index' of the newest frame, in VMD coordinates.
    private Vector3 RawPoint(int index) =>
        new Vector3(lastPositions[index * 3], lastPositions[index * 3 + 1], lastPositions[index * 3 + 2]);

    // Density of surface 's' at a point in VMD coordinates, from the candidate atoms.
    private float Density(Surface s, Vector3 point)
    {
        float sum = 0f;
        foreach (int i in candidates)
        {
            float r2 = (RawPoint(s.rep.indices[i]) - point).sqrMagnitude, cutoff = s.atoms[i].cutoff;
            if (r2 < cutoff * cutoff)
                sum += Mathf.Exp(-r2 * s.atoms[i].invTwoSigma2);
        }
        return sum;
    }

    // The candidate atom of surface 's' with the largest density at 'point', as an index into
    // Surface.atoms.
    private int StrongestAtom(Surface s, Vector3 point)
    {
        int best = candidates[0];
        float strongest = -1f;
        foreach (int i in candidates)
        {
            float r2 = (RawPoint(s.rep.indices[i]) - point).sqrMagnitude;
            float value = Mathf.Exp(-r2 * s.atoms[i].invTwoSigma2);
            if (value > strongest) { strongest = value; best = i; }
        }
        return best;
    }

    // Start building one surface for one frame:
    // 1. Copy the atom positions and take their bounding box,
    //    padded by the largest cutoff plus one grid spacing.
    // 2. Choose the grid: VMD's spacing, coarsened until the grid fits in MaxCells.
    // 3. Run the three compute kernels (clear, splat, build), writing into the back buffer.
    // 4. Request a readback of the triangle counter; Completed runs when it arrives.
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
            // First build: estimate the triangle count from the surface area of
            // the box in grid cells, as a power of two between 65536 and
            // MaxTriangles. Completed enlarges the buffer if this was too small.
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
        // ClearGrid runs at most 4096 groups of 256 threads. Each
        // thread clears every _ThreadCount-th grid point, so a grid
        // with more points than threads is still cleared completely.
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
        // One thread per grid cube (dims - 1 cubes per axis), in groups of 4 x 4 x 4.
        compute.Dispatch(buildKernel, CeilDiv(dims.x-1, 4), CeilDiv(dims.y-1, 4), CeilDiv(dims.z-1, 4));

        s.pendingBox = new Bounds();
        s.pendingBox.SetMinMax(origin, origin + (Vector3)(dims - Vector3Int.one)*spacing);
        s.hasPending = true;
        s.inFlight = true;
        s.building = positions;
        AsyncGPUReadback.Request(s.counter, request => Completed(s, request));
    }

    // The readback of the triangle counter arrived. If the build produced more
    // triangles than its buffer holds, build again into a larger buffer. Otherwise
    // swap the buffers, so that the new surface is drawn. Then start the queued
    // frame, if any. After a readback error the previous surface stays on display.
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

    // Make the shared grid buffers hold at least 'cells' points.
    // A new allocation adds 25%, as far as MaxCells allows.
    private void EnsureGrid(int cells)
    {
        if (density != null && cells <= gridCapacity) return;
        density?.Release(); colorSum?.Release();
        gridCapacity = Math.Max(cells, Math.Min(cells + cells/4, MaxCells));
        density = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gridCapacity, 4);
        colorSum = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gridCapacity*3, 4);
    }

    // Draw every finished surface for this frame straight from its vertex buffer. The
    // shader transforms the vertices from VMD coordinates with the two matrices set here.
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

    // Grid points per axis for a box of 'size'.
    private static Vector3Int Dims(Vector3 size, float spacing) => new Vector3Int(
        Mathf.CeilToInt(size.x/spacing)+1, Mathf.CeilToInt(size.y/spacing)+1, Mathf.CeilToInt(size.z/spacing)+1);
    // Thread groups needed for 'value' items, at least one.
    private static int CeilDiv(int value, int divisor) => Math.Max(1, (value + divisor - 1) / divisor);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    // Axis-aligned box that encloses 'box' after the transform 'matrix'.
    private static Bounds ToWorld(Bounds box, Matrix4x4 matrix)
    {
        Vector3 c = matrix.MultiplyPoint3x4(box.center), e = box.extents;
        Vector3 x = matrix.MultiplyVector(new Vector3(e.x,0,0)), y = matrix.MultiplyVector(new Vector3(0,e.y,0)), z = matrix.MultiplyVector(new Vector3(0,0,e.z));
        return new Bounds(c, 2*new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),
            Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y), Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z)));
    }

    // Release the GPU buffers and the material now. Called by
    // TrajectoryController before it destroys the scene object, and again by
    // OnDestroy. Afterwards Show, SetHighlight and late readbacks are ignored.
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
