import AVFoundation
import os

enum CameraPosition: String, CaseIterable, Identifiable {
    case back
    case front

    var id: String { rawValue }
    var label: String { self == .back ? "Rear" : "Front" }
    var avPosition: AVCaptureDevice.Position { self == .back ? .back : .front }
}

enum StreamResolution: String, CaseIterable, Identifiable {
    case hd720 = "720p"
    case hd1080 = "1080p"

    var id: String { rawValue }
    var width: Int { self == .hd720 ? 1280 : 1920 }
    var height: Int { self == .hd720 ? 720 : 1080 }
    var preset: AVCaptureSession.Preset { self == .hd720 ? .hd1280x720 : .hd1920x1080 }

    init?(width: Int, height: Int) {
        guard let match = Self.allCases.first(where: { $0.width == width && $0.height == height }) else { return nil }
        self = match
    }
}

enum CameraError: LocalizedError {
    case accessDenied
    case unavailable(CameraPosition)
    case configurationFailed(String)

    var errorDescription: String? {
        switch self {
        case .accessDenied: return "Camera access is off. Turn it on in Settings › iPhone Webcam."
        case .unavailable(let p): return "The \(p.label.lowercased()) camera isn't available on this iPhone."
        case .configurationFailed(let why): return "Couldn't start the camera: \(why)"
        }
    }
}

