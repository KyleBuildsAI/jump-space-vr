using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace JumpSpaceVR.OpenXR;

/// <summary>Controller state for one frame, in gamepad terms.</summary>
internal struct ControllerState
{
    public bool IsActive;
    public Vector2 LeftStick;
    public Vector2 RightStick;
    public float LeftTrigger;
    public float RightTrigger;
    public float LeftGrip;
    public float RightGrip;
    public bool ButtonA;
    public bool ButtonB;
    public bool ButtonX;
    public bool ButtonY;
    public bool Menu;
    public bool LeftStickClick;
    public bool RightStickClick;
}

/// <summary>
/// Reads VR controllers through the OpenXR action system directly. Unity's OpenXR input layer depends on a
/// managed package the game does not ship, so the mod creates its own action set on Unity's session.
/// </summary>
internal sealed unsafe class OpenXRControllers
{
    private const int XrSuccess = 0;
    private const int TypeActionStateBoolean = 23;
    private const int TypeActionStateFloat = 24;
    private const int TypeActionStateVector2f = 25;
    private const int TypeActionSetCreateInfo = 28;
    private const int TypeActionCreateInfo = 29;
    private const int TypeInteractionProfileSuggestedBinding = 51;
    private const int TypeActionStateGetInfo = 58;
    private const int TypeSessionActionSetsAttachInfo = 60;
    private const int TypeActionsSyncInfo = 61;

    private enum ActionType { Boolean = 1, Float = 2, Vector2 = 3 }

    private enum Action
    {
        LeftStick, RightStick, LeftTrigger, RightTrigger, LeftGrip, RightGrip,
        ButtonA, ButtonB, ButtonX, ButtonY, Menu, LeftStickClick, RightStickClick,
    }

    private static readonly (Action Action, string Name, ActionType Type)[] ActionDefinitions =
    {
        (Action.LeftStick, "left_stick", ActionType.Vector2),
        (Action.RightStick, "right_stick", ActionType.Vector2),
        (Action.LeftTrigger, "left_trigger", ActionType.Float),
        (Action.RightTrigger, "right_trigger", ActionType.Float),
        (Action.LeftGrip, "left_grip", ActionType.Float),
        (Action.RightGrip, "right_grip", ActionType.Float),
        (Action.ButtonA, "button_a", ActionType.Boolean),
        (Action.ButtonB, "button_b", ActionType.Boolean),
        (Action.ButtonX, "button_x", ActionType.Boolean),
        (Action.ButtonY, "button_y", ActionType.Boolean),
        (Action.Menu, "menu", ActionType.Boolean),
        (Action.LeftStickClick, "left_stick_click", ActionType.Boolean),
        (Action.RightStickClick, "right_stick_click", ActionType.Boolean),
    };

