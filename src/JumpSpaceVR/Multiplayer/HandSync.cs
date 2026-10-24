using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using JumpSpaceVR.Controls;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace JumpSpaceVR.Multiplayer;

/// <summary>
/// Shows other modded players' VR hands on their characters, and sends ours.
///
/// The game runs on Unity Netcode, where clients can only message the host. Every modded player sends its hand
/// poses (<see cref="HandPoseFrame"/>, relative to its character) to the host as a named message; a modded host
/// applies them and forwards them to the other modded clients. Players without the mod never receive anything,
/// and a host without the mod drops the unknown message. To keep that case quiet, a client only probes once a second
/// until the host answers, and sends at full rate after that.
///
/// Received hands bend the remote character's third-person arms with the same two-bone IK as the local arms, right
/// before rendering (after the game's animation), so the game animates the rest of the body as usual.
/// </summary>
internal sealed unsafe class HandSync
{
    private const string MessageName = "JumpSpaceVR.Hands";
    private const float SendInterval = 0.05f;
    private const float ProbeInterval = 1f;
    private const float HeartbeatInterval = 1f;
    private const float HostSilenceSeconds = 5f;
    private const float StaleSeconds = 1f;
    private const float SmoothingSeconds = 0.06f;
    private const float PlayerRefreshSeconds = 1f;
    // A pose from the local player itself, shown on its own third-person body (test option).
    private const ulong LoopbackOrigin = ulong.MaxValue;

    private sealed class Remote
    {
        public HandPoseFrame Target;
        public HandPoseFrame Shown;
        public bool HasShown;
        public float LastReceived;
        public bool HasSequence;
        public ushort LastSequence;
    }

    private sealed class Arm
    {
        public Transform Upper;
        public Transform Lower;
        public Transform Hand;
    }

    private readonly Dictionary<ulong, Remote> _remotes = new();
    private readonly HashSet<ulong> _moddedClients = new();
    private readonly Dictionary<ulong, Il2Cpp.PlayerController> _players = new();
    private readonly Dictionary<int, (Arm Left, Arm Right)> _arms = new();
    private readonly byte[] _sendBuffer = new byte[HandPoseFrame.Size];
    private readonly byte[] _receiveBuffer = new byte[HandPoseFrame.Size];

    private NetworkManager _registeredOn;
    private CustomMessagingManager.HandleNamedMessageDelegate _handler;
    private bool _registrationFailed;
    private HandPoseFrame _local;
    private float _localCapturedAt = -1f;
    private float _nextSend;
    private float _lastHostFrame = -1f;
    private ushort _sequence;
    private float _nextPlayerRefresh;

    /// <summary>Send our hands and show other modded players' hands.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Test option: show our own hands on our own third-person body, through the same encode/apply path.</summary>
    public bool Loopback { get; set; }

    // For tests and diagnostics.
    public int FramesSent { get; private set; }
    public int FramesReceived { get; private set; }
    public int RemotesShown { get; private set; }
    public float LastHandError { get; private set; }
    public bool HostHasMod => _lastHostFrame >= 0f && Time.unscaledTime - _lastHostFrame < HostSilenceSeconds;

    /// <summary>
    /// Records this frame's hand poses (world space). With <paramref name="rotations"/> they are hand bone poses
    /// (weapon in hand), otherwise only the positions are meaningful.
    /// </summary>
    public void CaptureLocal(HandPose? left, HandPose? right, bool rotations)
    {
        var root = LocalPlayerRoot();
        if (!Enabled || root == null || (!left.HasValue && !right.HasValue))
        {
            _localCapturedAt = -1f;
            return;
        }

        var inverse = Quaternion.Inverse(root.rotation);
        _local = new HandPoseFrame { Flags = rotations ? HandPoseFrame.Parts.Rotations : HandPoseFrame.Parts.None };
        if (left.HasValue)
        {
            _local.Flags |= HandPoseFrame.Parts.Left;
            _local.LeftPosition = root.InverseTransformPoint(left.Value.position);
            _local.LeftRotation = inverse * left.Value.rotation;
        }
        if (right.HasValue)
        {
            _local.Flags |= HandPoseFrame.Parts.Right;
            _local.RightPosition = root.InverseTransformPoint(right.Value.position);
            _local.RightRotation = inverse * right.Value.rotation;
        }
        _localCapturedAt = Time.unscaledTime;

        if (Loopback)
        {
            // Round-trip through the wire format so the test covers encoding too.
            var looped = _local;
            looped.Origin = LoopbackOrigin;
            looped.Write(_sendBuffer);
            if (HandPoseFrame.TryRead(_sendBuffer, _sendBuffer.Length, out var decoded))
                Store(decoded);
        }
    }

