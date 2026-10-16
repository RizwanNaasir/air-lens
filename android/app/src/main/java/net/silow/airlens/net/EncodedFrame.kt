package net.silow.airlens.net

import net.silow.airlens.util.AirLog
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicInteger

/**
 * One encoded access unit, laid out exactly as it goes on the wire:
 *
 * ```
 * data: [ 20-byte header, written in place at send time ][ Annex-B payload ... ][ spare capacity ]
 * ```
 *
 * The encoder copies the payload in once, at offset [Protocol.HEADER_SIZE]; the socket writer fills
 * the header in place and sends header + payload in a single `write()`. The array grows to the
 * stream's high-water mark and is then reused for the rest of the stream, so steady-state
 * streaming allocates nothing.
 *
 * Ownership is single and explicit: whoever holds a frame either passes it on or calls [recycle].
 */
class EncodedFrame internal constructor(initialPayloadCapacity: Int, private val pool: EncodedFramePool?) {
    var data: ByteArray = ByteArray(Protocol.HEADER_SIZE + initialPayloadCapacity)
        private set

    /** Payload length in bytes, excluding the header prefix. */
    var size = 0
        private set

    var ptsUs = 0L
    var flags = 0

    /** Guards against double recycling, which would hand one array to two owners. */
    internal var inPool = false

    val isKeyframe: Boolean get() = flags and Protocol.FLAG_KEYFRAME != 0
    val isConfig: Boolean get() = flags and Protocol.FLAG_CODEC_CONFIG != 0

    /** Total bytes on the wire for this frame. */
    val wireSize: Int get() = Protocol.HEADER_SIZE + size

    internal fun reset() {
        size = 0
        ptsUs = 0
        flags = 0
    }

    /** Appends [length] bytes from [src]'s current position (used for partial MediaCodec buffers). */
    fun append(src: ByteBuffer, length: Int) {
        ensurePayloadCapacity(size + length)
        src.get(data, Protocol.HEADER_SIZE + size, length)
        size += length
    }

    fun append(src: ByteArray, offset: Int, length: Int) {
        ensurePayloadCapacity(size + length)
        System.arraycopy(src, offset, data, Protocol.HEADER_SIZE + size, length)
        size += length
    }

    private fun ensurePayloadCapacity(payload: Int) {
        val needed = Protocol.HEADER_SIZE + payload
        if (data.size >= needed) return
        // Next power of two, keeping existing bytes: a partial frame may already be half-copied.
        val grown = Integer.highestOneBit(needed - 1) shl 1
        data = data.copyOf(grown)
    }

    fun recycle() {
        if (pool != null) pool.release(this)
    }
}

/**
 * Bounded free-list of [EncodedFrame]s. [outstandingCount] is exported to the status screen, so a
 * leak (a frame that is never recycled) shows up as a number that only goes up.
 */
class EncodedFramePool(
    private val maxRetained: Int = 64,
    private val initialPayloadCapacity: Int = 128 * 1024,
) {
    private val free = ArrayDeque<EncodedFrame>()
    private val outstanding = AtomicInteger()

    val outstandingCount: Int get() = outstanding.get()
    val retainedCount: Int get() = synchronized(free) { free.size }

    fun acquire(): EncodedFrame {
        val reused = synchronized(free) { free.removeLastOrNull() }
        outstanding.incrementAndGet()
        val frame = reused ?: EncodedFrame(initialPayloadCapacity, this)
        frame.inPool = false
        frame.reset()
        return frame
    }

    internal fun release(frame: EncodedFrame) {
        synchronized(free) {
            if (frame.inPool) {
                AirLog.e(TAG, "EncodedFrame recycled twice; ignoring the second recycle", IllegalStateException())
                return
            }
            frame.inPool = true
            outstanding.decrementAndGet()
            // Beyond the cap, drop the frame and let GC take it: only happens after a burst.
            if (free.size < maxRetained) free.addLast(frame)
        }
    }

    private companion object {
        const val TAG = "AirLens.Pool"
    }
}
