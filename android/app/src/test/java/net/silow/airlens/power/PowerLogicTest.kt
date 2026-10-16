package net.silow.airlens.power

import org.junit.Assert.assertEquals
import org.junit.Test

class PowerLogicTest {
    @Test
    fun batteryThresholdsWithHysteresis() {
        assertEquals(ThermalLevel.NORMAL, ThermalGovernor.levelForBattery(40.9f, ThermalLevel.NORMAL))
        assertEquals(ThermalLevel.WARM, ThermalGovernor.levelForBattery(41.0f, ThermalLevel.NORMAL))
        assertEquals(ThermalLevel.CRITICAL, ThermalGovernor.levelForBattery(47.5f, ThermalLevel.NORMAL))
        // Falling: stays WARM until 41 - 1.5 = 39.5 °C.
        assertEquals(ThermalLevel.WARM, ThermalGovernor.levelForBattery(40.0f, ThermalLevel.WARM))
        assertEquals(ThermalLevel.NORMAL, ThermalGovernor.levelForBattery(39.4f, ThermalLevel.WARM))
        // HOT falls back to WARM between 42.5 and 44.
        assertEquals(ThermalLevel.HOT, ThermalGovernor.levelForBattery(43.0f, ThermalLevel.HOT))
        assertEquals(ThermalLevel.WARM, ThermalGovernor.levelForBattery(42.0f, ThermalLevel.HOT))
    }

    @Test
    fun orientationSnapsWithDeadBand() {
        assertEquals(0, OrientationTracker.snap(10, 0))
        assertEquals(0, OrientationTracker.snap(50, 0)) // inside the dead band past 45°
        assertEquals(90, OrientationTracker.snap(60, 0))
        assertEquals(90, OrientationTracker.snap(40, 90)) // sticky near the boundary
        assertEquals(0, OrientationTracker.snap(30, 90))
        assertEquals(270, OrientationTracker.snap(290, 0))
        assertEquals(0, OrientationTracker.snap(350, 270))
        assertEquals(180, OrientationTracker.snap(180, 90))
    }
}
