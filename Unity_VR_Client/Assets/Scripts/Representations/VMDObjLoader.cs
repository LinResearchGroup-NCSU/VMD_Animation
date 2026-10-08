using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

// Loads geometry already produced by VMD. No molecular drawing algorithms here.
public static class VMDObjLoader
{
    public sealed class Scene
    {
        public readonly List<Part> parts = new List<Part>();
        public readonly HashSet<string> warnings = new HashSet<string>();
        public Bounds bounds;
        public int triangleCount;
    }

    public sealed class Part
    {
        public string group;
        public bool transparent;
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Color> colors = new List<Color>();
        public readonly List<Vector4> specular = new List<Vector4>();
        public readonly List<Vector3> ambient = new List<Vector3>();
        public readonly List<int> triangles = new List<int>();
    }

    private sealed class ObjMaterial
    {
        public Color diffuse = Color.gray;
        public Vector3 ambient = Vector3.zero;
        public Vector3 specular = Vector3.zero;
        public float opacity = 1f, shininess = 40f;
    }

    private readonly struct FaceVertex
    {
        public readonly int position, normal;
        public FaceVertex(int position, int normal) { this.position = position; this.normal = normal; }
    }

    public static Scene Load(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".obj", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Choose a VMD Wavefront .obj file.");
        CheckFile(path, 128L * 1024 * 1024);
        var scene = new Scene();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var vertexColors = new List<Color>();
        var materials = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);
        var batches = new Dictionary<string, Part>(StringComparer.Ordinal);
        string group = "VMD scene", materialName = "";
        var defaultMaterial = new ObjMaterial();
        bool hasBounds = false;
        int lineNumber = 0;
        try
        {
            foreach (string raw in File.ReadLines(path))
            {
                if ((lineNumber++ & 1023) == 0) token.ThrowIfCancellationRequested();
                string line = StripComment(raw);
                if (line.Length == 0) continue;
                string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (words[0])
                {
                    case "v":
                        if (words.Length < 4) throw new FormatException("Incomplete vertex.");
                        positions.Add(new Vector3(Number(words[1]), Number(words[2]), -Number(words[3])));
                        vertexColors.Add(words.Length >= 7
                            ? new Color(Number(words[4]), Number(words[5]), Number(words[6])) : Color.white);
                        if (positions.Count > 2000000) throw new FormatException("OBJ exceeds the 2-million vertex input limit.");
                        break;
                    case "vn":
                        if (words.Length < 4) throw new FormatException("Incomplete normal.");
                        normals.Add(new Vector3(Number(words[1]), Number(words[2]), -Number(words[3])).normalized);
                        if (normals.Count > 2000000) throw new FormatException("Too many OBJ normals.");
                        break;
                    case "mtllib":
                        string library = line.Substring(words[0].Length).Trim().Trim('"');
                        string directory = Path.GetDirectoryName(path);
                        string materialPath = Path.GetFullPath(Path.Combine(directory, library));
                        if (Path.IsPathRooted(library) ||
                            !materialPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new FormatException("MTL must be in the OBJ folder or a subfolder.");
                        ReadMaterials(materialPath, materials, scene.warnings, token);
                        break;
                    case "usemtl":
                        materialName = line.Substring(words[0].Length).Trim();
                        break;
                    case "g":
                    case "o":
                        group = line.Substring(words[0].Length).Trim();
                        break;
                    case "f":
                        if (words.Length < 4) throw new FormatException("A face needs at least 3 vertices.");
                        if (words.Length > 257) throw new FormatException("Face is too large.");
                        if (!materials.TryGetValue(materialName, out ObjMaterial material))
                        {
                            material = defaultMaterial;
                            scene.warnings.Add("Some faces have no resolved MTL material; using gray.");
                        }
                        bool transparent = material.opacity < 0.999f;
                        string key = group + (transparent ? "\ntransparent" : "\nopaque");
                        FaceVertex a = Index(words[1], positions.Count, normals.Count);
                        for (int j = 2; j < words.Length - 1; j++)
                        {
                            // Reflect Z and reverse winding to convert OBJ to Unity handedness.
                            FaceVertex b = Index(words[j + 1], positions.Count, normals.Count);
                            FaceVertex c = Index(words[j], positions.Count, normals.Count);
                            if (!batches.TryGetValue(key, out Part part) || part.vertices.Count + 3 > 60000)
                            {
                                part = new Part { group = group, transparent = transparent };
                                batches[key] = part;
                                scene.parts.Add(part);
                            }
                            Vector3 faceNormal = Vector3.Cross(positions[b.position] - positions[a.position],
                                positions[c.position] - positions[a.position]).normalized;
                            Add(a, part, material, faceNormal);
                            Add(b, part, material, faceNormal);
                            Add(c, part, material, faceNormal);
                            if (++scene.triangleCount > 500000)
                                throw new FormatException("OBJ exceeds 500,000 triangles; reduce VMD resolution or selection size.");
                        }
                        break;
                    case "l":
                    case "p":
                        scene.warnings.Add("OBJ lines/points are not displayed by this triangle-only static loader.");
                        break;
                    case "vt":
                    case "s":
                        break;
                    default:
                        scene.warnings.Add("Unrecognized OBJ records were skipped: " + words[0]);
                        break;
                }
            }
        }
        catch (Exception error) when (!(error is OperationCanceledException))
        {
            throw new FormatException($"OBJ line {lineNumber}: {error.Message}", error);
        }
        if (scene.triangleCount == 0)
            scene.warnings.Add("VMD exported no triangles. Check visible representations and whether the molecule supports the selected method.");
        return scene;

        void Add(FaceVertex vertex, Part part, ObjMaterial material, Vector3 faceNormal)
        {
            Vector3 position = positions[vertex.position];
            if (!hasBounds) { scene.bounds = new Bounds(position, Vector3.zero); hasBounds = true; }
            else scene.bounds.Encapsulate(position);
            part.triangles.Add(part.vertices.Count);
            part.vertices.Add(position);
            Vector3 normal = vertex.normal >= 0 ? normals[vertex.normal] : faceNormal;
            part.normals.Add(normal.sqrMagnitude > 0 ? normal : faceNormal);
            Color color = material.diffuse * vertexColors[vertex.position];
            color.a = material.opacity;
            part.colors.Add(color);
            part.specular.Add(new Vector4(material.specular.x, material.specular.y, material.specular.z, material.shininess));
            part.ambient.Add(material.ambient);
        }
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return (hash >= 0 ? line.Substring(0, hash) : line).Trim();
    }

