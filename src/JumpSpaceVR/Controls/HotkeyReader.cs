using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Reads mod hotkeys through the Win32 API. The game uses the new Input System, so the legacy
/// UnityEngine.Input class is not reliable here.
/// </summary>
internal sealed class HotkeyReader
{
    private const int VirtualKeyF1 = 0x70;
    private const int VirtualKeyA = 0x41;
    private const int VirtualKey0 = 0x30;

    private readonly HashSet<int> _heldKeys = new();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>True only on the frame the key goes down while the game window has focus.</summary>
    public bool WasPressed(KeyCode key)
    {
        var virtualKey = ToVirtualKey(key);
        if (virtualKey == 0)
            return false;

        var isDown = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        if (!isDown)
        {
            _heldKeys.Remove(virtualKey);
            return false;
        }

        if (!_heldKeys.Add(virtualKey))
            return false;

        return Application.isFocused;
    }

    private static int ToVirtualKey(KeyCode key)
    {
        if (key >= KeyCode.F1 && key <= KeyCode.F15)
            return VirtualKeyF1 + (key - KeyCode.F1);
        if (key >= KeyCode.A && key <= KeyCode.Z)
            return VirtualKeyA + (key - KeyCode.A);
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            return VirtualKey0 + (key - KeyCode.Alpha0);
        return key switch
        {
            KeyCode.UpArrow => 0x26,
            KeyCode.DownArrow => 0x28,
            KeyCode.LeftArrow => 0x25,
            KeyCode.RightArrow => 0x27,
            KeyCode.Return => 0x0D,
            KeyCode.Escape => 0x1B,
            KeyCode.Backspace => 0x08,
            KeyCode.Insert => 0x2D,
            KeyCode.Home => 0x24,
            KeyCode.End => 0x23,
            KeyCode.PageUp => 0x21,
            KeyCode.PageDown => 0x22,
            _ => 0,
        };
    }
}
