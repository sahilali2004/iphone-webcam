using PhoneWebcam.Core.Diagnostics;
using PhoneWebcam.Core.Transport;

namespace PhoneWebcam.Core.Devices;

/// <summary>A phone the PC can connect to right now.</summary>
/// <param name="Key">Stable identity: "usb:SERIAL" or "wifi:DEVICEID".</param>
/// <param name="Problem">Why it can't be used yet (e.g. USB debugging not allowed), or null.</param>
public sealed record AvailableDevice(string Key, string Name, DeviceEndpoint Endpoint, string? Problem = null)
{
    public bool IsUsable => Problem is null;

    public string Label => Endpoint.Kind == TransportKind.Usb ? $"{Name} · USB" : $"{Name} · Wi-Fi";
}

/// <summary>
/// Watches for phones: Android devices over USB (adb) and phones on Wi-Fi (UDP discovery), every few seconds.
/// USB entries come first because USB is the preferred transport.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly AdbClient? _adb;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveryReply>>> _discover;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<AvailableDevice> _devices = [];
    private Task? _loop;
    private bool _adbErrorLogged;

    public DeviceManager(AdbClient? adb, Func<CancellationToken, Task<IReadOnlyList<DiscoveryReply>>>? discover = null, TimeSpan? interval = null)
    {
        _adb = adb;
        _discover = discover ?? (ct => Discovery.ProbeAsync(TimeSpan.FromMilliseconds(800), ct: ct));
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Raised on a thread-pool thread whenever the list changes.</summary>
    public event Action<IReadOnlyList<AvailableDevice>>? DevicesChanged;

    public IReadOnlyList<AvailableDevice> Devices => Volatile.Read(ref _devices);

    public bool HasAdb => _adb is not null;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>One scan now (also used by tests).</summary>
    public async Task<IReadOnlyList<AvailableDevice>> ScanAsync(CancellationToken ct)
    {
        var usbTask = ScanUsbAsync(ct);
        var wifiTask = ScanWifiAsync(ct);
        var found = new List<AvailableDevice>();
        found.AddRange(await usbTask.ConfigureAwait(false));
        found.AddRange(await wifiTask.ConfigureAwait(false));

        var previous = Devices;
        Volatile.Write(ref _devices, found);
        if (!previous.SequenceEqual(found))
        {
            foreach (var d in found.Except(previous)) Log.Info($"Found {d.Label}{(d.Problem is null ? "" : $" ({d.Problem})")}");
            foreach (var d in previous.Except(found)) Log.Info($"Lost {d.Label}");
            DevicesChanged?.Invoke(found);
        }
        return found;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Device scan failed: {ex.Message}");
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<AvailableDevice>> ScanUsbAsync(CancellationToken ct)
    {
        if (_adb is null) return [];
        try
        {
            var devices = await _adb.ListDevicesAsync(ct).ConfigureAwait(false);
            _adbErrorLogged = false;
            return devices.Select(d => new AvailableDevice(
                "usb:" + d.Serial,
                d.Model,
                new DeviceEndpoint(TransportKind.Usb, d.Model, Serial: d.Serial),
                d.State switch
                {
                    AdbDeviceState.Unauthorized => "Tap Allow on the device's \"Allow USB debugging?\" prompt",
                    AdbDeviceState.Offline => "Device is offline; unplug and reconnect the cable",
                    _ => null,
                })).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            if (!_adbErrorLogged) Log.Warn($"adb unavailable: {ex.Message}");
            _adbErrorLogged = true;
            return [];
        }
    }

    private async Task<IReadOnlyList<AvailableDevice>> ScanWifiAsync(CancellationToken ct)
    {
        var replies = await _discover(ct).ConfigureAwait(false);
        return replies.Select(r => new AvailableDevice(
            "wifi:" + r.DeviceId,
            r.Name,
            new DeviceEndpoint(TransportKind.Wifi, r.Name, r.Address.ToString(), r.Port))).ToList();
    }
}
