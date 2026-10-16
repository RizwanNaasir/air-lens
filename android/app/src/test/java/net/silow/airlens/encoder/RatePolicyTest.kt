package net.silow.airlens.encoder

import net.silow.airlens.power.ThermalLevel
import org.junit.Assert.assertEquals
import org.junit.Test

class RatePolicyTest {
    // Steady AE rates the Huawei P30 reports: [15,15] [20,20] [24,24] [25,25] [30,30].
    private val p30Rates = listOf(15, 20, 24, 25, 30)
    private val max = 60_000_000 // H.264 4K30 at the HiSilicon encoder's ceiling

    private fun quality(link: Int, thermal: ThermalLevel = ThermalLevel.NORMAL) =
        RatePolicy.decide(RatePriority.QUALITY, 30, max, link, thermal, p30Rates)

    private fun smooth(link: Int, thermal: ThermalLevel = ThermalLevel.NORMAL) =
        RatePolicy.decide(RatePriority.SMOOTHNESS, 30, max, link, thermal, p30Rates)

    @Test
    fun healthyLinkGetsEverything() {
        assertEquals(RateDecision(30, max), quality(max))
        assertEquals(RateDecision(30, max), smooth(max))
    }

    @Test
    fun qualityModeSpendsCongestionAsFrameRateKeepingBitsPerFrame() {
        // 70% budget -> 21 fps worth of full-quality frames -> snaps to 20 fps at 2 Mbit/frame.
        assertEquals(RateDecision(20, 40_000_000), quality(42_000_000))
        // 49% -> 14.7 fps -> floor 15 fps; bits per frame finally drop (link-limited).
        assertEquals(RateDecision(15, 29_400_000), quality(29_400_000))
    }

    @Test
    fun smoothnessModeKeepsFrameRateAndCutsBitrate() {
        assertEquals(RateDecision(30, 42_000_000), smooth(42_000_000))
    }

    @Test
    fun thermalStepsLowerFrameRateFirstInQualityMode() {
        assertEquals(RateDecision(24, 48_000_000), quality(max, ThermalLevel.WARM))
        assertEquals(RateDecision(20, 40_000_000), quality(max, ThermalLevel.HOT))
        assertEquals(RateDecision(15, 30_000_000), quality(max, ThermalLevel.CRITICAL))
    }

    @Test
    fun thermalInSmoothnessModeCutsBitrateThenCapsFps() {
        assertEquals(RateDecision(30, 45_000_000), smooth(max, ThermalLevel.WARM))
        assertEquals(RateDecision(30, 30_000_000), smooth(max, ThermalLevel.HOT))
    }

    @Test
    fun snapNeverGoesBelowFloorOrAboveConfigured() {
        assertEquals(15, RatePolicy.snapDown(3.0, p30Rates, 30))
        assertEquals(30, RatePolicy.snapDown(99.0, p30Rates, 30))
        assertEquals(24, RatePolicy.snapDown(24.5, p30Rates, 30))
        // Configured 20 fps (e.g. telephoto at 4K): 24/25/30 are not candidates.
        assertEquals(20, RatePolicy.snapDown(30.0, p30Rates, 20))
    }
}
