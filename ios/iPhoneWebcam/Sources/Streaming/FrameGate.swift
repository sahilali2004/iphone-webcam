import Foundation

/// Coordinates the camera thread with the network (docs/protocol.md §5.1, latest-frame-wins):
/// at most one frame is being encoded or sent at a time; frames arriving meanwhile are skipped.
/// frameId advances for every captured frame, so the PC can count the skipped ones.
final class FrameGate {
    private let lock = NSLock()
    private var streaming = false
    private var busy = false
    private var frameId: UInt32 = 0
    private var _quality = 0.7
    private var _skipped: UInt64 = 0

    var quality: Double {
        get { lock.withLock { _quality } }
        set { lock.withLock { _quality = min(max(newValue, 0.1), 1.0) } }
    }

    var skipped: UInt64 { lock.withLock { _skipped } }

    func setStreaming(_ on: Bool) {
        lock.withLock { streaming = on }
    }

    /// Call once per captured frame. Returns the frame's id, and whether the caller should encode+send it.
    func capture() -> (id: UInt32, send: Bool) {
        lock.withLock {
            let id = frameId
            frameId &+= 1
            guard streaming else { return (id, false) }
            if busy {
                _skipped += 1
                return (id, false)
            }
            busy = true
            return (id, true)
        }
    }

    /// Must be called exactly once after a `capture()` that returned send == true.
    func finished() {
        lock.withLock { busy = false }
    }
}
