using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// Limits how far the sun's shadows reach while in VR. HDRP draws the shadow maps once per camera, so each eye pays
/// for every shadow-casting object again; a shorter shadow distance (and fewer cascades) cuts that CPU work, which is
/// what limits the frame rate in VR. A global, high-priority volume overrides the game's shadow settings; distance 0
/// hands them back.
/// </summary>
internal sealed class ShadowBudget
{
    private const int Priority = 1000;

    private GameObject _root;
    private Volume _volume;
    private HDShadowSettings _shadows;
    private bool _failed;
    private float _applied = -1f;

    /// <summary>
    /// The shadow distance for a setting (meters, 0 = automatic) and VR graphics level: automatic limits shadows at
    /// Low (40 m) and Medium (80 m), where frame rate matters more than far shadows. Measured in the hangar at the
    /// Low preset: 60 fps with the game's shadows, 69 fps at 60 m, 71 fps at 25 m.
    /// </summary>
    public static float DistanceFor(float setting, VrGraphicsQuality quality) =>
        setting > 0f ? setting : quality switch
        {
            VrGraphicsQuality.Low => 40f,
            VrGraphicsQuality.Medium => 80f,
            _ => 0f,
        };

    /// <summary>Applies a shadow distance in meters; 0 (or less) uses the game's own settings.</summary>
    public void Apply(float distance)
    {
        distance = Mathf.Max(0f, distance);
        if (Mathf.Approximately(distance, _applied))
            return;
        _applied = distance;
        if (distance <= 0f)
        {
            if (_volume != null)
                _volume.enabled = false;
            return;
        }
        if (!EnsureVolume())
            return;
        _shadows.maxShadowDistance.value = distance;
        _shadows.maxShadowDistance.overrideState = true;
        // Two cascades cover a short distance well and draw the casters half as often as four.
        _shadows.cascadeShadowSplitCount.value = 2;
        _shadows.cascadeShadowSplitCount.overrideState = true;
        _volume.enabled = true;
        Log.Msg($"VR shadow distance: {distance:F0} m.");
    }

    private bool EnsureVolume()
    {
        if (_volume != null && _root != null)
            return true;
        if (_failed)
            return false;
        try
        {
            _root = new GameObject("JumpSpaceVR_ShadowBudget") { layer = 0 };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _volume = _root.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = Priority;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _volume.sharedProfile = profile;
            _shadows = profile.Add(Il2CppType.Of<HDShadowSettings>(), false).Cast<HDShadowSettings>();
            return true;
        }
        catch (Exception exception)
        {
            _failed = true;
            Log.Warning($"VR shadow distance unavailable: {exception.Message}");
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _volume = null;
            return false;
        }
    }
}
