using System.Collections.Generic;
using UnityEngine.Rendering.HighDefinition;

namespace JumpSpaceVR.Rendering;

/// <summary>How much of HDRP's per-camera rendering the eye cameras do, for slower graphics cards.</summary>
internal enum VrGraphicsQuality
{
    /// <summary>Whatever the game's own graphics settings choose.</summary>
    Game,
    /// <summary>Drops effects that only hurt in VR (motion blur, depth of field, film grain, lens effects).</summary>
    High,
    /// <summary>Also drops screen-space reflections and global illumination, contact shadows and volumetric clouds.</summary>
    Medium,
    /// <summary>Also drops volumetric fog, ambient occlusion, refraction and distortion.</summary>
    Low,
}

/// <summary>
/// Applies <see cref="VrGraphicsQuality"/> to the eye cameras through HDRP's per-camera frame settings (the game's
/// camera already uses custom frame settings, so this only adds overrides) and puts the game's values back for Game.
/// Each eye renders the full frame, so every effect switched off saves its cost twice.
/// </summary>
internal sealed class GraphicsQuality
{
    private static readonly FrameSettingsField[] HighOff =
    {
        FrameSettingsField.MotionBlur, FrameSettingsField.DepthOfField, FrameSettingsField.FilmGrain,
        FrameSettingsField.LensDistortion, FrameSettingsField.ChromaticAberration, FrameSettingsField.PaniniProjection,
    };

    private static readonly FrameSettingsField[] MediumOff =
    {
        FrameSettingsField.SSR, FrameSettingsField.TransparentSSR, FrameSettingsField.SSGI, FrameSettingsField.ContactShadows,
        FrameSettingsField.VolumetricClouds,
    };

    private static readonly FrameSettingsField[] LowOff =
    {
        FrameSettingsField.Volumetrics, FrameSettingsField.SSAO, FrameSettingsField.Refraction,
        FrameSettingsField.RoughRefraction, FrameSettingsField.Distortion, FrameSettingsField.RoughDistortion,
    };

    private readonly Dictionary<int, Dictionary<FrameSettingsField, (bool Overridden, bool Enabled)>> _originals = new();

    /// <summary>Applies the quality to one camera's HDRP data. Call when the setting or the camera changes.</summary>
    public void Apply(HDAdditionalCameraData data, VrGraphicsQuality quality)
    {
        if (data == null)
            return;
        var id = data.GetInstanceID();
        if (!_originals.TryGetValue(id, out var originals))
        {
            originals = new Dictionary<FrameSettingsField, (bool, bool)>();
            _originals[id] = originals;
        }

        var off = new HashSet<FrameSettingsField>();
        if (quality >= VrGraphicsQuality.High)
            off.UnionWith(HighOff);
        if (quality >= VrGraphicsQuality.Medium)
            off.UnionWith(MediumOff);
        if (quality >= VrGraphicsQuality.Low)
            off.UnionWith(LowOff);

        data.customRenderingSettings = true;
        // Through the backing field: the interop's by-reference property hands out a copy, so writes to it are lost.
        var settings = data.m_RenderingPathCustomFrameSettings;
        var overrideMask = data.renderingPathCustomFrameSettingsOverrideMask;
        var bits = overrideMask.mask;
        foreach (var field in Fields())
        {
            if (off.Contains(field))
            {
                if (!originals.ContainsKey(field))
                    originals[field] = (bits[(uint)field], settings.IsEnabled(field));
                bits[(uint)field] = true;
                settings.SetEnabled(field, false);
            }
            else if (originals.TryGetValue(field, out var original))
            {
                // Hand the setting back to the game.
                bits[(uint)field] = original.Overridden;
                settings.SetEnabled(field, original.Enabled);
                originals.Remove(field);
            }
        }
        overrideMask.mask = bits;
        data.renderingPathCustomFrameSettingsOverrideMask = overrideMask;
        data.m_RenderingPathCustomFrameSettings = settings;
    }

    private static IEnumerable<FrameSettingsField> Fields()
    {
        foreach (var field in HighOff)
            yield return field;
        foreach (var field in MediumOff)
            yield return field;
        foreach (var field in LowOff)
            yield return field;
    }
}
