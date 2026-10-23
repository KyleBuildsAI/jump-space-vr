using MelonLoader;
using UnityEngine;

namespace JumpSpaceVR;

/// <summary>User-editable settings, stored in UserData/MelonPreferences.cfg under [JumpSpaceVR].</summary>
internal sealed class ModConfig
{
    private readonly MelonPreferences_Category _category;

    public MelonPreferences_Entry<bool> StartInVr { get; }
    public MelonPreferences_Entry<KeyCode> ToggleVrKey { get; }
    public MelonPreferences_Entry<KeyCode> RecenterKey { get; }
    public MelonPreferences_Entry<KeyCode> DiagnosticsKey { get; }
    public MelonPreferences_Entry<bool> PositionalTracking { get; }
    public MelonPreferences_Entry<float> WorldScale { get; }
    public MelonPreferences_Entry<float> RenderScale { get; }
    public MelonPreferences_Entry<bool> SinglePassInstanced { get; }
    public MelonPreferences_Entry<bool> MoveUiIntoVr { get; }
    public MelonPreferences_Entry<float> UiDistance { get; }
    public MelonPreferences_Entry<float> UiWidth { get; }
    public MelonPreferences_Entry<string> OpenXRRuntimeJson { get; }

    public ModConfig()
    {
        _category = MelonPreferences.CreateCategory("JumpSpaceVR", "Jump Space VR");

        StartInVr = _category.CreateEntry("StartInVr", true, "Start in VR",
            "Start the OpenXR session automatically once the game has loaded.");
        ToggleVrKey = _category.CreateEntry("ToggleVrKey", KeyCode.F8, "Toggle VR key");
        RecenterKey = _category.CreateEntry("RecenterKey", KeyCode.F7, "Recenter key",
            "Resets the seated position and facing direction to the current headset pose.");
        DiagnosticsKey = _category.CreateEntry("DiagnosticsKey", KeyCode.F9, "Diagnostics key",
            "Writes camera, canvas and XR state to the MelonLoader log.");
        PositionalTracking = _category.CreateEntry("PositionalTracking", true, "Positional tracking",
            "Apply headset translation (6DoF). Disable for rotation-only head tracking.");
        WorldScale = _category.CreateEntry("WorldScale", 1.0f, "World scale",
            "Multiplier for headset translation. Raise it if the world feels too big.");
        RenderScale = _category.CreateEntry("RenderScale", 1.0f, "Render scale",
            "Eye texture resolution multiplier (0.5 - 2.0). Lower it for performance.");
        SinglePassInstanced = _category.CreateEntry("SinglePassInstanced", false, "Single pass instanced",
            "Experimental. Faster, but the game's shaders may not support it (black or one-eye rendering).");
        MoveUiIntoVr = _category.CreateEntry("MoveUiIntoVr", true, "Move UI into VR",
            "Re-render screen-space menus and HUD on a panel floating in front of you.");
        UiDistance = _category.CreateEntry("UiDistance", 1.6f, "UI distance (m)");
        UiWidth = _category.CreateEntry("UiWidth", 1.9f, "UI width (m)");
        OpenXRRuntimeJson = _category.CreateEntry("OpenXRRuntimeJson", "", "OpenXR runtime override",
            "Optional path to an OpenXR runtime manifest (.json). Leave empty to use the system's active runtime.");
    }

    public float ClampedRenderScale => Mathf.Clamp(RenderScale.Value, 0.5f, 2.0f);
}
