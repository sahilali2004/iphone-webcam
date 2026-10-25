using System.IO;
using System.Text.Json;
using PhoneWebcam.Core.Diagnostics;

namespace PhoneWebcam.App;

/// <summary>User choices remembered between runs (%LOCALAPPDATA%\PhoneWebcam\settings.json). No secrets here.</summary>
public sealed class AppSettings
{
    public const string AutomaticDevice = "auto";
    public const string ManualDevice = "manual";

    /// <summary>"auto", "manual", or an AvailableDevice.Key.</summary>
    public string DeviceKey { get; set; } = AutomaticDevice;

    public string ManualHost { get; set; } = "";

    public int ManualPort { get; set; } = 47809;

    public string Camera { get; set; } = "back";

    public int Height { get; set; } = 1080;

    public bool MicrophoneOn { get; set; }

    /// <summary>Windows playback device id the phone's microphone is sent to (ideally a virtual cable).</summary>
    public string? AudioDeviceId { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneWebcam", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex)
        {
            Log.Warn($"Settings not saved: {ex.Message}");
        }
    }
}
