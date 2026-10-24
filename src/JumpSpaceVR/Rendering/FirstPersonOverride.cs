using System;
using UnityEngine;

namespace JumpSpaceVR.Rendering;

/// <summary>
/// The game swings its camera out on a spring arm for some moves (boosting in space, sitting in a chair) and shows
/// the third-person body. In a headset that throws the player out of their own body, so while the camera is out
/// the view is put back in the character's head and the third-person body is hidden (it would fill the view).
/// </summary>
internal sealed class FirstPersonOverride
{
    // In first person the camera sits in the head; a spring arm puts it metres away.
    private const float ThirdPersonDistance = 0.6f;
    private const float MaxSpringArmDistance = 10f;
    // The game swaps cameras when it enters and leaves third person; those swaps aren't new views to recenter on.
    private const float SwapGraceSeconds = 1f;
    private float _lastActiveTime = -10f;
    // From the head bone to between the eyes, in the camera's yaw frame (up, forward).
    private static readonly Vector3 EyeOffset = new(0f, 0.06f, 0.1f);

    private GameObject _instigator;
    private Il2CppKeepsake.FirstToThirdPersonVisibilityToggler _toggler;
    private int _playerId;
    private bool _hiding;

    /// <summary>True while the view is held in the head although the game shows third person.</summary>
    public bool Active { get; private set; }

    /// <summary>True during and just after the override, when camera swaps come from entering or leaving it.</summary>
    public bool RecentlyActive => Time.unscaledTime - _lastActiveTime < SwapGraceSeconds;

    /// <summary>Distance from the game camera to the character's head this frame (meters, for tests).</summary>
    public float CameraDistance { get; private set; }

    /// <summary>
    /// Where the view should be this frame: the character's eyes while the game camera is out in third person, or
    /// null to use the game camera. Call before rendering, before the eyes are placed.
    /// </summary>
    public Vector3? Update(bool enabled, Camera camera, bool cutscene)
    {
        var wasActive = Active;
        var head = Place(enabled, camera, cutscene);
        if (Active != wasActive)
            Log.Msg(Active
                ? $"Game camera went third person ({CameraDistance:F1} m from the head); keeping your view in the head."
                : "Game camera back in first person.");
        return head;
    }

    private Vector3? Place(bool enabled, Camera camera, bool cutscene)
    {
        Active = false;
        CameraDistance = 0f;
        try
        {
            var player = LocalPlayer();
            var head = player != null ? HeadOf(player) : null;
            if (!enabled || cutscene || camera == null || head == null)
            {
                Hide(false);
                return null;
            }

            BindToggler(player);
            CameraDistance = Vector3.Distance(camera.transform.position, head.position);
            // Only the game's own third-person mode (it shows the body then; while we hide it, stay on), and only
            // with the camera on a spring arm near the character: map and scripted cameras are seen from where they are.
            // The game's third-person views use their own camera; the first-person camera lives under the character's
            // camera controller (and the game may report the body as shown even then, e.g. in the prologue).
            var thirdPerson = _toggler != null && (_toggler.IsShowingThirdPersonBody || _hiding) &&
                              camera.GetComponentInParent<Il2Cpp.CameraController_Character>() == null;
            if (!thirdPerson || CameraDistance < ThirdPersonDistance || CameraDistance > MaxSpringArmDistance)
            {
                Hide(false);
                return null;
            }

            Active = true;
            _lastActiveTime = Time.unscaledTime;
            Hide(true);
            var yaw = Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f);
            return head.position + yaw * EyeOffset;
        }
        catch (Exception exception)
        {
            Log.Warning($"First person override failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Shows the body again (VR stopped).</summary>
    public void Release()
    {
        Hide(false);
        Active = false;
    }

    private void BindToggler(Il2Cpp.PlayerController player)
    {
        var id = player.GetInstanceID();
        if (id == _playerId && _toggler != null)
            return;
        Hide(false);
        _playerId = id;
        _toggler = player.GetComponentInChildren<Il2CppKeepsake.FirstToThirdPersonVisibilityToggler>(true);
    }

    private void Hide(bool hide)
    {
        if (hide == _hiding || _toggler == null)
        {
            if (_toggler == null)
                _hiding = false;
            return;
        }
        _instigator ??= CreateInstigator();
        if (hide)
            _toggler.HideAllMeshesByInstigator(_instigator);
        else
            _toggler.ClearHideAllMeshesByInstigator(_instigator);
        _hiding = hide;
    }

    private static GameObject CreateInstigator()
    {
        var instigator = new GameObject("JumpSpaceVR_FirstPerson");
        UnityEngine.Object.DontDestroyOnLoad(instigator);
        return instigator;
    }

    private static Il2Cpp.PlayerController LocalPlayer()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        return handler != null ? handler.localPlayerController : null;
    }

    private static Transform HeadOf(Il2Cpp.PlayerController player)
    {
        var blackboard = player.BlackboardData;
        var head = blackboard != null ? blackboard.m_CurrentTPHeadTransform.Value : null;
        return head != null ? head : player.HeadTransform;
    }
}
