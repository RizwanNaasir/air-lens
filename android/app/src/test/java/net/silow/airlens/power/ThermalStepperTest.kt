package net.silow.airlens.power

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ThermalStepperTest {
    private val s = ThermalStepper(hotHoldMs = 60_000, coolHoldMs = 300_000, maxSteps = 2)

    private fun run(level: ThermalLevel, fromMs: Long, toMs: Long): Long {
        var t = fromMs
        while (t <= toMs) {
            s.update(level, t)
            t += 1000
        }
        return t
    }

    @Test
    fun aShortHotSpellChangesNothing() {
        run(ThermalLevel.HOT, 0, 59_000)
        assertEquals(0, s.steps)
        run(ThermalLevel.WARM, 60_000, 200_000)
        assertEquals(0, s.steps)
    }

    @Test
    fun sustainedHeatStepsDownOncePerHoldAndStopsAtTheFloor() {
        run(ThermalLevel.HOT, 0, 60_000)
        assertEquals(1, s.steps)
        run(ThermalLevel.HOT, 61_000, 119_000)
        assertEquals(1, s.steps) // not before another full minute
        run(ThermalLevel.HOT, 120_000, 600_000)
        assertEquals(2, s.steps) // never below the lowest preset
    }

    @Test
    fun criticalReactsFaster() {
        run(ThermalLevel.CRITICAL, 0, 15_000)
        assertEquals(1, s.steps)
    }

    @Test
    fun warmHoldsTheStepAndOnlyLongCoolingStepsBackUp() {
        run(ThermalLevel.HOT, 0, 60_000)
        assertEquals(1, s.steps)
        run(ThermalLevel.WARM, 61_000, 1_000_000)
        assertEquals(1, s.steps) // warm is not cool enough
        run(ThermalLevel.NORMAL, 1_001_000, 1_299_000)
        assertEquals(1, s.steps)
        assertTrue(s.update(ThermalLevel.NORMAL, 1_301_000))
        assertEquals(0, s.steps)
        assertFalse(s.update(ThermalLevel.NORMAL, 1_302_000))
    }
}
