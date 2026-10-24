using System;
using HarmonyLib;
using Il2CppKeepsake;
using JumpSpaceVR.OpenXR;
using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>
/// Motion controls: aim with the right controller, turn with the right stick, hold the weapon in your hand.
///
/// Aiming follows UEVR's generic approach adapted to this game: every tick the player's spring arm (the object the
/// game's own look input and its ForceLookAt ability write to) is pointed at a spot a fixed distance along the
/// controller's ray. The game then fires, animates and replicates aim itself, so co-op stays consistent.
/// The headset view is built on a body yaw owned by the mod, so pointing the gun never rotates the world.
/// </summary>
internal sealed class MotionControls
{
    private const float AimConvergenceDistance = 10f;

    private static MotionControls _instance;

    public readonly BodyTurning Body = new();

    private bool _hasAimTarget;
    private Vector3 _aimTarget;

    private Transform _weapon;
    private int _weaponId;
    private bool _weaponMoved;
    private Vector3 _weaponBaseLocalPosition;
    private Quaternion _weaponBaseLocalRotation;
    private Vector3 _weaponAppliedLocalPosition;
    private Quaternion _weaponAppliedLocalRotation;

    private static int _tickCalls, _tickMatched, _tickApplied;
    private static Vector2 _lastError;

    /// <summary>Unity pitch is positive looking down; the game's look input may use the opposite sign.</summary>
    public static float PitchSign = -1f;
    private static string _lastArm = "-";

    public string Describe() =>
        $"active={IsActive} bodyYaw={Body.Yaw:F1} aimTarget={(_hasAimTarget ? _aimTarget.ToString() : "-")} " +
        $"weapon={(_weapon != null ? _weapon.name : "-")} springarmTicks={_tickCalls}/{_tickMatched}/{_tickApplied} lastError={_lastError} arm={_lastArm}";

    /// <summary>Developer test aid: extra rotation applied to the pointer (simulated controllers never move).</summary>
    public static Quaternion DebugPointerRotation = Quaternion.identity;

    /// <summary>Developer test aid: offset added to the hand position (simulated controllers sit at the head).</summary>
    public static Vector3 DebugHandOffset = Vector3.zero;

    /// <summary>True while motion aiming drives the game this frame.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True when the first-person rig was moved onto the controller for the last rendered frame.</summary>
    public bool WeaponInHand => Time.frameCount - _weaponPlacedFrame <= 1;

    private int _weaponPlacedFrame = -10;

    /// <summary>Whether the rig's item locator was found (otherwise only the configured offset places it).</summary>
    public bool HasItemLocator { get; private set; }

    /// <summary>Logs the first-person rig's transform hierarchy (bones, attach points) for mod development.</summary>
    public string DescribeRigHierarchy()
    {
        var rig = FindWeapon();
        if (rig == null)
            return "no first-person rig";
        var text = new System.Text.StringBuilder();
        AppendHierarchy(text, rig, 0);
        return text.ToString();
    }

    private static void AppendHierarchy(System.Text.StringBuilder text, Transform node, int depth)
    {
        if (depth > 24)
            return;
        text.Append(' ', depth * 2).Append(node.name)
            .Append($" local=({node.localPosition.x:F3},{node.localPosition.y:F3},{node.localPosition.z:F3})").AppendLine();
        for (var index = 0; index < node.childCount; index++)
            AppendHierarchy(text, node.GetChild(index), depth + 1);
    }

    /// <summary>True when the player's first-person rig holds an item (a gun in the hangar or a mission).</summary>
    public bool HoldsItem
    {
        get
        {
            var weapon = FindWeapon();
            var locator = weapon != null ? FindItemLocator(weapon) : null;
            return locator != null && locator.childCount > 0;
        }
    }

    /// <summary>Distance between the rig's item locator and the controller grip after placement, in meters.</summary>
    public float LastGripError { get; private set; }

    public static bool InstallHooks(HarmonyLib.Harmony harmony)
    {
        try
        {
            var tick = AccessTools.Method(typeof(SpringarmBehaviour), nameof(SpringarmBehaviour.Tick));
            harmony.Patch(tick, prefix: new HarmonyMethod(typeof(MotionControls), nameof(SpringarmTickPrefix)));
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning($"Motion aiming unavailable (could not hook the camera spring arm): {exception.Message}");
            return false;
        }
    }

