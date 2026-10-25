using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Video;

namespace PhoneWebcam.Core.VirtualCamera;

/// <summary>
/// Feeds decoded phone frames to the virtual camera (PhoneWebcamSource.dll inside the Windows Frame Server)
/// over the named pipe <c>\\.\pipe\PhoneWebcam.Frames</c>: 32-byte header + NV12 frame
/// (layout must match windows/VirtualCamera/src/FrameReceiver.h).
/// Latest-frame-wins: if the previous frame is still being written, the new one is dropped, never queued.
/// </summary>
public sealed class FramePublisher : IDisposable
{
    public const string PipeName = "PhoneWebcam.Frames";
    public const int HeaderSize = 32;
    public const uint Magic = 0x31465750; // 'PWF1'
    public const uint FormatNv12 = 1;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private readonly Task _loop;
    private readonly string _pipeName;
    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private ulong _frameId;
    private int _busy; // 1 while a converted frame waits for, or is being written to, the pipe
    private volatile bool _connected;
    private long _sent;
    private long _dropped;

    public FramePublisher(string? pipeName = null)
    {
        _pipeName = pipeName ?? PipeName;
        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    /// <summary>True while the virtual camera (i.e. some app is using "Phone Webcam") is reading frames.</summary>
    public bool IsCameraReading => _connected;

    public long SentFrames => Interlocked.Read(ref _sent);

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Called on the decode thread. Does not take ownership of <paramref name="frame"/>.</summary>
    public void Publish(VideoFrame frame)
    {
        if (!_connected || frame.Layout != PixelLayout.Bgr32 || (frame.Width | frame.Height) % 2 != 0) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        var size = HeaderSize + frame.Width * frame.Height * 3 / 2;
        if (_buffer.Length != size) _buffer = new byte[size];
        _width = frame.Width;
        _height = frame.Height;
        WriteHeader(_buffer, frame.Width, frame.Height, ++_frameId);
        ColorConvert.Bgr32ToNv12(frame.Pixels, frame.Width, frame.Height, frame.Stride, _buffer.AsSpan(HeaderSize));
        _frameReady.Release();
    }

    public static void WriteHeader(Span<byte> header, int width, int height, ulong frameId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], FormatNv12);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)(width * height * 3 / 2));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], frameId);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 4 << 20, CreateSecurity());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another Phone Webcam instance owns the pipe.
                Log.Warn($"Frame pipe unavailable: {ex.Message}");
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    _connected = true;
                    Log.Info("Virtual camera is reading frames (an app is using Phone Webcam)");
                    while (true)
                    {
                        await _frameReady.WaitAsync(ct).ConfigureAwait(false);
                        await pipe.WriteAsync(_buffer.AsMemory(0, HeaderSize + _width * _height * 3 / 2), ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _sent);
                        Volatile.Write(ref _busy, 0);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    Log.Info("Virtual camera stopped reading frames");
                }
                finally
                {
                    _connected = false;
                    _frameReady.Wait(0);
                    Volatile.Write(ref _busy, 0);
                }
            }
        }
    }

    /// <summary>
    /// The Frame Server runs as LOCAL SERVICE in session 0, so it must be allowed explicitly.
    /// Network access is denied: frames never leave this PC.
    /// </summary>
    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        var read = PipeAccessRights.Read | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), read, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
