import AVFoundation
import IWCore
import SwiftUI
import UIKit
import os

/// Coordinates camera, encoder and server, and holds UI state. Main actor.
@MainActor
final class AppModel: ObservableObject, StreamServerDelegate {
    enum Link: Equatable {
        case idle
        case waiting
        case connected(pc: String)
    }

    @Published private(set) var isRunning = false
    @Published private(set) var link: Link = .idle
    @Published private(set) var isStreaming = false
    @Published private(set) var isPaused = false
    @Published private(set) var captureFps: Double = 0
    @Published private(set) var pairingCode: String?
    @Published private(set) var errorMessage: String?
    @Published private(set) var address: String?
    @Published private(set) var torchOn = false
    @Published var position: CameraPosition = .back {
        didSet { if oldValue != position { reconfigure() } }
    }
    @Published var resolution: StreamResolution = .hd1080 {
        didSet { if oldValue != resolution { reconfigure() } }
    }

    let fps = 30
    let camera = CameraManager()

    private let log = Logger(subsystem: "iPhoneWebcam", category: "App")
    private let server = StreamServer()
    private let gate = FrameGate()
    private let encoder = JpegEncoder(quality: 0.7)
    private var inBackground = false
    private var interrupted = false

    var hasTorch: Bool { CameraManager.hasTorch(position) }

    init() {
        server.delegate = self
        camera.onFps = { [weak self] value in self?.captureFps = value }
        camera.onInterruption = { [weak self] value in self?.setInterrupted(value) }
        camera.onFrame = Self.makeFrameHandler(gate: gate, encoder: encoder, server: server)
    }

    // MARK: - User actions

    func start() {
        guard !isRunning else { return }
        errorMessage = nil
        Task {
            guard await CameraManager.requestAccess() else {
                errorMessage = CameraError.accessDenied.localizedDescription
                return
            }
            do {
                try await camera.configure(position: position, resolution: resolution, fps: fps)
            } catch {
                errorMessage = error.localizedDescription
                return
            }
            camera.start()
            server.start(deviceInfo: Self.deviceInfo())
            isRunning = true
            link = .waiting
            address = NetworkInfo.wifiAddress()
            UIApplication.shared.isIdleTimerDisabled = true
            log.info("Started")
        }
    }

    func stop() {
        guard isRunning else { return }
        gate.setStreaming(false)
        server.stop()
        camera.stop()
        isRunning = false
        isStreaming = false
        isPaused = false
        link = .idle
        pairingCode = nil
        captureFps = 0
        setTorch(false)
        UIApplication.shared.isIdleTimerDisabled = false
        log.info("Stopped")
    }

    func toggleTorch() {
        setTorch(!torchOn)
    }

    func focus(at devicePoint: CGPoint) {
        camera.focus(at: devicePoint)
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        guard isRunning else { return }
        switch phase {
        case .background:
            // iOS stops camera access for background apps; tell the PC instead of going silent.
            inBackground = true
            camera.stop()
            updateFlow()
        case .active:
            if inBackground {
                inBackground = false
                camera.start()
                // iOS may have torn the listener down while suspended; start() is a no-op if it's still alive.
                server.start(deviceInfo: Self.deviceInfo())
                address = NetworkInfo.wifiAddress()
                updateFlow()
            }
        default:
            break
        }
    }

    // MARK: - StreamServerDelegate (main queue)

    func server(stateChanged state: ServerState) {
        if case .failed(let why) = state, isRunning {
            errorMessage = "Network unavailable: \(why). Check Wi-Fi and Settings › Privacy › Local Network."
        } else if state == .listening {
            errorMessage = nil
        }
    }

    func server(pairingCodeChanged code: String?) {
        pairingCode = code
    }

    func server(clientChanged pcName: String?) {
        if let pcName {
            link = .connected(pc: pcName)
        } else {
            link = isRunning ? .waiting : .idle
            isStreaming = false
            setTorch(false)
            updateFlow()
        }
    }

    func server(startStream request: StartStream) {
        gate.quality = request.quality
        isStreaming = true
        applyRemoteSettings(camera: request.camera, width: request.width, height: request.height)
    }

    func serverStopStream() {
        isStreaming = false
        updateFlow()
    }

    func server(switchCamera camera: String) {
        applyRemoteSettings(camera: camera, width: resolution.width, height: resolution.height)
    }

    func server(setResolution width: Int, height: Int) {
        applyRemoteSettings(camera: position.rawValue, width: width, height: height)
    }

    func server(setQuality quality: Double) {
        gate.quality = quality
    }

    func server(setTorch on: Bool) {
        setTorch(on)
    }

    // MARK: - Internals

