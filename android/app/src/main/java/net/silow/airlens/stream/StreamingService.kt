package net.silow.airlens.stream

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.HandlerThread
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.SystemClock
import net.silow.airlens.util.AirLog
import net.silow.airlens.util.DeviceId
import net.silow.airlens.util.DeviceName
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import net.silow.airlens.AirLensApp
import net.silow.airlens.MainActivity
import net.silow.airlens.R
import net.silow.airlens.camera.CameraCapabilities
import net.silow.airlens.camera.LensCatalog
import net.silow.airlens.camera.LensOption
import net.silow.airlens.encoder.BitrateController
import net.silow.airlens.encoder.EncoderSelector
import net.silow.airlens.encoder.RatePolicy
import net.silow.airlens.net.ControlRequest
import net.silow.airlens.net.DeviceState
import net.silow.airlens.net.DiscoveryResponder
import net.silow.airlens.net.EncodedFramePool
import net.silow.airlens.net.ReceiverReport
import net.silow.airlens.net.StreamServer
import net.silow.airlens.power.OrientationTracker
import net.silow.airlens.power.PowerLocks
import net.silow.airlens.power.ThermalGovernor
import net.silow.airlens.power.ThermalLevel
import net.silow.airlens.power.ThermalStepper
import net.silow.airlens.util.runSync
import java.io.IOException
import java.net.Inet4Address
import java.net.NetworkInterface
import kotlin.math.abs

/**
 * Foreground service (type `camera`) that owns the stream for as long as the user wants it.
 *
 * Threads: lifecycle callbacks arrive on main and are forwarded to one **control thread**, which
 * is the only thread that creates, switches or closes pipelines. That keeps every state transition
 * serial without locks. Network callbacks that only nudge the encoder (keyframe requests) go
 * straight to the pipeline, whose methods only post to its own threads.
 *
 * Recovery: a pipeline error (camera taken by another app, encoder error, GL failure) closes that
 * pipeline and rebuilds it with exponential backoff (1 s → 10 s). The [StreamServer] stays up the
 * whole time, so the PC remains connected and simply sees a new stream epoch.
 */
class StreamingService : Service() {
    private val controlThread = HandlerThread("AirLens-Control").apply { start() }
    private val control = Handler(controlThread.looper)
    private val pool = EncodedFramePool()

    private var server: StreamServer? = null
    private var discovery: DiscoveryResponder? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    @Volatile private var pipeline: StreamPipeline? = null
    private var config: StreamConfig? = null
    private var bitrate: BitrateController? = null
    private var epoch = 0
    private var restartAttempts = 0
    private var lastReportDrops = -1L
    @Volatile private var thermalLevel = ThermalLevel.NORMAL
    private var lensOptions: List<LensOption>? = null
    private var activeLensKey: String? = null
    /** Presets of the active lens, as last sent to the PC. */
    private var presets: List<QualityPreset> = emptyList()
    /** Resolution steps taken because of heat; the saved settings never change for it. */
    private val stepper = ThermalStepper()
    /** What the running pipeline was built with (the saved settings, or a heat step below them). */
    private var runningConfig: StreamConfig? = null
    /** Manual focus 0 (far) .. 1 (near), or null for autofocus. Reset on lens switch. */
    private var manualFocus: Float? = null
    private var lastHeatTempC = Float.NaN
    private var lastHeatLevel = ThermalLevel.NORMAL

    private lateinit var powerLocks: PowerLocks
    private lateinit var thermal: ThermalGovernor
    private lateinit var orientation: OrientationTracker