/// Owns the AVCaptureSession. Frames come straight from AVCaptureVideoDataOutput (never the preview).
/// Output is fixed to landscape-right (home button / USB port on the right), matching the locked UI.
final class CameraManager: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    let session = AVCaptureSession()

    /// Called on the video queue for every captured frame.
    var onFrame: ((CMSampleBuffer, _ mirrored: Bool) -> Void)?
    /// Called on the main queue: measured capture fps (once per second).
    var onFps: ((Double) -> Void)?
    /// Called on the main queue when iOS interrupts/resumes the camera (e.g. another app takes it).
    var onInterruption: ((_ interrupted: Bool) -> Void)?

    private let log = Logger(subsystem: "iPhoneWebcam", category: "Camera")
    private let sessionQueue = DispatchQueue(label: "camera.session")
    private let videoQueue = DispatchQueue(label: "camera.video", qos: .userInteractive)
    private let output = AVCaptureVideoDataOutput()
    private var input: AVCaptureDeviceInput?
    private var mirrored = false
    private var fpsWindowStart = MonoClockSeconds.now
    private var fpsCount = 0

    override init() {
        super.init()
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(interrupted), name: .AVCaptureSessionWasInterrupted, object: session)
        center.addObserver(self, selector: #selector(interruptionEnded), name: .AVCaptureSessionInterruptionEnded, object: session)
        center.addObserver(self, selector: #selector(runtimeError), name: .AVCaptureSessionRuntimeError, object: session)
        center.addObserver(self, selector: #selector(subjectAreaChanged), name: .AVCaptureDeviceSubjectAreaDidChange, object: nil)
    }

    static func requestAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .video)
        default: return false
        }
    }

    static func hasTorch(_ position: CameraPosition) -> Bool {
        AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: position.avPosition)?.hasTorch ?? false
    }

    /// Reconfigures input, resolution and frame rate. Safe while running.
    func configure(position: CameraPosition, resolution: StreamResolution, fps: Int) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            sessionQueue.async {
                do {
                    try self.applyConfiguration(position: position, resolution: resolution, fps: fps)
                    cont.resume()
                } catch {
                    cont.resume(throwing: error)
                }
            }
        }
    }

    func start() {
        sessionQueue.async {
            guard !self.session.isRunning else { return }
            self.session.startRunning()
            self.log.info("Capture started")
        }
    }

    func stop() {
        sessionQueue.async {
            guard self.session.isRunning else { return }
            self.session.stopRunning()
            self.log.info("Capture stopped")
        }
    }

    func setTorch(_ on: Bool) {
        sessionQueue.async {
            guard let device = self.input?.device, device.hasTorch else { return }
            do {
                try device.lockForConfiguration()
                device.torchMode = on ? .on : .off
                device.unlockForConfiguration()
            } catch {
                self.log.error("Torch failed: \(error.localizedDescription)")
            }
        }
    }

    /// Point in device coordinates (0…1, landscape-right), e.g. from AVCaptureVideoPreviewLayer.captureDevicePointConverted.
    func focus(at point: CGPoint) {
        sessionQueue.async {
            guard let device = self.input?.device else { return }
            do {
                try device.lockForConfiguration()
                if device.isFocusPointOfInterestSupported {
                    device.focusPointOfInterest = point
                    device.focusMode = .autoFocus
                }
                if device.isExposurePointOfInterestSupported {
                    device.exposurePointOfInterest = point
                    device.exposureMode = .autoExpose
                }
                // Return to continuous mode when the scene changes.
                device.isSubjectAreaChangeMonitoringEnabled = true
                device.unlockForConfiguration()
            } catch {
                self.log.error("Focus failed: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Configuration (session queue)

    private func applyConfiguration(position: CameraPosition, resolution: StreamResolution, fps: Int) throws {
        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: position.avPosition) else {
            throw CameraError.unavailable(position)
        }

        session.beginConfiguration()
        defer { session.commitConfiguration() }

        if input?.device != device {
            if let input { session.removeInput(input) }
            let newInput: AVCaptureDeviceInput
            do {
                newInput = try AVCaptureDeviceInput(device: device)
            } catch {
                throw CameraError.configurationFailed(error.localizedDescription)
            }
            guard session.canAddInput(newInput) else { throw CameraError.configurationFailed("input rejected") }
            session.addInput(newInput)
            input = newInput
        }

        guard session.canSetSessionPreset(resolution.preset) else {
            throw CameraError.configurationFailed("\(resolution.rawValue) not supported by the \(position.label.lowercased()) camera")
        }
        session.sessionPreset = resolution.preset

        if !session.outputs.contains(output) {
            output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarFullRange]
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(self, queue: videoQueue)
            guard session.canAddOutput(output) else { throw CameraError.configurationFailed("output rejected") }
            session.addOutput(output)
        }

        if let connection = output.connection(with: .video) {
            if connection.isVideoOrientationSupported {
                connection.videoOrientation = .landscapeRight
            }
            if connection.isVideoMirroringSupported {
                connection.automaticallyAdjustsVideoMirroring = false
                connection.isVideoMirrored = position == .front
            }
            mirrored = connection.isVideoMirrored
        }

        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }
        let duration = CMTime(value: 1, timescale: CMTimeScale(fps))
        let supported = device.activeFormat.videoSupportedFrameRateRanges.contains { $0.minFrameRate <= Double(fps) && Double(fps) <= $0.maxFrameRate }
        if supported {
            device.activeVideoMinFrameDuration = duration
            device.activeVideoMaxFrameDuration = duration
        } else {
            log.warning("\(fps) fps not supported by active format; using default")
        }
        if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
        if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
        if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) { device.whiteBalanceMode = .continuousAutoWhiteBalance }

        log.info("Configured \(position.rawValue) \(resolution.rawValue) @\(fps)")
    }

    // MARK: - AVCaptureVideoDataOutputSampleBufferDelegate (video queue)

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        onFrame?(sampleBuffer, mirrored)

        fpsCount += 1
        let now = MonoClockSeconds.now
        if now - fpsWindowStart >= 1 {
            let fps = Double(fpsCount) / (now - fpsWindowStart)
            fpsCount = 0
            fpsWindowStart = now
            DispatchQueue.main.async { self.onFps?(fps) }
        }
    }

    // MARK: - Notifications

    @objc private func interrupted(_ note: Notification) {
        log.warning("Capture interrupted")
        DispatchQueue.main.async { self.onInterruption?(true) }
    }

    @objc private func interruptionEnded(_ note: Notification) {
        log.info("Capture interruption ended")
        DispatchQueue.main.async { self.onInterruption?(false) }
    }

    /// After a tap-to-focus, go back to continuous AF/AE once the scene changes.
    @objc private func subjectAreaChanged(_ note: Notification) {
        sessionQueue.async {
            guard let device = self.input?.device, (note.object as? AVCaptureDevice) == device else { return }
            do {
                try device.lockForConfiguration()
                if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
                if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
                device.isSubjectAreaChangeMonitoringEnabled = false
                device.unlockForConfiguration()
            } catch {
                self.log.error("Reset focus failed: \(error.localizedDescription)")
            }
        }
    }

    @objc private func runtimeError(_ note: Notification) {
        let error = note.userInfo?[AVCaptureSessionErrorKey] as? AVError
        log.error("Capture runtime error: \(error?.localizedDescription ?? "unknown")")
        // Media services reset: restart the session, per Apple's guidance.
        if error?.code == .mediaServicesWereReset {
            sessionQueue.async { self.session.startRunning() }
        }
    }
}

enum MonoClockSeconds {
    static var now: Double { Double(clock_gettime_nsec_np(CLOCK_UPTIME_RAW)) / 1e9 }
}
