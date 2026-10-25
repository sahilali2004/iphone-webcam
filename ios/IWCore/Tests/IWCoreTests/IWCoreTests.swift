import Foundation
import XCTest
@testable import IWCore

/// Checks the shared vectors in tests/protocol-vectors/vectors.json, generated from the C# implementation.
/// If these pass, the iPhone and Windows sides agree byte for byte.
final class VectorTests: XCTestCase {
    private func vectors() throws -> [String: Any] {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // IWCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // IWCore
            .deletingLastPathComponent()   // ios
            .deletingLastPathComponent()   // repo root
            .appendingPathComponent("tests/protocol-vectors/vectors.json")
        return try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
    }

    func testPairingMatchesWindows() throws {
        let v = try vectors()["pairing"] as! [String: String]
        let phone = Data(hex: v["phoneNonce"]!)
        let pc = Data(hex: v["pcNonce"]!)
        let code = v["code"]!
        let pcId = v["pcId"]!

        XCTAssertEqual(PairingCrypto.proof(code: code, phoneNonce: phone, pcNonce: pc, pcId: pcId).hex, v["proof"])
        let key = PairingCrypto.deriveKey(code: code, phoneNonce: phone, pcNonce: pc)
        XCTAssertEqual(key.hex, v["pairKey"])
        XCTAssertEqual(PairingCrypto.mac(pairKey: key, phoneNonce: phone, pcNonce: pc, pcId: pcId).hex, v["mac"])
    }

    func testHeadersMatchWindows() throws {
        let all = try vectors()
        let mh = all["messageHeader"] as! [String: Any]
        let header = MessageHeader(type: MessageType(rawValue: UInt8(mh["type"] as! Int))!,
                                   flags: UInt16(mh["flags"] as! Int),
                                   length: UInt32(mh["length"] as! Int),
                                   seq: UInt32(mh["seq"] as! Int))
        XCTAssertEqual(header.encoded().hex, mh["bytes"] as? String)
        XCTAssertEqual(try MessageHeader.parse(header.encoded()), header)

        let vf = all["videoFrameHeader"] as! [String: Any]
        let frame = VideoFrameHeader(codec: VideoCodec(rawValue: UInt8(vf["codec"] as! Int))!,
                                     flags: VideoFrameFlags(rawValue: UInt8(vf["flags"] as! Int)),
                                     rotation: UInt8(vf["rotation"] as! Int),
                                     width: UInt16(vf["width"] as! Int),
                                     height: UInt16(vf["height"] as! Int),
                                     frameId: UInt32(vf["frameId"] as! Int),
                                     ptsUs: UInt64(vf["ptsUs"] as! Int))
        XCTAssertEqual(frame.encoded().hex, vf["bytes"] as? String)
    }
}

final class ProtocolTests: XCTestCase {
    func testRejectsBadHeaders() {
        var bytes = MessageHeader(type: .ping, length: 8, seq: 0).encoded()
        bytes[0] = 0
        XCTAssertThrowsError(try MessageHeader.parse(bytes)) { XCTAssertEqual($0 as? ProtocolError, .badMagic) }

        let big = MessageHeader(type: .hello, length: MessageHeader.maxControlLength + 1, seq: 0).encoded()
        XCTAssertThrowsError(try MessageHeader.parse(big))
        let bigVideo = MessageHeader(type: .videoFrame, length: MessageHeader.maxControlLength + 1, seq: 0).encoded()
        XCTAssertNoThrow(try MessageHeader.parse(bigVideo))
    }

    func testEncoderIncrementsSeqAndFramesPayload() throws {
        var enc = MessageEncoder()
        let a = try enc.control(.streamState, StreamState(state: StreamState.streaming, width: 1280))
        let b = enc.ping(-5)
        XCTAssertEqual(try MessageHeader.parse(a).seq, 0)
        XCTAssertEqual(try MessageHeader.parse(b).seq, 1)
        XCTAssertEqual(try PingPayload.readPing(b.subdata(in: 16..<b.count)), -5)

        let json = String(decoding: a.subdata(in: 16..<a.count), as: UTF8.self)
        XCTAssertTrue(json.contains("\"state\":\"streaming\""))
        XCTAssertTrue(json.contains("\"width\":1280"))
        XCTAssertFalse(json.contains("reason"), "nil optionals must be omitted like on Windows")
    }

    func testDecodesWindowsStyleJson() throws {
        // Exactly what System.Text.Json (Web defaults) produces on the PC side.
        let json = #"{"camera":"back","width":1920,"height":1080,"fps":30,"codec":"mjpeg","quality":0.7}"#
        let start = try ProtocolJSON.decode(StartStream.self, from: Data(json.utf8))
        XCTAssertEqual(start, StartStream(camera: "back", width: 1920, height: 1080, fps: 30, codec: "mjpeg", quality: 0.7))
    }

    func testLocalAddressFilter() {
        XCTAssertTrue(LocalAddress.isLocal(ipv4: [192, 168, 1, 25]))
        XCTAssertTrue(LocalAddress.isLocal(ipv4: [127, 0, 0, 1]))
        XCTAssertFalse(LocalAddress.isLocal(ipv4: [8, 8, 8, 8]))
        XCTAssertFalse(LocalAddress.isLocal(ipv4: [172, 32, 0, 1]))
        var fe80 = [UInt8](repeating: 0, count: 16); fe80[0] = 0xFE; fe80[1] = 0x80; fe80[15] = 1
        XCTAssertTrue(LocalAddress.isLocal(ipv6: fe80))
        var google = [UInt8](repeating: 0, count: 16); google[0] = 0x20; google[1] = 0x01
        XCTAssertFalse(LocalAddress.isLocal(ipv6: google))
    }

    func testCodeAndConstantTimeCompare() {
        for _ in 0..<100 { XCTAssertEqual(PairingCrypto.newCode().count, 6) }
        XCTAssertTrue(PairingCrypto.matches(Data([1, 2]), Data([1, 2])))
        XCTAssertFalse(PairingCrypto.matches(Data([1, 2]), Data([1, 3])))
        XCTAssertFalse(PairingCrypto.matches(Data([1]), Data([1, 2])))
    }
}

extension Data {
    init(hex: String) {
        var bytes = [UInt8]()
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            bytes.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        self.init(bytes)
    }

    var hex: String { map { String(format: "%02x", $0) }.joined() }
}
