using System.Runtime.CompilerServices;
using System.Text.Json;
using PhoneWebcam.Core.Protocol;
using PhoneWebcam.Core.Security;
using Xunit;

namespace PhoneWebcam.Core.Tests;

/// <summary>
/// Pins the shared cross-language vectors (tests/protocol-vectors/vectors.json).
/// The Swift suite (ios/IWCore) checks the same file, so both sides must agree byte for byte.
/// </summary>
public class VectorTests
{
    private static JsonElement Load([CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tests", "protocol-vectors", "vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    [Fact]
    public void Pairing()
    {
        var v = Load().GetProperty("pairing");
        var code = v.GetProperty("code").GetString()!;
        var pcId = v.GetProperty("pcId").GetString()!;
        var phone = Hex(v, "phoneNonce");
        var pc = Hex(v, "pcNonce");

        Assert.Equal(Hex(v, "proof"), PairingCrypto.Proof(code, phone, pc, pcId));
        var key = PairingCrypto.DeriveKey(code, phone, pc);
        Assert.Equal(Hex(v, "pairKey"), key);
        Assert.Equal(Hex(v, "mac"), PairingCrypto.Mac(key, phone, pc, pcId));
    }

    [Fact]
    public void Headers()
    {
        var root = Load();
        var mh = root.GetProperty("messageHeader");
        var buffer = new byte[MessageHeader.Size];
        new MessageHeader((MessageType)mh.GetProperty("type").GetInt32(), (ushort)mh.GetProperty("flags").GetInt32(),
            mh.GetProperty("length").GetUInt32(), mh.GetProperty("seq").GetUInt32()).Write(buffer);
        Assert.Equal(Hex(mh, "bytes"), buffer);

        var vf = root.GetProperty("videoFrameHeader");
        var frame = new byte[VideoFrameHeader.Size];
        new VideoFrameHeader((VideoCodec)vf.GetProperty("codec").GetInt32(), (VideoFrameFlags)vf.GetProperty("flags").GetInt32(),
            (byte)vf.GetProperty("rotation").GetInt32(), (ushort)vf.GetProperty("width").GetInt32(), (ushort)vf.GetProperty("height").GetInt32(),
            vf.GetProperty("frameId").GetUInt32(), vf.GetProperty("ptsUs").GetUInt64()).Write(frame);
        Assert.Equal(Hex(vf, "bytes"), frame);
    }
}
