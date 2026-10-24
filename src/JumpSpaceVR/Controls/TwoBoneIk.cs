using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Analytic two-bone inverse kinematics (shoulder-elbow-wrist): bends an arm so the hand reaches a target, with the
/// elbow pointing towards a hint position. Works on any rig because it only rotates the existing bones.
/// </summary>
internal static class TwoBoneIk
{
    public static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 elbowHint,
        Quaternion? endRotation)
    {
        var shoulder = upper.position;
        var upperLength = Vector3.Distance(shoulder, lower.position);
        var lowerLength = Vector3.Distance(lower.position, end.position);
        if (upperLength < 1e-4f || lowerLength < 1e-4f)
            return;

        // Keep the target reachable (a fully straight arm would make the elbow direction undefined).
        var toTarget = target - shoulder;
        var reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upperLength - lowerLength) + 1e-3f, upperLength + lowerLength - 1e-3f);
        var direction = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : upper.up;
        target = shoulder + direction * reach;

        // Elbow: law of cosines in the plane spanned by the arm direction and the hint.
        var cosShoulder = (upperLength * upperLength + reach * reach - lowerLength * lowerLength) / (2f * upperLength * reach);
        var shoulderAngle = Mathf.Acos(Mathf.Clamp(cosShoulder, -1f, 1f));
        var bendAxis = Vector3.Cross(direction, elbowHint - shoulder);
        if (bendAxis.sqrMagnitude < 1e-8f)
            bendAxis = Vector3.Cross(direction, Vector3.down);
        if (bendAxis.sqrMagnitude < 1e-8f)
            bendAxis = Vector3.Cross(direction, Vector3.forward);
        bendAxis.Normalize();
        var elbow = shoulder + Quaternion.AngleAxis(shoulderAngle * Mathf.Rad2Deg, bendAxis) * direction * upperLength;

        upper.rotation = Quaternion.FromToRotation(lower.position - shoulder, elbow - shoulder) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, target - lower.position) * lower.rotation;
        if (endRotation.HasValue)
            end.rotation = endRotation.Value;
    }
}
