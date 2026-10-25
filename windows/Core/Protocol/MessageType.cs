namespace IPhoneWebcam.Core.Protocol;

/// <summary>Wire message types. See docs/protocol.md §3. Never repurpose an ID.</summary>
public enum MessageType : byte
{
    Hello = 0x01,
    DeviceInfo = 0x02,
    PairRequest = 0x03,
    PairConfirm = 0x04,
    Auth = 0x05,
    AuthResult = 0x06,

    StartStream = 0x10,
    StopStream = 0x11,
    StreamState = 0x12,
    CameraSwitch = 0x13,
    SetResolution = 0x14,
    SetFps = 0x15,
    SetQuality = 0x16,
    SetTorch = 0x17,

    Ping = 0x20,
    Pong = 0x21,

    Disconnect = 0x3F,
    Error = 0x40,

    VideoFrame = 0x80,
    AudioFrame = 0x81,
}

public static class MessageTypeExtensions
{
    public static bool IsMedia(this MessageType type) => (byte)type >= 0x80;
}
