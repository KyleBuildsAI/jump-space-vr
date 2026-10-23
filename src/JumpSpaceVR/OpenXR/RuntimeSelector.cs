using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace JumpSpaceVR.OpenXR;

/// <summary>Which OpenXR runtime the mod connects to.</summary>
internal enum RuntimePreference
{
    /// <summary>The VR app that is running (SteamVR / Steam Link, Virtual Desktop), else the Windows default.</summary>
    Auto,
    /// <summary>Whatever Windows has set as the active OpenXR runtime.</summary>
    SystemDefault,
    /// <summary>SteamVR (also used by Steam Link and by Virtual Desktop in SteamVR mode).</summary>
    SteamVR,
    /// <summary>Virtual Desktop's own runtime (VDXR).</summary>
    VirtualDesktop,
    /// <summary>Meta Quest Link / Air Link (the Meta Horizon PC app).</summary>
    MetaLink,
}

/// <summary>A runtime the mod can try, identified by its OpenXR manifest (null = Windows default).</summary>
internal readonly record struct RuntimeCandidate(string Name, string ManifestPath);

/// <summary>
/// Picks the OpenXR runtime(s) to try. The OpenXR loader honours XR_RUNTIME_JSON, so the mod can target a specific
/// runtime without changing the user's system default; UnityOpenXR reloads the loader on every attempt.
/// </summary>
internal static class RuntimeSelector
{
    private const string KhronosKey = @"SOFTWARE\Khronos\OpenXR\1";

    public static List<RuntimeCandidate> Candidates(RuntimePreference preference, string customManifest)
    {
        var candidates = new List<RuntimeCandidate>();
        if (!string.IsNullOrWhiteSpace(customManifest))
        {
            if (File.Exists(customManifest))
                candidates.Add(new RuntimeCandidate($"custom ({Path.GetFileName(customManifest)})", customManifest));
            else
                Log.Warning($"OpenXR runtime override not found: {customManifest}. Ignoring it.");
            if (candidates.Count > 0)
                return candidates;
        }

        var steamVr = FindRegisteredRuntime("steamxr");
        var virtualDesktop = FindRegisteredRuntime("virtualdesktop");
        var metaLink = FindRegisteredRuntime("oculus_openxr");

        switch (preference)
        {
            case RuntimePreference.SteamVR:
                Add(candidates, "SteamVR", steamVr);
                break;
            case RuntimePreference.VirtualDesktop:
                Add(candidates, "Virtual Desktop (VDXR)", virtualDesktop);
                break;
            case RuntimePreference.MetaLink:
                Add(candidates, "Meta Quest Link", metaLink);
                break;
            case RuntimePreference.SystemDefault:
                break;
            default:
                // Prefer the VR app the player is actually using right now.
                if (IsRunning("vrserver") || IsRunning("vrcompositor"))
                    Add(candidates, "SteamVR", steamVr);
                if (IsRunning("VirtualDesktop.Streamer"))
                    Add(candidates, "Virtual Desktop (VDXR)", virtualDesktop);
                break;
        }

        // The Windows default is always a reasonable last resort (and the only option if nothing else is found).
        candidates.Add(new RuntimeCandidate($"Windows default ({DescribeActiveRuntime()})", null));
        return candidates;
    }

    public static string DescribeActiveRuntime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KhronosKey);
            var path = key?.GetValue("ActiveRuntime") as string;
            return string.IsNullOrEmpty(path) ? "none set" : Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception exception)
        {
            return $"unknown: {exception.Message}";
        }
    }

    private static void Add(List<RuntimeCandidate> candidates, string name, string manifest)
    {
        if (manifest == null || candidates.Exists(candidate => candidate.ManifestPath == manifest))
            return;
        candidates.Add(new RuntimeCandidate(name, manifest));
    }

    /// <summary>Runtimes register their manifests under HKLM\SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes.</summary>
    private static string FindRegisteredRuntime(string fileNameFragment)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KhronosKey + @"\AvailableRuntimes");
            if (key != null)
            {
                foreach (var path in key.GetValueNames())
                {
                    if (Path.GetFileName(path).Contains(fileNameFragment, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                        return path;
                }
            }

            using var active = Registry.LocalMachine.OpenSubKey(KhronosKey);
            var activePath = active?.GetValue("ActiveRuntime") as string;
            if (activePath != null && Path.GetFileName(activePath).Contains(fileNameFragment, StringComparison.OrdinalIgnoreCase)
                && File.Exists(activePath))
            {
                return activePath;
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not read registered OpenXR runtimes: {exception.Message}");
        }
        return null;
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            var running = processes.Length > 0;
            foreach (var process in processes)
                process.Dispose();
            return running;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
