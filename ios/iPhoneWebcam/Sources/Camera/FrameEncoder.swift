import CoreImage
import CoreMedia
import ImageIO
import VideoToolbox
import os

/// Compresses one camera frame. Implementations call `completion` exactly once, on any queue.
protocol FrameEncoder: AnyObject {
    var quality: Double { get set }
    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime, completion: @escaping (Data?) -> Void)
}

/// Hardware JPEG via VideoToolbox. Falls back to Core Image if the session can't be created.
final class JpegEncoder: FrameEncoder {
    var quality: Double {
        didSet { if let session { applyQuality(session) } }
    }

    private let log = Logger(subsystem: "iPhoneWebcam", category: "Encoder")
    private var session: VTCompressionSession?
    private var sessionSize: (Int, Int) = (0, 0)
    private lazy var fallback = CoreImageJpegEncoder(quality: quality)
    private var usingFallback = false

    init(quality: Double) {
        self.quality = quality
    }

    deinit {
        if let session { VTCompressionSessionInvalidate(session) }
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime, completion: @escaping (Data?) -> Void) {
        let size = (CVPixelBufferGetWidth(pixelBuffer), CVPixelBufferGetHeight(pixelBuffer))
        if !usingFallback && (session == nil || sessionSize != size) {
            createSession(width: size.0, height: size.1)
        }
        guard !usingFallback, let session else {
            fallback.quality = quality
            fallback.encode(pixelBuffer, pts: pts, completion: completion)
            return
        }

        let status = VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
                                                     duration: .invalid, frameProperties: nil, infoFlagsOut: nil) { status, _, sample in
            guard status == noErr, let sample, let block = CMSampleBufferGetDataBuffer(sample) else {
                completion(nil)
                return
            }
            let length = CMBlockBufferGetDataLength(block)
            var data = Data(count: length)
            let copy = data.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: $0.baseAddress!) }
            completion(copy == kCMBlockBufferNoErr ? data : nil)
        }
        if status != noErr {
            log.error("VTCompressionSessionEncodeFrame failed: \(status)")
            completion(nil)
        }
    }

    private func createSession(width: Int, height: Int) {
        if let session { VTCompressionSessionInvalidate(session) }
        session = nil
        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_JPEG, encoderSpecification: nil,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &created)
        guard status == noErr, let created else {
            log.error("JPEG VTCompressionSession unavailable (\(status)); using Core Image fallback")
            usingFallback = true
            return
        }
        VTSessionSetProperty(created, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        applyQuality(created)
        VTCompressionSessionPrepareToEncodeFrames(created)
        session = created
        sessionSize = (width, height)
        log.info("JPEG encoder ready \(width)x\(height)")
    }

    private func applyQuality(_ session: VTCompressionSession) {
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_Quality, value: NSNumber(value: min(max(quality, 0.1), 1.0)))
    }
}

/// Software fallback: slower, but always available.
final class CoreImageJpegEncoder: FrameEncoder {
    var quality: Double
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!

    init(quality: Double) {
        self.quality = quality
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime, completion: @escaping (Data?) -> Void) {
        let options = [CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): quality]
        completion(context.jpegRepresentation(of: CIImage(cvPixelBuffer: pixelBuffer), colorSpace: colorSpace, options: options))
    }
}
