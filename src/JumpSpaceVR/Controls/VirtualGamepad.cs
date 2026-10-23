using System;
using System.Runtime.InteropServices;
using JumpSpaceVR.OpenXR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Presents the VR controllers to the game as a standard gamepad, so every action the game already supports
/// on a controller works without remapping.
///
/// Button layout: A/B/X/Y map to the matching face buttons, triggers to triggers, grips to bumpers,
/// thumbsticks (and clicks) to sticks, and the left menu button to Start.
///
/// State is pushed as raw Input System state events because the generic QueueStateEvent helper was
/// stripped from the game build.
/// </summary>
internal sealed unsafe class VirtualGamepad
{
    private const string DeviceName = "JumpSpaceVR Controllers";
    private const int StateEventType = 0x53544154;   // FourCC 'STAT' (first char in the high byte)
    private const int GamepadFormat = 0x47504144;    // FourCC 'GPAD'
    private const float StickDeadzone = 0.12f;
    private const float GripPressThreshold = 0.5f;

    // GamepadButton bit indices in GamepadState.buttons.
    private const int ButtonNorth = 4, ButtonEast = 5, ButtonSouth = 6, ButtonWest = 7;
    private const int ButtonLeftStick = 8, ButtonRightStick = 9, ButtonLeftShoulder = 10, ButtonRightShoulder = 11;
    private const int ButtonStart = 12;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GamepadStateEvent
    {
        // InputEvent header (NativeInputEvent: type, size, device, time, id; packed to 20 bytes)
        public int Type;
        public ushort SizeInBytes;
        public ushort DeviceId;
        public double Time;
        public int EventId;
        // StateEvent
        public int StateFormat;
        // GamepadState
        public uint Buttons;
        public float LeftStickX, LeftStickY;
        public float RightStickX, RightStickY;
        public float LeftTrigger;
        public float RightTrigger;
    }

    private InputDevice _device;
    private bool _creationFailed;
    private bool _wasActive;
    private (uint Buttons, Vector2 LeftStick, Vector2 RightStick, float LeftTrigger, float RightTrigger)? _lastSent;
    private bool _wasFocused;

    public bool Send(in ControllerState controllers)
    {
        if (!controllers.IsActive)
        {
            if (_wasActive)
                Queue(default);
            _wasActive = false;
            return false;
        }

        if (!EnsureDevice())
            return false;

        _wasActive = true;
        Queue(controllers);
        return true;
    }

    public void Remove()
    {
        if (_device == null)
            return;
        try
        {
            InputSystem.RemoveDevice(_device);
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not remove the virtual gamepad: {exception.Message}");
        }
        _device = null;
    }

    private bool EnsureDevice()
    {
        if (_device != null && _device.added)
            return true;
        if (_creationFailed)
            return false;

        try
        {
            _device = InputSystem.AddDevice("Gamepad", DeviceName, null);
            _lastSent = null;
            Log.Msg($"Virtual gamepad added (device id {_device.deviceId}).");
            return true;
        }
        catch (Exception exception)
        {
            _creationFailed = true;
            Log.Error($"Could not create the virtual gamepad: {exception.Message}");
            return false;
        }
    }

    private void Queue(in ControllerState controllers)
    {
        if (_device == null)
            return;

        var leftStick = ApplyDeadzone(controllers.LeftStick);
        var rightStick = ApplyDeadzone(controllers.RightStick);
        var buttons = BuildButtons(controllers);

        // Only send changes. An event every frame keeps the gamepad the Input System's current device, and the game
        // then re-locks the cursor every frame, which races with the laser pointer's clicks in menus. The state is
        // re-sent when the window regains focus, because the Input System resets devices on focus changes.
        var focused = Application.isFocused;
        var state = (buttons, leftStick, rightStick, controllers.LeftTrigger, controllers.RightTrigger);
        if (focused == _wasFocused && _lastSent == state)
            return;
        _wasFocused = focused;
        _lastSent = state;

        var stateEvent = new GamepadStateEvent
        {
            Type = StateEventType,
            SizeInBytes = (ushort)sizeof(GamepadStateEvent),
            DeviceId = (ushort)_device.deviceId,
            Time = InputEventTime.Now,
            StateFormat = GamepadFormat,
            Buttons = buttons,
            LeftStickX = leftStick.x,
            LeftStickY = leftStick.y,
            RightStickX = rightStick.x,
            RightStickY = rightStick.y,
            LeftTrigger = controllers.LeftTrigger,
            RightTrigger = controllers.RightTrigger,
        };

        InputSystem.QueueEvent(new InputEventPtr((InputEvent*)&stateEvent));
    }

    private static uint BuildButtons(in ControllerState controllers)
    {
        uint buttons = 0;
        Set(ref buttons, ButtonSouth, controllers.ButtonA);
        Set(ref buttons, ButtonEast, controllers.ButtonB);
        Set(ref buttons, ButtonWest, controllers.ButtonX);
        Set(ref buttons, ButtonNorth, controllers.ButtonY);
        Set(ref buttons, ButtonLeftShoulder, controllers.LeftGrip > GripPressThreshold);
        Set(ref buttons, ButtonRightShoulder, controllers.RightGrip > GripPressThreshold);
        Set(ref buttons, ButtonLeftStick, controllers.LeftStickClick);
        Set(ref buttons, ButtonRightStick, controllers.RightStickClick);
        Set(ref buttons, ButtonStart, controllers.Menu);
        return buttons;
    }

    private static void Set(ref uint buttons, int bit, bool pressed)
    {
        if (pressed)
            buttons |= 1u << bit;
    }

    private static Vector2 ApplyDeadzone(Vector2 stick)
    {
        var magnitude = stick.magnitude;
        if (magnitude < StickDeadzone)
            return Vector2.zero;
        var scaled = (magnitude - StickDeadzone) / (1f - StickDeadzone);
        return stick / magnitude * Mathf.Min(1f, scaled);
    }
}
