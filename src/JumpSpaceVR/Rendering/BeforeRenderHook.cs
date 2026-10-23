using System;
using HarmonyLib;
using UnityEngine;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Equivalent of Application.onBeforeRender. The event's add accessor was stripped from the game build,
/// so this patches the engine-invoked BeforeRenderHelper.Invoke instead. Runs after LateUpdate, right
/// before the frame is rendered.
/// </summary>
internal static class BeforeRenderHook
{
    private static Action _callback;

    public static bool Install(HarmonyLib.Harmony harmony, Action callback)
    {
        _callback = callback;
        try
        {
            var target = AccessTools.Method(typeof(BeforeRenderHelper), nameof(BeforeRenderHelper.Invoke));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(BeforeRenderHook), nameof(Postfix)));
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not hook BeforeRenderHelper.Invoke: {exception.Message}");
            return false;
        }
    }

    private static void Postfix()
    {
        try
        {
            _callback?.Invoke();
        }
        catch (Exception exception)
        {
            Log.Error($"Before-render callback failed: {exception}");
        }
    }
}
