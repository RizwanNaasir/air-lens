package net.silow.airlens.power

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import net.silow.airlens.util.AirLog

enum class ThermalLevel(
    /** Ceiling on the configured bitrate. */
    val bitrateFactor: Double,
    /** Ceiling on the frame rate, or null for no cap. */
    val fpsCap: Int?,
) {
    NORMAL(1.0, null),
    WARM(0.75, null),
    HOT(0.5, 30),
    CRITICAL(0.35, 24),
}

/**
 * Combines two heat signals and reports the hotter one:
 *
 * 1. `PowerManager` thermal status (API 29), when the device's thermal HAL reports it.
 * 2. **Battery temperature** from the sticky `ACTION_BATTERY_CHANGED` broadcast (no permission,
 *    polled every 15 s). This is the fallback for HALs that always report NONE, which Kirin
 *    devices such as the Huawei P30 may do. Thresholds 41 / 44 / 47 °C with 1.5 °C hysteresis.
 *
 * The listener runs on the main thread and only fires on changes.
 */
class ThermalGovernor(context: Context, private val onChange: (ThermalLevel) -> Unit) {
    private val appContext = context.applicationContext
    private val power = appContext.getSystemService(PowerManager::class.java)
    private val handler = Handler(Looper.getMainLooper())
    private var statusLevel = ThermalLevel.NORMAL
    private var batteryLevel = ThermalLevel.NORMAL
    private var reported = ThermalLevel.NORMAL
    private var running = false

    @Volatile var batteryTempC: Float = Float.NaN
        private set

    val level: ThermalLevel get() = reported

    private val statusListener = PowerManager.OnThermalStatusChangedListener { status ->
        statusLevel = when (status) {
            PowerManager.THERMAL_STATUS_NONE, PowerManager.THERMAL_STATUS_LIGHT -> ThermalLevel.NORMAL
            PowerManager.THERMAL_STATUS_MODERATE -> ThermalLevel.WARM
            PowerManager.THERMAL_STATUS_SEVERE -> ThermalLevel.HOT
            else -> ThermalLevel.CRITICAL
        }
        publish()
    }

    private val poll = object : Runnable {
        override fun run() {
            readBattery()
            handler.postDelayed(this, POLL_MS)
        }
    }

    fun start() {
        if (running) return
        running = true
        runCatching { power.addThermalStatusListener(handler::post, statusListener) }
            .onFailure { AirLog.w(TAG, "thermal status API unavailable: ${it.message}") }
        handler.post(poll)
    }

    fun stop() {
        if (!running) return
        running = false
        runCatching { power.removeThermalStatusListener(statusListener) }
        handler.removeCallbacks(poll)
    }

    private fun readBattery() {
        val intent: Intent = appContext.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return
        val tenths = intent.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE)
        if (tenths == Int.MIN_VALUE) return
        val c = tenths / 10f
        batteryTempC = c
        batteryLevel = levelForBattery(c, batteryLevel)
        publish()
    }

    private fun publish() {
        val next = maxOf(statusLevel, batteryLevel)
        if (next != reported) {
            AirLog.i(TAG, "thermal $reported -> $next (status=$statusLevel, battery=$batteryLevel @ $batteryTempC °C)")
            reported = next
            onChange(next)
        }
    }

    companion object {
        private const val TAG = "AirLens.Thermal"
        private const val POLL_MS = 15_000L
        private const val HYSTERESIS_C = 1.5f

        /** Pure step function with hysteresis: rising uses the threshold, falling needs 1.5 °C less. */
        fun levelForBattery(tempC: Float, current: ThermalLevel): ThermalLevel {
            val thresholds = listOf(ThermalLevel.WARM to 41f, ThermalLevel.HOT to 44f, ThermalLevel.CRITICAL to 47f)
            var level = ThermalLevel.NORMAL
            for ((candidate, threshold) in thresholds) {
                val effective = if (candidate <= current) threshold - HYSTERESIS_C else threshold
                if (tempC >= effective) level = candidate
            }
            return level
        }
    }
}