    /// <summary>Network work: registers the message handler for the current session and sends our hands.</summary>
    public void Update()
    {
        var manager = NetworkManager.Singleton;
        if (!Enabled || manager == null || !manager.IsListening)
        {
            _registeredOn = null;
            return;
        }
        if (!EnsureRegistered(manager))
            return;

        var now = Time.unscaledTime;
        if (now < _nextSend)
            return;

        var haveHands = _localCapturedAt >= 0f && now - _localCapturedAt < StaleSeconds;
        if (manager.IsServer)
        {
            // Host: our hands to every modded client, or a heartbeat so their probes know the host has the mod.
            _nextSend = now + (haveHands ? SendInterval : HeartbeatInterval);
            if (_moddedClients.Count > 0)
                SendLocal(manager, haveHands, toServer: false);
        }
        else
        {
            // Client: probe slowly until the host answers (a host without the mod drops the messages).
            _nextSend = now + (HostHasMod && haveHands ? SendInterval : ProbeInterval);
            SendLocal(manager, haveHands, toServer: true);
        }
    }

    /// <summary>Bends the remote players' arms to their hands. Call right before rendering.</summary>
    public void ApplyRemotes()
    {
        RemotesShown = 0;
        if (!Enabled || _remotes.Count == 0)
            return;
        RefreshPlayers();

        var now = Time.unscaledTime;
        var blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / SmoothingSeconds);
        foreach (var (origin, remote) in _remotes)
        {
            if (now - remote.LastReceived > StaleSeconds)
                continue;
            if (!_players.TryGetValue(origin, out var player) || player == null)
                continue;

            remote.Shown = remote.HasShown ? Blend(remote.Shown, remote.Target, blend) : remote.Target;
            remote.HasShown = true;
            if (Apply(player, remote.Shown))
                RemotesShown++;
        }
    }

    /// <summary>Forgets every remote player (session ended, VR stopped).</summary>
    public void Reset()
    {
        _remotes.Clear();
        _moddedClients.Clear();
        _players.Clear();
        _arms.Clear();
        _lastHostFrame = -1f;
        _localCapturedAt = -1f;
    }

    private bool EnsureRegistered(NetworkManager manager)
    {
        if (_registeredOn == manager)
            return true;
        if (_registrationFailed)
            return false;
        try
        {
            _handler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(
                new Action<ulong, FastBufferReader>(OnMessage));
            manager.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, _handler);
            _registeredOn = manager;
            Reset();
            Log.Msg("Multiplayer hands: listening for other modded players.");
            return true;
        }
        catch (Exception exception)
        {
            _registrationFailed = true;
            Log.Warning($"Multiplayer hands unavailable: {exception.Message}");
            return false;
        }
    }

    private void SendLocal(NetworkManager manager, bool haveHands, bool toServer)
    {
        var frame = haveHands ? _local : new HandPoseFrame();
        frame.Origin = manager.LocalClientId;
        frame.Sequence = ++_sequence;
        frame.Write(_sendBuffer);
        if (toServer)
            Send(manager, manager.ServerClientId, haveHands);
        else
        {
            foreach (var client in _moddedClients)
                Send(manager, client, haveHands);
        }
    }

    private void Send(NetworkManager manager, ulong client, bool haveHands)
    {
        var writer = new FastBufferWriter(HandPoseFrame.Size, Allocator.Temp, HandPoseFrame.Size);
        try
        {
            fixed (byte* bytes = _sendBuffer)
                writer.WriteBytesSafe(bytes, HandPoseFrame.Size, 0);
            // Hands are replaced 20 times a second: a late packet is useless, so don't make the transport resend it.
            manager.CustomMessagingManager.SendNamedMessage(MessageName, client, writer,
                haveHands ? NetworkDelivery.UnreliableSequenced : NetworkDelivery.Reliable);
            FramesSent++;
        }
        catch (Exception exception)
        {
            Log.Warning($"Multiplayer hands: sending to client {client} failed: {exception.Message}");
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void OnMessage(ulong sender, FastBufferReader reader)
    {
        try
        {
            if (reader.Length - reader.Position < HandPoseFrame.Size)
                return;
            fixed (byte* bytes = _receiveBuffer)
                reader.ReadBytesSafe(bytes, HandPoseFrame.Size, 0);
            if (!HandPoseFrame.TryRead(_receiveBuffer, _receiveBuffer.Length, out var frame))
                return;
            FramesReceived++;

            var manager = NetworkManager.Singleton;
            if (manager == null)
                return;
            if (manager.IsServer)
            {
                // A client speaks for itself only; pass its hands on to the other modded clients.
                frame.Origin = sender;
                if (_moddedClients.Add(sender))
                    Log.Msg($"Multiplayer hands: client {sender} has the mod.");
                frame.Write(_receiveBuffer);
                Array.Copy(_receiveBuffer, _sendBuffer, HandPoseFrame.Size);
                foreach (var client in _moddedClients)
                {
                    if (client != sender)
                        Send(manager, client, (frame.Flags & (HandPoseFrame.Parts.Left | HandPoseFrame.Parts.Right)) != 0);
                }
            }
            else if (sender == manager.ServerClientId)
            {
                if (!HostHasMod)
                    Log.Msg("Multiplayer hands: the host has the mod.");
                _lastHostFrame = Time.unscaledTime;
            }

            if (frame.Origin != manager.LocalClientId)
                Store(frame);
        }
        catch (Exception exception)
        {
            Log.Warning($"Multiplayer hands: bad message from client {sender}: {exception.Message}");
        }
    }

    private void Store(in HandPoseFrame frame)
    {
        if ((frame.Flags & (HandPoseFrame.Parts.Left | HandPoseFrame.Parts.Right)) == 0)
            return;
        if (!_remotes.TryGetValue(frame.Origin, out var remote))
        {
            remote = new Remote();
            _remotes[frame.Origin] = remote;
        }
        // Unreliable delivery can reorder; ignore anything older than what we already have.
        if (remote.HasSequence && frame.Origin != LoopbackOrigin && !HandPoseFrame.IsNewer(frame.Sequence, remote.LastSequence))
            return;
        remote.HasSequence = true;
        remote.LastSequence = frame.Sequence;
        remote.Target = frame;
        remote.LastReceived = Time.unscaledTime;
    }

    private bool Apply(Il2Cpp.PlayerController player, in HandPoseFrame frame)
    {
        var blackboard = player.BlackboardData;
        var body = blackboard != null ? blackboard.m_CurrentTPTransform.Value : null;
        if (body == null)
            return false;
        var arms = ArmsOf(body);
        if (arms.Left == null || arms.Right == null)
            return false;

        var root = player.transform;
        var rotations = (frame.Flags & HandPoseFrame.Parts.Rotations) != 0;
        var applied = false;
        if ((frame.Flags & HandPoseFrame.Parts.Right) != 0)
        {
            LastHandError = Solve(arms.Right, root, frame.RightPosition, frame.RightRotation, rotations, +1f);
            applied = true;
        }
        if ((frame.Flags & HandPoseFrame.Parts.Left) != 0)
        {
            Solve(arms.Left, root, frame.LeftPosition, frame.LeftRotation, rotations, -1f);
            applied = true;
        }
        return applied;
    }

    private static float Solve(Arm arm, Transform root, Vector3 localPosition, Quaternion localRotation, bool rotations, float side)
    {
        var target = root.TransformPoint(localPosition);
        var hint = arm.Upper.position + root.rotation * new Vector3(0.35f * side, -0.6f, -0.25f);
        TwoBoneIk.Solve(arm.Upper, arm.Lower, arm.Hand, target, hint, rotations ? root.rotation * localRotation : null);
        return Vector3.Distance(arm.Hand.position, target);
    }

    private (Arm Left, Arm Right) ArmsOf(Transform body)
    {
        var id = body.GetInstanceID();
        if (_arms.TryGetValue(id, out var arms) && arms.Left?.Hand != null && arms.Right?.Hand != null)
            return arms;
        arms = (FindArm(body, "Left"), FindArm(body, "Right"));
        _arms[id] = arms;
        if (arms.Left == null || arms.Right == null)
            Log.Warning($"Multiplayer hands: no arm bones under {body.name}.");
        return arms;
    }

    private static Arm FindArm(Transform body, string side)
    {
        var upper = VrArms.Find(body, side + "Arm");
        var lower = upper != null ? VrArms.Find(upper, side + "ForeArm") : null;
        var hand = lower != null ? VrArms.Find(lower, side + "Hand") : null;
        return hand == null ? null : new Arm { Upper = upper, Lower = lower, Hand = hand };
    }

    private void RefreshPlayers()
    {
        var now = Time.unscaledTime;
        if (now < _nextPlayerRefresh)
            return;
        _nextPlayerRefresh = now + PlayerRefreshSeconds;
        _players.Clear();
        foreach (var player in UnityEngine.Object.FindObjectsByType<Il2Cpp.PlayerController>(FindObjectsSortMode.None))
        {
            if (player == null)
                continue;
            if (player.IsLocalPlayer)
                _players[LoopbackOrigin] = player;
            else
                _players[player.ClientId] = player;
        }
    }

    private static Transform LocalPlayerRoot()
    {
        var handler = Il2Cpp.PlayerHandler.GetPlayerHandler;
        var player = handler != null ? handler.localPlayerController : null;
        return player != null ? player.transform : null;
    }

    private static HandPoseFrame Blend(in HandPoseFrame from, in HandPoseFrame to, float amount)
    {
        var blended = to;
        blended.LeftPosition = Vector3.Lerp(from.LeftPosition, to.LeftPosition, amount);
        blended.RightPosition = Vector3.Lerp(from.RightPosition, to.RightPosition, amount);
        blended.LeftRotation = Quaternion.Slerp(from.LeftRotation, to.LeftRotation, amount);
        blended.RightRotation = Quaternion.Slerp(from.RightRotation, to.RightRotation, amount);
        return blended;
    }
}
