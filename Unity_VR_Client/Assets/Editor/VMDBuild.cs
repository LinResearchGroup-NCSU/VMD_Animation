using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.XR.OpenXR;

// Command-line build of the Windows player, called by "vr build" (tools/vr.ps1) as
//   Unity.exe -batchmode -quit -buildTarget Win64 -projectPath Unity_VR_Client
//     -executeMethod VMDBuild.BuildWindows -vmdStereo SPI|MultiPass -vmdOutput <folder>
//
// One Windows OpenXR player serves every headset: the PC renders and the headset only displays and
// sends input (Valve Index through SteamVR; Meta Quest through Meta Horizon Link, Air Link or Steam
// Link). The OpenXR runtime is chosen when the player starts, not at build time.
//
// -vmdStereo picks the OpenXR stereo render mode for this build only (Single Pass Instanced or Multi
// Pass); the project setting is restored afterwards, so a build never leaves the project modified.
// -vmdOutput is the player folder; the player is <folder>/Unity_VR_Client.exe.
// The Editor exits with 0 on success and 1 on any failure.
public static class VMDBuild
{
    private const string ExecutableName = "Unity_VR_Client.exe";

    public static void BuildWindows()
    {
        int exitCode = 1;
        OpenXRSettings settings = null;
        OpenXRSettings.RenderMode? previousMode = null;
        try
        {
            string stereo = Argument("-vmdStereo") ?? "SPI";
            string output = Argument("-vmdOutput") ?? Path.Combine("Builds", "Windows", stereo);
            OpenXRSettings.RenderMode mode = ParseStereo(stereo);

            settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if (settings == null)
                throw new InvalidOperationException("No OpenXR settings for the Standalone build target.");
            previousMode = settings.renderMode;
            settings.renderMode = mode;

            string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
                throw new InvalidOperationException("No enabled scenes in File > Build Profiles.");

            string folder = Path.GetFullPath(output);
            Directory.CreateDirectory(folder);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(folder, ExecutableName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            Debug.Log($"[VMDBuild] Building {stereo} ({mode}) to {options.locationPathName}; scenes: {string.Join(", ", scenes)}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"[VMDBuild] Result: {summary.result}, {summary.totalErrors} error(s), {summary.totalWarnings} warning(s), " +
                      $"{summary.totalSize / (1024 * 1024)} MB, {summary.totalTime.TotalSeconds:F0} s.");
            if (summary.result == BuildResult.Succeeded)
                exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[VMDBuild] Build failed: {exception}");
        }
        finally
        {
            if (settings != null && previousMode.HasValue && settings.renderMode != previousMode.Value)
            {
                settings.renderMode = previousMode.Value;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
        }
        EditorApplication.Exit(exitCode);
    }

    private static OpenXRSettings.RenderMode ParseStereo(string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "spi":
            case "singlepassinstanced":
                return OpenXRSettings.RenderMode.SinglePassInstanced;
            case "multipass":
                return OpenXRSettings.RenderMode.MultiPass;
            default:
                throw new ArgumentException($"-vmdStereo must be SPI or MultiPass, not '{value}'.");
        }
    }

    // The value after 'name' on the Editor command line, or null.
    private static string Argument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length - 1; i++)
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                return arguments[i + 1];
        return null;
    }
}
