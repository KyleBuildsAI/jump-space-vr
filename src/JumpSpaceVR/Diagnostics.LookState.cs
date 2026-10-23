using System.Text;
using Il2Cpp;
using UnityEngine;

namespace JumpSpaceVR;

internal static partial class Diagnostics
{
    /// <summary>
    /// Dumps the player's look/aim state: which rotations the game keeps and in which frame, so motion aiming can
    /// drive the right one.
    /// </summary>
    private static string DescribeLookState()
    {
        var controllers = Object.FindObjectsByType<CameraController_Character>(FindObjectsSortMode.None);
        if (controllers == null || controllers.Length == 0)
            return "no character camera controller (not in a level)";

        var controller = controllers[0];
        var report = new StringBuilder();
        report.Append($"controller={controller.name} currentRotation={controller.m_currentRotation.eulerAngles} ");
        report.Append($"transformRot={controller.transform.rotation.eulerAngles} accumulatedDelta={controller.m_accumulatedDelta}\n");

        var root = controller.m_CameraRoot;
        if (root != null)
            report.Append($"    cameraRoot={root.name} rot={root.rotation.eulerAngles} localRot={root.localRotation.eulerAngles}\n");
        var camera = Camera.main;
        if (camera != null)
            report.Append($"    mainCamera rot={camera.transform.rotation.eulerAngles} fwd={camera.transform.forward}\n");

        var blackboard = controller.m_PlayerBlackboardData;
        if (blackboard != null)
        {
            report.Append($"    yaw={blackboard.m_YawRotation.Value:F1} pitchAim={blackboard.m_PitchAimSpace.Value:F1} ");
            report.Append($"lookDirWs={blackboard.m_LookDirectionWorldSpace.Value} clientAimLs={blackboard.m_ClientAimDirectionLs.Value}\n");
            var firstPerson = blackboard.m_CurrentFPTransform.Value;
            if (firstPerson != null)
            {
                report.Append($"    fpTransform={PathOf(firstPerson)}\n");
                report.Append($"        localPos={firstPerson.localPosition} localRot={firstPerson.localRotation.eulerAngles} worldPos={firstPerson.position}");
            }
        }
        return report.ToString();
    }
}
