using System.Buffers.Binary;

namespace IPhoneWebcam.Core.Protocol;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>16-byte little-endian header that precedes every message. See docs/protocol.md §2.</summary>
public readonly record struct MessageHeader(MessageType Type, ushort Flags, uint Length, uint Seq)
{
    public const int Size = 16;
    public const ushort Magic = 0x5749; // bytes 'I' 'W'
    public const byte Version = 1;
    public const uint MaxControlLength = 64 * 1024;
    public const uint MaxMediaLength = 8 * 1024 * 1024;

    public void Write(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest, Magic);
        dest[2] = Version;
        dest[3] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..], Seq);
    }

    public static MessageHeader Parse(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size)
            throw new ProtocolException("Header truncated");
        if (BinaryPrimitives.ReadUInt16LittleEndian(src) != Magic)
            throw new ProtocolException("Bad magic");
        if (src[2] != Version)
            throw new ProtocolException($"Unsupported protocol version {src[2]}");

        var type = (MessageType)src[3];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        var max = type.IsMedia() ? MaxMediaLength : MaxControlLength;
        if (length > max)
            throw new ProtocolException($"Payload too large for {type}: {length} bytes");

        return new MessageHeader(
            type,
            BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
            length,
            BinaryPrimitives.ReadUInt32LittleEndian(src[12..]));
    }
}
