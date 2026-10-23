using UnityEngine.InputSystem.LowLevel;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Timestamp for raw Input System events. The event header stores the Input System's internal time, which is the
/// public InputState.currentTime plus a startup offset. Events stamped with the public time look older than the
/// device's last update after any device reset (focus change, alt-tab) and are silently dropped from then on.
/// </summary>
internal static class InputEventTime
{
    public static double Now => InputState.currentTime + InputRuntime.s_CurrentTimeOffsetToRealtimeSinceStartup;
}
