package net.silow.airlens.encoder

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class BitrateControllerTest {
    private val c = BitrateController(maxBps = 10_000_000, minBps = 2_000_000)

    @Test
    fun congestionCutsMultiplicativelyWithCooldown() {
        assertEquals(7_000_000, c.onCongestion(1000))
        assertNull("second cut inside 500 ms cooldown", c.onCongestion(1200))
        assertEquals(4_900_000, c.onCongestion(1600))
    }

    @Test
    fun neverBelowMinimum() {
        var t = 0L
        repeat(20) { c.onCongestion(t); t += 1000 }
        assertEquals(2_000_000, c.targetBps)
    }

    @Test
    fun recoversOnlyAfterQuietPeriodAndCapsAtMax() {
        c.onCongestion(0)
        assertNull("too soon after a cut", c.onCleanReport(1000))
        var t = 2000L
        repeat(50) { c.onCleanReport(t); t += 1000 }
        assertEquals(10_000_000, c.targetBps)
        assertNull("already at max", c.onCleanReport(t))
    }
}
