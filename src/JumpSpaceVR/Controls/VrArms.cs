using System.Collections.Generic;
using UnityEngine;

namespace JumpSpaceVR.Controls;

/// <summary>A position and rotation (Unity's Pose has no usable constructor through the IL2CPP interop).</summary>
internal readonly struct HandPose
{
    public readonly Vector3 position;
    public readonly Quaternion rotation;

    public HandPose(Vector3 position, Quaternion rotation)
    {
        this.position = position;
        this.rotation = rotation;
    }
}

/// <summary>
/// Weapon in hand without moving the body: the held item (under the rig's ItemAttachLocator) is placed on the right
/// controller, and both arms of the first-person body are bent with two-bone IK so the hands follow.
///
/// The game animates the hands onto the weapon (right hand on the grip, left hand on the fore-end). Those animated
/// hand poses, taken relative to the weapon each frame, become the IK targets after the weapon is moved, so the
/// grip looks the same as in the flat game. With one hand the gun points along the right controller and the left
/// hand follows the left controller; when the left hand comes close to the fore-end it is highlighted, and holding
/// the left grip there makes it a two-handed hold: the gun then points from the right hand towards the left.
/// </summary>
internal sealed class VrArms
{
    private const float GrabRadius = 0.14f;
    private const float ReleaseRadius = 0.30f;
    private const float GripPressed = 0.6f;
    // The game's body sits behind the camera and its arms are short (0.52 m shoulder to wrist), so a gun held where
    // players hold it is often out of reach; arms may stretch up to this factor rather than leave the hand behind.
    private const float MaxArmStretch = 1.4f;

    private Transform _rig;
    private Transform _locator;
    private Bone _rightArm;
    private Bone _leftArm;
    private Transform _highlight;
    private readonly List<(Transform Bone, Quaternion Rotation)> _savedBones = new();
    private readonly List<(Transform Bone, Vector3 Position)> _savedPositions = new();
    private Vector3 _locatorBasePosition;
    private Quaternion _locatorBaseRotation;
    private bool _locatorMoved;

    private sealed class Bone
    {
        public Transform Upper;
        public Transform Lower;
        public Transform Hand;
    }

    /// <summary>True while the left hand holds the weapon's fore-end.</summary>
    public bool TwoHanded { get; private set; }

    /// <summary>True when the left hand is close enough to the fore-end to grab it.</summary>
    public bool CanGrab { get; private set; }

    /// <summary>Distance from the right hand bone to its target after the last solve (meters), for tests.</summary>
    public float RightHandError { get; private set; }

    /// <summary>Distance from the left hand bone to its target after the last solve (meters), for tests.</summary>
    public float LeftHandError { get; private set; }

    /// <summary>Fore-end grip position relative to the right-hand grip, in weapon space (meters), for tests.</summary>
    public Vector3 GripSpan { get; private set; }

    /// <summary>Where the weapon's barrel points after placement (world space), or null.</summary>
    public Ray? Barrel { get; private set; }

    /// <summary>Undoes last frame's changes (call early in the frame, before the game animates and simulates).</summary>
    public void Restore()
    {
        foreach (var (bone, rotation) in _savedBones)
        {
            if (bone != null)
                bone.localRotation = rotation;
        }
        _savedBones.Clear();
        foreach (var (bone, position) in _savedPositions)
        {
            if (bone != null)
                bone.localPosition = position;
        }
        _savedPositions.Clear();

        if (_locatorMoved && _locator != null)
        {
            _locator.localPosition = _locatorBasePosition;
            _locator.localRotation = _locatorBaseRotation;
        }
        _locatorMoved = false;
    }

    /// <summary>Weapon in hand is not active this frame: drop the hold and hide the grab highlight.</summary>
    public void Idle()
    {
        TwoHanded = false;
        CanGrab = false;
        Barrel = null;
        SetHighlight(null);
    }