    /// <summary>Suggested bindings per interaction profile. Runtimes pick the profile matching the hardware.</summary>
    private static readonly (string Profile, (Action Action, string Path)[] Bindings)[] Profiles =
    {
        ("/interaction_profiles/oculus/touch_controller", new[]
        {
            (Action.LeftStick, "/user/hand/left/input/thumbstick"),
            (Action.RightStick, "/user/hand/right/input/thumbstick"),
            (Action.LeftTrigger, "/user/hand/left/input/trigger/value"),
            (Action.RightTrigger, "/user/hand/right/input/trigger/value"),
            (Action.LeftGrip, "/user/hand/left/input/squeeze/value"),
            (Action.RightGrip, "/user/hand/right/input/squeeze/value"),
            (Action.ButtonA, "/user/hand/right/input/a/click"),
            (Action.ButtonB, "/user/hand/right/input/b/click"),
            (Action.ButtonX, "/user/hand/left/input/x/click"),
            (Action.ButtonY, "/user/hand/left/input/y/click"),
            (Action.Menu, "/user/hand/left/input/menu/click"),
            (Action.LeftStickClick, "/user/hand/left/input/thumbstick/click"),
            (Action.RightStickClick, "/user/hand/right/input/thumbstick/click"),
        }),
        ("/interaction_profiles/valve/index_controller", new[]
        {
            (Action.LeftStick, "/user/hand/left/input/thumbstick"),
            (Action.RightStick, "/user/hand/right/input/thumbstick"),
            (Action.LeftTrigger, "/user/hand/left/input/trigger/value"),
            (Action.RightTrigger, "/user/hand/right/input/trigger/value"),
            (Action.LeftGrip, "/user/hand/left/input/squeeze/value"),
            (Action.RightGrip, "/user/hand/right/input/squeeze/value"),
            (Action.ButtonA, "/user/hand/right/input/a/click"),
            (Action.ButtonB, "/user/hand/right/input/b/click"),
            (Action.ButtonX, "/user/hand/left/input/a/click"),
            (Action.ButtonY, "/user/hand/left/input/b/click"),
            (Action.Menu, "/user/hand/left/input/system/click"),
            (Action.LeftStickClick, "/user/hand/left/input/thumbstick/click"),
            (Action.RightStickClick, "/user/hand/right/input/thumbstick/click"),
        }),
        ("/interaction_profiles/khr/simple_controller", new[]
        {
            (Action.RightTrigger, "/user/hand/right/input/select/click"),
            (Action.LeftTrigger, "/user/hand/left/input/select/click"),
            (Action.Menu, "/user/hand/left/input/menu/click"),
        }),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionSetCreateInfo
    {
        public int Type;
        public void* Next;
        public fixed byte ActionSetName[64];
        public fixed byte LocalizedActionSetName[128];
        public uint Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionCreateInfo
    {
        public int Type;
        public void* Next;
        public fixed byte ActionName[64];
        public int ActionType;
        public uint CountSubactionPaths;
        public ulong* SubactionPaths;
        public fixed byte LocalizedActionName[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionSuggestedBinding
    {
        public ulong Action;
        public ulong Binding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrInteractionProfileSuggestedBinding
    {
        public int Type;
        public void* Next;
        public ulong InteractionProfile;
        public uint CountSuggestedBindings;
        public XrActionSuggestedBinding* SuggestedBindings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrSessionActionSetsAttachInfo
    {
        public int Type;
        public void* Next;
        public uint CountActionSets;
        public ulong* ActionSets;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActiveActionSet
    {
        public ulong ActionSet;
        public ulong SubactionPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionsSyncInfo
    {
        public int Type;
        public void* Next;
        public uint CountActiveActionSets;
        public XrActiveActionSet* ActiveActionSets;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionStateGetInfo
    {
        public int Type;
        public void* Next;
        public ulong Action;
        public ulong SubactionPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionStateBoolean
    {
        public int Type;
        public void* Next;
        public uint CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionStateFloat
    {
        public int Type;
        public void* Next;
        public float CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XrActionStateVector2f
    {
        public int Type;
        public void* Next;
        public float X;
        public float Y;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    private delegate* unmanaged<ulong, byte*, ulong*, int> _stringToPath;
    private delegate* unmanaged<ulong, XrActionSetCreateInfo*, ulong*, int> _createActionSet;
    private delegate* unmanaged<ulong, XrActionCreateInfo*, ulong*, int> _createAction;
    private delegate* unmanaged<ulong, XrInteractionProfileSuggestedBinding*, int> _suggestBindings;
    private delegate* unmanaged<ulong, XrSessionActionSetsAttachInfo*, int> _attachActionSets;
    private delegate* unmanaged<ulong, XrActionsSyncInfo*, int> _syncActions;
    private delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateBoolean*, int> _getBoolean;
    private delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateFloat*, int> _getFloat;
    private delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateVector2f*, int> _getVector2;

    private readonly Dictionary<Action, ulong> _actions = new();
    private int _generation;
    private ulong _actionSet;
    private ulong _attachedSession;
    private bool _setupFailed;
    private int _syncFailures;

    public bool IsReady => _attachedSession != 0 && _generation == OpenXRHooks.Generation && _attachedSession == OpenXRHooks.Session;

    /// <summary>Creates and attaches the action set. Must run after the session exists and before it begins.</summary>
    public bool Setup()
    {
        if (IsReady)
            return true;

        // A new OpenXR instance (VR toggled or headset reconnected) invalidates all previous handles.
        if (_generation != OpenXRHooks.Generation)
        {
            _generation = OpenXRHooks.Generation;
            _actionSet = 0;
            _actions.Clear();
            _attachedSession = 0;
            _setupFailed = false;
        }

        if (_setupFailed || OpenXRHooks.Session == 0)
            return false;

        try
        {
            if (!ResolveFunctions())
                return Fail("OpenXR input functions unavailable");

            var instance = OpenXRHooks.Instance;
            if (_actionSet == 0 && !CreateActionSet(instance))
                return false;

            foreach (var (profile, bindings) in Profiles)
                SuggestBindings(instance, profile, bindings);

            var session = OpenXRHooks.Session;
            var actionSet = _actionSet;
            var attachInfo = new XrSessionActionSetsAttachInfo
            {
                Type = TypeSessionActionSetsAttachInfo,
                CountActionSets = 1,
                ActionSets = &actionSet,
            };
            var result = _attachActionSets(session, &attachInfo);
            if (result != XrSuccess)
                return Fail($"xrAttachSessionActionSets failed ({result})");

            _attachedSession = session;
            Log.Msg("VR controller input ready.");
            return true;
        }
        catch (Exception exception)
        {
            return Fail(exception.Message);
        }
    }

    /// <summary>Syncs and reads all actions. Returns inactive state when controllers are unavailable.</summary>
    public ControllerState Read()
    {
        var state = new ControllerState();
        if (!IsReady)
            return state;

        var activeSet = new XrActiveActionSet { ActionSet = _actionSet };
        var syncInfo = new XrActionsSyncInfo { Type = TypeActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &activeSet };
        var result = _syncActions(_attachedSession, &syncInfo);
        if (result != XrSuccess)
        {
            // XR_SESSION_NOT_FOCUSED (8) is expected while the headset is off or a system menu is open.
            if (result != 8 && _syncFailures++ < 5)
                Log.Warning($"xrSyncActions failed ({result}).");
            return state;
        }

        state.LeftStick = ReadVector2(Action.LeftStick, ref state.IsActive);
        state.RightStick = ReadVector2(Action.RightStick, ref state.IsActive);
        state.LeftTrigger = ReadFloat(Action.LeftTrigger, ref state.IsActive);
        state.RightTrigger = ReadFloat(Action.RightTrigger, ref state.IsActive);
        state.LeftGrip = ReadFloat(Action.LeftGrip, ref state.IsActive);
        state.RightGrip = ReadFloat(Action.RightGrip, ref state.IsActive);
        state.ButtonA = ReadBoolean(Action.ButtonA, ref state.IsActive);
        state.ButtonB = ReadBoolean(Action.ButtonB, ref state.IsActive);
        state.ButtonX = ReadBoolean(Action.ButtonX, ref state.IsActive);
        state.ButtonY = ReadBoolean(Action.ButtonY, ref state.IsActive);
        state.Menu = ReadBoolean(Action.Menu, ref state.IsActive);
        state.LeftStickClick = ReadBoolean(Action.LeftStickClick, ref state.IsActive);
        state.RightStickClick = ReadBoolean(Action.RightStickClick, ref state.IsActive);
        return state;
    }

    private bool ResolveFunctions()
    {
        _stringToPath = (delegate* unmanaged<ulong, byte*, ulong*, int>)OpenXRHooks.Resolve("xrStringToPath");
        _createActionSet = (delegate* unmanaged<ulong, XrActionSetCreateInfo*, ulong*, int>)OpenXRHooks.Resolve("xrCreateActionSet");
        _createAction = (delegate* unmanaged<ulong, XrActionCreateInfo*, ulong*, int>)OpenXRHooks.Resolve("xrCreateAction");
        _suggestBindings = (delegate* unmanaged<ulong, XrInteractionProfileSuggestedBinding*, int>)OpenXRHooks.Resolve("xrSuggestInteractionProfileBindings");
        _attachActionSets = (delegate* unmanaged<ulong, XrSessionActionSetsAttachInfo*, int>)OpenXRHooks.Resolve("xrAttachSessionActionSets");
        _syncActions = (delegate* unmanaged<ulong, XrActionsSyncInfo*, int>)OpenXRHooks.Resolve("xrSyncActions");
        _getBoolean = (delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateBoolean*, int>)OpenXRHooks.Resolve("xrGetActionStateBoolean");
        _getFloat = (delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateFloat*, int>)OpenXRHooks.Resolve("xrGetActionStateFloat");
        _getVector2 = (delegate* unmanaged<ulong, XrActionStateGetInfo*, XrActionStateVector2f*, int>)OpenXRHooks.Resolve("xrGetActionStateVector2f");

        return _stringToPath != null && _createActionSet != null && _createAction != null && _suggestBindings != null
               && _attachActionSets != null && _syncActions != null && _getBoolean != null && _getFloat != null && _getVector2 != null;
    }

    private bool CreateActionSet(ulong instance)
    {
        var setInfo = new XrActionSetCreateInfo { Type = TypeActionSetCreateInfo, Priority = 0 };
        CopyString("jumpspacevr", setInfo.ActionSetName, 64);
        CopyString("Jump Space VR", setInfo.LocalizedActionSetName, 128);
        ulong actionSet;
        var result = _createActionSet(instance, &setInfo, &actionSet);
        if (result != XrSuccess)
            return Fail($"xrCreateActionSet failed ({result})");
        _actionSet = actionSet;

        foreach (var (action, name, type) in ActionDefinitions)
        {
            var actionInfo = new XrActionCreateInfo { Type = TypeActionCreateInfo, ActionType = (int)type };
            CopyString(name, actionInfo.ActionName, 64);
            CopyString(name.Replace('_', ' '), actionInfo.LocalizedActionName, 128);
            ulong handle;
            result = _createAction(actionSet, &actionInfo, &handle);
            if (result != XrSuccess)
                return Fail($"xrCreateAction({name}) failed ({result})");
            _actions[action] = handle;
        }
        return true;
    }

    private void SuggestBindings(ulong instance, string profile, (Action Action, string Path)[] bindings)
    {
        var suggested = new XrActionSuggestedBinding[bindings.Length];
        for (var index = 0; index < bindings.Length; index++)
        {
            suggested[index] = new XrActionSuggestedBinding
            {
                Action = _actions[bindings[index].Action],
                Binding = ToPath(instance, bindings[index].Path),
            };
        }

        fixed (XrActionSuggestedBinding* bindingPointer = suggested)
        {
            var info = new XrInteractionProfileSuggestedBinding
            {
                Type = TypeInteractionProfileSuggestedBinding,
                InteractionProfile = ToPath(instance, profile),
                CountSuggestedBindings = (uint)suggested.Length,
                SuggestedBindings = bindingPointer,
            };
            var result = _suggestBindings(instance, &info);
            if (result != XrSuccess)
                Log.Warning($"Runtime rejected bindings for {profile} ({result}).");
        }
    }

    private ulong ToPath(ulong instance, string path)
    {
        var bytes = Encoding.ASCII.GetBytes(path + "\0");
        ulong result = 0;
        fixed (byte* pathPointer = bytes)
            _stringToPath(instance, pathPointer, &result);
        return result;
    }

    private bool ReadBoolean(Action action, ref bool anyActive)
    {
        var info = new XrActionStateGetInfo { Type = TypeActionStateGetInfo, Action = _actions[action] };
        var state = new XrActionStateBoolean { Type = TypeActionStateBoolean };
        if (_getBoolean(_attachedSession, &info, &state) != XrSuccess)
            return false;
        // Some runtimes (e.g. Unity's mock) never set isActive; inactive inputs read as 0 everywhere, so a
        // non-zero value also proves a controller is there.
        anyActive |= state.IsActive != 0 || state.CurrentState != 0;
        return state.CurrentState != 0;
    }

    private float ReadFloat(Action action, ref bool anyActive)
    {
        var info = new XrActionStateGetInfo { Type = TypeActionStateGetInfo, Action = _actions[action] };
        var state = new XrActionStateFloat { Type = TypeActionStateFloat };
        if (_getFloat(_attachedSession, &info, &state) != XrSuccess)
            return 0f;
        anyActive |= state.IsActive != 0 || state.CurrentState != 0f;
        return state.CurrentState;
    }

    private Vector2 ReadVector2(Action action, ref bool anyActive)
    {
        var info = new XrActionStateGetInfo { Type = TypeActionStateGetInfo, Action = _actions[action] };
        var state = new XrActionStateVector2f { Type = TypeActionStateVector2f };
        if (_getVector2(_attachedSession, &info, &state) != XrSuccess)
            return Vector2.zero;
        anyActive |= state.IsActive != 0 || state.X != 0f || state.Y != 0f;
        return new Vector2(state.X, state.Y);
    }

    private static void CopyString(string value, byte* destination, int capacity)
    {
        var length = Math.Min(value.Length, capacity - 1);
        for (var index = 0; index < length; index++)
            destination[index] = (byte)value[index];
        destination[length] = 0;
    }

    private bool Fail(string reason)
    {
        _setupFailed = true;
        Log.Warning($"VR controllers unavailable: {reason}. Use a gamepad or mouse and keyboard.");
        return false;
    }
}
