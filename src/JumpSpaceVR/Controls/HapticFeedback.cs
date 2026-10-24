using JumpSpaceVR.OpenXR;
using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Controller vibration for the moments the game would rumble a gamepad (it sends none itself): firing (repeating
/// while the trigger is held, felt in both hands when the gun is held two-handed), grabbing or letting go of the
/// fore-end, and clicking UI with the laser pointer.
/// </summary>
internal sealed class HapticFeedback
{
    private const float FireInterval = 0.1f;
    private const float FireSeconds = 0.035f;
    private const float FireStrength = 0.8f;
    private const float SupportHandStrength = 0.45f;
    private const float TickSeconds = 0.02f;
    private const float TickStrength = 0.35f;

    private bool _triggerHeld;
    private float _nextFirePulse;
    private bool _wasTwoHanded;

    public void Update(OpenXRControllers controllers, in ControllerState state, bool enabled, float strength,
        bool firing, bool laserClick, bool twoHanded)
    {
        var triggerDown = state.RightTrigger > 0.5f;
        var now = Time.unscaledTime;
        if (enabled && strength > 0f)
        {
            if (firing && triggerDown && (!_triggerHeld || now >= _nextFirePulse))
            {
                controllers.Pulse(true, FireStrength * strength, FireSeconds);
                if (twoHanded)
                    controllers.Pulse(false, SupportHandStrength * strength, FireSeconds);
                _nextFirePulse = now + FireInterval;
            }
            if (laserClick && triggerDown && !_triggerHeld)
                controllers.Pulse(true, TickStrength * strength, TickSeconds);
            if (twoHanded != _wasTwoHanded)
                controllers.Pulse(false, TickStrength * strength, TickSeconds * 2f);
        }
        _triggerHeld = triggerDown;
        _wasTwoHanded = twoHanded;
    }
}
