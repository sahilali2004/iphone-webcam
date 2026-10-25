using System.Net;
using System.Net.Sockets;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using PhoneWebcam.Core.Timing;
using PhoneWebcam.Core.Transport;

namespace PhoneWebcam.TestSender;

public sealed class FakePhoneOptions
{
    public IPAddress Bind { get; init; } = IPAddress.Loopback;

    /// <summary>0 = pick a free port (tests).</summary>
    public int Port { get; init; } = DeviceEndpoint.DefaultPort;

    public string DeviceName { get; init; } = "Test Sender";

    public Func<int, int, int, double, IJpegSource> SourceFactory { get; init; } =
        (w, h, fps, q) => new SyntheticJpegSource(w, h, fps, q);
}

/// <summary>
/// Stand-in for the phone app: implements the phone side of docs/protocol.md
/// (listener, pairing, auth, START/STOP, PING/PONG, paced VIDEO_FRAMEs with latest-frame-wins).
/// </summary>
public sealed class FakePhoneServer(FakePhoneOptions options) : IDisposable
{
    private static readonly (int W, int H)[] SupportedSizes = [(1280, 720), (1920, 1080)];

    private readonly FakePhoneOptions _options = options;
    private readonly Dictionary<string, byte[]> _pairedPcs = [];
    private readonly string _deviceId = Guid.NewGuid().ToString();
    private readonly TcpListener _listener = new(options.Bind, options.Port);
    private int _activeClients;

    /// <summary>Raised when a PC requests pairing; the real app shows this code on screen.</summary>
    public event Action<string>? PairingCodeShown;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ActiveClients => Volatile.Read(ref _activeClients);

    public void Start() => _listener.Start();

