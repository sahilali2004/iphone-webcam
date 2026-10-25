import Foundation
import IWCore
import Network
import os

enum ServerState: Equatable {
    case stopped
    case listening
    case failed(String)
}

/// Events from the server. Always delivered on the main actor.
@MainActor
protocol StreamServerDelegate: AnyObject {
    func server(stateChanged state: ServerState)
    func server(pairingCodeChanged code: String?)
    /// A PC finished authenticating (name), or the authenticated PC went away (nil).
    func server(clientChanged pcName: String?)
    func server(startStream request: StartStream)
    func serverStopStream()
    func server(switchCamera camera: String)
    func server(setResolution width: Int, height: Int)
    func server(setQuality quality: Double)
    func server(setTorch on: Bool)
}

/// TCP listener on port 47809, advertised over Bonjour. Accepts one PC at a time.
/// All networking state lives on `queue`.
final class StreamServer {
    static let port: UInt16 = 47809
    static let serviceType = "_iphonewebcam._tcp"

    /// Only read inside `notify` on the main actor.
    weak var delegate: StreamServerDelegate?
    let queue = DispatchQueue(label: "net", qos: .userInitiated)
    let pairings = PairingStore()

    private let log = Logger(subsystem: "iPhoneWebcam", category: "Server")
    private var listener: NWListener?
    private var client: ClientConnection?
    /// Read on the network queue; set via `start`.
    private(set) var deviceInfo = DeviceInfoTemplate()

    func start(deviceInfo: DeviceInfoTemplate) {
        let deviceName = deviceInfo.deviceName
        queue.async { [self] in
            self.deviceInfo = deviceInfo
            guard listener == nil else { return }
            do {
                let tcp = NWProtocolTCP.Options()
                tcp.noDelay = true
                let params = NWParameters(tls: nil, tcp: tcp)
                params.allowLocalEndpointReuse = true
                let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: Self.port)!)
                listener.service = NWListener.Service(
                    name: deviceName,
                    type: Self.serviceType,
                    txtRecord: NWTXTRecord(["id": DeviceIdentity.deviceId, "name": deviceName, "proto": "1"]))
                listener.stateUpdateHandler = { [weak self] state in self?.listenerStateChanged(state) }
                listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
                listener.start(queue: queue)
                self.listener = listener
            } catch {
                log.error("Listener failed: \(error.localizedDescription)")
                notify { $0.server(stateChanged: .failed(error.localizedDescription)) }
            }
        }
    }

    func stop() {
        queue.async { [self] in
            client?.close(reason: "user", notifyPeer: true)
            client = nil
            listener?.cancel()
            listener = nil
            notify { $0.server(stateChanged: .stopped) }
        }
    }

    /// Hands an encoded frame to the connected PC. `done` is called once the bytes are handed to the
    /// network stack (or dropped), on the network queue.
    func send(frame data: Data, header: VideoFrameHeader, done: @escaping () -> Void) {
        queue.async { [self] in
            guard let client, client.isAuthorized else {
                done()
                return
            }
            client.sendFrame(header: header, data: data, done: done)
        }
    }

    func send(streamState: StreamState) {
        queue.async { [self] in
            client?.sendControl(.streamState, streamState)
        }
    }

    func send(error code: String, message: String) {
        queue.async { [self] in
            client?.sendControl(.error, ErrorMessage(code: code, message: message))
        }
    }

    // MARK: - Internals (network queue)

    private func listenerStateChanged(_ state: NWListener.State) {
        switch state {
        case .ready:
            log.info("Listening on port \(Self.port)")
            notify { $0.server(stateChanged: .listening) }
        case .failed(let error):
            log.error("Listener failed: \(error.localizedDescription)")
            listener?.cancel()
            listener = nil
            notify { $0.server(stateChanged: .failed(error.localizedDescription)) }
        case .waiting(let error):
            // Typically: Local Network permission denied.
            log.warning("Listener waiting: \(error.localizedDescription)")
            notify { $0.server(stateChanged: .failed("Waiting for network: \(error.localizedDescription)")) }
        default:
            break
        }
    }

    private func accept(_ connection: NWConnection) {
        guard Self.isLocal(connection.endpoint) else {
            log.warning("Rejected non-local connection from \(String(describing: connection.endpoint))")
            connection.cancel()
            return
        }
        if client != nil {
            log.warning("Rejected second client \(String(describing: connection.endpoint))")
            ClientConnection.rejectBusy(connection, queue: queue)
            return
        }
        log.info("Client connected: \(String(describing: connection.endpoint))")
        let client = ClientConnection(connection: connection, server: self)
        self.client = client
        client.start()
    }

    func clientClosed(_ closed: ClientConnection) {
        guard client === closed else { return }
        client = nil
        let wasAuthorized = closed.isAuthorized
        notify { delegate in
            delegate.server(pairingCodeChanged: nil)
            if wasAuthorized { delegate.server(clientChanged: nil) }
        }
    }

    func notify(_ body: @escaping @MainActor (StreamServerDelegate) -> Void) {
        Task { @MainActor [weak self] in
            if let delegate = self?.delegate { body(delegate) }
        }
    }

    static func isLocal(_ endpoint: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = endpoint else { return false }
        switch host {
        case .ipv4(let address): return LocalAddress.isLocal(ipv4: [UInt8](address.rawValue))
        case .ipv6(let address): return LocalAddress.isLocal(ipv6: [UInt8](address.rawValue))
        default: return false
        }
    }
}

/// Static parts of DEVICE_INFO supplied by the app.
struct DeviceInfoTemplate {
    var deviceName = "iPhone"
    var osVersion = ""
    var appVersion = "0.1.0"
    var cameras: [CameraInfo] = []
    var formats: [FormatInfo] = []
}