    public void Activate() => _instance = this;

    /// <summary>True while this instance receives the spring-arm hook (motion aiming can steer the camera).</summary>
    public static bool IsHookedInstance(MotionControls motion) => _instance == motion;

    public void Deactivate()
    {
        IsActive = false;
        _hasAimTarget = false;
        Arms.Idle();
        RestoreWeapon();
        if (_instance == this)
            _instance = null;
    }

    /// <summary>
    /// Per-frame input handling (call from Update, before the gamepad state is sent): the right stick turns the
    /// body instead of the camera, and the left stick is made body-relative.
    /// </summary>
    public void ProcessInput(ref ControllerState state, bool enabled, TurnMode turnMode, float snapAngle, float smoothSpeed)
    {
        IsActive = enabled && state.RightPointer.IsValid;
        if (!IsActive)
        {
            _hasAimTarget = false;
            return;
        }

        var camera = Camera.main;
        var gameYaw = camera != null ? camera.transform.eulerAngles.y : Body.Yaw;
        if (!Body.IsInitialized)
            Body.Reset(gameYaw);

        Body.Update(state.RightStick.x, turnMode, snapAngle, smoothSpeed, Time.unscaledDeltaTime);
        state.LeftStick = Body.BodyRelativeStick(state.LeftStick, gameYaw);
        // Look comes from the controller now; the right stick only turns the body.
        state.RightStick = Vector2.zero;
    }

    /// <summary>
    /// Computes where the gun points in the world and places the weapon in the right hand. Call before rendering,
    /// with poses already relative to the recentered origin and the frame the eyes are built on.
    /// </summary>
    /// <param name="sightShift">
    /// With the weapon left where the game puts it, a world-space shift applied to it (used while aiming down sights to
    /// line the sights up with one eye instead of the point between the eyes).
    /// </param>
    public void UpdatePose(Vector3 viewOrigin, Quaternion viewRotation, in TrackedPose hand, in TrackedPose pointer,
        in TrackedPose leftHand, in TrackedPose leftPointer, float leftGrip, float worldScale, Vector3 weaponOffset, bool moveWeapon,
        Vector3 sightShift, float leftHandRoll, Vector3 head)
    {
        if (!IsActive || !pointer.IsValid)
        {
            _hasAimTarget = false;
            Arms.Idle();
            RestoreWeapon();
            return;
        }

        var pointerPosition = viewOrigin + viewRotation * (pointer.Position * worldScale);
        var pointerRotation = viewRotation * pointer.Rotation * DebugPointerRotation;
        _aimTarget = pointerPosition + pointerRotation * Vector3.forward * AimConvergenceDistance;
        _hasAimTarget = true;

        if (moveWeapon && hand.IsValid)
        {
            var handPose = new HandPose(viewOrigin + viewRotation * ((hand.Position + DebugHandOffset) * worldScale), pointerRotation);
            // Left hand: grip position with the aim pose's rotation, matching how the right hand drives the gun.
            HandPose? left = leftHand.IsValid
                ? new HandPose(viewOrigin + viewRotation * (leftHand.Position * worldScale),
                    viewRotation * (leftPointer.IsValid ? leftPointer.Rotation : leftHand.Rotation))
                : null;
            var rig = FindWeapon();
            if (rig == null || !Arms.Update(rig, handPose, pointerRotation, left, leftGrip, weaponOffset, viewRotation, leftHandRoll, head))
                Arms.Idle();
            else
            {
                _weaponPlacedFrame = Time.frameCount;
                HasItemLocator = true;
                LastGripError = Arms.RightHandError;
                // With two hands the gun no longer points along the right controller: aim where the barrel points.
                if (Arms.TwoHanded && Arms.Barrel.HasValue)
                    _aimTarget = Arms.Barrel.Value.origin + Arms.Barrel.Value.direction * AimConvergenceDistance;
            }
        }
        else
        {
            Arms.Idle();
            if (sightShift.sqrMagnitude > 1e-8f)
                ShiftWeapon(sightShift);
            else
                RestoreWeapon();
        }
    }

