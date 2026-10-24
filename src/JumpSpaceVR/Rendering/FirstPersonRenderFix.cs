using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppKeepsake;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// The game draws the first-person arms and weapon (layer 29) with its own HDRP custom passes, a separate flat
/// camera and its own field of view (Keepsake.FirstPersonCamera, its depth pre-pass and motion vectors, and the
/// fullscreen FirstPersonCustomPass); the main camera excludes that layer. In a headset those passes produce the
/// same flat image in both eyes, so the weapon cannot be fused and is seen double.
///
/// While enabled, this skips all of those passes and adds the first-person layer to the eye cameras instead, so
/// the weapon renders as normal scene geometry through the real per-eye projections.
/// </summary>
internal static class FirstPersonRenderFix
{
    private static bool _enabled;
    private static bool _hooked;

    public static bool Enabled => _enabled;

    /// <summary>True once the game's weapon pass can be skipped; without it the fix stays off (no double weapon).</summary>
    public static bool Hooked => _hooked;

    // The main pass must be skippable for the fix to be safe; the others are skipped too when present.
    private static readonly Type[] FirstPersonPasses =
    {
        typeof(FirstPersonCamera),
        typeof(FirstPersonCameraPrePass),
        typeof(FirstPersonCameraMotionVectors),
        typeof(Il2Cpp.FirstPersonCustomPass),
    };

    private static readonly Dictionary<string, int> SkippedCalls = new();

    public static bool InstallHooks(HarmonyLib.Harmony harmony)
    {
        foreach (var pass in FirstPersonPasses)
        {
            try
            {
                // The base CustomPass declares more Execute overloads, so name the parameter list explicitly.
                var execute = AccessTools.DeclaredMethod(pass, "Execute", new[] { typeof(CustomPassContext) });
                if (execute == null)
                    throw new MissingMethodException(pass.Name, "Execute");
                harmony.Patch(execute, prefix: new HarmonyMethod(typeof(FirstPersonRenderFix), nameof(ExecutePrefix)));
                if (pass == typeof(FirstPersonCamera))
                    _hooked = true;
            }
            catch (Exception exception)
            {
                Log.Warning($"Could not hook first-person pass {pass.Name}: {exception.Message}");
            }
        }
        return _hooked;
    }

    public static void Apply(bool enabled)
    {
        enabled &= _hooked;
        if (enabled == _enabled)
            return;
        _enabled = enabled;
        Log.Msg($"First-person weapon {(enabled ? "rendered in stereo by the eye cameras" : "back on the game's own pass")}.");
    }

    /// <summary>
    /// Culling mask bit for the first-person layer, to add to eye cameras while the fix is active. Read every time:
    /// the game sets the layer index once a level loads, and before that it reads 0 (which would add the Default
    /// layer instead and leave the weapon out of the eye cameras).
    /// </summary>
    public static int LayerMask
    {
        get
        {
            if (!_enabled)
                return 0;
            var layer = FirstPersonCamera.LayerIndex;
            return 1 << (layer > 0 ? layer : DefaultFirstPersonLayer);
        }
    }

    private const int DefaultFirstPersonLayer = 29;

    private static bool ExecutePrefix(CustomPass __instance)
    {
        if (!_enabled)
            return true;
        var name = __instance.GetIl2CppType().Name;
        SkippedCalls[name] = SkippedCalls.TryGetValue(name, out var count) ? count + 1 : 1;
        return false;
    }

    /// <summary>How often each first-person pass was skipped, for diagnostics and tests.</summary>
    public static string DescribeSkippedPasses()
    {
        if (SkippedCalls.Count == 0)
            return "none skipped";
        var text = new StringBuilder();
        foreach (var (name, count) in SkippedCalls)
            text.Append($"{name}={count} ");
        return text.ToString().TrimEnd();
    }

    public static int SkippedPassCount(string name) => SkippedCalls.TryGetValue(name, out var count) ? count : 0;

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
        return $"layer {layer}, fix active={_enabled}, passes skipped: {DescribeSkippedPasses()}, renderers:\n    " +
               string.Join("\n    ", roots);
    }
}
