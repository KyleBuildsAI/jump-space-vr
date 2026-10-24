using System;
using System.Globalization;
using JumpSpaceVR.Controls;
using JumpSpaceVR.OpenXR;
using UnityEngine;

namespace JumpSpaceVR.Testing;

/// <summary>
/// A scripted stand-in for a real gamepad in test scenarios ("pad rightstick 1 0", "pad view 1", "pad release"). It is
/// an ordinary Input System gamepad, so the game and the mod see it exactly like a plugged-in Xbox or PlayStation pad.
/// </summary>
internal sealed class TestGamepad
{
    public const string DeviceName = "JumpSpaceVR TestPad";

    private readonly VirtualGamepad _device = new(DeviceName);
    private ControllerState _state = new() { IsActive = true };

    public void Set(string control, string x, string y)
    {
        var first = Parse(x);
        var second = Parse(y);
        var pressed = first > 0.5f;
        switch (control.ToLowerInvariant())
        {
            case "leftstick": _state.LeftStick = new Vector2(first, second); break;
            case "rightstick": _state.RightStick = new Vector2(first, second); break;
            case "lefttrigger": _state.LeftTrigger = first; break;
            case "righttrigger": _state.RightTrigger = first; break;
            case "a": _state.ButtonA = pressed; break;
            case "b": _state.ButtonB = pressed; break;
            case "x": _state.ButtonX = pressed; break;
            case "y": _state.ButtonY = pressed; break;
            case "start": _state.Menu = pressed; break;
            case "view": _state.View = pressed; break;
            case "leftstickclick": _state.LeftStickClick = pressed; break;
            case "rightstickclick": _state.RightStickClick = pressed; break;
            case "release": _state = new ControllerState { IsActive = true }; break;
            default: throw new ArgumentException($"unknown pad control '{control}'");
        }
    }

    /// <summary>Sends the scripted state (only changes reach the Input System). Call every frame.</summary>
    public void Update() => _device.Send(_state);

    private static float Parse(string value) =>
        string.IsNullOrEmpty(value) ? 0f : float.Parse(value, CultureInfo.InvariantCulture);
}
