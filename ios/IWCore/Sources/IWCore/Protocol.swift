import Foundation

/// Wire protocol v1. Mirrors windows/Core/Protocol and docs/protocol.md. All integers little-endian.

public enum MessageType: UInt8 {
    case hello = 0x01
    case deviceInfo = 0x02
    case pairRequest = 0x03
    case pairConfirm = 0x04
    case auth = 0x05
    case authResult = 0x06

    case startStream = 0x10
    case stopStream = 0x11
    case streamState = 0x12
    case cameraSwitch = 0x13
    case setResolution = 0x14
    case setFps = 0x15
    case setQuality = 0x16
    case setTorch = 0x17

    case ping = 0x20
    case pong = 0x21

    case disconnect = 0x3F
    case error = 0x40

    case videoFrame = 0x80
    case audioFrame = 0x81

    public var isMedia: Bool { rawValue >= 0x80 }
}

public enum ProtocolError: Error, Equatable {
    case truncated
    case badMagic
    case unsupportedVersion(UInt8)
    case payloadTooLarge(UInt32)
}

public struct MessageHeader: Equatable {
    public static let size = 16
    public static let magic: UInt16 = 0x5749 // bytes 'I' 'W'
    public static let version: UInt8 = 1
    public static let maxControlLength: UInt32 = 64 * 1024
    public static let maxMediaLength: UInt32 = 8 * 1024 * 1024

    /// Raw type byte; unknown types are kept so they can be skipped (forward compatibility).
    public var rawType: UInt8
    public var flags: UInt16
    public var length: UInt32
    public var seq: UInt32

    public var type: MessageType? { MessageType(rawValue: rawType) }

    public init(type: MessageType, flags: UInt16 = 0, length: UInt32, seq: UInt32) {
        self.rawType = type.rawValue
        self.flags = flags
        self.length = length
        self.seq = seq
    }

    init(rawType: UInt8, flags: UInt16, length: UInt32, seq: UInt32) {
        self.rawType = rawType
        self.flags = flags
        self.length = length
        self.seq = seq
    }

    public func encoded() -> Data {
        var w = ByteWriter(capacity: Self.size)
        w.u16(Self.magic)
        w.u8(Self.version)
        w.u8(rawType)
        w.u16(flags)
        w.u16(0)
        w.u32(length)
        w.u32(seq)
        return w.data
    }

    public static func parse(_ data: Data) throws -> MessageHeader {
        guard data.count >= size else { throw ProtocolError.truncated }
        var r = ByteReader(data)
        guard r.u16() == magic else { throw ProtocolError.badMagic }
        let version = r.u8()
        guard version == Self.version else { throw ProtocolError.unsupportedVersion(version) }
        let rawType = r.u8()
        let flags = r.u16()
        _ = r.u16()
        let length = r.u32()
        let seq = r.u32()
        let max = rawType >= 0x80 ? maxMediaLength : maxControlLength
        guard length <= max else { throw ProtocolError.payloadTooLarge(length) }
        return MessageHeader(rawType: rawType, flags: flags, length: length, seq: seq)
    }
}

public enum VideoCodec: UInt8 {
    case mjpeg = 1
    case h264 = 2
    case hevc = 3
}

public struct VideoFrameFlags: OptionSet {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }

    public static let keyframe = VideoFrameFlags(rawValue: 1 << 0)
    public static let mirrored = VideoFrameFlags(rawValue: 1 << 1)
    public static let discontinuity = VideoFrameFlags(rawValue: 1 << 2)
}

public struct VideoFrameHeader: Equatable {
    public static let size = 20

    public var codec: VideoCodec
    public var flags: VideoFrameFlags
    public var rotation: UInt8
    public var width: UInt16
    public var height: UInt16
    public var frameId: UInt32
    public var ptsUs: UInt64

    public init(codec: VideoCodec, flags: VideoFrameFlags, rotation: UInt8, width: UInt16, height: UInt16, frameId: UInt32, ptsUs: UInt64) {
        self.codec = codec
        self.flags = flags
        self.rotation = rotation
        self.width = width
        self.height = height
        self.frameId = frameId
        self.ptsUs = ptsUs
    }

    public func encoded() -> Data {
        var w = ByteWriter(capacity: Self.size)
        w.u8(codec.rawValue)
        w.u8(flags.rawValue)
        w.u8(rotation)
        w.u8(0)
        w.u16(width)
        w.u16(height)
        w.u32(frameId)
        w.u64(ptsUs)
        return w.data
    }
}

/// Builds complete messages (header + payload) with an incrementing sequence number.
/// Not thread-safe: use from the connection's queue only.
public struct MessageEncoder {
    private var seq: UInt32 = 0

    public init() {}

    public mutating func message(_ type: MessageType, payload: Data = Data(), flags: UInt16 = 0) -> Data {
        var out = MessageHeader(type: type, flags: flags, length: UInt32(payload.count), seq: seq).encoded()
        seq &+= 1
        out.append(payload)
        return out
    }

    public mutating func control<T: Encodable>(_ type: MessageType, _ value: T) throws -> Data {
        message(type, payload: try ProtocolJSON.encoder.encode(value))
    }

    public mutating func ping(_ senderTimeUs: Int64) -> Data {
        var w = ByteWriter(capacity: 8)
        w.i64(senderTimeUs)
        return message(.ping, payload: w.data)
    }

    public mutating func pong(echoed: Int64, responder: Int64) -> Data {
        var w = ByteWriter(capacity: 16)
        w.i64(echoed)
        w.i64(responder)
        return message(.pong, payload: w.data)
    }

    /// Header + frame header + bitstream, as one buffer so it is written atomically.
    public mutating func videoFrame(_ header: VideoFrameHeader, data: Data) -> Data {
        var payload = header.encoded()
        payload.append(data)
        return message(.videoFrame, payload: payload)
    }
}

public enum PingPayload {
    public static func readPing(_ data: Data) throws -> Int64 {
        guard data.count >= 8 else { throw ProtocolError.truncated }
        var r = ByteReader(data)
        return r.i64()
    }
}

// MARK: - Byte helpers

struct ByteWriter {
    private(set) var data: Data

    init(capacity: Int) {
        data = Data(capacity: capacity)
    }

    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
    mutating func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
    mutating func u64(_ v: UInt64) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
    mutating func i64(_ v: Int64) { u64(UInt64(bitPattern: v)) }
}

struct ByteReader {
    private let bytes: [UInt8]
    private var offset = 0

    init(_ data: Data) {
        bytes = [UInt8](data)
    }

    mutating func u8() -> UInt8 {
        defer { offset += 1 }
        return bytes[offset]
    }

    mutating func u16() -> UInt16 { UInt16(u8()) | UInt16(u8()) << 8 }
    mutating func u32() -> UInt32 { UInt32(u16()) | UInt32(u16()) << 16 }
    mutating func u64() -> UInt64 { UInt64(u32()) | UInt64(u32()) << 32 }
    mutating func i64() -> Int64 { Int64(bitPattern: u64()) }
}
