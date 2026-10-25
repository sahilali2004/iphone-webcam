using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneWebcam.Core.Audio;
using PhoneWebcam.Core.Devices;
using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using PhoneWebcam.Core.Session;
using PhoneWebcam.Core.Transport;
using PhoneWebcam.Core.Video;
using PhoneWebcam.Core.VirtualCamera;

namespace PhoneWebcam.App.ViewModels;

public enum Tally
{
    Idle,
    Busy,
    Live,
}

/// <summary>An entry in the phone picker.</summary>
public sealed record DeviceOption(string Key, string Label);

/// <summary>
/// Dashboard: finds phones (USB via adb, Wi-Fi via discovery), keeps a connection to the chosen one, and
/// runs the webcam: phone → decode → preview + virtual camera ("Phone Webcam" in other apps).
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IPairingStore _pairings = new DpapiPairingStore();
    private readonly AdbClient? _adb;
    private readonly DeviceManager _devices;
    private readonly WebcamController _controller;
    private readonly FramePipeline _pipeline = new(new MjpegDecoder());
    private readonly FramePublisher _publisher = new();
    private readonly VirtualCameraHost _virtualCamera = new();
    private readonly AudioOutput _audio = new();
    private AudioDevice? _selectedAudioDevice;
    private bool _microphoneOn;
    private bool _microphoneStreaming;
    private string _microphoneText = "";
    private readonly DispatcherTimer _statsTimer;

    private VideoFrame? _latestFrame;
    private int _renderQueued;
    private int _statsTicks;
    private bool _disposed;

    private Tally _tally = Tally.Idle;
    private string _status = "Looking for phones";
    private string _statusDetail = "Connect a phone by USB, or open Phone Webcam on a phone on this Wi-Fi.";
    private string _deviceName = "No phone";
    private DeviceOption? _selectedDevice;
    private string _manualHost;
    private string _manualPort;
    private bool _isFront;
    private bool _is720;
    private bool _webcamOn;
    private bool _canStart;
    private string _connectionText = "—";
    private string _resolutionText = "—";
    private string _fpsText = "—";
    private string _latencyText = "—";
    private string _bitrateText = "—";
    private string _dropsText = "—";
    private bool _showPairing;
    private string _pairingCode = "";
    private string? _pairingError;
    private string _lastLog = "";
    private string _virtualCameraText = "Starting the camera for other apps…";
    private WriteableBitmap? _preview;

    public MainViewModel()
    {
        _manualHost = _settings.ManualHost;
        _manualPort = _settings.ManualPort.ToString();
        _isFront = _settings.Camera == "front";
        _is720 = _settings.Height == 720;

        var adbPath = AdbClient.Locate();
        _adb = adbPath is null ? null : new AdbClient(adbPath);
        if (_adb is null) Log.Warn("adb not found: Android USB connections are unavailable (Wi-Fi still works)");
        _devices = new DeviceManager(_adb);
        _controller = new WebcamController(_pairings, kind => kind == TransportKind.Usb && _adb is not null ? new AdbTransport(_adb) : new WifiTransport());

        ToggleWebcamCommand = new RelayCommand(ToggleWebcam, () => _canStart || _webcamOn);
        SubmitPairingCommand = new RelayCommand(SubmitPairing, () => _pairingCode.Trim().Length == 6);
        ApplyManualCommand = new RelayCommand(() => _ = RetargetAsync());

        DeviceOptions.Add(new DeviceOption(AppSettings.AutomaticDevice, "Automatic (USB first)"));
        DeviceOptions.Add(new DeviceOption(AppSettings.ManualDevice, "Enter an address…"));
        _selectedDevice = DeviceOptions.FirstOrDefault(o => o.Key == _settings.DeviceKey) ?? DeviceOptions[0];
        if (_settings.DeviceKey.StartsWith("usb:") || _settings.DeviceKey.StartsWith("wifi:"))
        {
            // Remembered device: keep it selectable even before it's found again.
            _selectedDevice = new DeviceOption(_settings.DeviceKey, "Last used phone");
            DeviceOptions.Insert(1, _selectedDevice);
        }

        _controller.StatusChanged += (status, detail) => Ui(() => OnControllerStatus(status, detail));
        _controller.DeviceConnected += info => Ui(() => DeviceName = info.DeviceName);
        _controller.PairingCodeRequired += () => Ui(() =>
        {
            PairingCode = "";
            PairingError = null;
            ShowPairing = true;
        });
        _controller.PairingFailed += reason => Ui(() => PairingError = reason);
        _controller.FrameReceived += _pipeline.Submit;
        _controller.AudioReceived += (_, pcm) => _audio.Write(pcm);
        _controller.AudioStateChanged += state => Ui(() => OnAudioState(state));
        _pipeline.FrameDecoded += OnFrameDecoded;
        _devices.DevicesChanged += list => Ui(() => OnDevicesChanged(list));

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStats(), _dispatcher);
        _statsTimer.Start();
        Log.Written += entry => Ui(() => LastLog = entry.ToString());

        LoadAudioDevices();
        _microphoneOn = _settings.MicrophoneOn;
        if (_microphoneOn) _ = Guard(_controller.SetMicrophoneAsync(true));
        UpdateMicrophoneText();

        _ = StartVirtualCameraAsync();
        _devices.Start();
        _ = RetargetAsync();
    }

    public ObservableCollection<DeviceOption> DeviceOptions { get; } = [];

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];

    /// <summary>"Use phone microphone".</summary>
    public bool MicrophoneOn
    {
        get => _microphoneOn;
        set
        {
            if (!Set(ref _microphoneOn, value)) return;
            _settings.MicrophoneOn = value;
            _settings.Save();
            if (!value) _audio.Stop();
            _ = Guard(_controller.SetMicrophoneAsync(value));
            UpdateMicrophoneText();
        }
    }

    public AudioDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (value is null || !Set(ref _selectedAudioDevice, value)) return;
            _settings.AudioDeviceId = value.Id;
            _settings.Save();
            if (_microphoneStreaming) StartAudioOutput();
            UpdateMicrophoneText();
        }
    }

    public string MicrophoneText { get => _microphoneText; private set => Set(ref _microphoneText, value); }

    public ICommand ToggleWebcamCommand { get; }

    public ICommand SubmitPairingCommand { get; }

    public ICommand ApplyManualCommand { get; }

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || !Set(ref _selectedDevice, value)) return;
            Raise(nameof(IsManual));
            _settings.DeviceKey = value.Key;
            _settings.Save();
            _ = RetargetAsync();
        }
    }

    public bool IsManual => _selectedDevice?.Key == AppSettings.ManualDevice;

    public string ManualHost { get => _manualHost; set => Set(ref _manualHost, value); }

    public string ManualPort { get => _manualPort; set => Set(ref _manualPort, value); }

    public bool IsFront
    {
        get => _isFront;
        set
        {
            if (Set(ref _isFront, value)) ApplySettings();
        }
    }

    public bool IsRear
    {
        get => !_isFront;
        set => IsFront = !value;
    }

    public bool Is720
    {
        get => _is720;
        set
        {
            if (Set(ref _is720, value)) ApplySettings();
        }
    }

    public bool Is1080
    {
        get => !_is720;
        set => Is720 = !value;
    }

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

    public string WebcamButtonText => _webcamOn ? "Stop Webcam" : "Start Webcam";

    public bool ShowPairing { get => _showPairing; private set => Set(ref _showPairing, value); }

    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            // RequerySuggested only fires on keyboard/mouse input; paste or automation wouldn't re-enable Pair.
            if (Set(ref _pairingCode, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? PairingError { get => _pairingError; private set => Set(ref _pairingError, value); }

    public string LastLog { get => _lastLog; private set => Set(ref _lastLog, value); }

    /// <summary>Whether other apps can select "Phone Webcam", in plain words.</summary>
    public string VirtualCameraText { get => _virtualCameraText; private set => Set(ref _virtualCameraText, value); }

    public WriteableBitmap? Preview
    {
        get => _preview;
        private set
        {
            if (Set(ref _preview, value)) Raise(nameof(HasPreview));
        }
    }

    public bool HasPreview => _preview is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statsTimer.Stop();
        // Tell the phone this was deliberate. Bounded so closing never hangs.
        _controller.Session?.DisconnectAsync("app_closed").Wait(TimeSpan.FromMilliseconds(300));
        _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _devices.Dispose();
        _pipeline.Dispose();
        _publisher.Dispose();
        _virtualCamera.Dispose();
        _audio.Dispose();
    }

    // ---- Microphone ----

    private void LoadAudioDevices()
    {
        try
        {
            foreach (var device in AudioOutput.ListDevices()) AudioDevices.Add(device);
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio devices unavailable: {ex.Message}");
        }
        // Saved choice, else a working virtual cable, else any cable, else the first device.
        _selectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == _settings.AudioDeviceId)
                               ?? AudioDevices.FirstOrDefault(d => d.IsVirtualCable && d.Warning is null)
                               ?? AudioDevices.FirstOrDefault(d => d.IsVirtualCable)
                               ?? AudioDevices.FirstOrDefault();
    }

    private void OnAudioState(AudioState state)
    {
        _microphoneStreaming = state.State == AudioStates.Streaming;
        if (_microphoneStreaming) StartAudioOutput();
        else _audio.Stop();
        UpdateMicrophoneText(state.State == AudioStates.Denied ? state.Reason ?? "The phone didn't allow microphone access." : null);
    }

    private void StartAudioOutput()
    {
        if (_selectedAudioDevice is null) return;
        try
        {
            _audio.Start(_selectedAudioDevice.Id, 48000, 1);
        }
        catch (Exception ex)
        {
            Log.Error($"Audio output failed: {ex.Message}");
            UpdateMicrophoneText($"Couldn't play to {_selectedAudioDevice.Name}: {ex.Message}");
        }
    }

    private void UpdateMicrophoneText(string? problem = null)
    {
        var device = _selectedAudioDevice;
        MicrophoneText = problem
            ?? (!_microphoneOn ? "Send the phone's microphone to this PC, for use in calls."
                : device is null ? "No playback devices found."
                : device.Warning is not null ? device.Warning
                : device.IsVirtualCable ? $"In your call app, choose the microphone “{device.MicrophoneName}”."
                : $"Playing on {device.Name}. To use the phone as a microphone in other apps, install the free VB-CABLE (vb-audio.com/Cable) and choose it here.");
    }

    // ---- Device selection ----

    private void OnDevicesChanged(IReadOnlyList<AvailableDevice> devices)
    {
        // Keep "Automatic" first and "Enter an address…" last; rebuild the device entries in between.
        var selectedKey = _selectedDevice?.Key;
        for (var i = DeviceOptions.Count - 1; i >= 0; i--)
        {
            var key = DeviceOptions[i].Key;
            if (key != AppSettings.AutomaticDevice && key != AppSettings.ManualDevice && key != selectedKey) DeviceOptions.RemoveAt(i);
        }
        foreach (var device in devices)
        {
            var option = new DeviceOption(device.Key, device.Label);
            var existing = DeviceOptions.FirstOrDefault(o => o.Key == device.Key);
            if (existing is null)
            {
                DeviceOptions.Insert(DeviceOptions.Count - 1, option);
            }
            else if (existing.Label != option.Label)
            {
                // Selected entry found again: refresh its label without changing the selection.
                var index = DeviceOptions.IndexOf(existing);
                DeviceOptions[index] = option;
                if (existing == _selectedDevice) Set(ref _selectedDevice, option, nameof(SelectedDevice));
            }
        }
        _ = RetargetAsync();
    }

    private DeviceEndpoint? ResolveTarget(out string? problem)
    {
        problem = null;
        var devices = _devices.Devices;
        var key = _selectedDevice?.Key ?? AppSettings.AutomaticDevice;

        if (key == AppSettings.ManualDevice)
        {
            if (string.IsNullOrWhiteSpace(ManualHost)) return null;
            if (!int.TryParse(ManualPort, out var port) || port is < 1 or > 65535)
            {
                problem = "Port must be a number between 1 and 65535.";
                return null;
            }
            _settings.ManualHost = ManualHost.Trim();
            _settings.ManualPort = port;
            _settings.Save();
            return new DeviceEndpoint(TransportKind.Wifi, ManualHost.Trim(), ManualHost.Trim(), port);
        }

        if (key == AppSettings.AutomaticDevice)
        {
            var best = devices.FirstOrDefault(d => d.IsUsable && d.Endpoint.Kind == TransportKind.Usb)
                       ?? devices.FirstOrDefault(d => d.IsUsable);
            problem = best is null ? devices.FirstOrDefault()?.Problem : null;
            return best?.Endpoint;
        }

        var chosen = devices.FirstOrDefault(d => d.Key == key);
        problem = chosen?.Problem ?? (chosen is null ? "That phone isn't connected right now." : null);
        return chosen is { IsUsable: true } ? chosen.Endpoint : null;
    }

    private async Task RetargetAsync()
    {
        var target = ResolveTarget(out var problem);
        if (!Equals(target, _controller.Target))
        {
            Log.Info(target is null ? "No phone selected" : $"Using {target}");
            ConnectionText = target is null ? "—" : target.Kind == TransportKind.Usb ? "USB" : $"Wi-Fi · {target.Host}";
            await _controller.SetTargetAsync(target);
        }
        if (target is null)
        {
            SetStatus(Tally.Idle, "No phone", problem ?? NoDeviceHint());
            DeviceName = "No phone";
            UpdateCanStart(false);
        }
    }

    private string NoDeviceHint() => _adb is null
        ? "Open Phone Webcam on a phone on this Wi-Fi, or choose Enter an address. USB needs Android platform-tools (adb)."
        : "Connect a phone by USB (with USB debugging on), or open Phone Webcam on a phone on this Wi-Fi.";

    // ---- Webcam ----

    private StartStream CurrentSettings() =>
        _is720 ? new StartStream(_isFront ? "front" : "back", 1280, 720, 30, "mjpeg", 0.7)
               : new StartStream(_isFront ? "front" : "back", 1920, 1080, 30, "mjpeg", 0.7);

    private void ApplySettings()
    {
        Raise(nameof(IsRear));
        Raise(nameof(Is1080));
        _settings.Camera = _isFront ? "front" : "back";
        _settings.Height = _is720 ? 720 : 1080;
        _settings.Save();
        _ = Guard(_controller.ApplySettingsAsync(CurrentSettings()));
    }

    private void ToggleWebcam()
    {
        _webcamOn = !_webcamOn;
        Raise(nameof(WebcamButtonText));
        if (_webcamOn)
        {
            _ = Guard(_controller.ApplySettingsAsync(CurrentSettings()).ContinueWith(_ => _controller.StartWebcamAsync()).Unwrap());
        }
        else
        {
            _ = Guard(_controller.StopWebcamAsync());
            Preview = null;
        }
    }

    private void OnControllerStatus(WebcamStatus status, string detail)
    {
        if (status != WebcamStatus.Pairing) ShowPairing = false;
        switch (status)
        {
            case WebcamStatus.NoDevice:
                SetStatus(Tally.Idle, "No phone", detail);
                break;
            case WebcamStatus.Connecting:
            case WebcamStatus.Retrying:
                SetStatus(Tally.Busy, "Waiting for phone", detail);
                break;
            case WebcamStatus.Pairing:
                SetStatus(Tally.Busy, "Pair this PC", detail);
                break;
            case WebcamStatus.Connected:
                SetStatus(Tally.Busy, "Phone connected", _webcamOn ? "Starting the camera…" : "Press Start Webcam to use the phone as your webcam.");
                break;
            case WebcamStatus.Live:
                SetStatus(Tally.Live, "Live", "Apps using “Phone Webcam” see the phone's camera.");
                break;
            case WebcamStatus.Paused:
                SetStatus(Tally.Busy, "Paused on phone", detail);
                break;
        }
        UpdateCanStart(status is WebcamStatus.Connected or WebcamStatus.Live or WebcamStatus.Paused);
        if (status is not (WebcamStatus.Live or WebcamStatus.Paused))
        {
            Preview = null;
            ResolutionText = FpsText = LatencyText = BitrateText = DropsText = "—";
        }
    }

    private void UpdateCanStart(bool value)
    {
        _canStart = value;
        CommandManager.InvalidateRequerySuggested();
    }

    private void SubmitPairing()
    {
        PairingError = null;
        try
        {
            _ = Guard(_controller.SubmitPairingCodeAsync(PairingCode));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            PairingError = ex.Message;
        }
    }

    private async Task StartVirtualCameraAsync()
    {
        if (!VirtualCameraHost.IsInstalled)
        {
            VirtualCameraText = "The camera for other apps isn't installed. Run the Phone Webcam installer.";
            return;
        }
        try
        {
            var hr = await _virtualCamera.StartAsync();
            VirtualCameraText = hr >= 0
                ? "Choose “Phone Webcam” as the camera in Chrome, Teams, Zoom, OBS or the Camera app."
                : VirtualCameraHost.Explain(hr);
        }
        catch (Exception ex)
        {
            Log.Error($"Virtual camera: {ex.Message}");
            VirtualCameraText = "The camera for other apps couldn't start. See the log for details.";
        }
    }

    // ---- Frames: decode thread → virtual camera + preview ----

    private void OnFrameDecoded(VideoFrame frame)
    {
        _controller.Session?.Stats.OnFrameDecoded(frame.DecodeUs);
        _publisher.Publish(frame);
        Interlocked.Exchange(ref _latestFrame, frame)?.Dispose();
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            _dispatcher.BeginInvoke(RenderLatest, DispatcherPriority.Render);
    }

    private unsafe void RenderLatest()
    {
        Volatile.Write(ref _renderQueued, 0);
        using var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is null || !_webcamOn)
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
        var session = _controller.Session;
        if (session is null || _controller.Status != WebcamStatus.Live)
            return;
        session.Stats.DroppedOnPc = _pipeline.DroppedFrames + _publisher.DroppedFrames;
        var s = session.Stats.Sample();
        if (s.Width == 0)
            return;
        ResolutionText = $"{s.Width} × {s.Height}";
        FpsText = $"{s.DecodeFps:0.0} fps";
        LatencyText = s.LatencyMs is { } latency ? $"{latency + (s.DecodeMs ?? 0):0} ms" : "measuring";
        BitrateText = $"{s.BitrateMbps:0.0} Mbps";
        DropsText = $"{s.DroppedOnPhone + s.DroppedOnPc}";

        if (++_statsTicks % 10 == 0)
            Log.Info($"Stats: {s.Width}x{s.Height} {s.DecodeFps:0.0} fps, {s.BitrateMbps:0.0} Mbps, latency {s.LatencyMs:0.0} ms, decode {s.DecodeMs:0.0} ms, dropped phone/pc {s.DroppedOnPhone}/{s.DroppedOnPc}, apps reading: {_publisher.IsCameraReading}");
    }

    // ---- Helpers ----

    private static async Task Guard(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Debug($"Command failed: {ex.Message}");
        }
    }

    private void SetStatus(Tally tally, string status, string detail)
    {
        Tally = tally;
        Status = status;
        StatusDetail = detail;
    }

    private void Ui(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
