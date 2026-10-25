using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneWebcam.Core.Diagnostics;
using IPhoneWebcam.Core.Protocol;
using IPhoneWebcam.Core.Security;
using IPhoneWebcam.Core.Session;
using IPhoneWebcam.Core.Transport;
using IPhoneWebcam.Core.Video;

namespace IPhoneWebcam.App.ViewModels;

public enum Tally
{
    Idle,
    Busy,
    Live,
}

/// <summary>
/// Phase 3 receiver: connect to an iPhone (or TestSender) by address, pair, show the live feed and stats,
/// and reconnect automatically. Device discovery and the virtual camera arrive in later phases.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly IPairingStore _pairings = new DpapiPairingStore();
    private readonly ITransport _transport = new WifiTransport();
    private readonly FramePipeline _pipeline = new(new MjpegDecoder());
    private readonly DispatcherTimer _statsTimer;

    private CancellationTokenSource? _connectCts;
    private PhoneSession? _session;
    private VideoFrame? _latestFrame;
    private int _renderQueued;
    private int _statsTicks;

    private string _host = "127.0.0.1";
    private string _port = DeviceEndpoint.DefaultPort.ToString();
    private Tally _tally = Tally.Idle;
    private string _status = "Not connected";
    private string _statusDetail = "Enter the iPhone's address, then connect.";
    private string _deviceName = "No iPhone";
    private string _connectionText = "—";
    private string _resolutionText = "—";
    private string _fpsText = "—";
    private string _latencyText = "—";
    private string _bitrateText = "—";
    private string _dropsText = "—";
    private bool _isActive;
    private bool _showPairing;
    private string _pairingCode = "";
    private string? _pairingError;
    private string _lastLog = "";
    private WriteableBitmap? _preview;

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(ToggleConnection);
        SubmitPairingCommand = new RelayCommand(SubmitPairing, () => _pairingCode.Trim().Length == 6);
        _pipeline.FrameDecoded += OnFrameDecoded;
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStats(), _dispatcher);
        Log.Written += entry => Ui(() => LastLog = entry.ToString());
    }

    public ICommand ConnectCommand { get; }

    public ICommand SubmitPairingCommand { get; }

    public string Host { get => _host; set => Set(ref _host, value); }

    public string Port { get => _port; set => Set(ref _port, value); }

    public Tally Tally { get => _tally; private set => Set(ref _tally, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }

    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }

    public string ResolutionText { get => _resolutionText; private set => Set(ref _resolutionText, value); }

    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }

    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }

    public string BitrateText { get => _bitrateText; private set => Set(ref _bitrateText, value); }

    public string DropsText { get => _dropsText; private set => Set(ref _dropsText, value); }

    /// <summary>Connecting, connected or retrying — i.e. the button means "Disconnect".</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (Set(ref _isActive, value))
                Raise(nameof(ConnectButtonText));
        }
    }

    public string ConnectButtonText => IsActive ? "Disconnect" : "Connect";

    public bool ShowPairing { get => _showPairing; private set => Set(ref _showPairing, value); }

    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            // RequerySuggested only fires on keyboard/mouse input; paste or automation wouldn't re-enable Pair.
            if (Set(ref _pairingCode, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? PairingError { get => _pairingError; private set => Set(ref _pairingError, value); }

    public string LastLog { get => _lastLog; private set => Set(ref _lastLog, value); }

    public WriteableBitmap? Preview
    {
        get => _preview;
        private set
        {
            if (Set(ref _preview, value))
                Raise(nameof(HasPreview));
        }
    }

    public bool HasPreview => _preview is not null;

    public void Dispose()
    {
        // Tell the iPhone this was deliberate so it doesn't treat it as a dropped link. Bounded so closing never hangs.
        _session?.DisconnectAsync("app_closed").Wait(TimeSpan.FromMilliseconds(300));
        _connectCts?.Cancel();
        _pipeline.Dispose();
    }

    private void ToggleConnection()
    {
        if (IsActive)
        {
            _ = _session?.DisconnectAsync();
            _connectCts?.Cancel();
            return;
        }

        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            SetStatus(Tally.Idle, "Check the port", "Port must be a number between 1 and 65535.");
            return;
        }

        var endpoint = new DeviceEndpoint(TransportKind.Wifi, Host.Trim(), Host.Trim(), port);
        _connectCts = new CancellationTokenSource();
        IsActive = true;
        _statsTimer.Start();
        _ = RunConnectionAsync(endpoint, _connectCts.Token);
    }

    private async Task RunConnectionAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(Tally.Busy, attempt == 0 ? "Connecting" : "Reconnecting", endpoint.ToString());
                var stream = await _transport.ConnectAsync(endpoint, ct);
                attempt = 0;
                await using var session = new PhoneSession(stream, _pairings);
                AttachSession(session, endpoint);
                await session.RunAsync(ct);

                // Peer closed gracefully (user stopped the iPhone app): don't hammer it with retries.
                SetStatus(Tally.Idle, "iPhone disconnected", "Connect again when the iPhone app is ready.");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Connection to {endpoint} failed: {ex.GetType().Name}: {ex.Message}");
                SetStatus(Tally.Busy, "Waiting for iPhone", Explain(ex, endpoint));
            }
            finally
            {
                DetachSession();
            }

            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)], ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Ui(() =>
        {
            IsActive = false;
            _statsTimer.Stop();
            if (Tally != Tally.Idle)
                SetStatus(Tally.Idle, "Not connected", "Enter the iPhone's address, then connect.");
        });
    }

    private void AttachSession(PhoneSession session, DeviceEndpoint endpoint)
    {
        _session = session;
        session.FrameReceived += _pipeline.Submit;
        session.DeviceInfoReceived += info => Ui(() =>
        {
            DeviceName = info.DeviceName;
            ConnectionText = $"Wi-Fi · {endpoint.Host}";
        });
        session.PairingCodeRequired += () => Ui(() =>
        {
            PairingCode = "";
            PairingError = null;
            ShowPairing = true;
            SetStatus(Tally.Busy, "Pair this PC", "Type the 6-digit code shown on the iPhone.");
        });
        session.PairingFailed += reason => Ui(() => PairingError = reason);
        session.StateChanged += state => Ui(() =>
        {
            switch (state)
            {
                case SessionState.Authorized:
                    ShowPairing = false;
                    SetStatus(Tally.Busy, "Connected", "Starting the camera on the iPhone.");
                    break;
                case SessionState.Streaming:
                    ShowPairing = false;
                    SetStatus(Tally.Live, "Live", $"Receiving from {DeviceName}.");
                    break;
            }
        });
        session.StreamStateChanged += state => Ui(() =>
        {
            if (state.State == StreamStates.Paused)
                SetStatus(Tally.Busy, "Paused on iPhone", state.Reason ?? "Bring the iPhone app to the foreground to resume.");
        });
    }

    private void DetachSession()
    {
        _session = null;
        Ui(() =>
        {
            ShowPairing = false;
            Preview = null;
            DeviceName = "No iPhone";
            ConnectionText = ResolutionText = FpsText = LatencyText = BitrateText = DropsText = "—";
        });
    }

    private void SubmitPairing()
    {
        var session = _session;
        if (session is null)
            return;
        PairingError = null;
        try
        {
            _ = session.SubmitPairingCodeAsync(PairingCode);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            PairingError = ex.Message;
        }
    }

    private static string Explain(Exception ex, DeviceEndpoint endpoint) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"Nothing is accepting connections at {endpoint.Host}:{endpoint.Port}. Open iPhone Webcam on the iPhone and keep it in the foreground.",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            $"{endpoint.Host} can't be reached. Check the PC and iPhone are on the same network.",
        OperationCanceledException or TimeoutException =>
            "The iPhone stopped responding. Unlock it and keep iPhone Webcam open.",
        InvalidOperationException => ex.Message,
        EndOfStreamException or IOException => "The connection dropped. Retrying.",
        ProtocolException => "The iPhone app sent data this version doesn't understand. Update both apps.",
        _ => "Connection failed. Retrying.",
    };

    // ---- Preview: latest-frame-wins hand-off from the decode thread to the UI thread ----

    private void OnFrameDecoded(VideoFrame frame)
    {
        _session?.Stats.OnFrameDecoded(frame.DecodeUs);
        Interlocked.Exchange(ref _latestFrame, frame)?.Dispose();
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            _dispatcher.BeginInvoke(RenderLatest, DispatcherPriority.Render);
    }

    private unsafe void RenderLatest()
    {
        Volatile.Write(ref _renderQueued, 0);
        using var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is null || _session is null)
            return;

        var bitmap = _preview;
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);

        fixed (byte* pixels = frame.Pixels)
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), (nint)pixels, frame.ByteLength, frame.Stride);
        Preview = bitmap;
    }

    private void RefreshStats()
    {
        var session = _session;
        if (session is null)
            return;
        session.Stats.DroppedOnPc = _pipeline.DroppedFrames;
        var s = session.Stats.Sample();
        if (s.Width == 0)
            return;
        ResolutionText = $"{s.Width} × {s.Height}";
        FpsText = $"{s.DecodeFps:0.0} fps";
        LatencyText = s.LatencyMs is { } latency ? $"{latency + (s.DecodeMs ?? 0):0} ms" : "measuring";
        BitrateText = $"{s.BitrateMbps:0.0} Mbps";
        DropsText = $"{s.DroppedOnPhone + s.DroppedOnPc}";

        if (++_statsTicks % 10 == 0)
            Log.Info($"Stats: {s.Width}x{s.Height} {s.DecodeFps:0.0} fps, {s.BitrateMbps:0.0} Mbps, latency {s.LatencyMs:0.0} ms, decode {s.DecodeMs:0.0} ms, dropped phone/pc {s.DroppedOnPhone}/{s.DroppedOnPc}");
    }

    private void SetStatus(Tally tally, string status, string detail) => Ui(() =>
    {
        Tally = tally;
        Status = status;
        StatusDetail = detail;
    });

    private void Ui(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
