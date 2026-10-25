using NAudio.CoreAudioApi;
using NAudio.Wave;
using PhoneWebcam.Core.Diagnostics;

namespace PhoneWebcam.Core.Audio;

/// <summary>A Windows playback device the phone's microphone can be sent to.</summary>
/// <param name="MicrophoneName">For a virtual cable: the recording device apps should pick as their microphone.</param>
/// <param name="Warning">Why this cable will distort audio (e.g. its two sides use different formats), or null.</param>
public sealed record AudioDevice(string Id, string Name, string? MicrophoneName, string? Warning = null)
{
    public bool IsVirtualCable => MicrophoneName is not null;

    public string Label => IsVirtualCable ? $"{Name} → mic “{MicrophoneName}”" : Name;
}

/// <summary>
/// Plays the phone's microphone audio into a Windows playback device. Pointed at a virtual audio cable
/// (VB-CABLE, Steam Streaming Microphone, ...), the cable's recording side becomes a microphone that Zoom,
/// Teams or Chrome can select. Windows has no user-mode API to create a microphone (unlike cameras), so a
/// signed cable driver is required for that; see docs/microphone.md.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    /// <summary>Above this much queued audio, drop the backlog: live audio must not drift behind video.</summary>
    public static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(150);

    private readonly object _gate = new();
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private string? _deviceId;
    private long _trims;

    /// <summary>Known virtual cables: playback name fragment → recording device name.</summary>
    private static readonly (string Playback, string Microphone)[] KnownCables =
    [
        ("CABLE Input", "CABLE Output (VB-Audio Virtual Cable)"),
        ("Steam Streaming Microphone", "Microphone (Steam Streaming Microphone)"),
        ("VoiceMeeter Input", "VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)"),
        ("Voicemeeter Input", "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)"),
    ];

    public long BacklogTrims => Interlocked.Read(ref _trims);

    public static IReadOnlyList<AudioDevice> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var list = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var name = device.FriendlyName;
                var cable = KnownCables.FirstOrDefault(c => name.Contains(c.Playback, StringComparison.OrdinalIgnoreCase));
                var mic = cable.Microphone is null ? null : captures.FirstOrDefault(c => c.FriendlyName == cable.Microphone);
                list.Add(new AudioDevice(device.ID, name, cable.Microphone, mic is null ? null : CheckCable(device, mic)));
            }
        }
        foreach (var capture in captures) capture.Dispose();
        // Virtual cables first: they're the useful targets.
        return [.. list.OrderByDescending(d => d.IsVirtualCable).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A cable passes bytes from its playback side to its recording side; if the two sides are configured with
    /// different rates or sample sizes (seen with Steam Streaming Microphone: 192 kHz/16-bit vs 48 kHz/32-bit),
    /// the microphone hears garbled, wrong-pitch audio. Detect it so the app can say so.
    /// </summary>
    private static string? CheckCable(MMDevice playback, MMDevice microphone)
    {
        try
        {
            var p = playback.AudioClient.MixFormat;
            var m = microphone.AudioClient.MixFormat;
            if (p.SampleRate == m.SampleRate && p.BitsPerSample == m.BitsPerSample) return null;
            return $"This cable's two sides use different formats ({p.SampleRate / 1000.0:0.#} kHz {p.BitsPerSample}-bit vs {m.SampleRate / 1000.0:0.#} kHz {m.BitsPerSample}-bit), so the microphone will sound distorted. Use VB-CABLE, or set both sides to the same format in Sound settings › More sound settings.";
        }
        catch (Exception ex)
        {
            Log.Debug($"Cable format check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Starts (or retargets) playback to <paramref name="deviceId"/>.</summary>
    public void Start(string deviceId, int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_output is not null && _deviceId == deviceId && _buffer!.WaveFormat.SampleRate == sampleRate && _buffer.WaveFormat.Channels == channels)
                return;
            StopLocked();

            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // silence on underrun keeps the device running
            };
            _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _output.Init(_buffer);
            _output.Play();
            _deviceId = deviceId;
            Log.Info($"Phone microphone → {device.FriendlyName} ({sampleRate} Hz, {channels} ch)");
        }
    }

    /// <summary>Queues PCM s16le. Called on the network receive thread.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_buffer is null) return;
            if (_buffer.BufferedDuration > MaxBuffered)
            {
                _buffer.ClearBuffer();
                Interlocked.Increment(ref _trims);
            }
            var bytes = pcm.ToArray();
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    public void Dispose() => Stop();

    private void StopLocked()
    {
        if (_output is null) return;
        try
        {
            _output.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio stop: {ex.Message}");
        }
        _output.Dispose();
        _output = null;
        _buffer = null;
        _deviceId = null;
    }
}
