using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

// Approximate coordinate-driven deformation of an exported mesh, NOT a replacement
// for VMD's surface/cartoon algorithms. Topology stays fixed until the next export.
// VDW vertices translate with the nearest selected atom; other methods blend the
// displacements of four selected neighbors. Pause re-exports authoritative geometry.
//
// This is what lets the VMD mesh route play a trajectory without exporting every frame.
// - Load reads scene.binding, the file the bridge writes next to scene.obj
//   (scene_write_binding in static_scene.tcl), and ties every mesh vertex to atoms.
// - UpdateCoordinates moves the vertices for a new coordinate frame.
// VMDStaticScene uploads the moved vertices to its meshes; RepresentationController loads the
// binding together with the OBJ and feeds the coordinate frames during playback.
//
// scene.binding is a text file with one record per line:
//   VMD_BINDING 1                  format marker, always the first line
//   molecule <molid>               VMD's top molecule; only its reps are bound
//   matrix <16 numbers>            row-major 4x4 transform from VMD coordinates to OBJ space
//   atom <x> <y> <z>               one per atom, at the exported frame, in VMD coordinates
//   rep <index> <method> <atom indices...>   one per visible rep: its method and selected atoms
public sealed class VMDMeshBinding
{
    // The bound vertices of one mesh part (one entry of VMDObjLoader.Scene.parts).
    // - reference: vertex positions as exported, never changed.
    // - vertices: current positions, rewritten by UpdateCoordinates and read by VMDStaticScene.
    // - influences: for each vertex, the atoms it follows.
    // - rigidAtoms: the part belongs to a VDW rep, where each vertex follows a single atom.
    public sealed class Part
    {
        public Vector3[] reference, vertices;
        public Influence[] influences;
        public bool rigidAtoms;
    }
    // Up to four atoms (indices a to d) and their weights, which sum to 1. Unused slots have
    // weight 0. Displacement returns the weighted sum of those atoms' movements.
    public struct Influence
    {
        public int a, b, c, d;
        public Vector4 weights;
        public Vector3 Displacement(Vector3[] moves) => moves[a] * weights.x +
            moves[b] * weights.y + moves[c] * weights.z + moves[d] * weights.w;
    }
    // Bound parts, keyed by their index in VMDObjLoader.Scene.parts. Parts that belong to no
    // bound rep have no entry and stay static.
    // Index keys avoid retaining the OBJ parser's colors, normals and triangle lists.
    public readonly Dictionary<int, Part> parts = new Dictionary<int, Part>();
    // referenceAtoms: atom positions at the exported frame, in mesh space.
    // moves: each atom's displacement from its reference position for the current frame.
    private Vector3[] referenceAtoms, moves;
    // The "matrix" record: VMD coordinates to OBJ space, before the handedness flip.
    private Matrix4x4 transform = Matrix4x4.identity;

