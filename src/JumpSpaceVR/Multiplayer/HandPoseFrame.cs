using System;
using UnityEngine;

namespace JumpSpaceVR.Multiplayer;

/// <summary>
/// One player's VR hand poses for one network tick, relative to that player's character (its PlayerController
/// transform), so they survive ships, elevators and anything else the character is parented to.
///
/// Wire format (little endian, <see cref="Size"/> bytes): version, flags, sequence (u16), origin client id (u64),
/// then left and right hand: position in millimetres (3 x i16) and rotation (4 x i16, components x 32767).
/// </summary>
internal struct HandPoseFrame
{
    public const byte Version = 1;
    public const int Size = 1 + 1 + 2 + 8 + 2 * HandBytes;
    private const int HandBytes = 3 * 2 + 4 * 2;
    private const float MillimetresPerMetre = 1000f;
    private const float QuaternionScale = 32767f;

    [Flags]
    public enum Parts : byte
    {
        None = 0,
        Left = 1,
        Right = 2,
        /// <summary>The rotations are hand bone rotations (weapon in hand); otherwise only positions are meaningful.</summary>
        Rotations = 4,
    }

    public Parts Flags;
    public ushort Sequence;
    public ulong Origin;
    public Vector3 LeftPosition;
    public Quaternion LeftRotation;
    public Vector3 RightPosition;
    public Quaternion RightRotation;

    public void Write(byte[] buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer needs {Size} bytes.", nameof(buffer));
        var offset = 0;
        buffer[offset++] = Version;
        buffer[offset++] = (byte)Flags;
        WriteUInt16(buffer, ref offset, Sequence);
        WriteUInt64(buffer, ref offset, Origin);
        WriteHand(buffer, ref offset, LeftPosition, LeftRotation);
        WriteHand(buffer, ref offset, RightPosition, RightRotation);
    }

    /// <summary>Reads a frame; false for another version or a short buffer (a newer or broken sender).</summary>
    public static bool TryRead(byte[] buffer, int length, out HandPoseFrame frame)
    {
        frame = default;
        if (length < Size || buffer[0] != Version)
            return false;
        var offset = 1;
        frame.Flags = (Parts)buffer[offset++];
        frame.Sequence = ReadUInt16(buffer, ref offset);
        frame.Origin = ReadUInt64(buffer, ref offset);
        ReadHand(buffer, ref offset, out frame.LeftPosition, out frame.LeftRotation);
        ReadHand(buffer, ref offset, out frame.RightPosition, out frame.RightRotation);
        return true;
    }

    /// <summary>True when <paramref name="sequence"/> is newer than <paramref name="last"/>, allowing for wrap-around.</summary>
    public static bool IsNewer(ushort sequence, ushort last) => (short)(sequence - last) > 0;

    private static void WriteHand(byte[] buffer, ref int offset, Vector3 position, Quaternion rotation)
    {
        WriteInt16(buffer, ref offset, ToMillimetres(position.x));
        WriteInt16(buffer, ref offset, ToMillimetres(position.y));
        WriteInt16(buffer, ref offset, ToMillimetres(position.z));
        var normalized = Normalize(rotation);
        WriteInt16(buffer, ref offset, (short)Mathf.RoundToInt(normalized.x * QuaternionScale));
        WriteInt16(buffer, ref offset, (short)Mathf.RoundToInt(normalized.y * QuaternionScale));
        WriteInt16(buffer, ref offset, (short)Mathf.RoundToInt(normalized.z * QuaternionScale));
        WriteInt16(buffer, ref offset, (short)Mathf.RoundToInt(normalized.w * QuaternionScale));
    }

    private static void ReadHand(byte[] buffer, ref int offset, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(ReadInt16(buffer, ref offset), ReadInt16(buffer, ref offset), ReadInt16(buffer, ref offset))
                   / MillimetresPerMetre;
        rotation = Normalize(new Quaternion(ReadInt16(buffer, ref offset) / QuaternionScale, ReadInt16(buffer, ref offset) / QuaternionScale,
            ReadInt16(buffer, ref offset) / QuaternionScale, ReadInt16(buffer, ref offset) / QuaternionScale));
    }

    private static short ToMillimetres(float metres) =>
        (short)Mathf.Clamp(Mathf.RoundToInt(metres * MillimetresPerMetre), short.MinValue, short.MaxValue);

    private static Quaternion Normalize(Quaternion rotation)
    {
        var length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
        return length < 1e-6f
            ? Quaternion.identity
            : new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
    }

    private static void WriteInt16(byte[] buffer, ref int offset, short value) => WriteUInt16(buffer, ref offset, (ushort)value);

    private static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
    {
        buffer[offset++] = (byte)value;
        buffer[offset++] = (byte)(value >> 8);
    }

    private static void WriteUInt64(byte[] buffer, ref int offset, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
            buffer[offset++] = (byte)(value >> shift);
    }

    private static short ReadInt16(byte[] buffer, ref int offset) => (short)ReadUInt16(buffer, ref offset);

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        var value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        offset += 2;
        return value;
    }

    private static ulong ReadUInt64(byte[] buffer, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 8)
            value |= (ulong)buffer[offset++] << shift;
        return value;
    }
}
