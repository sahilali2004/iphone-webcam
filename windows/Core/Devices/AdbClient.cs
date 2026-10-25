using System.Diagnostics;
using System.Text.RegularExpressions;
using PhoneWebcam.Core.Diagnostics;

namespace PhoneWebcam.Core.Devices;

public enum AdbDeviceState
{
    Ready,
    /// <summary>USB debugging not yet allowed on the device ("Allow USB debugging?" prompt).</summary>
    Unauthorized,
    Offline,
}

public sealed record AdbDevice(string Serial, string Model, AdbDeviceState State);

/// <summary>
/// Thin wrapper over the adb command line (Android SDK platform-tools). adb is not bundled: the Android SDK
/// licence doesn't allow redistribution, so we use an existing install.
/// </summary>
public sealed partial class AdbClient(string adbPath)
{
    public string AdbPath { get; } = adbPath;

    /// <summary>Finds adb.exe: next to the app, the Android Studio SDK, ANDROID_HOME/ANDROID_SDK_ROOT, or PATH.</summary>
    public static string? Locate()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
                candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct)
    {
        var output = await RunAsync(["devices", "-l"], ct).ConfigureAwait(false);
        return ParseDevices(output);
    }

    /// <summary>Forwards a free local TCP port to <paramref name="devicePort"/> on the device; returns the local port.</summary>
    public async Task<int> ForwardAsync(string serial, int devicePort, CancellationToken ct)
    {
        var output = await RunAsync(["-s", serial, "forward", "tcp:0", $"tcp:{devicePort}"], ct).ConfigureAwait(false);
        if (!int.TryParse(output.Trim(), out var port) || port <= 0)
            throw new InvalidOperationException($"adb forward failed: {output.Trim()}");
        return port;
    }

    public async Task RemoveForwardAsync(int localPort)
    {
        try
        {
            await RunAsync(["forward", "--remove", $"tcp:{localPort}"], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }

    internal static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var state = parts[1] switch
            {
                "device" => AdbDeviceState.Ready,
                "unauthorized" => AdbDeviceState.Unauthorized,
                _ => AdbDeviceState.Offline,
            };
            var model = ModelRegex().Match(line) is { Success: true } m ? m.Groups[1].Value.Replace('_', ' ') : parts[0];
            devices.Add(new AdbDevice(parts[0], model, state));
        }
        return devices;
    }

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start adb");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await stderr.ConfigureAwait(false);
            Log.Debug($"adb {string.Join(' ', args)} exited {process.ExitCode}: {error.Trim()}");
            throw new InvalidOperationException(error.Trim().Length > 0 ? error.Trim() : $"adb exited {process.ExitCode}");
        }
        return output;
    }

    [GeneratedRegex(@"model:(\S+)")]
    private static partial Regex ModelRegex();
}
