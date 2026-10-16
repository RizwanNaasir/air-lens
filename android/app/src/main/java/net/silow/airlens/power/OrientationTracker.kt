package net.silow.airlens.power

import android.content.Context
import android.hardware.SensorManager
import android.view.OrientationEventListener

/**
 * Reports the device's clockwise rotation from its natural orientation, snapped to 0/90/180/270.
 *
 * Uses the accelerometer rather than display rotation: the display may be off (screen-off mode) or
 * locked to portrait, but the picture should still follow how the phone is mounted. A 20° dead band
 * around each 45° boundary stops flapping when the phone sits at an angle; a phone lying flat
 * (orientation unknown) keeps the last rotation.
 */
class OrientationTracker(context: Context, private val onRotation: (Int) -> Unit) {
    var rotation = 0
        private set

    private val listener = object : OrientationEventListener(context.applicationContext, SensorManager.SENSOR_DELAY_NORMAL) {
        override fun onOrientationChanged(orientation: Int) {
            if (orientation == ORIENTATION_UNKNOWN) return
            val next = snap(orientation, rotation)
            if (next != rotation) {
                rotation = next
                onRotation(next)
            }
        }
    }

    fun start() {
        if (listener.canDetectOrientation()) listener.enable()
    }

    fun stop() = listener.disable()

    companion object {
        private const val DEAD_BAND = 20

        fun snap(orientation: Int, current: Int): Int {
            // Distance from the current quadrant's centre, wrapped to [-180, 180).
            val delta = ((orientation - current + 540) % 360) - 180
            if (kotlin.math.abs(delta) <= 45 + DEAD_BAND / 2) return current
            return (((orientation + 45) / 90) % 4) * 90
        }
    }
}
