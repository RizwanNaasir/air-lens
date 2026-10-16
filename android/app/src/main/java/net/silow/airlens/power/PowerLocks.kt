package net.silow.airlens.power

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import net.silow.airlens.util.AirLog

/**
 * Wake and Wi-Fi locks for one stream.
 *
 * **Wakelock only in screen-off mode.** In stealth mode the activity keeps the screen on
 * (black, brightness 0), which keeps the CPU awake without a wakelock.
 *
 * **Rotation every 45 minutes.** EMUI's PowerGenie/HwPFWService (Huawei P30 and others)
 * force-stops apps holding one wakelock for more than ~60 minutes. A fresh lock is acquired
 * *before* the old one is released, so the CPU is never unprotected, and no single lock ever
 * looks "abnormal". Each lock also carries a 60-minute timeout as a last resort if the
 * process dies without releasing it.
 *
 * **Wi-Fi.** `FULL_LOW_LATENCY` (API 29) disables 802.11 power save, but only while the app is
 * foreground with the screen on, and only if the vendor HAL implements it. On API 29-33 the
 * older `FULL_HIGH_PERF` lock is held as well; whichever the HAL honours applies.
 */
class PowerLocks(context: Context) {
    private val appContext = context.applicationContext
    private val power = appContext.getSystemService(PowerManager::class.java)
    private val wifi = appContext.getSystemService(WifiManager::class.java)
    private val handler = Handler(Looper.getMainLooper())
    private var wakeLock: PowerManager.WakeLock? = null
    private val wifiLocks = mutableListOf<WifiManager.WifiLock>()
    private val renew = object : Runnable {
        override fun run() {
            acquireFreshWakeLock()
            handler.postDelayed(this, WAKELOCK_ROTATION_MS)
        }
    }

    val holdsWakeLock: Boolean get() = wakeLock?.isHeld == true

    /** Must be called on the main thread. Idempotent. */
    fun acquire(holdWakeLock: Boolean) {
        if (wifiLocks.isEmpty()) acquireWifiLocks()
        handler.removeCallbacks(renew)
        if (holdWakeLock) {
            acquireFreshWakeLock()
            handler.postDelayed(renew, WAKELOCK_ROTATION_MS)
        } else {
            releaseWakeLock()
        }
    }

    /** Releases everything. Safe to call repeatedly and from onDestroy. */
    fun release() {
        handler.removeCallbacks(renew)
        releaseWakeLock()
        wifiLocks.forEach { runCatching { if (it.isHeld) it.release() } }
        wifiLocks.clear()
    }

    private fun acquireFreshWakeLock() {
        val fresh = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKELOCK_TAG).apply {
            setReferenceCounted(false)
            acquire(WAKELOCK_TIMEOUT_MS)
        }
        val old = wakeLock
        wakeLock = fresh
        old?.let { runCatching { if (it.isHeld) it.release() } }
    }

    private fun releaseWakeLock() {
        wakeLock?.let { runCatching { if (it.isHeld) it.release() } }
        wakeLock = null
    }

    @Suppress("DEPRECATION")
    private fun acquireWifiLocks() {
        val modes = buildList {
            add(WifiManager.WIFI_MODE_FULL_LOW_LATENCY)
            if (Build.VERSION.SDK_INT < 34) add(WifiManager.WIFI_MODE_FULL_HIGH_PERF)
        }
        for (mode in modes) {
            runCatching {
                wifi.createWifiLock(mode, "AirLens:wifi-$mode").apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }.onSuccess { wifiLocks += it }
                .onFailure { AirLog.w(TAG, "Wi-Fi lock mode $mode unavailable: ${it.message}") }
        }
    }

    private companion object {
        const val TAG = "AirLens.Power"
        const val WAKELOCK_TAG = "AirLens:stream"
        const val WAKELOCK_ROTATION_MS = 45 * 60 * 1000L
        const val WAKELOCK_TIMEOUT_MS = 60 * 60 * 1000L
    }
}
