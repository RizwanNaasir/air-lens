package net.silow.airlens.net

import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * The queue between the encoder thread (producer) and one session's socket writer (consumer).
 *
 * Policy, in order of precedence:
 * 1. **The producer never blocks.** [offer] is O(1) apart from overflow drops, so the camera and
 *    encoder keep their own pace whatever the network does.
 * 2. **Codec config is never dropped.** It is tiny, and without it nothing decodes.
 * 3. **After any drop, send nothing until a keyframe.** With no B-frames every P-frame references
 *    the previous one, so a hole would corrupt the picture until the next IDR anyway.
 * 4. **A keyframe obsoletes everything queued before it.** If deltas are still waiting when an IDR
 *    arrives, they are discarded for free: this is the "skip to keyframe" that drains a backlog.
 * 5. **Bounded by frames and by bytes.** Exceeding either budget drops every unsent picture and asks
 *    the encoder for an IDR ([onOverflow]). Latency is capped at roughly the byte budget's worth of
 *    airtime; memory is capped at the same amount.
 *
 * A keyframe larger than the whole byte budget is still sent: there is no smaller way to recover.
 */
class SendQueue(
    @Volatile var maxFrames: Int,
    @Volatile var maxBytes: Int,
    private val onOverflow: () -> Unit,
) {
    private val lock = ReentrantLock()
    private val notEmpty = lock.newCondition()
    private val frames = ArrayDeque<EncodedFrame>()
    private var queuedBytes = 0
    private var waitingForKeyframe = true // a new session must start on a keyframe
    private var pendingDiscontinuity = false
    private var kicked = false
    private var closed = false

    @Volatile var droppedFrames = 0L
        private set

    @Volatile var overflowCount = 0L
        private set

    val size: Int get() = lock.withLock { frames.size }
    val bytes: Int get() = lock.withLock { queuedBytes }

    /** Takes ownership of [frame]. Returns false if it was dropped (and already recycled). */
    fun offer(frame: EncodedFrame): Boolean {
        var overflowed = false
        val accepted = lock.withLock {
            if (closed) {
                frame.recycle()
                return false
            }
            if (frame.isConfig) {
                enqueueLocked(frame)
                return true
            }
            if (waitingForKeyframe) {
                if (!frame.isKeyframe) {
                    dropLocked(frame)
                    return false
                }
                waitingForKeyframe = false
            } else if (frame.isKeyframe && dropQueuedPicturesLocked() > 0) {
                pendingDiscontinuity = true
            }

            if (frames.size >= maxFrames || queuedBytes + frame.size > maxBytes) {
                overflowed = true
                overflowCount++
                pendingDiscontinuity = dropQueuedPicturesLocked() > 0 || pendingDiscontinuity
                if (!frame.isKeyframe) {
                    dropLocked(frame)
                    waitingForKeyframe = true
                    pendingDiscontinuity = true
                    false
                } else {
                    acceptPictureLocked(frame)
                    true
                }
            } else {
                acceptPictureLocked(frame)
                true
            }
        }
        // Outside the lock: the callback reaches into the encoder.
        if (overflowed) onOverflow()
        return accepted
    }

    /**
     * Waits up to [timeoutMs] for a frame. Returns null on timeout, on [kick], or once closed.
     * The caller owns the returned frame.
     */
    fun poll(timeoutMs: Long): EncodedFrame? = lock.withLock {
        var nanos = TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        while (frames.isEmpty() && !closed && !kicked) {
            if (nanos <= 0L) return null
            nanos = notEmpty.awaitNanos(nanos)
        }
        kicked = false
        if (closed) return null
        val frame = frames.removeFirstOrNull() ?: return null
        queuedBytes -= frame.size
        frame
    }

    /** Wakes a waiting [poll] early so the writer can send a control packet (e.g. PONG). */
    fun kick() = lock.withLock {
        kicked = true
        notEmpty.signalAll()
    }

    /** Drops everything, including config, and waits for a keyframe again. Used on encoder restarts. */
    fun reset() = lock.withLock {
        while (frames.isNotEmpty()) dropLocked(frames.removeFirst())
        queuedBytes = 0
        waitingForKeyframe = true
        pendingDiscontinuity = false
    }

    /** Recycles every queued frame; later offers are recycled immediately. Idempotent. */
    fun close() = lock.withLock {
        closed = true
        while (frames.isNotEmpty()) frames.removeFirst().recycle()
        queuedBytes = 0
        notEmpty.signalAll()
    }

    private fun acceptPictureLocked(frame: EncodedFrame) {
        if (pendingDiscontinuity) {
            frame.flags = frame.flags or Protocol.FLAG_DISCONTINUITY
            pendingDiscontinuity = false
        }
        enqueueLocked(frame)
    }

    private fun enqueueLocked(frame: EncodedFrame) {
        frames.addLast(frame)
        queuedBytes += frame.size
        notEmpty.signal()
    }

    /** Drops queued pictures but keeps codec config. Returns how many pictures were dropped. */
    private fun dropQueuedPicturesLocked(): Int {
        if (frames.isEmpty()) return 0
        var dropped = 0
        val it = frames.iterator()
        while (it.hasNext()) {
            val f = it.next()
            if (!f.isConfig) {
                it.remove()
                queuedBytes -= f.size
                dropLocked(f)
                dropped++
            }
        }
        return dropped
    }

    private fun dropLocked(frame: EncodedFrame) {
        if (!frame.isConfig) droppedFrames++
        frame.recycle()
    }
}