    /// <summary>
    /// Places the weapon and arms for this frame. Hand poses are world space; <paramref name="leftGrip"/> is the left
    /// controller's grip value.
    /// </summary>
    public bool Update(Transform rig, HandPose rightHand, Quaternion rightAim, HandPose? leftHand, float leftGrip, Vector3 gripOffset,
        Quaternion bodyRotation)
    {
        // Only while something is held (in the prologue the arms are animated without an item; leave them alone).
        if (!Bind(rig) || _locator.childCount == 0)
            return false;

        // The animated grip poses, relative to the weapon, before anything moves.
        var weaponPose = new HandPose(_locator.position, _locator.rotation);
        var rightInWeapon = Relative(weaponPose, new HandPose(_rightArm.Hand.position, _rightArm.Hand.rotation));
        var leftInWeapon = Relative(weaponPose, new HandPose(_leftArm.Hand.position, _leftArm.Hand.rotation));
        GripSpan = leftInWeapon.position - rightInWeapon.position;

        // Weapon orientation: along the right controller, or from the right hand to the left hand when two-handed.
        UpdateGrabState(rightHand, rightAim, leftHand, leftGrip, rightInWeapon, leftInWeapon);
        var weaponRotation = rightAim;
        if (TwoHanded && leftHand.HasValue)
        {
            // Turn the weapon so its right-to-left grip axis points from the right hand to the left hand, keeping the
            // roll of the right controller.
            var gripAxis = weaponRotation * (leftInWeapon.position - rightInWeapon.position);
            var handsAxis = leftHand.Value.position - rightHand.position;
            weaponRotation = Quaternion.FromToRotation(gripAxis, handsAxis) * weaponRotation;
        }

        // Land the animated right-hand grip point on the controller (plus the user's fine-tuning offset).
        var weaponPosition = rightHand.position - weaponRotation * (rightInWeapon.position - gripOffset);
        if (!_locatorMoved)
        {
            _locatorBasePosition = _locator.localPosition;
            _locatorBaseRotation = _locator.localRotation;
        }
        _locator.SetPositionAndRotation(weaponPosition, weaponRotation);
        _locatorMoved = true;
        var placed = new HandPose(weaponPosition, weaponRotation);
        Barrel = new Ray(weaponPosition, weaponRotation * Vector3.forward);

        // Arms: right hand on the grip; left hand on the fore-end when holding it, otherwise following the controller.
        var rightTarget = Absolute(placed, rightInWeapon);
        SolveArm(_rightArm, rightTarget, bodyRotation, +1f);
        RightHandError = Vector3.Distance(_rightArm.Hand.position, rightTarget.position);

        // Left hand: on the fore-end when holding it (or when the left controller isn't tracked, as in the flat
        // game), otherwise following the left controller.
        var leftOnWeapon = TwoHanded || !leftHand.HasValue;
        var leftTarget = leftOnWeapon ? Absolute(placed, leftInWeapon) : new HandPose(leftHand.Value.position, _leftArm.Hand.rotation);
        SolveArm(_leftArm, leftTarget, bodyRotation, -1f, keepHandRotation: !leftOnWeapon);
        LeftHandError = Vector3.Distance(_leftArm.Hand.position, leftTarget.position);

        // Show where to grab while the free left hand is close to the fore-end.
        SetHighlight(CanGrab && !TwoHanded ? Absolute(placed, leftInWeapon).position : null);
        return true;
    }

    private void UpdateGrabState(HandPose rightHand, Quaternion rightAim, HandPose? leftHand, float leftGrip, HandPose rightInWeapon, HandPose leftInWeapon)
    {
        if (!leftHand.HasValue)
        {
            TwoHanded = false;
            CanGrab = false;
            return;
        }

        // Where the fore-end would be with the weapon held one-handed this frame.
        var oneHanded = new HandPose(rightHand.position - rightAim * rightInWeapon.position, rightAim);
        var foreEnd = Absolute(oneHanded, leftInWeapon).position;
        var distance = Vector3.Distance(leftHand.Value.position, foreEnd);
        CanGrab = distance < GrabRadius;

        var gripping = leftGrip > GripPressed;
        if (!gripping)
            TwoHanded = false;
        else if (!TwoHanded && CanGrab)
            TwoHanded = true;
        else if (TwoHanded && Vector3.Distance(leftHand.Value.position, rightHand.position) > ReleaseRadius + Vector3.Distance(foreEnd, rightHand.position))
            TwoHanded = false;
    }

