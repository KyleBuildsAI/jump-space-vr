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
    public MelonPreferences_Entry<KeyCode> SnapshotKey { get; }
    public MelonPreferences_Entry<KeyCode> ModMenuKey { get; }
    public MelonPreferences_Entry<KeyCode> UiSmallerKey { get; }
    public MelonPreferences_Entry<KeyCode> UiLargerKey { get; }
    public MelonPreferences_Entry<KeyCode> UiCloserKey { get; }
    public MelonPreferences_Entry<KeyCode> UiFurtherKey { get; }
    public MelonPreferences_Entry<bool> PositionalTracking { get; }
    public MelonPreferences_Entry<float> WorldScale { get; }
    public MelonPreferences_Entry<bool> LevelHorizon { get; }
    public MelonPreferences_Entry<bool> StereoWeapon { get; }
    public MelonPreferences_Entry<bool> StabilizeView { get; }
    public MelonPreferences_Entry<float> RenderScale { get; }
    public MelonPreferences_Entry<bool> SinglePassInstanced { get; }
    public MelonPreferences_Entry<bool> MoveUiIntoVr { get; }
    public MelonPreferences_Entry<float> UiDistance { get; }
    public MelonPreferences_Entry<float> UiWidth { get; }
    public MelonPreferences_Entry<VrUi.UiFollowMode> UiFollow { get; }
    public MelonPreferences_Entry<float> UiSmoothing { get; }
    public MelonPreferences_Entry<string> OpenXRRuntimeJson { get; }
    public MelonPreferences_Entry<bool> SnapshotOnStartup { get; }
    public MelonPreferences_Entry<bool> ControllersAsGamepad { get; }
    public MelonPreferences_Entry<bool> SyntheticControllers { get; }
    public MelonPreferences_Entry<bool> MockControllerInput { get; }

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
        SnapshotKey = _category.CreateEntry("SnapshotKey", KeyCode.F10, "Eye snapshot key",
            "Saves both eye images to UserData/JumpSpaceVR for troubleshooting.");
        ModMenuKey = _category.CreateEntry("ModMenuKey", KeyCode.Insert, "Mod menu key",
            "Opens the in-game VR settings menu (also: hold the right thumbstick click for one second).");
        UiSmallerKey = _category.CreateEntry("UiSmallerKey", KeyCode.F5, "UI smaller key");
        UiLargerKey = _category.CreateEntry("UiLargerKey", KeyCode.F6, "UI larger key");
        UiCloserKey = _category.CreateEntry("UiCloserKey", KeyCode.F3, "UI closer key");
        UiFurtherKey = _category.CreateEntry("UiFurtherKey", KeyCode.F4, "UI further key");
        PositionalTracking = _category.CreateEntry("PositionalTracking", true, "Positional tracking",
            "Apply headset translation (6DoF). Disable for rotation-only head tracking.");
        WorldScale = _category.CreateEntry("WorldScale", 1.0f, "World scale",
            "Multiplier for headset translation. Raise it if the world feels too big.");
        LevelHorizon = _category.CreateEntry("LevelHorizon", true, "Level horizon",
            "Only the game's turning (yaw) moves your view; your head does all looking up and down. " +
            "Strongly recommended for comfort.");
        StereoWeapon = _category.CreateEntry("StereoWeapon", true, "Stereo weapon",
            "Render the first-person weapon and arms with the eye cameras instead of the game's flat overlay pass, " +
            "so the gun is not seen double.");
        StabilizeView = _category.CreateEntry("StabilizeView", true, "Stabilize view",
            "Remove head bob, recoil kick and knockback shake from the headset view.");
        RenderScale = _category.CreateEntry("RenderScale", 1.0f, "Render scale",
            "Eye texture resolution multiplier (0.5 - 2.0). Lower it for performance.");
        SinglePassInstanced = _category.CreateEntry("SinglePassInstanced", false, "Single pass instanced",
            "Experimental. Faster, but the game's shaders may not support it (black or one-eye rendering).");
        MoveUiIntoVr = _category.CreateEntry("MoveUiIntoVr", true, "Move UI into VR",
            "Re-render screen-space menus and HUD on a panel floating in front of you.");
        UiDistance = _category.CreateEntry("UiDistance", 1.8f, "UI distance (m)");
        UiWidth = _category.CreateEntry("UiWidth", 2.4f, "UI width (m)");
        UiFollow = _category.CreateEntry("UiFollow", VrUi.UiFollowMode.Lazy, "UI follow mode",
            "Fixed: stays in front of your body. Lazy: eases back in front when you look away. Smooth: follows your face.");
        UiSmoothing = _category.CreateEntry("UiSmoothing", 0.12f, "UI smoothing (s)",
            "How softly the panel follows in Smooth mode. 0 = rigidly locked to your view.");
        OpenXRRuntimeJson = _category.CreateEntry("OpenXRRuntimeJson", "", "OpenXR runtime override",
            "Optional path to an OpenXR runtime manifest (.json). Leave empty to use the system's active runtime.");
        ControllersAsGamepad = _category.CreateEntry("ControllersAsGamepad", true, "VR controllers as gamepad",
            "Play with VR controllers: sticks, triggers, grips (bumpers) and face buttons act like an Xbox controller. " +
            "Click both thumbsticks to recenter.");
        SyntheticControllers = _category.CreateEntry("SyntheticControllers", false, "Synthetic controllers",
            "Developer option: feed a fixed test input through the virtual gamepad when no controllers are active.");
        MockControllerInput = _category.CreateEntry("MockControllerInput", false, "Mock controller input",
            "Developer option: drive simulated controllers via XR_EXT_conformance_automation (Unity mock runtime only).");
        SnapshotOnStartup = _category.CreateEntry("SnapshotOnStartup", false, "Snapshot on startup",
            "Developer option: save eye snapshots once, shortly after VR starts.");
    }

    /// <summary>Changes a UI panel setting in place and saves it, so adjustments survive restarts.</summary>
    public void Adjust(MelonPreferences_Entry<float> entry, float factor, float min, float max)
    {
        entry.Value = Mathf.Clamp(entry.Value * factor, min, max);
        _category.SaveToFile(false);
    }

    public float ClampedRenderScale => Mathf.Clamp(RenderScale.Value, 0.5f, 2.0f);
}
