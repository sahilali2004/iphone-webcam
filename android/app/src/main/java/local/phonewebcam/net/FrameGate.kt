package local.phonewebcam.net

/**
 * Coordinates the camera thread with the network (docs/protocol.md §5.1, latest-frame-wins).
 * Up to [MAX_IN_FLIGHT] frames may be between "encode started" and "written to socket", so the next
 * frame can be encoded while the previous one is still being sent; anything beyond that is skipped.
 * frameId advances for every captured frame, so the PC can count the skipped ones.
 */
class FrameGate {
    companion object {
        const val MAX_IN_FLIGHT = 2
    }

    private var streaming = false
    private var inFlight = 0
    private var frameId = 0L

    @Volatile
    var quality = 0.7
        set(value) {
            field = value.coerceIn(0.1, 1.0)
        }

    @Synchronized
    fun setStreaming(on: Boolean) {
        streaming = on
    }

    /** Call once per captured frame. Returns the frame's id and whether to encode+send it. */
    @Synchronized
    fun capture(): Pair<Long, Boolean> {
        val id = frameId
        frameId = (frameId + 1) and 0xFFFFFFFFL
        if (!streaming || inFlight >= MAX_IN_FLIGHT) return id to false
        inFlight++
        return id to true
    }

    /** Must be called exactly once after a capture() that returned true. */
    @Synchronized
    fun finished() {
        inFlight--
    }
}