    private void SolveArm(Bone arm, HandPose target, Quaternion bodyRotation, float side, bool keepHandRotation = false)
    {
        Save(arm.Upper);
        Save(arm.Lower);
        Save(arm.Hand);
        var shoulder = arm.Upper.position;
        // Elbows hang down and slightly out to the side and back.
        var hint = shoulder + bodyRotation * new Vector3(0.35f * side, -0.6f, -0.25f);
        var handRotation = keepHandRotation ? arm.Hand.rotation : target.rotation;

        var armLength = Vector3.Distance(shoulder, arm.Lower.position) + Vector3.Distance(arm.Lower.position, arm.Hand.position);
        var stretch = Mathf.Clamp(Vector3.Distance(shoulder, target.position) / armLength, 1f, MaxArmStretch);
        if (stretch > 1.001f)
        {
            _savedPositions.Add((arm.Lower, arm.Lower.localPosition));
            _savedPositions.Add((arm.Hand, arm.Hand.localPosition));
            arm.Lower.localPosition *= stretch;
            arm.Hand.localPosition *= stretch;
        }
        TwoBoneIk.Solve(arm.Upper, arm.Lower, arm.Hand, target.position, hint, handRotation);
    }

    private void Save(Transform bone)
    {
        foreach (var (saved, _) in _savedBones)
        {
            if (saved == bone)
                return;
        }
        _savedBones.Add((bone, bone.localRotation));
    }

    private bool Bind(Transform rig)
    {
        if (rig == null)
            return false;
        if (rig == _rig && _locator != null && _rightArm?.Hand != null && _leftArm?.Hand != null)
            return true;

        _rig = rig;
        _locator = Find(rig, "ItemAttachLocator");
        _rightArm = FindArm(rig, "Right");
        _leftArm = FindArm(rig, "Left");
        var ok = _locator != null && _rightArm != null && _leftArm != null;
        if (!ok)
            Log.Warning("Weapon in hand: the first-person rig has no ItemAttachLocator or arm bones; leaving it as the game places it.");
        return ok;
    }

    private static Bone FindArm(Transform rig, string side)
    {
        var upper = Find(rig, side + "Arm");
        var lower = upper != null ? Find(upper, side + "ForeArm") : null;
        var hand = lower != null ? Find(lower, side + "Hand") : null;
        return hand == null ? null : new Bone { Upper = upper, Lower = lower, Hand = hand };
    }

    private static Transform Find(Transform parent, string name)
    {
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child.name == name)
                return child;
            var found = Find(child, name);
            if (found != null)
                return found;
        }
        return null;
    }

    private static HandPose Relative(HandPose parent, HandPose child)
    {
        var inverse = Quaternion.Inverse(parent.rotation);
        return new HandPose(inverse * (child.position - parent.position), inverse * child.rotation);
    }

    private static HandPose Absolute(HandPose parent, HandPose local) =>
        new(parent.position + parent.rotation * local.position, parent.rotation * local.rotation);

    private void SetHighlight(Vector3? position)
    {
        if (position == null)
        {
            if (_highlight != null)
                _highlight.gameObject.SetActive(false);
            return;
        }

        if (_highlight == null)
            _highlight = CreateHighlight();
        if (_highlight == null)
            return;
        _highlight.gameObject.SetActive(true);
        _highlight.position = position.Value;
    }

    private static Transform CreateHighlight()
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "JumpSpaceVR_GripHighlight";
        Object.DontDestroyOnLoad(sphere);
        var collider = sphere.GetComponent<Collider>();
        if (collider != null)
            Object.Destroy(collider);
        sphere.transform.localScale = Vector3.one * 0.05f;
        sphere.layer = 29;
        var renderer = sphere.GetComponent<Renderer>();
        var shader = Shader.Find("HDRP/Unlit");
        if (renderer != null && shader != null)
        {
            var material = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.SetColor("_UnlitColor", new Color(0.3f, 0.9f, 1f, 1f));
            renderer.sharedMaterial = material;
        }
        return sphere.transform;
    }
}
