using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace JumpSpaceVR.Controls;

/// <summary>
/// A mouse the game can use like a real one, driven by the controller laser pointer. State is pushed as raw Input
/// System state events (the generic helpers were stripped from the game build).
/// </summary>
internal sealed unsafe class VirtualMouse
{
    private const string DeviceName = "JumpSpaceVR Pointer";
    private const int StateEventType = 0x53544154; // FourCC 'STAT'
    private const int MouseFormat = 0x4D4F5553;    // FourCC 'MOUS'

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MouseStateEvent
    {
        // InputEvent header (NativeInputEvent: type, size, device, time, id; packed to 20 bytes)
        public int Type;
        public ushort SizeInBytes;
        public ushort DeviceId;
        public double Time;
        public int EventId;
        // StateEvent
        public int StateFormat;
        // MouseState
        public float PositionX, PositionY;
        public float DeltaX, DeltaY;
        public float ScrollX, ScrollY;
        public ushort Buttons;
        public ushort DisplayIndex;
        public ushort ClickCount;
    }

    private InputDevice _device;
    private bool _creationFailed;
    private Vector2 _lastPosition;

    /// <summary>Left button state the Input System holds for the virtual mouse.</summary>
    public bool DeviceLeftPressed => _device != null && _device.TryCast<Mouse>()?.leftButton.isPressed == true;

    /// <summary>Pointer position the Input System holds for the virtual mouse.</summary>
    public Vector2 DevicePosition => _device?.TryCast<Mouse>()?.position.ReadValue() ?? Vector2.zero;

    /// <summary>Moves the pointer to a screen position (pixels, origin bottom-left) with the left button state.</summary>
    public void Send(Vector2 screenPosition, bool leftButton)
    {
        if (!EnsureDevice())
            return;

        var delta = screenPosition - _lastPosition;
        _lastPosition = screenPosition;
        var stateEvent = new MouseStateEvent
        {
            Type = StateEventType,
            SizeInBytes = (ushort)sizeof(MouseStateEvent),
            DeviceId = (ushort)_device.deviceId,
            Time = InputEventTime.Now,
            StateFormat = MouseFormat,
            PositionX = screenPosition.x,
            PositionY = screenPosition.y,
            DeltaX = delta.x,
            DeltaY = delta.y,
            Buttons = (ushort)(leftButton ? 1 : 0),
        };
        InputSystem.QueueEvent(new InputEventPtr((InputEvent*)&stateEvent));
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
            Log.Warning($"Could not remove the virtual mouse: {exception.Message}");
        }
        _device = null;
    }

    private string DescribeLayout()
    {
        var text = $"state {_device.stateBlock.format} {_device.stateBlock.sizeInBits} bits;";
        foreach (var name in new[] { "position", "delta", "scroll", "press", "leftButton", "rightButton", "clickCount", "displayIndex" })
        {
            var control = _device.TryGetChildControl(name);
            if (control != null)
                text += $" {name}@{control.stateBlock.byteOffset}.{control.stateBlock.bitOffset}";
        }
        return text;
    }

    private bool EnsureDevice()
    {
        if (_device != null && _device.added)
            return true;
        if (_creationFailed)
            return false;
        try
        {
            _device = InputSystem.AddDevice("Mouse", DeviceName, null);
            Log.Msg($"Virtual pointer mouse added (device id {_device.deviceId}). {DescribeLayout()}");
            return true;
        }
        catch (Exception exception)
        {
            _creationFailed = true;
            Log.Error($"Could not create the virtual mouse: {exception.Message}");
            return false;
        }
    }
}
