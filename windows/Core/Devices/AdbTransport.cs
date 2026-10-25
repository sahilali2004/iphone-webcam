using System.Net;
using System.Net.Sockets;
using PhoneWebcam.Core.Transport;

namespace PhoneWebcam.Core.Devices;

/// <summary>
/// USB for Android: <c>adb forward</c> a free local port to the phone's port 47809, then plain TCP to 127.0.0.1.
/// The forward is removed when the stream is disposed.
/// </summary>
public sealed class AdbTransport(AdbClient adb) : ITransport
{
    public TransportKind Kind => TransportKind.Usb;

    public async Task<Stream> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        if (endpoint.Kind != TransportKind.Usb || string.IsNullOrEmpty(endpoint.Serial))
            throw new ArgumentException("USB endpoint requires an adb serial", nameof(endpoint));

        var localPort = await adb.ForwardAsync(endpoint.Serial, endpoint.Port, ct).ConfigureAwait(false);
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WifiTransport.ConnectTimeout);
            await client.ConnectAsync(IPAddress.Loopback, localPort, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            await adb.RemoveForwardAsync(localPort).ConfigureAwait(false);
            throw;
        }
        return new ForwardedStream(client, () => _ = adb.RemoveForwardAsync(localPort));
    }

    /// <summary>NetworkStream that also tears down the adb forward.</summary>
    private sealed class ForwardedStream(TcpClient client, Action onClose) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();
        private int _closed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _inner.Dispose();
                client.Dispose();
                onClose();
            }
            base.Dispose(disposing);
        }
    }
}
