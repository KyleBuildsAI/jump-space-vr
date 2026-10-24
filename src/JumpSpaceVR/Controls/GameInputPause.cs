using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Keeps the game from acting on input while the VR menu is open. A real gamepad can't be held back the way the
/// mod's virtual one is (its buttons reach the game directly), so while the menu is open the game's enabled input
/// actions are switched off, and exactly those are switched back on when it closes. The menu itself reads the
/// devices directly, which still works with the actions off; the mod's hotkeys don't use the game's actions either.
/// </summary>
internal sealed class GameInputPause
{
    private readonly List<InputAction> _paused = new();
    private bool _active;

    /// <summary>Number of the game's input actions currently enabled (for tests).</summary>
    public static int EnabledActionCount => InputSystem.ListEnabledActions().Count;

    /// <summary>Call every frame with whether the game's input should be held back.</summary>
    public void Update(bool pause)
    {
        if (pause == _active)
            return;
        _active = pause;
        try
        {
            if (pause)
                Pause();
            else
                Resume();
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not {(pause ? "pause" : "resume")} the game's input: {exception.Message}");
        }
    }

    private void Pause()
    {
        _paused.Clear();
        var enabled = InputSystem.ListEnabledActions();
        for (var index = 0; index < enabled.Count; index++)
            _paused.Add(enabled[index]);
        foreach (var action in _paused)
            action.Disable();
    }

    private void Resume()
    {
        // Only what this paused, and only if still off: actions the game enabled meanwhile are already on.
        foreach (var action in _paused)
        {
            if (action != null && !action.enabled)
                action.Enable();
        }
        _paused.Clear();
    }
}
