using System.Reflection;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using PhoneWebcam.Core.Timing;
using PhoneWebcam.Core.Video;

namespace PhoneWebcam.Core.Session;

public enum SessionState
{
    Handshaking,
    AwaitingPairingCode,
    Authorized,
    Streaming,
    Closed,
}

public sealed class SessionOptions
{
    public string PcName { get; init; } = Environment.MachineName;

    public StartStream Stream { get; set; } = new("back", 1920, 1080, 30, "mjpeg", 0.7);

    /// <summary>Send START_STREAM as soon as authorization succeeds.</summary>
    public bool AutoStart { get; init; } = true;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>No inbound bytes for this long = link is dead (protocol.md §6).</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// PC side of one connection to the phone app: handshake, pairing/auth, keepalive, clock sync,
/// and demultiplexing of control messages and video frames. Transport-agnostic (takes any Stream).
/// </summary>
public delegate void AudioFrameHandler(AudioFrameHeader header, ReadOnlySpan<byte> pcm);

public sealed class PhoneSession : IAsyncDisposable
{
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly Stream _stream;
    private readonly IPairingStore _pairings;
    private readonly SessionOptions _options;
    private readonly MessageReader _reader;
    private readonly MessageWriter _writer;
    private readonly HashSet<MessageType> _unknownTypesLogged = [];

    private CancellationTokenSource? _cts;
    private Exception? _failure;
    private long _lastInboundUs;
    private byte[]? _phoneNonce;
    private bool _authenticatingWithKey;
    private byte[]? _pendingKey;

    public PhoneSession(Stream stream, IPairingStore pairings, SessionOptions? options = null)
    {
        _stream = stream;
        _pairings = pairings;
        _options = options ?? new SessionOptions();
        _reader = new MessageReader(stream);
        _writer = new MessageWriter(stream);
    }

    public event Action<SessionState>? StateChanged;

    public event Action<DeviceInfo>? DeviceInfoReceived;

    /// <summary>The phone is showing a 6-digit code; call <see cref="SubmitPairingCodeAsync"/>.</summary>
    public event Action? PairingCodeRequired;

    public event Action<string>? PairingFailed;

    public event Action<StreamState>? StreamStateChanged;

    public event Action<ErrorMessage>? RemoteError;

    /// <summary>Raised on the receive loop. The handler takes ownership of the frame (must dispose).</summary>
    public event Action<EncodedFrame>? FrameReceived;

    /// <summary>Raised on the receive loop. The PCM span is only valid during the call: copy it.</summary>
    public event AudioFrameHandler? AudioReceived;

    public event Action<AudioState>? AudioStateChanged;

    public SessionState State { get; private set; } = SessionState.Handshaking;

    public DeviceInfo? Device { get; private set; }

    public StreamState? LastStreamState { get; private set; }

    public ClockSync Clock { get; } = new();

    public StreamStats Stats { get; } = new();

