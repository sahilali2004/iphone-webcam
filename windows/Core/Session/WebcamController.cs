using System.Net.Sockets;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using PhoneWebcam.Core.Transport;
using PhoneWebcam.Core.Video;

namespace PhoneWebcam.Core.Session;

public enum WebcamStatus
{
    /// <summary>No target device chosen / found.</summary>
    NoDevice,
    Connecting,
    /// <summary>Connection failed; retrying automatically.</summary>
    Retrying,
    Pairing,
    /// <summary>Connected and authorized; webcam not started.</summary>
    Connected,
    Live,
    /// <summary>Webcam on, but the phone paused (app in background, camera busy).</summary>
    Paused,
}

/// <summary>
/// The connection lifecycle behind the dashboard: keeps a session to the chosen phone (reconnecting with
/// backoff), handles pairing, and starts/stops the stream. "Webcam on" is remembered across reconnects,
/// so a dropped cable or Wi-Fi blip resumes streaming by itself. Events are raised on background threads.
/// </summary>
public sealed class WebcamController : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly IPairingStore _pairings;
    private readonly Func<TransportKind, ITransport> _transports;
    private readonly object _gate = new();
    private DeviceEndpoint? _target;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private PhoneSession? _session;
    private StartStream _settings = new("back", 1920, 1080, 30, "mjpeg", 0.7);
    private bool _webcamOn;

    public WebcamController(IPairingStore pairings, Func<TransportKind, ITransport> transports)
    {
        _pairings = pairings;
        _transports = transports;
    }

    public event Action<WebcamStatus, string>? StatusChanged;

    public event Action<DeviceInfo>? DeviceConnected;

    public event Action? PairingCodeRequired;

    public event Action<string>? PairingFailed;

    /// <summary>Raised on the receive thread; the handler owns the frame and must dispose it.</summary>
    public event Action<EncodedFrame>? FrameReceived;

    public WebcamStatus Status { get; private set; } = WebcamStatus.NoDevice;

    public DeviceEndpoint? Target
    {
        get
        {
            lock (_gate) return _target;
        }
    }

    public PhoneSession? Session => Volatile.Read(ref _session);

    public bool IsWebcamOn
    {
        get
        {
            lock (_gate) return _webcamOn;
        }
    }

    public StartStream Settings
    {
        get
        {
            lock (_gate) return _settings;
        }
    }

    /// <summary>Connects to <paramref name="endpoint"/> (dropping any current connection), or disconnects if null.</summary>
    public async Task SetTargetAsync(DeviceEndpoint? endpoint)
    {
        CancellationTokenSource? oldCts;
        Task? oldLoop;
        lock (_gate)
        {
            if (Equals(_target, endpoint)) return;
            _target = endpoint;
            oldCts = _loopCts;
            oldLoop = _loop;
            _loopCts = null;
            _loop = null;
        }

        if (oldCts is not null)
        {
            var session = Session;
            if (session is not null) await session.DisconnectAsync("switching device").ConfigureAwait(false);
            oldCts.Cancel();
            try
            {
                if (oldLoop is not null) await oldLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            oldCts.Dispose();
        }

        if (endpoint is null)
        {
            SetStatus(WebcamStatus.NoDevice, "Connect a phone by USB or Wi-Fi.");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (!Equals(_target, endpoint))
            {
                cts.Dispose();
                return;
            }
            _loopCts = cts;
            _loop = Task.Run(() => RunAsync(endpoint, cts.Token));
        }
    }

    public Task StartWebcamAsync()
    {
        lock (_gate) _webcamOn = true;
        Log.Info("Webcam on");
        return Session is { State: SessionState.Authorized or SessionState.Streaming } s ? s.StartStreamAsync(Settings) : Task.CompletedTask;
    }

    public Task StopWebcamAsync()
    {
        lock (_gate) _webcamOn = false;
        Log.Info("Webcam off");
        return Session is { State: SessionState.Streaming } s ? s.StopStreamAsync() : Task.CompletedTask;
    }

    /// <summary>Camera / resolution / quality. Applied immediately if streaming.</summary>
    public Task ApplySettingsAsync(StartStream settings)
    {
        bool on;
        lock (_gate)
        {
            _settings = settings;
            on = _webcamOn;
        }
        return on && Session is { State: SessionState.Streaming or SessionState.Authorized } s ? s.StartStreamAsync(settings) : Task.CompletedTask;
    }

    public Task SubmitPairingCodeAsync(string code) =>
        Session?.SubmitPairingCodeAsync(code) ?? throw new InvalidOperationException("Not connected");

    public async ValueTask DisposeAsync() => await SetTargetAsync(null).ConfigureAwait(false);

    private async Task RunAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(attempt == 0 ? WebcamStatus.Connecting : WebcamStatus.Retrying, $"Connecting to {endpoint.DisplayName}…");
            try
            {
                var stream = await _transports(endpoint.Kind).ConnectAsync(endpoint, ct).ConfigureAwait(false);
                await using var session = new PhoneSession(stream, _pairings, new SessionOptions { AutoStart = false, Stream = Settings });
                Attach(session, endpoint);
                Volatile.Write(ref _session, session);
                try
                {
                    await session.RunAsync(ct).ConfigureAwait(false);
                    attempt = 0; // graceful close: the phone stopped its camera; wait and try again
                    SetStatus(WebcamStatus.Retrying, $"{endpoint.DisplayName} closed the connection. Waiting for it to start again.");
                }
                finally
                {
                    Volatile.Write(ref _session, null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug($"Connection to {endpoint} failed: {ex.GetType().Name}: {ex.Message}");
                SetStatus(WebcamStatus.Retrying, Explain(ex, endpoint));
            }

            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Attach(PhoneSession session, DeviceEndpoint endpoint)
    {
        var via = endpoint.Kind == TransportKind.Usb ? "USB" : "Wi-Fi";
        session.FrameReceived += frame =>
        {
            var handler = FrameReceived;
            if (handler is null) frame.Dispose();
            else handler(frame);
        };
        session.DeviceInfoReceived += info => DeviceConnected?.Invoke(info);
        session.PairingCodeRequired += () =>
        {
            SetStatus(WebcamStatus.Pairing, "Type the 6-digit code shown on the phone.");
            PairingCodeRequired?.Invoke();
        };
        session.PairingFailed += reason => PairingFailed?.Invoke(reason);
        session.StateChanged += state =>
        {
            if (state == SessionState.Authorized && session.LastStreamState is null)
            {
                SetStatus(WebcamStatus.Connected, $"Connected over {via}.");
                if (IsWebcamOn) _ = SafeAsync(() => session.StartStreamAsync(Settings));
            }
        };
        session.StreamStateChanged += state =>
        {
            switch (state.State)
            {
                case StreamStates.Streaming:
                    SetStatus(WebcamStatus.Live, $"Streaming over {via}.");
                    break;
                case StreamStates.Paused:
                    SetStatus(WebcamStatus.Paused, state.Reason ?? "Paused on the phone.");
                    break;
                default:
                    SetStatus(WebcamStatus.Connected, $"Connected over {via}.");
                    break;
            }
        };
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void SetStatus(WebcamStatus status, string detail)
    {
        Status = status;
        StatusChanged?.Invoke(status, detail);
    }

    public static string Explain(Exception ex, DeviceEndpoint endpoint) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } or EndOfStreamException when endpoint.Kind == TransportKind.Usb =>
            $"Open Phone Webcam on {endpoint.DisplayName} and tap Start camera.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"Nothing is accepting connections at {endpoint.Host}:{endpoint.Port}. Open Phone Webcam on the phone and tap Start camera.",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            $"{endpoint.Host} can't be reached. Check the PC and phone are on the same network.",
        OperationCanceledException or TimeoutException =>
            "The phone stopped responding. Keep Phone Webcam open on the phone.",
        EndOfStreamException or IOException => "The connection dropped. Reconnecting.",
        ProtocolException => "The phone app sent data this version doesn't understand. Update both apps.",
        InvalidOperationException => ex.Message,
        _ => "Connection failed. Retrying.",
    };
}