    override fun onCreate() {
        super.onCreate()
        powerLocks = PowerLocks(this)
        thermal = ThermalGovernor(this) { level -> control.post { applyThermal(level) } }
        orientation = OrientationTracker(this) { rotation -> pipeline?.setDeviceRotation(rotation) }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                val cfg = StreamConfig.fromBundle(intent.extras ?: android.os.Bundle())
                if (!enterForeground()) {
                    stopSelf()
                    return START_NOT_STICKY
                }
                powerLocks.acquire(holdWakeLock = cfg.powerMode == PowerMode.SCREEN_OFF)
                thermal.start()
                orientation.start()
                control.post { startStreaming(cfg) }
            }
            ACTION_SWITCH_LENS -> {
                val key = intent.getStringExtra(EXTRA_LENS_KEY)
                control.post { switchLens(key) }
            }
            ACTION_SET_FOCUS -> {
                val value = intent.getFloatExtra(EXTRA_FOCUS, -1f).takeIf { it >= 0f }
                val lock = intent.getBooleanExtra(EXTRA_FOCUS_LOCK, false)
                control.post { setFocus(if (lock) lockedFocus() else value) }
            }
            ACTION_STOP -> shutdown()
            else -> if (pipeline == null) stopSelf() // restarted by the system without a request
        }
        // Never sticky: a camera service restarted from the background would be denied the camera.
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        control.runSync(SHUTDOWN_TIMEOUT_MS) { stopStreaming() }
        controlThread.quitSafely()
        orientation.stop()
        thermal.stop()
        powerLocks.release()
        super.onDestroy()
    }

    private fun shutdown() {
        control.runSync(SHUTDOWN_TIMEOUT_MS) { stopStreaming() }
        orientation.stop()
        thermal.stop()
        powerLocks.release()
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ---- control thread ------------------------------------------------------------------

    private fun startStreaming(cfg: StreamConfig) {
        config = cfg
        if (!ensureServer(cfg.port)) return
        restartAttempts = 0
        control.removeCallbacks(restartRunnable)
        rebuildPipeline()
        control.removeCallbacks(statsTicker)
        control.post(statsTicker)
    }

    private fun ensureServer(port: Int): Boolean {
        server?.let { if (it.boundPort == port) return true }
        server?.stop()
        server = null
        stopDiscovery()
        return try {
            server = StreamServer(port, pool, serverListener).also { it.start() }
            startDiscovery(port)
            true
        } catch (e: IOException) {
            publish { it.copy(state = StreamState.ERROR, message = "Port $port unavailable: ${e.message}") }
            false
        }
    }

    /**
     * Lets the PC find this phone on Wi-Fi (docs/PROTOCOL.md, "Discovery"). Optional: streaming
     * works without it, so a failure is only logged. The multicast lock keeps phones that filter
     * broadcast frames in power save from dropping the PC's query.
     */
    private fun startDiscovery(port: Int) {
        val responder = DiscoveryResponder(port) {
            DiscoveryResponder.announcePacket(DeviceId.get(this), DeviceName.get(this), port, server?.activeSession != null)
        }
        try {
            responder.start()
            discovery = responder
            multicastLock = getSystemService(WifiManager::class.java)?.createMulticastLock("AirLens discovery")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: IOException) {
            AirLog.w(TAG, "discovery unavailable on UDP $port: ${e.message}")
        }
    }

    private fun stopDiscovery() {
        discovery?.close()
        discovery = null
        multicastLock?.let { if (it.isHeld) it.release() }
        multicastLock = null
    }

    private fun rebuildPipeline() {
        pipeline?.close()
        pipeline = null
        val cfg = config ?: return
        val srv = server ?: return
        val lens = LensCatalog.find(this, cfg.lensKey)
        if (lens == null) {
            publish { it.copy(state = StreamState.ERROR, message = "No usable camera found") }
            return
        }
        presets = QualityPresets.forModes(CameraCapabilities.videoModes(lens.descriptor))
        val run = effectiveConfig(cfg)
        runningConfig = run
        epoch++
        val p = StreamPipeline(this, run, lens, pool, srv, epoch, pipelineListener)
        p.initialFocus(manualFocus)
        pipeline = p
        publish {
            it.copy(
                state = if (restartAttempts == 0) StreamState.STARTING else StreamState.RECOVERING,
                lensKey = lens.key,
                lensLabel = lens.label,
                width = run.outputWidth,
                height = run.outputHeight,
                fps = run.fps,
                port = cfg.port,
                powerMode = cfg.powerMode,
                priority = cfg.priority,
                focus = manualFocus,
                manualFocusSupported = lens.descriptor.supportsManualFocus,
                heatLowered = heatLabel(cfg, run),
            )
        }
        publishDeviceState()
        try {
            p.start()
            p.setDeviceRotation(orientation.rotation)
            // A fresh link estimate per pipeline: the new stream may have a very different bitrate.
            bitrate = BitrateController(p.configuredBitrateBps)
            applyRate()
            publish { it.copy(codecName = p.encoderChoice?.codecName, maxBitrateBps = p.configuredBitrateBps) }
        } catch (e: Exception) {
            AirLog.e(TAG, "pipeline start failed", e)
            onPipelineFailure(p, e.message ?: e.toString())
        }
    }

    /**
     * Keeps the quality the user picked across lenses: "Best picture" on the telephoto is its own
     * best mode (4K20 on the P30), not the main lens's 4K30. A lens without that preset falls back
     * to its best one. Settings changed in Advanced ("custom") are left as they are.
     */
    private fun switchLens(key: String?) {
        val cfg = config ?: return
        val lens = LensCatalog.find(this, key) ?: return
        // Focus distances do not carry over between lenses.
        manualFocus = null
        pipeline?.setFocus(null)
        val previousQuality = QualityPresets.matching(cfg, presets)
        var updated = cfg.copy(lensKey = lens.key)
        val carried = if (previousQuality == QualityPresets.CUSTOM) null else {
            val next = QualityPresets.forModes(CameraCapabilities.videoModes(lens.descriptor))
            next.firstOrNull { it.id == previousQuality } ?: next.firstOrNull()
        }
        val restart = carried != null && !carried.matches(updated)
        if (restart) updated = carried.applyTo(updated).normalized()
        config = updated
        updated.save(StreamConfig.prefs(this))
        publish {
            it.copy(
                lensKey = lens.key,
                lensLabel = lens.label,
                configRevision = it.configRevision + 1,
                focus = null,
                manualFocusSupported = lens.descriptor.supportsManualFocus,
            )
        }
        val p = pipeline
        if (restart || p == null || !p.switchLens(lens)) {
            rebuildPipeline()
        } else {
            applyRate()
            publishDeviceState()
        }
    }

    /** A lens, quality, focus or virtual-camera request from the PC. Always answered with a DEVICE_STATE. */
    private fun handleControl(request: ControlRequest) {
        if (config == null) return
        request.virtualCamera?.let { on -> publish { it.copy(pcVirtualCamera = on) } }
        when {
            request.focusLock -> setFocus(lockedFocus(), answer = false)
            request.focusAuto -> setFocus(null, answer = false)
            request.focus != null -> setFocus(request.focus, answer = false)
        }
        request.lens?.let { if (it != activeLensKey) switchLens(it) }
        val preset = request.quality?.let { id -> presets.firstOrNull { it.id == id } }
        val cfg = config ?: return
        if (preset != null && !preset.matches(cfg)) {
            val updated = preset.applyTo(cfg).normalized()
            config = updated
            updated.save(StreamConfig.prefs(this))
            publish { it.copy(configRevision = it.configRevision + 1) }
            AirLog.i(TAG, "PC chose quality ${preset.id}: ${preset.width}x${preset.height}@${preset.fps} ${preset.codec}")
            rebuildPipeline() // publishes the new device state
        } else {
            publishDeviceState()
        }
    }

    /** Fixed focus where autofocus has the lens now, so turning autofocus off keeps a sharp picture. */
    private fun lockedFocus(): Float = pipeline?.autofocusPosition() ?: manualFocus ?: 0.5f

    /** Manual focus (0 far .. 1 near) or autofocus (null), from the PC or the phone's own settings. */
    private fun setFocus(value: Float?, answer: Boolean = true) {
        manualFocus = value
        pipeline?.setFocus(value)
        publish { it.copy(focus = value) }
        if (answer) publishDeviceState()
    }

    /**
     * The settings the stream actually runs with: the saved ones, or while the phone is hot, a
     * lower preset ([ThermalStepper]). Heat never changes the saved settings, so the stream goes
     * back to the chosen quality once the phone has cooled down.
     */
    private fun effectiveConfig(cfg: StreamConfig): StreamConfig {
        val steps = stepper.steps
        if (steps == 0 || presets.isEmpty()) return cfg
        val chosen = presets.indexOfFirst { it.matches(cfg) }
        val target = ((if (chosen >= 0) chosen else 0) + steps).coerceAtMost(presets.lastIndex)
        return if (target == chosen) cfg else presets[target].applyTo(cfg).normalized()
    }

    private fun heatLabel(cfg: StreamConfig, run: StreamConfig): String? =
        if (run == cfg) null else presets.firstOrNull { it.matches(run) }?.label

    /** The stepper moved: rebuild only if that changes what the stream runs with. */
    private fun onHeatStep() {
        val cfg = config ?: return
        val run = effectiveConfig(cfg)
        AirLog.i(TAG, "heat: ${stepper.steps} step(s) below the chosen quality at ${thermal.batteryTempC} °C ($thermalLevel)")
        if (run == runningConfig) return
        rebuildPipeline()
    }

    /**
     * Sends the PC what it may change: the lenses, the active lens's quality presets and which of
     * them is running (DEVICE_STATE). Runs on the control thread after every lens or settings change.
     */
    private fun publishDeviceState() {
        val srv = server ?: return
        val cfg = config ?: return
        val all = lensOptions ?: LensCatalog.lenses(this).also { lensOptions = it }
        val lens = all.firstOrNull { it.key == cfg.lensKey } ?: LensCatalog.defaultLens(all) ?: return
        activeLensKey = lens.key
        presets = QualityPresets.forModes(CameraCapabilities.videoModes(lens.descriptor))
        val state = DeviceState(
            device = deviceName(),
            id = DeviceId.get(this),
            lensKey = lens.key,
            lenses = all.map { DeviceState.Lens(it.key, it.label, it.kind.name.lowercase()) },
            quality = QualityPresets.matching(effectiveConfig(cfg), presets),
            presets = presets.map { p ->
                val ceiling = EncoderSelector.select(p.codec, p.width, p.height, p.fps)?.maxBitrateBps ?: 0
                DeviceState.Preset(
                    id = p.id,
                    label = p.label,
                    tag = p.tag,
                    width = p.width,
                    height = p.height,
                    fps = p.fps,
                    codec = if (p.codec == VideoCodec.HEVC) "hevc" else "h264",
                    bitrateBps = p.applyTo(cfg).resolveBitrate(ceiling),
                    direct = p.direct,
                )
            },
            focus = DeviceState.Focus(lens.descriptor.supportsManualFocus, manualFocus),
            heat = DeviceState.Heat(thermal.batteryTempC, thermalLevel.name.lowercase(), heatLabel(cfg, effectiveConfig(cfg))),
        )
        lastHeatTempC = thermal.batteryTempC
        lastHeatLevel = thermalLevel
        srv.updateDeviceState(state.toJson())
    }

    private fun deviceName(): String = DeviceName.get(this)

    /**
     * Single place where the link budget, the thermal level and the user's priority become an
     * (fps, bitrate) target for the pipeline. See [RatePolicy].
     */
    private fun applyRate() {
        val p = pipeline ?: return
        val cfg = config ?: return
        val decision = RatePolicy.decide(
            priority = cfg.priority,
            configuredFps = p.configuredFps,
            configuredBitrateBps = p.configuredBitrateBps,
            linkBudgetBps = bitrate?.targetBps ?: p.configuredBitrateBps,
            thermal = thermalLevel,
            steadyFps = p.steadyFps,
        )
        p.applyRate(decision)
        publish { it.copy(fps = decision.fps, bitrateBps = decision.bitrateBps) }
    }

    private fun onPipelineFailure(p: StreamPipeline, reason: String) {
        if (p !== pipeline) return // a stale pipeline's late error
        p.close()
        pipeline = null
        restartAttempts++
        val delay = minOf(MAX_BACKOFF_MS, 1000L shl minOf(restartAttempts - 1, 4))
        AirLog.w(TAG, "pipeline failed ($reason); retry #$restartAttempts in $delay ms")
        publish { it.copy(state = StreamState.RECOVERING, message = "$reason. Retrying in ${delay / 1000} s") }
        control.removeCallbacks(restartRunnable)
        control.postDelayed(restartRunnable, delay)
    }

    private val restartRunnable = Runnable { rebuildPipeline() }

    private fun stopStreaming() {
        control.removeCallbacks(restartRunnable)
        control.removeCallbacks(statsTicker)
        pipeline?.close()
        pipeline = null
        server?.stop()
        server = null
        stopDiscovery()
        config = null
        runningConfig = null
        manualFocus = null
        stepper.reset()
        publish { StreamStatus(state = StreamState.IDLE) }
    }

    private fun applyThermal(level: ThermalLevel) {
        thermalLevel = level
        publish { it.copy(thermal = level) }
        applyRate()
    }

    private val statsTicker = object : Runnable {
        private var ticks = 0
        override fun run() {
            val p = pipeline
            val session = server?.activeSession
            publish {
                it.copy(
                    framesEncoded = p?.framesEncoded ?: 0,
                    framesSent = session?.framesSent ?: 0,
                    framesDropped = session?.queue?.droppedFrames ?: 0,
                    glFramesSkipped = p?.glFramesSkipped ?: 0,
                    encoderFeatures = p?.encoderFeatures.orEmpty(),
                    poolOutstanding = pool.outstandingCount,
                    batteryTempC = thermal.batteryTempC,
                    wakeLockHeld = powerLocks.holdsWakeLock,
                    addresses = if (ticks % 5 == 0) localAddresses() else it.addresses,
                )
            }
            ticks++
            if (stepper.update(thermalLevel, SystemClock.elapsedRealtime())) onHeatStep()
            // Keep the PC's temperature readout current (the battery is read every 15 s).
            val temp = thermal.batteryTempC
            val tempMoved = !temp.isNaN() && (lastHeatTempC.isNaN() || abs(temp - lastHeatTempC) >= 0.5f)
            if (tempMoved || thermalLevel != lastHeatLevel) publishDeviceState()
            control.postDelayed(this, 1000)
        }
    }

    // ---- callbacks from other threads ------------------------------------------------------

    private val pipelineListener = object : StreamPipeline.Listener {
        override fun onPipelineStarted(pipeline: StreamPipeline) {
            control.post {
                if (pipeline !== this@StreamingService.pipeline) return@post
                publish { it.copy(state = StreamState.STREAMING, message = null) }
                // Only call it stable after 10 s; a camera that dies right away keeps backing off.
                control.postDelayed({ if (pipeline === this@StreamingService.pipeline) restartAttempts = 0 }, 10_000)
            }
        }

        override fun onPipelineError(pipeline: StreamPipeline, reason: String) {
            control.post { onPipelineFailure(pipeline, reason) }
        }
    }

    private val serverListener = object : StreamServer.Listener {
        override fun onClientConnected(remoteAddress: String) {
            publish { it.copy(clientAddress = remoteAddress, clientSinceMs = SystemClock.elapsedRealtime()) }
        }

        override fun onClientDisconnected(remoteAddress: String, reason: String) {
            publish { it.copy(clientAddress = null, clientSinceMs = 0, pcVirtualCamera = false) }
        }

        override fun onKeyframeNeeded() {
            pipeline?.requestKeyframe()
        }

        override fun onCongestion() {
            control.post {
                if (bitrate?.onCongestion(SystemClock.elapsedRealtime()) != null) applyRate()
            }
        }

        override fun onControl(request: ControlRequest) {
            control.post { handleControl(request) }
        }

        override fun onReceiverReport(report: ReceiverReport) {
            control.post {
                val clean = lastReportDrops >= 0 && report.framesDropped == lastReportDrops
                lastReportDrops = report.framesDropped
                if (clean && bitrate?.onCleanReport(SystemClock.elapsedRealtime()) != null) applyRate()
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private fun enterForeground(): Boolean = try {
        ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        true
    } catch (e: Exception) {
        // ForegroundServiceStartNotAllowedException (started from background) or SecurityException
        // (camera permission missing on API 34+).
        AirLog.e(TAG, "cannot enter foreground", e)
        publish { it.copy(state = StreamState.ERROR, message = "Cannot start camera service: ${e.message}") }
        false
    }

    private fun buildNotification(): Notification {
        val stop = PendingIntent.getService(
            this, 0,
            Intent(this, StreamingService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, AirLensApp.CHANNEL_STREAMING)
            .setSmallIcon(R.drawable.ic_stat_stream)
            .setContentTitle(getString(R.string.notification_title))
            .setOngoing(true)
            .setContentIntent(open)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .addAction(0, getString(R.string.notification_stop), stop)
            .build()
    }

    private fun publish(transform: (StreamStatus) -> StreamStatus) = StreamStatusBus.update(transform)

    companion object {
        private const val TAG = "AirLens.Service"
        const val ACTION_START = "net.silow.airlens.action.START"
        const val ACTION_STOP = "net.silow.airlens.action.STOP"
        const val ACTION_SWITCH_LENS = "net.silow.airlens.action.SWITCH_LENS"
        const val ACTION_SET_FOCUS = "net.silow.airlens.action.SET_FOCUS"
        const val EXTRA_LENS_KEY = "lensKey"
        const val EXTRA_FOCUS = "focus"
        const val EXTRA_FOCUS_LOCK = "focus_lock"
        private const val NOTIFICATION_ID = 1
        private const val MAX_BACKOFF_MS = 10_000L
        private const val SHUTDOWN_TIMEOUT_MS = 5_000L

        /** Must be called while the activity is visible (Android 11+ while-in-use camera rules). */
        fun start(context: Context, config: StreamConfig) {
            val intent = Intent(context, StreamingService::class.java)
                .setAction(ACTION_START)
                .putExtras(config.toBundle())
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            runCatching { context.startService(Intent(context, StreamingService::class.java).setAction(ACTION_STOP)) }
        }

        fun switchLens(context: Context, lensKey: String) {
            runCatching {
                context.startService(
                    Intent(context, StreamingService::class.java)
                        .setAction(ACTION_SWITCH_LENS)
                        .putExtra(EXTRA_LENS_KEY, lensKey),
                )
            }
        }

        /** Turns autofocus off, keeping focus where autofocus has it now. */
        fun lockFocus(context: Context) {
            runCatching {
                context.startService(
                    Intent(context, StreamingService::class.java)
                        .setAction(ACTION_SET_FOCUS)
                        .putExtra(EXTRA_FOCUS_LOCK, true),
                )
            }
        }

        /** Manual focus 0 (far) .. 1 (near), or null for autofocus. */
        fun setFocus(context: Context, value: Float?) {
            runCatching {
                context.startService(
                    Intent(context, StreamingService::class.java)
                        .setAction(ACTION_SET_FOCUS)
                        .putExtra(EXTRA_FOCUS, value ?: -1f),
                )
            }
        }

        /** IPv4 addresses of active non-loopback interfaces, for "connect your PC to …". */
        fun localAddresses(): List<String> = runCatching {
            NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { nif -> nif.inetAddresses.toList().filterIsInstance<Inet4Address>().map { it.hostAddress ?: "" } }
                .filter { it.isNotEmpty() }
        }.getOrDefault(emptyList())
    }
}