    /// <summary>
    /// Runs until the peer disconnects gracefully (returns) or the link fails (throws:
    /// <see cref="EndOfStreamException"/>, <see cref="TimeoutException"/>, <see cref="ProtocolException"/>, IO errors).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_cts is not null)
            throw new InvalidOperationException("A session can only be run once");
        // Owned by the session (disposed in DisposeAsync), not this method: Disconnect/Dispose may run after RunAsync returns.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        _lastInboundUs = MonoClock.NowUs;
        var keepAlive = KeepAliveLoopAsync(cts.Token);
        try
        {
            SetState(SessionState.Handshaking);
            await _writer.WriteControlAsync(MessageType.Hello, new Hello(1, _pairings.PcId, _options.PcName, AppVersion), cts.Token).ConfigureAwait(false);

            while (true)
            {
                Message message;
                try
                {
                    message = await _reader.ReadAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_failure is not null)
                {
                    throw _failure;
                }

                _lastInboundUs = MonoClock.NowUs;
                if (!await HandleAsync(message, cts.Token).ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            cts.Cancel();
            try
            {
                await keepAlive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            SetState(SessionState.Closed);
        }
    }

    public Task SubmitPairingCodeAsync(string code)
    {
        if (State != SessionState.AwaitingPairingCode || _phoneNonce is null)
            throw new InvalidOperationException("Not waiting for a pairing code");
        code = code.Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            throw new ArgumentException("Pairing code must be 6 digits", nameof(code));

        var pcNonce = PairingCrypto.NewNonce();
        _pendingKey = PairingCrypto.DeriveKey(code, _phoneNonce, pcNonce);
        _authenticatingWithKey = false;
        var proof = PairingCrypto.Proof(code, _phoneNonce, pcNonce, _pairings.PcId);
        return _writer.WriteControlAsync(MessageType.PairConfirm, new PairConfirm(Convert.ToBase64String(pcNonce), Convert.ToBase64String(proof)), Token);
    }

    public Task StartStreamAsync(StartStream? settings = null)
    {
        if (settings is not null)
            _options.Stream = settings;
        Log.Info($"Requesting stream {_options.Stream.Width}x{_options.Stream.Height}@{_options.Stream.Fps} {_options.Stream.Camera}");
        return _writer.WriteControlAsync(MessageType.StartStream, _options.Stream, Token);
    }

    public Task StopStreamAsync() => _writer.WriteControlAsync(MessageType.StopStream, new Empty(), Token);

    public Task StartAudioAsync(int sampleRate = 48000, int channels = 1) =>
        _writer.WriteControlAsync(MessageType.StartAudio, new StartAudio(sampleRate, channels), Token);

    public Task StopAudioAsync() => _writer.WriteControlAsync(MessageType.StopAudio, new Empty(), Token);

    public Task SendControlAsync<T>(MessageType type, T message) => _writer.WriteControlAsync(type, message, Token);

    public async Task DisconnectAsync(string reason = "user")
    {
        try
        {
            await _writer.WriteControlAsync(MessageType.Disconnect, new Disconnect(reason), Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        _cts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    private async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var handedOff = false;
        try
        {
            switch (message.Header.Type)
            {
                case MessageType.DeviceInfo:
                    await OnDeviceInfoAsync(ProtocolJson.Deserialize<DeviceInfo>(message), ct).ConfigureAwait(false);
                    break;

                case MessageType.AuthResult:
                    await OnAuthResultAsync(ProtocolJson.Deserialize<AuthResult>(message), ct).ConfigureAwait(false);
                    break;

                case MessageType.StreamState:
                    var state = ProtocolJson.Deserialize<StreamState>(message);
                    LastStreamState = state;
                    Log.Info($"Stream {state.State}: {state.Camera} {state.Width}x{state.Height}@{state.Fps} {state.Codec}{(state.Reason is null ? "" : $" ({state.Reason})")}");
                    SetState(state.State == StreamStates.Streaming ? SessionState.Streaming : SessionState.Authorized);
                    StreamStateChanged?.Invoke(state);
                    break;

                case MessageType.VideoFrame:
                    handedOff = OnVideoFrame(message);
                    break;

                case MessageType.AudioFrame:
                    var payload = message.Payload.Span;
                    var audioHeader = AudioFrameHeader.Parse(payload);
                    AudioReceived?.Invoke(audioHeader, payload[AudioFrameHeader.Size..]);
                    break;

                case MessageType.AudioState:
                    var audioState = ProtocolJson.Deserialize<AudioState>(message);
                    Log.Info($"Phone microphone {audioState.State}{(audioState.Reason is null ? "" : $" ({audioState.Reason})")}");
                    AudioStateChanged?.Invoke(audioState);
                    break;

                case MessageType.Ping:
                    await _writer.WritePongAsync(ProtocolJson.ReadPing(message), MonoClock.NowUs, ct).ConfigureAwait(false);
                    break;

                case MessageType.Pong:
                    var (t0, t1) = ProtocolJson.ReadPong(message);
                    Clock.AddSample(t0, t1, MonoClock.NowUs);
                    Stats.RttMs = Clock.RttUs / 1000.0;
                    break;

                case MessageType.Error:
                    var error = ProtocolJson.Deserialize<ErrorMessage>(message);
                    Log.Error($"Phone reported error {error.Code}: {error.Message}");
                    RemoteError?.Invoke(error);
                    break;

                case MessageType.Disconnect:
                    Log.Info($"Phone disconnected: {ProtocolJson.Deserialize<Disconnect>(message).Reason}");
                    return false;

                default:
                    if (_unknownTypesLogged.Add(message.Header.Type))
                        Log.Warn($"Ignoring unsupported message type 0x{(byte)message.Header.Type:X2}");
                    break;
            }
            return true;
        }
        finally
        {
            if (!handedOff)
                message.Dispose();
        }
    }

    private async Task OnDeviceInfoAsync(DeviceInfo info, CancellationToken ct)
    {
        Device = info;
        _phoneNonce = Convert.FromBase64String(info.Nonce);
        Log.Info($"Connected to {info.DeviceName} ({info.Model}, {info.OsVersion}, app {info.AppVersion})");
        DeviceInfoReceived?.Invoke(info);

        var key = _pairings.GetKey(info.DeviceId);
        if (info.Paired && key is not null)
        {
            var pcNonce = PairingCrypto.NewNonce();
            var mac = PairingCrypto.Mac(key, _phoneNonce, pcNonce, _pairings.PcId);
            _authenticatingWithKey = true;
            await _writer.WriteControlAsync(MessageType.Auth, new Auth(Convert.ToBase64String(pcNonce), Convert.ToBase64String(mac)), ct).ConfigureAwait(false);
            return;
        }

        if (key is not null)
        {
            Log.Info("Phone no longer recognises this PC; re-pairing");
            _pairings.RemoveKey(info.DeviceId);
        }
        await RequestPairingAsync(ct).ConfigureAwait(false);
    }

    private async Task OnAuthResultAsync(AuthResult result, CancellationToken ct)
    {
        if (result.Ok)
        {
            if (_pendingKey is not null && Device is not null)
            {
                _pairings.SaveKey(Device.DeviceId, _pendingKey);
                Log.Info("Paired with phone");
            }
            _pendingKey = null;
            Log.Info("Authorized");
            SetState(SessionState.Authorized);
            if (_options.AutoStart)
                await StartStreamAsync().ConfigureAwait(false);
            return;
        }

        _pendingKey = null;
        if (_authenticatingWithKey && Device is not null)
        {
            Log.Warn($"Stored pairing rejected ({result.Reason}); re-pairing");
            _pairings.RemoveKey(Device.DeviceId);
            await RequestPairingAsync(ct).ConfigureAwait(false);
            return;
        }

        Log.Warn($"Pairing failed: {result.Reason}");
        PairingFailed?.Invoke(result.Reason ?? "Incorrect code");
    }

    private async Task RequestPairingAsync(CancellationToken ct)
    {
        _authenticatingWithKey = false;
        await _writer.WriteControlAsync(MessageType.PairRequest, new Empty(), ct).ConfigureAwait(false);
        SetState(SessionState.AwaitingPairingCode);
        PairingCodeRequired?.Invoke();
    }

    private bool OnVideoFrame(Message message)
    {
        var header = VideoFrameHeader.Parse(message.Payload.Span);
        var now = MonoClock.NowUs;
        long? latency = Clock.ToLocal((long)header.PtsUs) is { } captured ? now - captured : null;
        Stats.OnFrameReceived(header, (int)message.Header.Length, latency);

        var handler = FrameReceived;
        if (handler is null)
            return false;
        handler(new EncodedFrame(header, message, now));
        return true;
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        var timeoutUs = (long)_options.InactivityTimeout.TotalMicroseconds;
        try
        {
            while (true)
            {
                if (MonoClock.NowUs - _lastInboundUs > timeoutUs)
                {
                    _failure = new TimeoutException($"No data from phone for {_options.InactivityTimeout.TotalSeconds:0} s");
                    _cts?.Cancel();
                    return;
                }
                await _writer.WritePingAsync(MonoClock.NowUs, ct).ConfigureAwait(false);
                await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _failure = ex;
            _cts?.Cancel();
        }
    }

    private void SetState(SessionState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
