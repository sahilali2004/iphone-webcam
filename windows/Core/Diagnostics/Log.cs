namespace IPhoneWebcam.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()}] {Message}";
}

/// <summary>
/// Minimal process-wide log. Never log frame data or pairing secrets.
/// Sinks subscribe to <see cref="Written"/>; the file sink is opt-in via <see cref="EnableFile"/>.
/// </summary>
public static class Log
{
    private static readonly object FileGate = new();
    private static StreamWriter? _file;

    public static event Action<LogEntry>? Written;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void EnableFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (FileGate)
        {
            _file?.Dispose();
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
            return;
        var entry = new LogEntry(DateTime.Now, level, message);
        System.Diagnostics.Debug.WriteLine(entry.ToString());
        lock (FileGate)
            _file?.WriteLine(entry.ToString());
        Written?.Invoke(entry);
    }
}
