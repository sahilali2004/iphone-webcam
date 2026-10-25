using System.Buffers;

namespace IPhoneWebcam.Core.Protocol;

/// <summary>
/// A received message. The payload is rented from <see cref="ArrayPool{T}.Shared"/>;
/// whoever ends up owning the message must dispose it exactly once.
/// </summary>
public sealed class Message : IDisposable
{
    private byte[]? _buffer;

    internal Message(MessageHeader header, byte[] buffer)
    {
        Header = header;
        _buffer = buffer;
    }

    public MessageHeader Header { get; }

    public ReadOnlyMemory<byte> Payload =>
        (_buffer ?? throw new ObjectDisposedException(nameof(Message))).AsMemory(0, (int)Header.Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
