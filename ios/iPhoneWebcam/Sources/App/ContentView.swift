import SwiftUI

/// Landscape layout: viewfinder on the left, one control panel on the right (mirrors the Windows app).
struct ContentView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(spacing: 16) {
            viewfinder
            panel
                .frame(width: 250)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(Color(.systemBackground))
    }

    // MARK: - Viewfinder

    private var viewfinder: some View {
        ZStack {
            Color.black
            if model.isRunning {
                CameraPreview(session: model.camera.session) { model.focus(at: $0) }
            } else {
                VStack(spacing: 6) {
                    Text("Camera off")
                        .font(.title2)
                        .foregroundStyle(.white)
                    Text("Start the camera, then connect from iPhone Webcam on your PC.")
                        .font(.subheadline)
                        .foregroundStyle(Color(white: 0.65))
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 300)
                }
            }
            if let code = model.pairingCode {
                PairingCodeCard(code: code)
            }
            ViewfinderCorners()
        }
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }

    // MARK: - Panel

    private var panel: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Circle()
                    .fill(tallyColor)
                    .frame(width: 12, height: 12)
                Text(statusTitle)
                    .font(.title2.weight(.medium))
            }
            Text(statusDetail)
                .font(.footnote)
                .foregroundStyle(.secondary)
                .padding(.leading, 22)
                .padding(.top, 2)
                .fixedSize(horizontal: false, vertical: true)

            Divider().padding(.vertical, 12)

            Picker("Camera", selection: $model.position) {
                ForEach(CameraPosition.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            Picker("Resolution", selection: $model.resolution) {
                ForEach(StreamResolution.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .padding(.top, 8)

            HStack {
                Text(model.captureFps > 0 ? String(format: "%.0f fps", model.captureFps) : "\(model.fps) fps")
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                Spacer()
                Button {
                    model.toggleTorch()
                } label: {
                    Image(systemName: model.torchOn ? "flashlight.on.fill" : "flashlight.off.fill")
                }
                .disabled(!model.isRunning || !model.hasTorch)
                .accessibilityLabel(model.torchOn ? "Turn torch off" : "Turn torch on")
            }
            .font(.subheadline)
            .padding(.top, 10)

            if let error = model.errorMessage {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .padding(.top, 10)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 8)

            if model.isRunning, let address = model.address {
                Text("Connect from your PC to \(address)")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .padding(.bottom, 8)
            }

            Button {
                model.isRunning ? model.stop() : model.start()
            } label: {
                Text(model.isRunning ? "Stop Camera" : "Start Camera")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
            }
            .buttonStyle(.borderedProminent)
            .tint(model.isRunning ? .gray : .accentColor)
        }
    }

    private var tallyColor: Color {
        if model.isStreaming && !model.isPaused, case .connected = model.link { return Color(red: 0.90, green: 0.20, blue: 0.18) }
        if model.isRunning { return Color(red: 0.91, green: 0.64, blue: 0.09) }
        return Color(white: 0.55)
    }

    private var statusTitle: String {
        switch model.link {
        case .idle: return "Not connected"
        case .waiting: return model.pairingCode != nil ? "Pairing" : "Waiting for PC"
        case .connected: return model.isPaused ? "Paused" : (model.isStreaming ? "Live" : "Connected")
        }
    }

    private var statusDetail: String {
        switch model.link {
        case .idle: return "Camera is off"
        case .waiting: return model.pairingCode != nil ? "Type the code on your PC" : "Open iPhone Webcam on your PC"
        case .connected(let pc): return "Wi-Fi · \(pc)"
        }
    }
}

/// Shown while a PC is pairing. Digits grouped 3+3 for reading aloud/typing.
private struct PairingCodeCard: View {
    let code: String

    var body: some View {
        VStack(spacing: 6) {
            Text("Pair with your PC")
                .font(.headline)
            Text("\(String(code.prefix(3))) \(String(code.suffix(3)))")
                .font(.system(size: 44, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .accessibilityLabel(code.map(String.init).joined(separator: " "))
            Text("Type this code in iPhone Webcam on the PC.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 20)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// Camera-viewfinder corner brackets.
private struct ViewfinderCorners: View {
    var body: some View {
        GeometryReader { geo in
            let inset: CGFloat = 12
            let arm: CGFloat = 20
            Path { p in
                let w = geo.size.width, h = geo.size.height
                let corners: [(x: CGFloat, y: CGFloat, dx: CGFloat, dy: CGFloat)] = [
                    (inset, inset, 1, 1), (w - inset, inset, -1, 1), (w - inset, h - inset, -1, -1), (inset, h - inset, 1, -1),
                ]
                for (x, y, dx, dy) in corners {
                    p.move(to: CGPoint(x: x, y: y + dy * arm))
                    p.addLine(to: CGPoint(x: x, y: y))
                    p.addLine(to: CGPoint(x: x + dx * arm, y: y))
                }
            }
            .stroke(Color.white.opacity(0.85), lineWidth: 2)
        }
        .allowsHitTesting(false)
    }
}
