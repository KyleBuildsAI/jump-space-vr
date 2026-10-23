using System.Text;
using Il2Cpp;
using UnityEngine;

namespace JumpSpaceVR;

internal static partial class Diagnostics
{
    /// <summary>
    /// Dumps the player's look/aim state: which rotations the game keeps and in which frame, so motion aiming can
    /// drive the right one, and the first-person rig so the weapon can be put in the hand.
    /// </summary>
    private static string DescribeLookState()
    {
        var controllers = Object.FindObjectsByType<CameraController_Character>(FindObjectsSortMode.None);
        if (controllers == null || controllers.Length == 0)
            return "no character camera controller (not in a level)";

        var controller = controllers[0];
        var report = new StringBuilder();
        report.Append($"controller={controller.name} currentRotation={controller.m_currentRotation.eulerAngles} ");
        report.AppendLine($"transformRot={controller.transform.rotation.eulerAngles} accumulatedDelta={controller.m_accumulatedDelta}");

        var root = controller.m_CameraRoot;
        if (root != null)
            report.AppendLine($"    cameraRoot={root.name} rot={root.rotation.eulerAngles} localRot={root.localRotation.eulerAngles}");
        var camera = Camera.main;
        if (camera != null)
            report.AppendLine($"    mainCamera rot={camera.transform.rotation.eulerAngles} fwd={camera.transform.forward}");

        var blackboard = controller.m_PlayerBlackboardData;
        if (blackboard == null)
            return report.ToString();

        report.Append($"    yaw={blackboard.m_YawRotation.Value:F1} pitchAim={blackboard.m_PitchAimSpace.Value:F1} ");
        report.AppendLine($"lookDirWs={blackboard.m_LookDirectionWorldSpace.Value} clientAimLs={blackboard.m_ClientAimDirectionLs.Value}");

        var firstPerson = blackboard.m_CurrentFPTransform.Value;
        if (firstPerson != null)
        {
            report.AppendLine($"    fpTransform={PathOf(firstPerson)}");
            report.AppendLine($"        localPos={firstPerson.localPosition} localRot={firstPerson.localRotation.eulerAngles} worldPos={firstPerson.position}");
            AppendChildren(report, firstPerson, 1, 3);
        }

        var itemAnimator = blackboard.m_FirstPersonItemAnimator.Value;
        if (itemAnimator != null)
        {
            var item = itemAnimator.transform;
            report.AppendLine($"    fpItem={PathOf(item)}");
            report.AppendLine($"        worldPos={item.position} worldRot={item.rotation.eulerAngles} parent={(item.parent != null ? item.parent.name : "-")}");
        }
        return report.ToString();
    }

    private static void AppendChildren(StringBuilder report, Transform parent, int depth, int maxDepth)
    {
        if (depth > maxDepth)
            return;
        for (var index = 0; index < parent.childCount && index < 12; index++)
        {
            var child = parent.GetChild(index);
            report.Append(' ', 4 + depth * 2).Append(child.name);
            report.AppendLine(child.gameObject.activeInHierarchy ? "" : " (inactive)");
            AppendChildren(report, child, depth + 1, maxDepth);
        }
    }
}
