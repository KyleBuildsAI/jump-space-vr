using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppKeepsake;
using UnityEngine;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// The game draws the first-person arms and weapon (layer 29) with its own HDRP custom pass, its own camera and
/// its own field of view (Keepsake.FirstPersonCamera); the main camera excludes that layer. In a headset that
/// pass produces the same flat image in both eyes, so the weapon cannot be fused and is seen double.
///
/// While enabled, this skips the custom pass and adds the first-person layer to the eye cameras instead, so the
/// weapon renders as normal scene geometry through the real per-eye projections.
/// </summary>
internal static class FirstPersonRenderFix
{
    private static bool _enabled;
    private static int _layerMask;

    public static bool Enabled => _enabled;

    public static bool InstallHooks(HarmonyLib.Harmony harmony)
    {
        try
        {
            var execute = AccessTools.Method(typeof(FirstPersonCamera), nameof(FirstPersonCamera.Execute));
            harmony.Patch(execute, prefix: new HarmonyMethod(typeof(FirstPersonRenderFix), nameof(ExecutePrefix)));
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not hook the first-person weapon pass: {exception.Message}");
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        if (enabled == _enabled)
            return;
        _enabled = enabled;
        Log.Msg($"First-person weapon {(enabled ? "rendered in stereo by the eye cameras" : "back on the game's own pass")}.");
    }

    /// <summary>Culling mask bit for the first-person layer, to add to eye cameras while the fix is active.</summary>
    public static int LayerMask
    {
        get
        {
            if (!_enabled)
                return 0;
            if (_layerMask == 0)
            {
                try
                {
                    _layerMask = 1 << FirstPersonCamera.LayerIndex;
                }
                catch (Exception)
                {
                    _layerMask = 1 << 29;
                }
            }
            return _layerMask;
        }
    }

    private static bool ExecutePrefix() => !_enabled;

    /// <summary>Lists the hierarchies drawn on the first-person layer (arms, weapon) for diagnostics.</summary>
    public static string DescribeFirstPersonObjects()
    {
        int layer;
        try
        {
            layer = FirstPersonCamera.LayerIndex;
        }
        catch (Exception exception)
        {
            return $"unavailable ({exception.GetType().Name})";
        }

        var roots = new HashSet<string>();
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer.gameObject.layer != layer || !renderer.enabled)
                continue;
            var path = new StringBuilder(renderer.name);
            for (var parent = renderer.transform.parent; parent != null; parent = parent.parent)
            {
                path.Insert(0, parent.name + "/");
                if (parent.name == "MainCameraRoot" || parent.name == "PlayerCamera")
                    break;
            }
            roots.Add(path.ToString());
            if (roots.Count >= 12)
                break;
        }
        return $"layer {layer}, fix active={_enabled}, renderers:\n    " + string.Join("\n    ", roots);
    }
}
