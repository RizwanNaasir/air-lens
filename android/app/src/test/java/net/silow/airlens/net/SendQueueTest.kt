package net.silow.airlens.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SendQueueTest {
    private val pool = EncodedFramePool()
    private var overflows = 0
    private val queue = SendQueue(maxFrames = 4, maxBytes = 1000) { overflows++ }

    private fun frame(size: Int, flags: Int = 0, pts: Long = 0): EncodedFrame =
        pool.acquire().also {
            it.append(ByteArray(size), 0, size)
            it.flags = flags
            it.ptsUs = pts
        }

    private fun key(size: Int = 100, pts: Long = 0) = frame(size, Protocol.FLAG_KEYFRAME, pts)
    private fun delta(size: Int = 100, pts: Long = 0) = frame(size, 0, pts)
    private fun config() = frame(20, Protocol.FLAG_CODEC_CONFIG)

    private fun drain(): List<EncodedFrame> = buildList {
        while (true) add(queue.poll(0) ?: break)
    }

    @Test
    fun newSessionDropsDeltasUntilFirstKeyframe() {
        assertFalse(queue.offer(delta()))
        assertFalse(queue.offer(delta()))
        assertTrue(queue.offer(key()))
        assertTrue(queue.offer(delta()))
        assertEquals(2, queue.size)
        assertEquals(2L, queue.droppedFrames)
        assertEquals(0, overflows) // start-up drops are not congestion
    }

    @Test
    fun configIsNeverDroppedEvenWhileWaitingForKeyframe() {
        assertTrue(queue.offer(config()))
        assertFalse(queue.offer(delta()))
        val out = drain()
        assertEquals(1, out.size)
        assertTrue(out[0].isConfig)
        out.forEach { it.recycle() }
    }

    @Test
    fun overflowDropsEverythingUnsentAndWaitsForKeyframe() {
        queue.offer(key(pts = 1))
        queue.offer(delta(pts = 2))
        queue.offer(delta(pts = 3))
        queue.offer(delta(pts = 4))
        // Fifth picture exceeds maxFrames = 4.
        assertFalse(queue.offer(delta(pts = 5)))
        assertEquals(1, overflows)
        assertEquals(0, queue.size)
        // Deltas are refused until a keyframe; the keyframe carries DISCONTINUITY.
        assertFalse(queue.offer(delta(pts = 6)))
        assertTrue(queue.offer(key(pts = 7)))
        val out = drain()
        assertEquals(listOf(7L), out.map { it.ptsUs })
        assertTrue(out[0].flags and Protocol.FLAG_DISCONTINUITY != 0)
        out.forEach { it.recycle() }
    }

    @Test
    fun byteBudgetTriggersOverflow() {
        queue.offer(key(size = 600))
        assertFalse(queue.offer(delta(size = 500)))
        assertEquals(1, overflows)
    }

    @Test
    fun oversizedKeyframeIsStillSent() {
        queue.offer(key(size = 100))
        assertTrue(queue.offer(key(size = 5000)))
        val out = drain()
        assertEquals(1, out.size)
        assertEquals(5000, out[0].size)
        out.forEach { it.recycle() }
    }

    @Test
    fun keyframeSkipsQueuedDeltasButKeepsConfig() {
        queue.offer(key(pts = 1))
        queue.offer(delta(pts = 2))
        queue.offer(config())
        queue.offer(delta(pts = 3))
        assertTrue(queue.offer(key(pts = 4)))
        val out = drain()
        assertEquals(2, out.size)
        assertTrue(out[0].isConfig)
        assertEquals(4L, out[1].ptsUs)
        assertTrue(out[1].flags and Protocol.FLAG_DISCONTINUITY != 0)
        assertEquals(0, overflows)
        out.forEach { it.recycle() }
    }

    @Test
    fun closeRecyclesEverythingAndLaterOffersAreRecycled() {
        queue.offer(key())
        queue.offer(delta())
        queue.close()
        assertFalse(queue.offer(key()))
        assertNull(queue.poll(0))
        assertEquals(0, pool.outstandingCount)
    }

    @Test
    fun kickWakesPollWithoutLosingFrames() {
        queue.kick()
        assertNull(queue.poll(1000)) // returns immediately instead of waiting a second
        queue.offer(key())
        queue.kick()
        val f = queue.poll(1000)
        assertTrue(f != null && f.isKeyframe)
        f!!.recycle()
    }

    @Test
    fun noLeaksAcrossLongRandomRun() {
        val rnd = java.util.Random(1)
        repeat(20_000) { i ->
            val f = if (i % 60 == 0) key(rnd.nextInt(800)) else delta(rnd.nextInt(300))
            queue.offer(f)
            if (rnd.nextInt(3) != 0) queue.poll(0)?.recycle()
        }
        drain().forEach { it.recycle() }
        assertEquals(0, pool.outstandingCount)
    }
}
