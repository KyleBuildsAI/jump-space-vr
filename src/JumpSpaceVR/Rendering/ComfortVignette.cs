using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace JumpSpaceVR.Rendering;

/// <summary>How strongly the edges of the view darken while the game moves or turns you.</summary>
internal enum ComfortVignetteLevel
{
    Off,
    Low,
    Medium,
    High,
}

/// <summary>
/// Comfort vignette: while the stick moves or turns the player (motion the inner ear doesn't feel), the edges of the
/// view darken, which reduces motion sickness for many people. Only artificial motion counts: the view's base
/// position and yaw, which the game and the mod's turning move, never the player's own head movement. Drawn with a
/// global HDRP vignette override on a high-priority volume.
/// </summary>
internal sealed class ComfortVignette
{
    private const int Priority = 1001;
    // Full strength at a run or a brisk smooth turn.
    private const float FullSpeed = 3f;
    private const float FullTurnRate = 90f;
    // Jumps this large in one frame are teleports or recentering, not motion.
    private const float MaxPlausibleStep = 5f;
    private const float RiseSeconds = 0.1f;
    private const float FallSeconds = 0.3f;

    private GameObject _root;
    private Volume _volume;
    private Vignette _vignette;
    private bool _failed;
    private bool _hasLast;
    private Vector3 _lastPosition;
    private float _lastYaw;

    /// <summary>Current vignette strength (0 to 1, for tests).</summary>
    public float Intensity { get; private set; }

    /// <summary>Call once per rendered frame with the view's base pose (before head tracking is added).</summary>
    public void Update(ComfortVignetteLevel level, Vector3 basePosition, Quaternion baseRotation)
    {
        var deltaTime = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
        var yaw = baseRotation.eulerAngles.y;
        var target = 0f;
        if (_hasLast && level != ComfortVignetteLevel.Off)
        {
            var step = basePosition - _lastPosition;
            step.y = 0f;
            var speed = step.magnitude < MaxPlausibleStep ? step.magnitude / deltaTime : 0f;
            var turnRate = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) / deltaTime;
            target = Strength(level) * Mathf.Clamp01(Mathf.Max(speed / FullSpeed, turnRate / FullTurnRate));
        }
        _lastPosition = basePosition;
        _lastYaw = yaw;
        _hasLast = true;

        var seconds = target > Intensity ? RiseSeconds : FallSeconds;
        Intensity = Mathf.Lerp(Intensity, target, 1f - Mathf.Exp(-deltaTime / seconds));
        if (Intensity < 0.005f && target <= 0f)
            Intensity = 0f;
        Show(Intensity);
    }

    /// <summary>Removes the vignette (VR stopped).</summary>
    public void Clear()
    {
        Intensity = 0f;
        _hasLast = false;
        Show(0f);
    }

    private static float Strength(ComfortVignetteLevel level) => level switch
    {
        ComfortVignetteLevel.Low => 0.3f,
        ComfortVignetteLevel.Medium => 0.45f,
        ComfortVignetteLevel.High => 0.6f,
        _ => 0f,
    };

    private void Show(float intensity)
    {
        if (intensity <= 0f)
        {
            if (_volume != null)
                _volume.enabled = false;
            return;
        }
        if (!EnsureVolume())
            return;
        _vignette.intensity.value = intensity;
        _vignette.intensity.overrideState = true;
        _volume.enabled = true;
    }

    private bool EnsureVolume()
    {
        if (_volume != null && _root != null)
            return true;
        if (_failed)
            return false;
        try
        {
            _root = new GameObject("JumpSpaceVR_ComfortVignette") { layer = 0 };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _volume = _root.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = Priority;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _volume.sharedProfile = profile;
            _vignette = profile.Add(Il2CppType.Of<Vignette>(), false).Cast<Vignette>();
            _vignette.color.value = Color.black;
            _vignette.color.overrideState = true;
            _vignette.center.value = new Vector2(0.5f, 0.5f);
            _vignette.center.overrideState = true;
            _vignette.smoothness.value = 1f;
            _vignette.smoothness.overrideState = true;
            _vignette.rounded.value = true;
            _vignette.rounded.overrideState = true;
            return true;
        }
        catch (Exception exception)
        {
            _failed = true;
            Log.Warning($"Comfort vignette unavailable: {exception.Message}");
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _volume = null;
            return false;
        }
    }
}