    private void ShiftWeapon(Vector3 worldShift)
    {
        var weapon = FindWeapon();
        if (weapon == null)
            return;
        if (!_weaponMoved)
        {
            _weaponBaseLocalPosition = weapon.localPosition;
            _weaponBaseLocalRotation = weapon.localRotation;
        }
        weapon.position += worldShift;
        _weaponAppliedLocalPosition = weapon.localPosition;
        _weaponAppliedLocalRotation = weapon.localRotation;
        _weaponMoved = true;
    }

    /// <summary>Weapon and arm placement while "Weapon in hand" is on.</summary>
    public VrArms Arms { get; } = new();

    /// <summary>Puts the first-person weapon back where the game placed it (start of frame, before gameplay).</summary>
    public void RestoreWeapon()
    {
        Arms.Restore();
        if (!_weaponMoved)
            return;
        _weaponMoved = false;
        if (_weapon == null)
            return;
        if (_weapon.localPosition == _weaponAppliedLocalPosition && _weapon.localRotation == _weaponAppliedLocalRotation)
        {
            _weapon.localPosition = _weaponBaseLocalPosition;
            _weapon.localRotation = _weaponBaseLocalRotation;
        }
    }

    private Transform _locator;
    private int _locatorOwnerId;

    private Transform FindItemLocator(Transform rig)
    {
        var id = rig.GetInstanceID();
        if (id == _locatorOwnerId && _locator != null)
            return _locator;
        _locatorOwnerId = id;
        _locator = FindChildRecursive(rig, "ItemAttachLocator", 0);
        return _locator;
    }

    private static Transform FindChildRecursive(Transform parent, string name, int depth)
    {
        if (depth > 6)
            return null;
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child.name == name)
                return child;
            var found = FindChildRecursive(child, name, depth + 1);
            if (found != null)
                return found;
        }
        return null;
    }

    private Transform FindWeapon()
    {
        var camera = Camera.main;
        if (camera == null)
            return null;

        var controller = camera.GetComponentInParent<Il2Cpp.CameraController_Character>();
        var blackboard = controller != null ? controller.m_PlayerBlackboardData : null;
        var weapon = blackboard != null ? blackboard.m_CurrentFPTransform.Value : null;
        if (weapon == null)
        {
            _weapon = null;
            _weaponId = 0;
            return null;
        }

        var id = weapon.GetInstanceID();
        if (id != _weaponId)
        {
            RestoreWeapon();
            _weapon = weapon;
            _weaponId = id;
        }
        return _weapon;
    }

    private static void SpringarmTickPrefix(SpringarmBehaviour __instance)
    {
        _tickCalls++;
        var motion = _instance;
        if (motion == null || !motion.IsActive || !motion._hasAimTarget)
            return;

        try
        {
            var camera = Camera.main;
            var arm = __instance.SpringArmTransform;
            _lastArm = arm != null ? arm.name + (camera != null && camera.transform.IsChildOf(arm) ? "(parent of camera)" : "(not camera's)") : "null";
            if (camera == null || arm == null || !camera.transform.IsChildOf(arm))
                return; // another player's or a non-player spring arm
            _tickMatched++;

            var from = camera.transform.position;
            var direction = motion._aimTarget - from;
            if (direction.sqrMagnitude < 0.0001f)
                return;

            // Feed the angle between where the camera looks and where the controller points through the same
            // entry point the game's look input uses. Correcting the remaining error every tick converges in a
            // frame or two and is independent of look sensitivity and acceleration settings.
            var current = camera.transform.rotation.eulerAngles;
            var wanted = Quaternion.LookRotation(direction, Vector3.up).eulerAngles;
            var yawError = Mathf.DeltaAngle(current.y, wanted.y);
            var pitchError = Mathf.DeltaAngle(current.x, wanted.x) * PitchSign;
            if (Mathf.Abs(yawError) < 0.01f && Mathf.Abs(pitchError) < 0.01f)
                return;
            __instance.RotateSpringArm(yawError, pitchError);
            _tickApplied++;
            _lastError = new Vector2(yawError, pitchError);
        }
        catch (Exception exception)
        {
            Log.Error($"Motion aim failed: {exception.Message}");
            motion._hasAimTarget = false;
        }
    }
}