    private static void CheckFile(string path, long maxBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Export file not found. VMD and Unity must share the same filesystem.", path);
        if (file.Length > maxBytes) throw new FormatException("Export file exceeds the static import size limit.");
    }

    private static float Number(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            float.IsNaN(value) || float.IsInfinity(value))
            throw new FormatException("Invalid numeric value: " + text);
        return value;
    }

    private static FaceVertex Index(string token, int positions, int normals)
    {
        string[] parts = token.Split('/');
        if (parts.Length > 3) throw new FormatException("Invalid face index.");
        return new FaceVertex(Resolve(parts[0], positions),
            parts.Length == 3 && parts[2].Length > 0 ? Resolve(parts[2], normals) : -1);
    }

    private static int Resolve(string text, int count)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
            throw new FormatException("Invalid OBJ index: " + text);
        long index = value > 0 ? (long)value - 1 : (long)count + value;
        if (index < 0 || index >= count) throw new FormatException("OBJ index outside available data.");
        return (int)index;
    }

    private static void ReadMaterials(string path, Dictionary<string, ObjMaterial> materials,
        HashSet<string> warnings, CancellationToken token)
    {
        CheckFile(path, 16L * 1024 * 1024);
        ObjMaterial current = null;
        foreach (string raw in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (line.Length == 0) continue;
            string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words[0] == "newmtl")
            {
                current = new ObjMaterial();
                materials[line.Substring(6).Trim()] = current;
                continue;
            }
            if (current == null) continue;
            if ((words[0] == "Kd" || words[0] == "Ks" || words[0] == "Ka") && words.Length >= 4)
            {
                Vector3 value = new Vector3(Number(words[1]), Number(words[2]), Number(words[3]));
                if (words[0] == "Kd") current.diffuse = new Color(value.x, value.y, value.z);
                else if (words[0] == "Ks") current.specular = value;
                else current.ambient = value;
            }
            else if (words[0] == "Ns" && words.Length >= 2)
                current.shininess = Mathf.Clamp(Number(words[1]), 1, 1000);
            else if ((words[0] == "d" || words[0] == "Tr") && words.Length >= 2)
                current.opacity = Mathf.Clamp01(words[0] == "Tr" ? 1 - Number(words[1]) : Number(words[words.Length - 1]));
            else if (words[0].StartsWith("map_", StringComparison.Ordinal))
                warnings.Add("Texture maps are not imported; this loader uses OBJ geometry and MTL colors.");
        }
    }
}
