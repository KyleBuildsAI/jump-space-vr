using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace JumpSpaceVR.Rendering;

/// <summary>Colour grading presets applied on top of the game's own look.</summary>
internal enum ColorPreset
{
    Off,
    Vivid,
    Cinematic,
    ColdSpace,
    Warm,
    Bright,
    Noir,
}

/// <summary>
/// Colour grading inside HDRP: one global, high-priority volume whose overrides (colour adjustments, white balance)
/// both eye cameras pick up, so the grade is identical in each eye, costs almost nothing and needs no ReShade.
/// A preset only overrides the values it changes; everything else stays as the game's own volumes set it.
/// </summary>
internal sealed class ColorGrading
{
    private const float Priority = 1000f;

    private GameObject _root;
    private Volume _volume;
    private ColorAdjustments _adjustments;
    private WhiteBalance _whiteBalance;
    private ColorPreset _applied = ColorPreset.Off;
    private bool _failed;

    public void Apply(ColorPreset preset)
    {
        if (preset == _applied && (preset == ColorPreset.Off || _volume != null))
            return;
        if (preset == ColorPreset.Off)
        {
            if (_volume != null)
                _volume.enabled = false;
            _applied = preset;
            return;
        }
        if (!EnsureVolume())
            return;

        ResetOverrides();
        switch (preset)
        {
            case ColorPreset.Vivid:
                Set(_adjustments.saturation, 25f);
                Set(_adjustments.contrast, 12f);
                break;
            case ColorPreset.Cinematic:
                Set(_adjustments.contrast, 20f);
                Set(_adjustments.saturation, -10f);
                Set(_whiteBalance.temperature, 8f);
                break;
            case ColorPreset.ColdSpace:
                Set(_whiteBalance.temperature, -20f);
                Set(_whiteBalance.tint, -5f);
                Set(_adjustments.contrast, 10f);
                Set(_adjustments.saturation, 5f);
                break;
            case ColorPreset.Warm:
                Set(_whiteBalance.temperature, 20f);
                Set(_adjustments.saturation, 10f);
                break;
            case ColorPreset.Bright:
                Set(_adjustments.postExposure, 0.5f);
                Set(_adjustments.contrast, 5f);
                break;
            case ColorPreset.Noir:
                Set(_adjustments.saturation, -100f);
                Set(_adjustments.contrast, 30f);
                break;
        }
        _volume.enabled = true;
        _applied = preset;
        Log.Msg($"Colour grading: {preset}.");
    }

    private void ResetOverrides()
    {
        _adjustments.postExposure.overrideState = false;
        _adjustments.contrast.overrideState = false;
        _adjustments.saturation.overrideState = false;
        _whiteBalance.temperature.overrideState = false;
        _whiteBalance.tint.overrideState = false;
    }

    private static void Set(FloatParameter parameter, float value)
    {
        parameter.value = value;
        parameter.overrideState = true;
    }

    private static void Set(ClampedFloatParameter parameter, float value)
    {
        parameter.value = value;
        parameter.overrideState = true;
    }

    private bool EnsureVolume()
    {
        if (_volume != null && _root != null)
            return true;
        if (_failed)
            return false;
        try
        {
            _root = new GameObject("JumpSpaceVR_ColorGrading") { layer = 0 };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _volume = _root.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = Priority;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _volume.sharedProfile = profile;
            _adjustments = profile.Add(Il2CppType.Of<ColorAdjustments>(), false).Cast<ColorAdjustments>();
            _whiteBalance = profile.Add(Il2CppType.Of<WhiteBalance>(), false).Cast<WhiteBalance>();
            return true;
        }
        catch (Exception exception)
        {
            _failed = true;
            Log.Warning($"Colour grading unavailable: {exception.Message}");
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _volume = null;
            return false;
        }
    }
}
