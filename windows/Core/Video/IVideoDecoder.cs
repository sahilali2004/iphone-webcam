using PhoneWebcam.Core.Protocol;

namespace PhoneWebcam.Core.Video;

/// <summary>Decodes one codec. Called from a single decode thread; implementations need not be thread-safe.</summary>
public interface IVideoDecoder : IDisposable
{
    VideoCodec Codec { get; }

    VideoFrame Decode(EncodedFrame frame);
}
