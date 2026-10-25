using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneWebcam.Core.Protocol;

/// <summary>JSON payloads of control messages. Field names are camelCase on the wire.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Deserialize<T>(Message message) =>
        JsonSerializer.Deserialize<T>(message.Payload.Span, Options)
        ?? throw new ProtocolException($"Empty {message.Header.Type} payload");

    public static (long EchoedTimeUs, long ResponderTimeUs) ReadPong(Message message)
    {
        var span = message.Payload.Span;
        if (span.Length < 16)
            throw new ProtocolException("PONG truncated");
        return (BinaryPrimitives.ReadInt64LittleEndian(span), BinaryPrimitives.ReadInt64LittleEndian(span[8..]));
    }

    public static long ReadPing(Message message)
    {
        var span = message.Payload.Span;
        if (span.Length < 8)
            throw new ProtocolException("PING truncated");
        return BinaryPrimitives.ReadInt64LittleEndian(span);
    }
}

public sealed record Empty;

public sealed record Hello(int Proto, string PcId, string PcName, string AppVersion);

public sealed record CameraInfo(string Id, bool Torch);

public sealed record FormatInfo(int W, int H, int[] Fps);

public sealed record DeviceInfo(
    string DeviceId,
    string DeviceName,
    string Model,
    string OsVersion,
    string AppVersion,
    string Nonce,
    bool Paired,
    CameraInfo[] Cameras,
    FormatInfo[] Formats,
    string[] Codecs);

public sealed record PairConfirm(string PcNonce, string Proof);

public sealed record Auth(string PcNonce, string Mac);

public sealed record AuthResult(bool Ok, string? Reason = null);

public sealed record StartStream(string Camera, int Width, int Height, int Fps, string Codec, double Quality);

public sealed record StreamState(
    string State,
    string? Reason = null,
    string? Camera = null,
    int Width = 0,
    int Height = 0,
    int Fps = 0,
    string? Codec = null);

public sealed record CameraSwitch(string Camera);

public sealed record SetResolution(int Width, int Height);

public sealed record SetFps(int Fps);

public sealed record SetQuality(double Quality);

public sealed record SetTorch(bool On);

public sealed record Disconnect(string Reason);

public sealed record ErrorMessage(string Code, string Message);

public static class StreamStates
{
    public const string Streaming = "streaming";
    public const string Stopped = "stopped";
    public const string Paused = "paused";
}

public static class ErrorCodes
{
    public const string Busy = "busy";
    public const string Unauthorized = "unauthorized";
    public const string Unsupported = "unsupported";
    public const string CameraUnavailable = "camera_unavailable";
    public const string BadRequest = "bad_request";
}
