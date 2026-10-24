using System;
using HarmonyLib;
using UnityEngine;

namespace JumpSpaceVR.Tracking;

/// <summary>
/// Makes the game's Wwise listener follow the headset. The listener sits on the game camera, which the mod only turns
/// to the head pose just before rendering, after Wwise has already read it; so sound would follow the game's camera,
/// not the player's head. After the camera's AkGameObj updates its Wwise position each frame, this re-sends it with
/// the latest head pose (one frame old, which is inaudible).
/// </summary>
internal sealed class HeadAudio
{
    private static HeadAudio _instance;
    private GameObject _listener;
    private Vector3 _position;
    private Vector3 _forward = Vector3.forward;
    private Vector3 _up = Vector3.up;
    private int _poseFrame = -10;

    public bool Enabled { get; set; } = true;

    /// <summary>Frames sent with the head pose (diagnostics and tests).</summary>
    public static int Updates { get; private set; }

    public static bool InstallHooks(HarmonyLib.Harmony harmony)
    {
        try
        {
            var update = AccessTools.DeclaredMethod(typeof(Il2Cpp.AkGameObj), nameof(Il2Cpp.AkGameObj.Update), Type.EmptyTypes);
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(HeadAudio), nameof(AfterAkGameObjUpdate)));
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning($"Head-tracked audio unavailable (could not hook Wwise): {exception.Message}");
            return false;
        }
    }

    public void Activate() => _instance = this;

    /// <summary>Remembers the head pose of this frame (the point between the eyes, facing where the head faces).</summary>
    public void UpdateHeadPose(Camera leftEye, Camera rightEye)
    {
        if (leftEye == null || rightEye == null)
            return;
        _listener = leftEye.gameObject;
        _position = (leftEye.transform.position + rightEye.transform.position) * 0.5f;
        _forward = leftEye.transform.forward;
        _up = leftEye.transform.up;
        _poseFrame = Time.frameCount;
    }

    private static void AfterAkGameObjUpdate(Il2Cpp.AkGameObj __instance)
    {
        var audio = _instance;
        if (audio == null || !audio.Enabled || audio._listener == null || Time.frameCount - audio._poseFrame > 2)
            return;
        if (__instance.gameObject.GetInstanceID() != audio._listener.GetInstanceID())
            return;
        try
        {
            Il2Cpp.AkSoundEngine.SetObjectPosition(audio._listener, audio._position, audio._forward, audio._up);
            Updates++;
        }
        catch (Exception exception)
        {
            audio.Enabled = false;
            Log.Warning($"Head-tracked audio turned off: {exception.Message}");
        }
    }
}
