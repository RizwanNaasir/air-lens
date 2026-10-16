package net.silow.airlens.util

import android.os.SystemClock

/**
 * Phone-side latency breakdown, logged every 3 s:
 *
 * - **sensor → GL**: camera timestamp (start of exposure) to the GL stage latching the buffer.
 *   This is exposure, ISP and the camera HAL's pipeline depth.
 * - **sensor → encoded**: camera timestamp to the encoder emitting the compressed frame. The
 *   difference between the two is the GL pass plus the hardware encoder's own delay.
 *
 * Camera timestamps use CLOCK_MONOTONIC or CLOCK_BOOTTIME depending on the device
 * (SENSOR_INFO_TIMESTAMP_SOURCE); the clock that gives a plausible age is used.
 */
object PipelineTiming {
    private const val TAG = "AirLens.Timing"
    private const val REPORT_MS = 3000L

    private class Window {
        var sum = 0.0
        var max = 0.0
        var count = 0

        fun add(ms: Double) {
            sum += ms
            if (ms > max) max = ms
            count++
        }

        fun text() = if (count == 0) "-" else "avg %.1f ms, max %.1f ms".format(sum / count, max)

        fun reset() {
            sum = 0.0
            max = 0.0
            count = 0
        }
    }

    private val gl = Window()
    private val encoded = Window()
    private var lastReport = SystemClock.elapsedRealtime()

    /** Age in ms of a camera timestamp, or -1 if no clock gives a plausible value. */
    fun ageMs(timestampNs: Long): Double {
        val mono = System.nanoTime() - timestampNs
        val boot = SystemClock.elapsedRealtimeNanos() - timestampNs
        val best = listOf(mono, boot).filter { it in 0..2_000_000_000L }.minOrNull() ?: return -1.0
        return best / 1e6
    }

    @Synchronized
    fun onGlLatched(cameraTimestampNs: Long) {
        val ms = ageMs(cameraTimestampNs)
        if (ms >= 0) gl.add(ms)
    }

    @Synchronized
    fun onEncoded(ptsUs: Long) {
        val ms = ageMs(ptsUs * 1000)
        if (ms >= 0) encoded.add(ms)
        val now = SystemClock.elapsedRealtime()
        if (now - lastReport >= REPORT_MS) {
            AirLog.i(TAG, "sensor->GL ${gl.text()} | sensor->encoded ${encoded.text()} (${encoded.count} frames)")
            gl.reset()
            encoded.reset()
            lastReport = now
        }
    }
}
