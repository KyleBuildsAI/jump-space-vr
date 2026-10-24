using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JumpSpaceVR.Controls;

/// <summary>Which controls drive the game in VR.</summary>
internal enum ControllerMode
{
    /// <summary>Whichever was used last: VR motion controllers or a gamepad.</summary>
    Auto,
    MotionControllers,
    Gamepad,
}

/// <summary>
/// Real gamepads (Xbox/XInput, PlayStation DualSense/DualShock, Switch Pro, and anything Steam Input exposes as a
/// gamepad) next to VR motion controllers. The game reads gamepads itself; this decides when the player is using one,
/// so motion aiming, the laser and the mod's virtual gamepad step aside and the view follows the game camera. It also
/// gives pads the mod's shortcuts (hold View/Select for the VR menu; the game only uses it to invite friends; click
/// both sticks to recenter, as on the motion controllers), menu navigation and rumble.
/// </summary>
internal sealed class PhysicalGamepads
{
    private const float ActivityThreshold = 0.35f;
    private const float HoldSeconds = 1f;
    private const float FireInterval = 0.1f;
    private const float FireSeconds = 0.06f;
    private const float FireStrength = 0.6f;

    private readonly string _ignoredDeviceName;
    private float _lastPadUse = -1f;
    private float _lastControllerUse = -1f;
    private float _selectHeldSince = -1f;
    private bool _holdHandled;
    private bool _stickChordHeld;
    private float _rumbleUntil;
    private Gamepad _rumbling;
    private bool _triggerHeld;
    private float _nextFirePulse;

    public PhysicalGamepads(string ignoredDeviceName) => _ignoredDeviceName = ignoredDeviceName;

    /// <summary>The physical pad used most recently, or null.</summary>
    public Gamepad Current { get; private set; }

    /// <summary>True while the player plays with a gamepad rather than the motion controllers.</summary>
    public bool GamepadActive { get; private set; }

    /// <summary>The current pad's triggers (0 when no pad is in use).</summary>
    public float LeftTrigger => GamepadActive && Current != null ? Current.leftTrigger.ReadValue() : 0f;
    public float RightTrigger => GamepadActive && Current != null ? Current.rightTrigger.ReadValue() : 0f;

    /// <summary>Rumble pulses started so far (for tests).</summary>
    public int RumblePulses { get; private set; }

    public bool RecenterRequested { get; private set; }
    public bool MenuToggleRequested { get; private set; }

    public void Update(ControllerMode mode, in ControllerState motionControllers)
    {
        RecenterRequested = false;
        MenuToggleRequested = false;
        var now = Time.unscaledTime;

        foreach (var device in InputSystem.devices)
        {
            var pad = device.TryCast<Gamepad>();
            if (pad == null || pad.name == _ignoredDeviceName || !pad.enabled)
                continue;
            if (IsActive(pad))
            {
                _lastPadUse = now;
                Current = pad;
            }
        }
        if (motionControllers.IsActive && IsActive(motionControllers))
            _lastControllerUse = now;

        GamepadActive = mode switch
        {
            ControllerMode.Gamepad => true,
            ControllerMode.MotionControllers => false,
            _ => _lastPadUse > _lastControllerUse,
        };

        UpdateShortcuts(now);
        if (_rumbling != null && now >= _rumbleUntil)
            StopRumble();
    }

    /// <summary>The current pad as controller input for the VR menu (stick or d-pad, A/Cross, B/Circle).</summary>
    public ControllerState MenuInput()
    {
        var pad = Current;
        if (pad == null || !GamepadActive)
            return default;
        var stick = pad.leftStick.ReadValue();
        var dpad = pad.dpad.ReadValue();
        return new ControllerState
        {
            IsActive = true,
            LeftStick = dpad.sqrMagnitude > stick.sqrMagnitude ? dpad : stick,
            ButtonA = pad.buttonSouth.isPressed,
            ButtonB = pad.buttonEast.isPressed,
        };
    }

    /// <summary>Rumbles while firing (repeating while the trigger is held), like the motion controllers' haptics.</summary>
    public void UpdateFireRumble(bool enabled, float strength, bool firing)
    {
        var triggerDown = RightTrigger > 0.5f;
        var now = Time.unscaledTime;
        if (enabled && strength > 0f && firing && triggerDown && (!_triggerHeld || now >= _nextFirePulse))
        {
            Rumble(FireStrength * strength, FireSeconds);
            _nextFirePulse = now + FireInterval;
        }
        _triggerHeld = triggerDown;
    }

    /// <summary>Rumbles the current pad (both motors) for a short time.</summary>
    public void Rumble(float strength, float seconds)
    {
        var pad = Current;
        if (pad == null || strength <= 0f)
            return;
        if (_rumbling != pad)
            StopRumble();
        pad.SetMotorSpeeds(strength, strength);
        RumblePulses++;
        _rumbling = pad;
        _rumbleUntil = Time.unscaledTime + seconds;
    }

    /// <summary>Stops any rumble (when VR stops or vibration is switched off).</summary>
    public void StopRumble()
    {
        // A pad unplugged mid-rumble is no longer added; there is nothing to stop then.
        if (_rumbling != null && _rumbling.added)
            _rumbling.SetMotorSpeeds(0f, 0f);
        _rumbling = null;
    }

    private void UpdateShortcuts(float now)
    {
        var pad = Current;
        var usable = pad != null && GamepadActive;

        var stickChord = usable && pad.leftStickButton.isPressed && pad.rightStickButton.isPressed;
        RecenterRequested = stickChord && !_stickChordHeld;
        _stickChordHeld = stickChord;

        if (!usable || !pad.selectButton.isPressed)
        {
            _selectHeldSince = -1f;
            _holdHandled = false;
            return;
        }
        if (_selectHeldSince < 0f)
            _selectHeldSince = now;
        if (_holdHandled || now - _selectHeldSince < HoldSeconds)
            return;
        _holdHandled = true;
        MenuToggleRequested = true;
    }

    private static bool IsActive(Gamepad pad) =>
        pad.leftStick.ReadValue().magnitude > ActivityThreshold || pad.rightStick.ReadValue().magnitude > ActivityThreshold ||
        pad.leftTrigger.ReadValue() > ActivityThreshold || pad.rightTrigger.ReadValue() > ActivityThreshold ||
        pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.buttonWest.isPressed || pad.buttonNorth.isPressed ||
        pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.startButton.isPressed || pad.selectButton.isPressed ||
        pad.leftStickButton.isPressed || pad.rightStickButton.isPressed ||
        pad.dpad.ReadValue().sqrMagnitude > 0.25f;

    private static bool IsActive(in ControllerState state) =>
        state.LeftStick.magnitude > ActivityThreshold || state.RightStick.magnitude > ActivityThreshold ||
        state.LeftTrigger > ActivityThreshold || state.RightTrigger > ActivityThreshold ||
        state.ButtonA || state.ButtonB || state.ButtonX || state.ButtonY || state.Menu;
}