    private static func deviceInfo() -> DeviceInfoTemplate {
        DeviceInfoTemplate(
            deviceName: UIDevice.current.name,
            osVersion: "iOS \(UIDevice.current.systemVersion)",
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0",
            cameras: CameraPosition.allCases.map { CameraInfo(id: $0.rawValue, torch: CameraManager.hasTorch($0)) },
            formats: StreamResolution.allCases.map { FormatInfo(w: $0.width, h: $0.height, fps: [30]) })
    }

    /// Runs on the camera queue: capture → gate → encode → send. Never blocks; skipped frames show up
    /// as frameId gaps on the PC. `nonisolated` so the closure isn't main-actor isolated.
    nonisolated private static func makeFrameHandler(gate: FrameGate, encoder: JpegEncoder, server: StreamServer) -> (CMSampleBuffer, Bool) -> Void {
        { sample, mirrored in
            let (frameId, send) = gate.capture()
            guard send else { return }
            guard let pixelBuffer = CMSampleBufferGetImageBuffer(sample) else {
                gate.finished()
                return
            }
            let pts = CMSampleBufferGetPresentationTimeStamp(sample)
            let ptsUs = UInt64(max(0, CMTimeGetSeconds(pts) * 1_000_000))
            let width = UInt16(CVPixelBufferGetWidth(pixelBuffer))
            let height = UInt16(CVPixelBufferGetHeight(pixelBuffer))
            let quality = gate.quality
            if encoder.quality != quality { encoder.quality = quality }

            encoder.encode(pixelBuffer, pts: pts) { data in
                guard let data else {
                    gate.finished()
                    return
                }
                let header = VideoFrameHeader(codec: .mjpeg, flags: mirrored ? [.keyframe, .mirrored] : [.keyframe],
                                              rotation: 0, width: width, height: height, frameId: frameId, ptsUs: ptsUs)
                server.send(frame: data, header: header) { gate.finished() }
            }
        }
    }

    /// Apply camera/resolution requested by the PC (clamped to what we support), then report what we actually use.
    private func applyRemoteSettings(camera requestedCamera: String, width: Int, height: Int) {
        let newPosition = CameraPosition(rawValue: requestedCamera) ?? position
        let newResolution = StreamResolution(width: width, height: height) ?? resolution
        // Assigning triggers reconfigure() through didSet only when something changed.
        if newPosition == position && newResolution == resolution {
            updateFlow()
        } else {
            setWithoutReconfigure(position: newPosition, resolution: newResolution)
            reconfigure()
        }
    }

    private var suppressReconfigure = false

    private func setWithoutReconfigure(position: CameraPosition, resolution: StreamResolution) {
        suppressReconfigure = true
        self.position = position
        self.resolution = resolution
        suppressReconfigure = false
    }

    private func reconfigure() {
        guard isRunning, !suppressReconfigure else { return }
        let position = self.position
        let resolution = self.resolution
        Task {
            do {
                try await camera.configure(position: position, resolution: resolution, fps: fps)
                if !CameraManager.hasTorch(position) { torchOn = false }
                updateFlow()
            } catch {
                errorMessage = error.localizedDescription
                server.send(error: ErrorMessage.cameraUnavailable, message: error.localizedDescription)
            }
        }
    }

    /// Single place that decides whether frames flow, and tells the PC.
    private func updateFlow() {
        let paused = inBackground || interrupted
        isPaused = isStreaming && paused
        gate.setStreaming(isStreaming && !paused)
        guard case .connected = link else { return }

        let state: StreamState
        if !isStreaming {
            state = StreamState(state: StreamState.stopped)
        } else if paused {
            state = StreamState(state: StreamState.paused,
                                reason: inBackground ? "iPhone Webcam is in the background. Open it on the iPhone to resume." : "The camera is being used by another app.",
                                camera: position.rawValue, width: resolution.width, height: resolution.height, fps: fps, codec: "mjpeg")
        } else {
            state = StreamState(state: StreamState.streaming, camera: position.rawValue,
                                width: resolution.width, height: resolution.height, fps: fps, codec: "mjpeg")
        }
        server.send(streamState: state)
    }

    private func setInterrupted(_ value: Bool) {
        interrupted = value
        updateFlow()
    }

    private func setTorch(_ on: Bool) {
        let on = on && hasTorch && isRunning
        torchOn = on
        camera.setTorch(on)
    }
}

enum NetworkInfo {
    /// IPv4 address of the Wi-Fi interface (en0), for manual connection from the PC.
    static func wifiAddress() -> String? {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return nil }
        defer { freeifaddrs(head) }
        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let entry = pointer.pointee
            guard let addr = entry.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
                  String(cString: entry.ifa_name) == "en0" else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                return String(cString: host)
            }
        }
        return nil
    }
}
