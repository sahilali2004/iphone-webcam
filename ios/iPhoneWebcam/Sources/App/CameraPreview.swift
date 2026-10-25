import AVFoundation
import SwiftUI

/// Live preview from the capture session (display only; frames for the PC come from the data output).
/// Tap to focus/expose at a point.
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    let onTap: (CGPoint) -> Void

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspect
        view.onTap = onTap
        return view
    }

    func updateUIView(_ view: PreviewView, context: Context) {
        view.onTap = onTap
    }

    final class PreviewView: UIView {
        var onTap: ((CGPoint) -> Void)?

        override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
        var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }

        override init(frame: CGRect) {
            super.init(frame: frame)
            backgroundColor = .black
            addGestureRecognizer(UITapGestureRecognizer(target: self, action: #selector(tapped(_:))))
        }

        required init?(coder: NSCoder) {
            fatalError("init(coder:) is not used")
        }

        override func layoutSubviews() {
            super.layoutSubviews()
            // UI is locked to landscape-right, the same orientation as the stream.
            if let connection = previewLayer.connection, connection.isVideoOrientationSupported {
                connection.videoOrientation = .landscapeRight
            }
        }

        @objc private func tapped(_ recognizer: UITapGestureRecognizer) {
            let point = recognizer.location(in: self)
            onTap?(previewLayer.captureDevicePointConverted(fromLayerPoint: point))
        }
    }
}
