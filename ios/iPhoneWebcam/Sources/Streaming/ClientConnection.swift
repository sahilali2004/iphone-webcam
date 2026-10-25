import Foundation
import IWCore
import Network
import os

/// iPhone side of one PC session (docs/protocol.md §4): handshake, pairing/auth, keepalive,
/// command dispatch and frame sending. Confined to the server's network queue.
final class ClientConnection {
    private static let inactivityTimeoutUs: Int64 = 3_000_000
    private static let pairingLockout: TimeInterval = 30
    private static var pairingLockedUntil = Date.distantPast

    private unowned let server: StreamServer
    private let connection: NWConnection
    private let log = Logger(subsystem: "iPhoneWebcam", category: "Session")
    private var encoder = MessageEncoder()
    private let phoneNonce = PairingCrypto.newNonce()
    private var pcId: String?
    private var pcName = "PC"
    private var pairingCode: String?
    private var failedAttempts = 0
    private var lastInboundUs = MonoClock.nowUs
    private var keepAlive: DispatchSourceTimer?
    private var closed = false

    private(set) var isAuthorized = false

    init(connection: NWConnection, server: StreamServer) {
        self.connection = connection
        self.server = server
    }

    static func rejectBusy(_ connection: NWConnection, queue: DispatchQueue) {
        var encoder = MessageEncoder()
        connection.start(queue: queue)
        let message = (try? encoder.control(.error, ErrorMessage(code: ErrorMessage.busy, message: "Another PC is connected"))) ?? Data()
        connection.send(content: message, completion: .contentProcessed { _ in connection.cancel() })
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed(let error):
                self.log.info("Connection failed: \(error.localizedDescription)")
                self.close(reason: "network", notifyPeer: false)
            case .cancelled:
                self.close(reason: "cancelled", notifyPeer: false)
            default:
                break
            }
        }
        connection.start(queue: server.queue)
        receiveHeader()
        startKeepAlive()
    }

    func close(reason: String, notifyPeer: Bool) {
        guard !closed else { return }
        closed = true
        keepAlive?.cancel()
        keepAlive = nil
        log.info("Closing session: \(reason)")
        if notifyPeer, let message = try? encoder.control(.disconnect, Disconnect(reason: reason)) {
            connection.send(content: message, completion: .contentProcessed { [connection] _ in connection.cancel() })
        } else {
            connection.cancel()
        }
        server.clientClosed(self)
    }

    // MARK: - Sending

    func sendControl<T: Encodable>(_ type: MessageType, _ value: T) {
        guard !closed else { return }
        do {
            send(try encoder.control(type, value))
        } catch {
            log.error("Encode \(String(describing: type)) failed: \(error.localizedDescription)")
        }
    }

    func sendFrame(header: VideoFrameHeader, data: Data, done: @escaping () -> Void) {
        guard !closed else {
            done()
            return
        }
        send(encoder.videoFrame(header, data: data), completion: done)
    }

    private func send(_ data: Data, completion: (() -> Void)? = nil) {
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            completion?()
            if let error, let self {
                self.log.info("Send failed: \(error.localizedDescription)")
                self.close(reason: "send failed", notifyPeer: false)
            }
        })
    }

    // MARK: - Receiving: exact-length reads of header, then payload

    private func receiveHeader() {
        connection.receive(minimumIncompleteLength: MessageHeader.size, maximumLength: MessageHeader.size) { [weak self] data, _, isComplete, error in
            guard let self, !self.closed else { return }
            guard let data, data.count == MessageHeader.size, error == nil else {
                self.close(reason: isComplete ? "peer closed" : "receive failed", notifyPeer: false)
                return
            }
            do {
                let header = try MessageHeader.parse(data)
                if header.length == 0 {
                    self.dispatch(header, Data())
                } else {
                    self.receivePayload(header)
                }
            } catch {
                self.log.error("Bad header: \(String(describing: error))")
                self.close(reason: "protocol error", notifyPeer: false)
            }
        }
    }

    private func receivePayload(_ header: MessageHeader) {
        let length = Int(header.length)
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] data, _, isComplete, error in
            guard let self, !self.closed else { return }
            guard let data, data.count == length, error == nil else {
                self.close(reason: isComplete ? "peer closed" : "receive failed", notifyPeer: false)
                return
            }
            self.dispatch(header, data)
        }
    }

    private func dispatch(_ header: MessageHeader, _ payload: Data) {
        lastInboundUs = MonoClock.nowUs
        do {
            if try handle(header, payload) {
                receiveHeader()
            }
        } catch {
            log.error("Bad \(String(describing: header.type)) payload: \(error.localizedDescription)")
            sendControl(.error, ErrorMessage(code: ErrorMessage.badRequest, message: "Malformed message"))
            receiveHeader()
        }
    }

    // MARK: - Message handling. Returns false when the session should stop reading.

    private func handle(_ header: MessageHeader, _ payload: Data) throws -> Bool {
        guard let type = header.type else {
            return true // unknown type: payload already consumed, skip it
        }

        let allowedBeforeAuth: Set<MessageType> = [.hello, .pairRequest, .pairConfirm, .auth, .ping, .pong, .disconnect]
        if !isAuthorized && !allowedBeforeAuth.contains(type) {
            log.warning("Unauthorized \(String(describing: type)); closing")
            sendControl(.error, ErrorMessage(code: ErrorMessage.unauthorized, message: "Not authorized"))
            close(reason: "unauthorized", notifyPeer: false)
            return false
        }

        switch type {
        case .hello:
            let hello = try ProtocolJSON.decode(Hello.self, from: payload)
            pcId = hello.pcId
            pcName = hello.pcName
            log.info("HELLO from \(hello.pcName, privacy: .public)")
            let t = server.deviceInfo
            sendControl(.deviceInfo, DeviceInfo(
                deviceId: DeviceIdentity.deviceId,
                deviceName: t.deviceName,
                model: DeviceIdentity.model,
                osVersion: t.osVersion,
                appVersion: t.appVersion,
                nonce: phoneNonce.base64EncodedString(),
                paired: server.pairings.key(for: hello.pcId) != nil,
                cameras: t.cameras,
                formats: t.formats,
                codecs: ["mjpeg"]))

        case .pairRequest:
            if Date() < Self.pairingLockedUntil {
                sendControl(.authResult, AuthResult(ok: false, reason: "Too many attempts. Try again in 30 seconds."))
            } else {
                let code = PairingCrypto.newCode()
                pairingCode = code
                failedAttempts = 0
                log.info("Pairing requested by \(self.pcName, privacy: .public)")
                server.notify { $0.server(pairingCodeChanged: code) }
            }

        case .pairConfirm:
            try handlePairConfirm(try ProtocolJSON.decode(PairConfirm.self, from: payload))

        case .auth:
            let auth = try ProtocolJSON.decode(Auth.self, from: payload)
            guard let pcId, let key = server.pairings.key(for: pcId),
                  let pcNonce = Data(base64Encoded: auth.pcNonce), let mac = Data(base64Encoded: auth.mac),
                  PairingCrypto.matches(PairingCrypto.mac(pairKey: key, phoneNonce: phoneNonce, pcNonce: pcNonce, pcId: pcId), mac)
            else {
                sendControl(.authResult, AuthResult(ok: false, reason: "Unknown PC"))
                return true
            }
            authorize()

        case .startStream:
            let request = try ProtocolJSON.decode(StartStream.self, from: payload)
            server.notify { $0.server(startStream: request) }
        case .stopStream:
            server.notify { $0.serverStopStream() }
        case .cameraSwitch:
            let request = try ProtocolJSON.decode(CameraSwitch.self, from: payload)
            server.notify { $0.server(switchCamera: request.camera) }
        case .setResolution:
            let request = try ProtocolJSON.decode(SetResolution.self, from: payload)
            server.notify { $0.server(setResolution: request.width, height: request.height) }
        case .setQuality:
            let request = try ProtocolJSON.decode(SetQuality.self, from: payload)
            server.notify { $0.server(setQuality: request.quality) }
        case .setTorch:
            let request = try ProtocolJSON.decode(SetTorch.self, from: payload)
            server.notify { $0.server(setTorch: request.on) }
        case .setFps:
            sendControl(.error, ErrorMessage(code: ErrorMessage.unsupported, message: "Only 30 fps is supported"))

        case .ping:
            send(encoder.pong(echoed: try PingPayload.readPing(payload), responder: MonoClock.nowUs))
        case .pong:
            break

        case .disconnect:
            let reason = (try? ProtocolJSON.decode(Disconnect.self, from: payload).reason) ?? "unknown"
            log.info("PC disconnected: \(reason, privacy: .public)")
            close(reason: "peer: \(reason)", notifyPeer: false)
            return false

        case .error:
            let error = try ProtocolJSON.decode(ErrorMessage.self, from: payload)
            log.error("PC reported \(error.code, privacy: .public): \(error.message, privacy: .public)")

        case .deviceInfo, .authResult, .streamState, .videoFrame, .audioFrame:
            log.warning("Unexpected \(String(describing: type)) from PC")
        }
        return true
    }

    private func handlePairConfirm(_ confirm: PairConfirm) throws {
        guard let code = pairingCode, let pcId,
              let pcNonce = Data(base64Encoded: confirm.pcNonce), let proof = Data(base64Encoded: confirm.proof) else {
            sendControl(.authResult, AuthResult(ok: false, reason: "No pairing in progress"))
            return
        }

        guard PairingCrypto.matches(PairingCrypto.proof(code: code, phoneNonce: phoneNonce, pcNonce: pcNonce, pcId: pcId), proof) else {
            failedAttempts += 1
            if failedAttempts >= 3 {
                pairingCode = nil
                Self.pairingLockedUntil = Date().addingTimeInterval(Self.pairingLockout)
                server.notify { $0.server(pairingCodeChanged: nil) }
                sendControl(.authResult, AuthResult(ok: false, reason: "Too many attempts. Try again in 30 seconds."))
            } else {
                sendControl(.authResult, AuthResult(ok: false, reason: "Incorrect code"))
            }
            return
        }

        server.pairings.save(key: PairingCrypto.deriveKey(code: code, phoneNonce: phoneNonce, pcNonce: pcNonce), pcId: pcId, pcName: pcName)
        pairingCode = nil
        log.info("Paired with \(self.pcName, privacy: .public)")
        server.notify { $0.server(pairingCodeChanged: nil) }
        authorize()
    }

    private func authorize() {
        isAuthorized = true
        sendControl(.authResult, AuthResult(ok: true))
        let name = pcName
        server.notify { $0.server(clientChanged: name) }
    }

    // MARK: - Keepalive: ping every second, drop the link after 3 s of silence (protocol.md §6)

    private func startKeepAlive() {
        let timer = DispatchSource.makeTimerSource(queue: server.queue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in
            guard let self, !self.closed else { return }
            if MonoClock.nowUs - self.lastInboundUs > Self.inactivityTimeoutUs {
                self.close(reason: "timeout", notifyPeer: false)
                return
            }
            self.send(self.encoder.ping(MonoClock.nowUs))
        }
        timer.resume()
        keepAlive = timer
    }
}
