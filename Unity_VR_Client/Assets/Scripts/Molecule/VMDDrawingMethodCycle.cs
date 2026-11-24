using System;
using System.Collections.Generic;

// Order of the drawing-method cycle (right B / RepresentationController.NextRepresentation).
// Plain C# without UnityEngine, so it can be checked outside the Editor.
//
// The cycle starts at Original, the scene as its own script set it up, which the bridge can
// restore after every change (VMD_VR_Bridge/static_scene.tcl, scene_set_representation). The
// other entries are the methods Unity draws. When every displayed rep already uses one of them,
// that method is left out: Original already shows it, with the script's own parameters, and a
// second copy made the cycle look stuck (QuickSurf -> NewCartoon -> Original, which looked like
// QuickSurf again). A scene with mixed methods (for example cartoon protein, stick membrane and
// sphere ions) keeps all four, since none of them reproduces it.
public static class VMDDrawingMethodCycle
{
    public const string Original = "Original";
    public static readonly string[] Methods = { "VDW", "CPK", "QuickSurf", "NewCartoon" };

    // The cycle for a scene whose displayed reps use 'displayed' (method names in any letter case;
    // VMD also writes "Quicksurf"). Original comes first.
    public static string[] Build(IEnumerable<string> displayed)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (displayed != null) foreach (string method in displayed) if (!string.IsNullOrEmpty(method)) used.Add(method);
        string single = null;
        if (used.Count == 1) foreach (string method in used) single = method;
        var cycle = new List<string> { Original };
        foreach (string method in Methods)
            if (!string.Equals(method, single, StringComparison.OrdinalIgnoreCase)) cycle.Add(method);
        return cycle.ToArray();
    }

    // The entry 'direction' steps (+1 next, -1 previous) after 'current', wrapping around. When
    // 'current' is not in the cycle, the cycle is taken to be at Original.
    public static string Next(string[] cycle, string current, int direction)
    {
        if (cycle == null || cycle.Length == 0) throw new ArgumentException("Empty cycle.", nameof(cycle));
        int index = Array.FindIndex(cycle, entry => string.Equals(entry, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        int step = direction >= 0 ? 1 : -1;
        return cycle[(index + step + cycle.Length) % cycle.Length];
    }
}
