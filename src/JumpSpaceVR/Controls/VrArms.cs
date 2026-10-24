using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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
    // How far the body follows the player down when they duck or lean down, in meters (further and the legs would
    // sink through the floor).
    private const float MaxBodyDrop = 0.35f;

    private Transform _rig;
    private Transform _locator;
    // Some items hang off the right hand's socket instead of the item locator; they follow the hand.
    private Transform _handSocket;
    private Transform _item;
    private Transform _cameraJoint;
    private Vector3 _rigBasePosition;
    private Quaternion _rigBaseRotation;
    private bool _rigMoved;
    private Bone _rightArm;
    private Bone _leftArm;
    private Transform _highlight;
    private readonly List<(Transform Bone, Quaternion Rotation)> _savedBones = new();
    private readonly List<(Transform Bone, Vector3 Position)> _savedPositions = new();
    private Vector3 _locatorBasePosition;
    private Quaternion _locatorBaseRotation;
    private bool _locatorMoved;
    private bool _bindFailed;

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

    /// <summary>
    /// How far the item ended up from where it was meant to be after the last placement (meters, plus 1 mm per degree),
    /// for tests: an item on the hand socket only gets there through the arm solve.
    /// </summary>
    public float ItemPlacementError { get; private set; }

    /// <summary>Distance from the left hand bone to its target after the last solve (meters), for tests.</summary>
    public float LeftHandError { get; private set; }

    /// <summary>Fore-end grip position relative to the right-hand grip, in weapon space (meters), for tests.</summary>
    public Vector3 GripSpan { get; private set; }

    /// <summary>Horizontal distance from the head to the body's camera joint after placement (meters), for tests.</summary>
    public float BodyHeadDistance { get; private set; }

    /// <summary>Yaw of the body after placement relative to the body direction (degrees), for tests.</summary>
    public float BodyYawError { get; private set; }

    /// <summary>Where the weapon's barrel points after placement (world space), or null.</summary>
    public Ray? Barrel { get; private set; }

    /// <summary>World poses of the hand bones after the last placement, for sharing with other players.</summary>
    public bool TryGetHandBones(out HandPose left, out HandPose right)
    {
        left = default;
        right = default;
        if (_leftArm?.Hand == null || _rightArm?.Hand == null)
            return false;
        left = new HandPose(_leftArm.Hand.position, _leftArm.Hand.rotation);
        right = new HandPose(_rightArm.Hand.position, _rightArm.Hand.rotation);
        return true;
    }

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

        if (_rigMoved && _rig != null)
        {
            _rig.localPosition = _rigBasePosition;
            _rig.localRotation = _rigBaseRotation;
        }
        _rigMoved = false;

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
        SetHighlight(null, Quaternion.identity);
    }

    /// <summary>
    /// Places the weapon and arms for this frame. Hand poses are world space (position of the grip, rotation of the
    /// aim pose); <paramref name="leftGrip"/> is the left controller's grip value; <paramref name="head"/> is the
    /// world position between the eyes.
    /// </summary>
    public bool Update(Transform rig, HandPose rightHand, Quaternion rightAim, HandPose? leftHand, float leftGrip, Vector3 gripOffset,
        Quaternion bodyRotation, float leftHandRoll, Vector3 head)
    {
        // Only while something is held (in the prologue the arms are animated without an item; leave them alone).
        // The game hangs the item on the item locator, or (some spawns) on the right hand's socket.
        if (!Bind(rig))
            return false;
        var onHand = _locator.childCount == 0;
        _item = !onHand ? _locator : _handSocket != null && _handSocket.childCount > 0 ? _handSocket.GetChild(0) : null;
        if (_item == null)
            return false;

        PlaceBodyUnderHead(head, bodyRotation);

        // The animated grip poses, relative to the weapon, before anything moves.
        var weaponPose = new HandPose(_item.position, _item.rotation);
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
        // An item on the locator is placed directly; one on the hand socket follows the right hand, which the arm
        // solve below puts exactly where that item's grip must be.
        if (!onHand)
        {
            if (!_locatorMoved)
            {
                _locatorBasePosition = _locator.localPosition;
                _locatorBaseRotation = _locator.localRotation;
            }
            _locator.SetPositionAndRotation(weaponPosition, weaponRotation);
            _locatorMoved = true;
        }
        var placed = new HandPose(weaponPosition, weaponRotation);
        Barrel = new Ray(weaponPosition, weaponRotation * Vector3.forward);

        // Arms: right hand on the grip; left hand on the fore-end when holding it, otherwise following the controller.
        var rightTarget = Absolute(placed, rightInWeapon);
        SolveArm(_rightArm, rightTarget, bodyRotation, +1f);
        RightHandError = Vector3.Distance(_rightArm.Hand.position, rightTarget.position);

        // Left hand: on the fore-end when holding it (or when the left controller isn't tracked, as in the flat
        // game), otherwise following the left controller.
        var leftOnWeapon = TwoHanded || !leftHand.HasValue;
        // A free hand keeps the game's fore-end grip shape but turns with the left controller, as if that controller
        // were the gun's fore-end, so it follows the wrist instead of staying in the animated (upside-down) pose.
        var leftTarget = leftOnWeapon
            ? Absolute(placed, leftInWeapon)
            : new HandPose(leftHand.Value.position,
                leftHand.Value.rotation * Quaternion.AngleAxis(leftHandRoll, Vector3.forward) * leftInWeapon.rotation);
        SolveArm(_leftArm, leftTarget, bodyRotation, -1f);
        LeftHandError = Vector3.Distance(_leftArm.Hand.position, leftTarget.position);
        ItemPlacementError = Vector3.Distance(_item.position, weaponPosition) + Quaternion.Angle(_item.rotation, weaponRotation) * 0.001f;

        // Show where to grab while the free left hand is close to the fore-end.
        if (CanGrab && !TwoHanded)
        {
            SetHighlight(ForeEndCenter(Absolute(placed, leftInWeapon).position, weaponRotation), weaponRotation);
        }
        else
            SetHighlight(null, weaponRotation);
        return true;
    }

    /// <summary>
    /// The game's first-person body hangs under the game camera: it bobs with the walk animation and turns with the
    /// aim, while the VR view follows the head. Seen from the headset it sways under the player and the shoulders
    /// clip into view when leaning or aiming to the side. Stand it upright, facing the body direction, with its camera
    /// joint under the head (following ducks a little, never rising above the game's height).
    /// </summary>
    private void PlaceBodyUnderHead(Vector3 head, Quaternion bodyRotation)
    {
        if (_cameraJoint == null)
            return;
        if (!_rigMoved)
        {
            _rigBasePosition = _rig.localPosition;
            _rigBaseRotation = _rig.localRotation;
        }

        // The body rotation is already level with the character's floor (a rolled ship tilts it), so stand the body
        // on that floor rather than upright in the world.
        var joint = _cameraJoint.position;
        var turn = bodyRotation * Quaternion.Inverse(_rig.rotation);
        _rig.SetPositionAndRotation(joint + turn * (_rig.position - joint), bodyRotation);

        var up = bodyRotation * Vector3.up;
        var offset = head - _cameraJoint.position;
        var rise = Vector3.Dot(offset, up);
        offset += up * (Mathf.Clamp(rise, -MaxBodyDrop, 0f) - rise);
        _rig.position += offset;
        _rigMoved = true;

        var remaining = head - _cameraJoint.position;
        BodyHeadDistance = (remaining - up * Vector3.Dot(remaining, up)).magnitude;
        BodyYawError = Quaternion.Angle(bodyRotation, _rig.rotation);
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

    private void SolveArm(Bone arm, HandPose target, Quaternion bodyRotation, float side)
    {
        Save(arm.Upper);
        Save(arm.Lower);
        Save(arm.Hand);
        var shoulder = arm.Upper.position;
        // Elbows hang down and slightly out to the side and back.
        var hint = shoulder + bodyRotation * new Vector3(0.35f * side, -0.6f, -0.25f);
        var handRotation = target.rotation;

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
        // A rig without the expected bones is searched (and reported) once, not every frame.
        if (rig == _rig && _bindFailed)
            return false;

        _rig = rig;
        _locator = Find(rig, "ItemAttachLocator");
        _handSocket = Find(rig, HandSocketName);
        _cameraJoint = Find(rig, "CameraJoint");
        _rigMoved = false;
        _rightArm = FindArm(rig, "Right");
        _leftArm = FindArm(rig, "Left");
        var ok = _locator != null && _rightArm != null && _leftArm != null;
        _bindFailed = !ok;
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

    internal const string HandSocketName = "Socket_Right_Attach";

    internal static Transform Find(Transform parent, string name)
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

    private static readonly string[] ForeEndNames = { "Pump", "ForeGrip", "Foregrip", "FrontGrip", "Handguard", "HandGuard" };

    /// <summary>
    /// Centre of the part the left hand holds, on the gun's axis: the fore-end part if the gun has one (a pump moves,
    /// so the rings move with it), otherwise the point on the barrel line nearest the hand's grip.
    /// </summary>
    private Vector3 ForeEndCenter(Vector3 leftGrip, Quaternion weaponRotation)
    {
        foreach (var name in ForeEndNames)
        {
            var part = Find(_item, name);
            if (part != null)
                return part.position;
        }
        var muzzle = Find(_item, "MuzzlePos");
        if (muzzle == null)
            return leftGrip;
        var axis = weaponRotation * Vector3.forward;
        return muzzle.position + axis * Vector3.Dot(leftGrip - muzzle.position, axis);
    }

    // Two glowing bands around the gun, either side of where the left hand grabs (a ring mesh, so the gun stays visible).
    private const float RingRadius = 0.085f;
    private const float RingTubeRadius = 0.005f;
    private const float RingSpacing = 0.09f;

    /// <summary>Two thin glowing rings around the gun, either side of where the left hand grabs.</summary>
    private void SetHighlight(Vector3? center, Quaternion weaponRotation)
    {
        if (center == null)
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
        // The rings are built around their Z axis; turn it to the barrel direction.
        _highlight.SetPositionAndRotation(center.Value, weaponRotation);
    }

    private static Transform CreateHighlight()
    {
        var root = new GameObject("JumpSpaceVR_GripHighlight");
        Object.DontDestroyOnLoad(root);
        root.layer = 29;
        var shader = Shader.Find("HDRP/Unlit");
        Material material = null;
        if (shader != null)
        {
            material = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.SetColor("_UnlitColor", new Color(0.3f, 0.85f, 1f, 1f));
        }

        var mesh = CreateRingMesh(RingRadius, RingTubeRadius);
        foreach (var offset in new[] { -RingSpacing / 2f, RingSpacing / 2f })
        {
            var ring = new GameObject("Ring") { layer = 29 };
            ring.transform.SetParent(root.transform, false);
            ring.transform.localPosition = new Vector3(0f, 0f, offset);
            ring.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ring.AddComponent<MeshRenderer>();
            if (material != null)
                renderer.sharedMaterial = material;
        }
        return root.transform;
    }

    /// <summary>A torus around the Z axis (a thin ring).</summary>
    private static Mesh CreateRingMesh(float radius, float tubeRadius)
    {
        const int Segments = 40;
        const int Sides = 8;
        var vertices = new Vector3[Segments * Sides];
        var triangles = new int[Segments * Sides * 6];
        for (var segment = 0; segment < Segments; segment++)
        {
            var around = segment * Mathf.PI * 2f / Segments;
            var centre = new Vector3(Mathf.Cos(around), Mathf.Sin(around), 0f);
            for (var side = 0; side < Sides; side++)
            {
                var tube = side * Mathf.PI * 2f / Sides;
                vertices[segment * Sides + side] = centre * (radius + tubeRadius * Mathf.Cos(tube)) + Vector3.forward * (tubeRadius * Mathf.Sin(tube));
                var next = (segment + 1) % Segments;
                var nextSide = (side + 1) % Sides;
                var index = (segment * Sides + side) * 6;
                triangles[index] = segment * Sides + side;
                triangles[index + 1] = next * Sides + side;
                triangles[index + 2] = segment * Sides + nextSide;
                triangles[index + 3] = segment * Sides + nextSide;
                triangles[index + 4] = next * Sides + side;
                triangles[index + 5] = next * Sides + nextSide;
            }
        }

        var mesh = new Mesh { name = "JumpSpaceVR_GripRing", hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
        mesh.triangles = new Il2CppStructArray<int>(triangles);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
