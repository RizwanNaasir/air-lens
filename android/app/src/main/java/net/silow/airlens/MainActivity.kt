package net.silow.airlens

import android.content.Intent
import android.os.Bundle
import android.os.PowerManager
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import net.silow.airlens.stream.StreamConfig
import net.silow.airlens.stream.StreamingService
import net.silow.airlens.stream.VideoCodec
import net.silow.airlens.ui.AirLensScreen
import net.silow.airlens.ui.AirLensTheme
import net.silow.airlens.util.DebugTuning

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            AirLensTheme {
                AirLensScreen(onStealthChanged = ::applyStealthWindow)
            }
        }
        handleDebugCommand(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleDebugCommand(intent)
    }

    /**
     * Debug builds only: drive streaming from ADB for on-device testing, e.g.
     *
     * ```
     * adb shell am start -n net.silow.airlens/.MainActivity --ez autostart true --es lens 3 --es codec HEVC --ei bitrate 0
     * adb shell am start -n net.silow.airlens/.MainActivity --ez autostop true
     * ```
     *
     * The service is started from here, while the activity is in front, which is what Android 11+
     * requires for camera access from a foreground service.
     */
    private fun handleDebugCommand(intent: Intent?) {
        if (!BuildConfig.DEBUG || intent == null) return
        if (intent.getBooleanExtra("autostop", false)) {
            StreamingService.stop(this)
            return
        }
        if (!intent.getBooleanExtra("autostart", false)) return
        intent.getStringExtra("template")?.let { DebugTuning.recordTemplate = it != "preview" }
        if (intent.hasExtra("oprate")) DebugTuning.operatingRate = intent.getIntExtra("oprate", 0).takeIf { it > 0 }
        val prefs = StreamConfig.prefs(this)
        var cfg = StreamConfig.load(prefs)
        intent.getStringExtra("lens")?.let { cfg = cfg.copy(lensKey = it) }
        intent.getStringExtra("codec")?.let { name -> VideoCodec.entries.firstOrNull { it.name == name }?.let { cfg = cfg.copy(codec = it) } }
        if (intent.hasExtra("bitrate")) cfg = cfg.copy(bitrateBps = intent.getIntExtra("bitrate", StreamConfig.BITRATE_MAX))
        if (intent.hasExtra("width")) cfg = cfg.copy(outputWidth = intent.getIntExtra("width", cfg.outputWidth))
        if (intent.hasExtra("height")) cfg = cfg.copy(outputHeight = intent.getIntExtra("height", cfg.outputHeight))
        if (intent.hasExtra("fps")) cfg = cfg.copy(fps = intent.getIntExtra("fps", cfg.fps))
        if (intent.hasExtra("direct")) cfg = cfg.copy(directMode = intent.getBooleanExtra("direct", false))
        cfg = cfg.normalized()
        cfg.save(prefs)
        StreamingService.start(this, cfg)
    }

    /**
     * Stealth mode: the activity stays in front with the screen on but black. That keeps the
     * low-latency Wi-Fi lock honoured and the CPU awake without a wakelock; on OLED panels the
     * black pixels are unlit, so the panel draws close to nothing. Sustained performance mode asks
     * the SoC for clocks it can hold indefinitely, avoiding throttle/boost oscillation on long streams.
     *
     * [dark] = false shows the controls at normal brightness while staying in stealth.
     */
    private fun applyStealthWindow(active: Boolean, dark: Boolean) {
        val w = window
        val insets = WindowCompat.getInsetsController(w, w.decorView)
        val supportsSustained = getSystemService(PowerManager::class.java).isSustainedPerformanceModeSupported
        if (active) {
            w.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            w.attributes = w.attributes.apply {
                screenBrightness = if (dark) {
                    WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_OFF
                } else {
                    WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                }
            }
            if (supportsSustained) w.setSustainedPerformanceMode(true)
            insets.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            insets.hide(WindowInsetsCompat.Type.systemBars())
        } else {
            w.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            w.attributes = w.attributes.apply { screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE }
            if (supportsSustained) w.setSustainedPerformanceMode(false)
            insets.show(WindowInsetsCompat.Type.systemBars())
        }
    }
}