    // Read the binding file next to 'objPath' (same name, extension .binding) and bind the
    // vertices of 'scene' to atoms. Returns null when there is nothing to deform with: no binding
    // file, no matrix or atoms in it, or no mesh part that belongs to a bound rep. Throws for a
    // malformed or oversized file.
    // Runs with OBJ parsing on a worker thread. No GameObjects or native Mesh calls.
    public static VMDMeshBinding Load(string objPath, VMDObjLoader.Scene scene, CancellationToken token)
    {
        string path = Path.ChangeExtension(objPath, ".binding");
        if (!File.Exists(path)) return null; // Legacy/local static exports remain loadable.
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new FormatException("Binding file exceeds 64 MB.");
        var result = new VMDMeshBinding();
        var atoms = new List<Vector3>();
        // Per rep, keyed by the OBJ group name its geometry is looked up under: the selected atom
        // indices and the drawing method.
        var selections = new Dictionary<string, int[]>();
        var methods = new Dictionary<string, string>();
        int molid = -1, line = 0;
        bool hasMatrix = false;
        foreach (string raw in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (++line == 1) { if (raw != "VMD_BINDING 1") throw new FormatException("Unknown binding format."); continue; }
            string[] words = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            switch (words[0])
            {
                case "molecule": molid = int.Parse(words[1], CultureInfo.InvariantCulture); break;
                case "matrix":
                    if (words.Length != 17) throw new FormatException("Binding matrix needs 16 values.");
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++) result.transform[row, col] = Number(words[1 + row * 4 + col]);
                    hasMatrix = true;
                    break;
                case "atom":
                    if (words.Length != 4 || atoms.Count >= 2000000) throw new FormatException("Invalid binding atoms.");
                    atoms.Add(new Vector3(Number(words[1]), Number(words[2]), Number(words[3])));
                    break;
                case "rep":
                    if (words.Length < 3 || molid < 0) throw new FormatException("Invalid binding representation.");
                    // Mesh parts are matched to this rep by their OBJ group name.
                    string group = "vmd_mol" + molid + "_rep" + int.Parse(words[1], CultureInfo.InvariantCulture);
                    var indices = new int[words.Length - 3];
                    for (int i = 0; i < indices.Length; i++)
                    {
                        indices[i] = int.Parse(words[i + 3], CultureInfo.InvariantCulture);
                        if (indices[i] < 0 || indices[i] >= atoms.Count) throw new FormatException("Binding atom index out of range.");
                    }
                    selections[group] = indices;
                    methods[group] = words[2];
                    break;
                default: throw new FormatException("Unknown binding record.");
            }
        }
        if (!hasMatrix || atoms.Count == 0) return null;
        result.referenceAtoms = atoms.ToArray();
        result.moves = new Vector3[atoms.Count];
        // Bring the reference atoms into mesh space, where the vertices are.
        var transformed = new Vector3[atoms.Count];
        for (int i = 0; i < atoms.Count; i++)
        {
            transformed[i] = result.ToMeshPoint(atoms[i]);
            if (!Valid(transformed[i])) throw new FormatException("Binding transform overflows coordinates.");
        }
        result.referenceAtoms = transformed;
        // For each rep, bind every vertex of its mesh parts to the nearest of the rep's own atoms.
        foreach (var selection in selections)
        {
            if (selection.Value.Length == 0) continue;
            var tree = new Neighbors(transformed, selection.Value, token);
            bool rigid = methods[selection.Key] == "VDW";
            // The OBJ loader stores three vertices per triangle, so positions repeat; search the
            // neighbors of each distinct position once.
            var cache = new Dictionary<Vector3, Influence>();
            for (int partIndex = 0; partIndex < scene.parts.Count; partIndex++)
            {
                var meshPart = scene.parts[partIndex];
                if (meshPart.group != selection.Key) continue;
                var part = new Part { reference = meshPart.vertices.ToArray(), vertices = meshPart.vertices.ToArray(),
                    influences = new Influence[meshPart.vertices.Count], rigidAtoms = rigid };
                for (int i = 0; i < part.reference.Length; i++)
                {
                    if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                    Vector3 point = part.reference[i];
                    if (!cache.TryGetValue(point, out Influence influence))
                    {
                        influence = tree.Bind(point, rigid ? 1 : 4);
                        cache.Add(point, influence);
                    }
                    part.influences[i] = influence;
                }
                result.parts.Add(partIndex, part);
            }
        }
        return result.parts.Count == 0 ? null : result;
    }

    // Convert an atom position from VMD coordinates to the mesh space of the imported OBJ.
    private Vector3 ToMeshPoint(Vector3 position)
    {
        Vector3 p = transform.MultiplyPoint3x4(position);
        return new Vector3(p.x, p.y, -p.z); // Same handedness conversion as VMDObjLoader.
    }
    // Move the bound vertices for a new coordinate frame (VMD coordinates, one entry per atom, in
    // the binding's atom order). Returns false, leaving the vertices as they were, when the atom
    // count differs from the binding's or a coordinate is not finite. Called by
    // VMDStaticScene.UpdateCoordinates during mesh-route playback.
    // No per-frame arrays or neighbor searches. Always deform from the reference frame,
    // not from last frame's vertices, so returning to the reference has no drift.
    public bool UpdateCoordinates(IReadOnlyList<Vector3> coordinates)
    {
        if (coordinates == null || coordinates.Count != referenceAtoms.Length) return false;
        for (int i = 0; i < coordinates.Count; i++)
        {
            Vector3 p = coordinates[i];
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return false;
            moves[i] = ToMeshPoint(p) - referenceAtoms[i];
            if (!Valid(moves[i])) return false;
        }
        foreach (var part in parts.Values)
            for (int i = 0; i < part.vertices.Length; i++)
                part.vertices[i] = part.reference[i] + part.influences[i].Displacement(moves);
        return true;
    }
    private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    private static bool Valid(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    // Parse a finite float of the binding file; throws otherwise.
    private static float Number(string text)
    {
        float n = float.Parse(text, CultureInfo.InvariantCulture);
        if (!Finite(n)) throw new FormatException("Non-finite binding value.");
        return n;
    }

    // Nearest-atom search over one rep's selected atoms.
    // Balanced kd-tree avoids scanning every atom for every exported vertex.
    private sealed class Neighbors
    {
        // All atom positions in mesh space, indexed by atom index.
        private readonly Vector3[] points;
        // The rep's atom indices arranged as an implicit kd-tree: the middle element of a range
        // is its node, the halves on either side are its subtrees, and the split axis cycles
        // x, y, z with depth.
        private readonly int[] order;
        // Result of the search in progress: the nearest atoms found so far, closest first, and
        // their squared distances.
        private readonly int[] best = new int[4];
        private readonly float[] distances = new float[4];
        private readonly CancellationToken token;
        public Neighbors(Vector3[] points, int[] indices, CancellationToken token)
        {
            this.token = token;
            this.points = points; order = (int[])indices.Clone(); Build(0, order.Length, 0);
        }
        // Sort order[start..end) along 'axis' so that its middle element becomes the node, then
        // build both halves along the next axis.
        private void Build(int start, int end, int axis)
        {
            token.ThrowIfCancellationRequested();
            if (end - start <= 1) return;
            Array.Sort(order, start, end - start, Comparer<int>.Create((a, b) => points[a][axis].CompareTo(points[b][axis])));
            int mid = (start + end) / 2;
            Build(start, mid, (axis + 1) % 3); Build(mid + 1, end, (axis + 1) % 3);
        }
        // Find the 'count' atoms nearest to 'point' (1 for VDW, otherwise 4, or fewer when the
        // rep has fewer atoms) and weight them by inverse squared distance, normalized to sum 1.
        public Influence Bind(Vector3 point, int count)
        {
            count = Math.Min(count, order.Length);
            for (int i = 0; i < 4; i++) { best[i] = order[0]; distances[i] = float.PositiveInfinity; }
            Search(point, 0, order.Length, 0, count);
            var weights = new Vector4();
            // A vertex on an atom center follows that atom alone; this also avoids dividing by 0.
            if (distances[0] < 1e-12f) weights.x = 1f;
            else
            {
                float sum = 0;
                for (int i = 0; i < count; i++) { weights[i] = distances[0] / distances[i]; sum += weights[i]; }
                weights /= sum;
            }
            return new Influence { a = best[0], b = best[1], c = best[2], d = best[3], weights = weights };
        }
        // Visit the subtree order[start..end): insert its node into the sorted result, then search
        // the half containing 'point', and the other half only if it can still hold a closer atom.
        private void Search(Vector3 point, int start, int end, int axis, int count)
        {
            if (start >= end) return;
            int mid = (start + end) / 2, atom = order[mid];
            float distance = (point - points[atom]).sqrMagnitude;
            for (int i = 0; i < count; i++)
                if (distance < distances[i])
                {
                    for (int j = count - 1; j > i; j--) { distances[j] = distances[j - 1]; best[j] = best[j - 1]; }
                    distances[i] = distance; best[i] = atom; break;
                }
            // Signed distance from 'point' to this node's splitting plane.
            float delta = point[axis] - points[atom][axis];
            int next = (axis + 1) % 3;
            if (delta < 0)
            {
                Search(point, start, mid, next, count);
                if (delta * delta <= distances[count - 1]) Search(point, mid + 1, end, next, count);
            }
            else
            {
                Search(point, mid + 1, end, next, count);
                if (delta * delta <= distances[count - 1]) Search(point, start, mid, next, count);
            }
        }
    }
}