    public async Task RunAsync(CancellationToken ct)
    {
        Start();
        Log.Info($"Fake phone listening on {_options.Bind}:{Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _listener.Dispose();

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        client.NoDelay = true;
        client.SendBufferSize = 1 << 20;
        var remote = client.Client.RemoteEndPoint;
        using var _ = client;
        var stream = client.GetStream();
        var writer = new MessageWriter(stream);

        if (Interlocked.Increment(ref _activeClients) > 1)
        {
            Log.Warn($"Rejecting second client {remote}");
            await TrySend(() => writer.WriteControlAsync(MessageType.Error, new ErrorMessage(ErrorCodes.Busy, "Another PC is connected"))).ConfigureAwait(false);
            Interlocked.Decrement(ref _activeClients);
            return;
        }

        Log.Info($"Client connected: {remote}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var connection = new Connection(this, stream, writer, cts.Token);
        try
        {
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ProtocolException or SocketException)
        {
            Log.Info($"Client {remote} gone: {ex.GetType().Name}");
        }
        finally
        {
            cts.Cancel();
            await connection.StopStreamingAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _activeClients);
        }
    }

    private static async Task TrySend(Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Connection(FakePhoneServer server, Stream stream, MessageWriter writer, CancellationToken ct)
    {
        private readonly MessageReader _reader = new(stream);
        private readonly byte[] _phoneNonce = PairingCrypto.NewNonce();
        private string? _pcId;
        private string? _code;
        private int _failedAttempts;
        private bool _authorized;
        private StartStream? _settings;
        private CancellationTokenSource? _streamCts;
        private Task? _streamTask;

        public async Task RunAsync()
        {
            // Ends when the connection's token is cancelled (in HandleClientAsync's finally).
            _ = PingLoopAsync();
            while (true)
            {
                using var message = await _reader.ReadAsync(ct).ConfigureAwait(false);
                if (!await HandleAsync(message).ConfigureAwait(false))
                    break;
            }
        }

        private async Task<bool> HandleAsync(Message message)
        {
            var type = message.Header.Type;
            if (!_authorized && type is not (MessageType.Hello or MessageType.PairRequest or MessageType.PairConfirm or MessageType.Auth or MessageType.Ping or MessageType.Pong or MessageType.Disconnect))
            {
                await writer.WriteControlAsync(MessageType.Error, new ErrorMessage(ErrorCodes.Unauthorized, "Not authorized"), ct).ConfigureAwait(false);
                return false;
            }

            switch (type)
            {
                case MessageType.Hello:
                    var hello = ProtocolJson.Deserialize<Hello>(message);
                    _pcId = hello.PcId;
                    Log.Info($"HELLO from {hello.PcName}");
                    bool paired;
                    lock (server._pairedPcs)
                        paired = server._pairedPcs.ContainsKey(_pcId);
                    await writer.WriteControlAsync(MessageType.DeviceInfo, new DeviceInfo(
                        server._deviceId,
                        server._options.DeviceName,
                        "TestSender",
                        Environment.OSVersion.VersionString,
                        "0.1.0",
                        Convert.ToBase64String(_phoneNonce),
                        paired,
                        [new CameraInfo("back", false), new CameraInfo("front", false)],
                        SupportedSizes.Select(s => new FormatInfo(s.W, s.H, [30])).ToArray(),
                        ["mjpeg"]), ct).ConfigureAwait(false);
                    break;

                case MessageType.PairRequest:
                    _code = PairingCrypto.NewCode();
                    _failedAttempts = 0;
                    Log.Info("Pairing requested; showing code");
                    server.PairingCodeShown?.Invoke(_code);
                    break;

                case MessageType.PairConfirm:
                    await OnPairConfirmAsync(ProtocolJson.Deserialize<PairConfirm>(message)).ConfigureAwait(false);
                    break;

                case MessageType.Auth:
                    await OnAuthAsync(ProtocolJson.Deserialize<Auth>(message)).ConfigureAwait(false);
                    break;

                case MessageType.StartStream:
                    await StartStreamingAsync(ProtocolJson.Deserialize<StartStream>(message)).ConfigureAwait(false);
                    break;

                case MessageType.StopStream:
                    await StopStreamingAsync().ConfigureAwait(false);
                    await SendStateAsync(StreamStates.Stopped).ConfigureAwait(false);
                    break;

                case MessageType.CameraSwitch when _settings is not null:
                    await StartStreamingAsync(_settings with { Camera = ProtocolJson.Deserialize<CameraSwitch>(message).Camera }).ConfigureAwait(false);
                    break;

                case MessageType.SetResolution when _settings is not null:
                    var res = ProtocolJson.Deserialize<SetResolution>(message);
                    await StartStreamingAsync(_settings with { Width = res.Width, Height = res.Height }).ConfigureAwait(false);
                    break;

                case MessageType.Ping:
                    await writer.WritePongAsync(ProtocolJson.ReadPing(message), MonoClock.NowUs, ct).ConfigureAwait(false);
                    break;

                case MessageType.Pong:
                    break;

                case MessageType.Disconnect:
                    Log.Info($"PC disconnected: {ProtocolJson.Deserialize<Disconnect>(message).Reason}");
                    return false;

                default:
                    await writer.WriteControlAsync(MessageType.Error, new ErrorMessage(ErrorCodes.Unsupported, $"{type} not supported by test sender"), ct).ConfigureAwait(false);
                    break;
            }
            return true;
        }

        private async Task OnPairConfirmAsync(PairConfirm confirm)
        {
            if (_code is null || _pcId is null)
            {
                await writer.WriteControlAsync(MessageType.AuthResult, new AuthResult(false, "No pairing in progress"), ct).ConfigureAwait(false);
                return;
            }

            var pcNonce = Convert.FromBase64String(confirm.PcNonce);
            var expected = PairingCrypto.Proof(_code, _phoneNonce, pcNonce, _pcId);
            if (!PairingCrypto.Matches(expected, Convert.FromBase64String(confirm.Proof)))
            {
                var tooMany = ++_failedAttempts >= 3;
                if (tooMany)
                    _code = null;
                await writer.WriteControlAsync(MessageType.AuthResult, new AuthResult(false, tooMany ? "Too many attempts; request a new code" : "Incorrect code"), ct).ConfigureAwait(false);
                return;
            }

            lock (server._pairedPcs)
                server._pairedPcs[_pcId] = PairingCrypto.DeriveKey(_code, _phoneNonce, pcNonce);
            _code = null;
            _authorized = true;
            Log.Info("Paired");
            await writer.WriteControlAsync(MessageType.AuthResult, new AuthResult(true), ct).ConfigureAwait(false);
        }

        private async Task OnAuthAsync(Auth auth)
        {
            byte[]? key = null;
            if (_pcId is not null)
            {
                lock (server._pairedPcs)
                    key = server._pairedPcs.GetValueOrDefault(_pcId);
            }
            var ok = key is not null && PairingCrypto.Matches(
                PairingCrypto.Mac(key, _phoneNonce, Convert.FromBase64String(auth.PcNonce), _pcId!),
                Convert.FromBase64String(auth.Mac));
            _authorized = ok;
            await writer.WriteControlAsync(MessageType.AuthResult, new AuthResult(ok, ok ? null : "Unknown PC"), ct).ConfigureAwait(false);
        }

        private async Task StartStreamingAsync(StartStream requested)
        {
            await StopStreamingAsync().ConfigureAwait(false);

            // Clamp to what we support, like the real app does.
            var size = SupportedSizes.Contains((requested.Width, requested.Height)) ? (requested.Width, requested.Height) : SupportedSizes[^1];
            var fps = Math.Clamp(requested.Fps, 1, 60);
            _settings = requested with { Width = size.Item1, Height = size.Item2, Fps = fps, Codec = "mjpeg" };

            var source = server._options.SourceFactory(_settings.Width, _settings.Height, fps, Math.Clamp(requested.Quality, 0.1, 1.0));
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _streamTask = Task.Run(() => StreamLoopAsync(source, _streamCts.Token));
            await SendStateAsync(StreamStates.Streaming).ConfigureAwait(false);
        }

        public async Task StopStreamingAsync()
        {
            if (_streamCts is null)
                return;
            _streamCts.Cancel();
            try
            {
                await _streamTask!.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
            catch (System.Threading.Channels.ChannelClosedException ex)
            {
                Log.Warn($"Frame source stopped: {ex.InnerException?.Message ?? ex.Message}");
            }
            _streamCts.Dispose();
            _streamCts = null;
        }

        private Task SendStateAsync(string state) =>
            writer.WriteControlAsync(MessageType.StreamState, new StreamState(
                state, null, _settings?.Camera, _settings?.Width ?? 0, _settings?.Height ?? 0, _settings?.Fps ?? 0, _settings?.Codec), ct);

        /// <summary>
        /// Sends each frame as the source captures it. The sequence number becomes frameId, so frames the
        /// source produced while we were still writing show up on the PC as gaps (latest-frame-wins).
        /// </summary>
        private async Task StreamLoopAsync(IJpegSource source, CancellationToken token)
        {
            using var _ = source;
            var header = new byte[VideoFrameHeader.Size];
            var mirrored = _settings!.Camera == "front";

            while (!token.IsCancellationRequested)
            {
                var image = await source.NextAsync(token).ConfigureAwait(false);
                new VideoFrameHeader(
                    VideoCodec.Mjpeg,
                    VideoFrameFlags.Keyframe | (mirrored ? VideoFrameFlags.Mirrored : 0),
                    0,
                    (ushort)image.Width,
                    (ushort)image.Height,
                    image.Sequence,
                    (ulong)image.CapturedUs).Write(header);
                await writer.WriteAsync(MessageType.VideoFrame, header, image.Data, ct: token).ConfigureAwait(false);
            }
        }

        private async Task PingLoopAsync()
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    await writer.WritePingAsync(MonoClock.NowUs, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }
}
