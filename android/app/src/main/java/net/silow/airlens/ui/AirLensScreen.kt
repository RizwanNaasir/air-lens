package net.silow.airlens.ui

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.LinkOff
import androidx.compose.material.icons.outlined.Sync
import androidx.compose.material3.BottomSheetScaffold
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberBottomSheetScaffoldState
import androidx.compose.material3.rememberStandardBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.layout.positionInParent
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import net.silow.airlens.camera.CameraCapabilities
import net.silow.airlens.camera.LensCatalog
import net.silow.airlens.camera.LensOption
import net.silow.airlens.camera.VideoModeOption
import net.silow.airlens.encoder.EncoderSelector
import net.silow.airlens.power.OemPowerGuide
import net.silow.airlens.stream.PowerMode
import net.silow.airlens.stream.QualityPreset
import net.silow.airlens.stream.QualityPresets
import net.silow.airlens.stream.StreamConfig
import net.silow.airlens.stream.StreamState
import net.silow.airlens.stream.StreamStatusBus
import net.silow.airlens.stream.StreamingService
import net.silow.airlens.stream.VideoCodec

private val requiredPermissions = buildList {
    add(Manifest.permission.CAMERA)
    if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
}.toTypedArray()

private fun hasCameraPermission(context: Context) =
    context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

/** How long the controls stay visible after a tap in stealth mode, counted from the last touch. */
private const val REVEAL_MS = 8_000L

/**
 * Design B, "Viewfinder": the picture as the PC gets it, the lens controls on it, and a bottom
 * sheet with the connection, the Quality presets and Start/Stop. Pulling the sheet up (or "All
 * settings") shows every individual setting. In landscape, which a phone mounted as a webcam
 * usually is, the sheet becomes a panel beside the picture.
 *
 * Lens changes apply live. A new quality restarts the stream; the service keeps the chosen
 * quality across lenses and tells the PC (DEVICE_STATE), whose cards follow along.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AirLensScreen(onStealthChanged: (active: Boolean, dark: Boolean) -> Unit) {
    val context = LocalContext.current
    val prefs = remember { StreamConfig.prefs(context) }
    var config by remember { mutableStateOf(StreamConfig.load(prefs)) }
    val status by StreamStatusBus.status.collectAsStateWithLifecycle()
    var cameraGranted by remember { mutableStateOf(hasCameraPermission(context)) }
    var startAfterGrant by remember { mutableStateOf(false) }
    var showReport by remember { mutableStateOf(false) }
    var showLicenses by remember { mutableStateOf(false) }
    var revealed by remember { mutableStateOf(false) }
    var touches by remember { mutableIntStateOf(0) }
    var carryQuality by remember { mutableStateOf<String?>(null) }
    var previewAnyway by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    val lenses by produceState<List<LensOption>?>(null, cameraGranted) {
        value = withContext(Dispatchers.Default) { LensCatalog.lenses(context) }
    }
    val selectedLens = lenses?.let { all -> all.firstOrNull { it.key == config.lensKey } ?: LensCatalog.defaultLens(all) }
    val modes by produceState(emptyList<VideoModeOption>(), selectedLens?.key) {
        value = selectedLens?.let { lens -> withContext(Dispatchers.Default) { CameraCapabilities.videoModes(lens.descriptor) } }
            ?: emptyList()
    }
    val presets = remember(modes) { QualityPresets.forModes(modes) }
    val presetMaxBps by produceState(emptyMap<String, Int>(), presets) {
        value = withContext(Dispatchers.Default) {
            presets.associate { p -> p.id to (EncoderSelector.select(p.codec, p.width, p.height, p.fps)?.maxBitrateBps ?: 0) }
        }
    }
    val encoderMaxBps by produceState(0, config.codec, config.outputWidth, config.outputHeight, config.fps) {
        value = withContext(Dispatchers.Default) {
            EncoderSelector.select(config.codec, config.outputWidth, config.outputHeight, config.fps)?.maxBitrateBps ?: 0
        }
    }
    val running = status.state != StreamState.IDLE && status.state != StreamState.ERROR
    val stealthActive = running && status.powerMode == PowerMode.STEALTH
    val dark = stealthActive && !revealed

    // The service changed the saved settings (the PC picked a lens or quality): show them.
    LaunchedEffect(status.configRevision) { config = StreamConfig.load(prefs) }

    fun update(transform: (StreamConfig) -> StreamConfig) {
        config = transform(config).normalized()
        config.save(prefs)
    }

    val preview = remember { PreviewController(context.applicationContext) }
    DisposableEffect(Unit) { onDispose { preview.close() } }
    SideEffect { preview.update(running, if (cameraGranted) selectedLens else null, config) }

    fun startStream(cfg: StreamConfig) {
        preview.releaseCamera()
        StreamingService.start(context, cfg.copy(lensKey = selectedLens?.key))
    }

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        cameraGranted = result[Manifest.permission.CAMERA] == true || hasCameraPermission(context)
        if (cameraGranted && startAfterGrant) startStream(config)
        startAfterGrant = false
    }

    fun start() {
        if (cameraGranted) {
            startStream(config)
        } else {
            startAfterGrant = true
            permissionLauncher.launch(requiredPermissions)
        }
    }

    // Quality first: the first run takes the lens's best preset. A lens change while idle keeps the
    // chosen preset (the service does the same while streaming). Otherwise only repair a mode the
    // lens cannot do. Not while streaming: the running output wins and lens switches happen in place.
    LaunchedEffect(modes) {
        if (running || modes.isEmpty()) return@LaunchedEffect
        val carry = carryQuality
        carryQuality = null
        when {
            !StreamConfig.isSaved(prefs) -> presets.firstOrNull()?.let { p -> update { p.applyTo(it) } }
            carry != null && carry != QualityPresets.CUSTOM -> {
                val p = presets.firstOrNull { it.id == carry } ?: presets.firstOrNull()
                if (p != null && !p.matches(config)) update { p.applyTo(it) }
            }
            modes.none { it.matches(config) } -> {
                val pick = modes.firstOrNull { it.width == config.outputWidth && it.height == config.outputHeight } ?: modes.first()
                update {
                    val codec = if (it.codec in pick.codecs) it.codec else pick.codecs.first()
                    it.copy(outputWidth = pick.width, outputHeight = pick.height, fps = pick.fps, codec = codec)
                }
            }
        }
    }

    val rear = lenses.orEmpty().filter { !it.isFront }
    val front = lenses.orEmpty().firstOrNull { it.isFront }

    fun selectLens(lens: LensOption) {
        if (lens.key == selectedLens?.key) return
        if (running) {
            update { it.copy(lensKey = lens.key) }
            StreamingService.switchLens(context, lens.key) // the service carries the quality over
        } else {
            carryQuality = QualityPresets.matching(config, presets)
            update { it.copy(lensKey = lens.key) }
        }
    }

    fun selectPreset(p: QualityPreset) {
        if (p.matches(config)) return
        update { p.applyTo(it) }
        if (running) startStream(config)
    }

    val quality = QualityPresets.matching(config, presets)
    val summary = presets.firstOrNull { it.id == quality }?.let { presetSummary(it, presetMaxBps[it.id] ?: 0) }
        ?: if (presets.isEmpty()) "" else "Custom settings: see All settings"

    val now by produceState(SystemClock.elapsedRealtime(), status.clientSinceMs) {
        while (true) {
            value = SystemClock.elapsedRealtime()
            delay(1000)
        }
    }
    val live = if (running && status.clientAddress != null && status.clientSinceMs > 0) {
        LiveInfo(
            elapsed = formatElapsed(now - status.clientSinceMs),
            // adb forward arrives on loopback; anything else came over Wi-Fi.
            usb = status.clientAddress!!.startsWith("127."),
            fps = status.fps,
            mbps = (status.bitrateBps / 1_000_000.0).toInt(),
            batteryTempC = status.batteryTempC,
        )
    } else {
        null
    }
    val addresses = status.addresses.ifEmpty { remember(status.state) { StreamingService.localAddresses() } }
    val ip = addresses.firstOrNull()
    val where = ip?.let { "USB cable, or Wi-Fi at $it" } ?: "USB cable (Wi-Fi is off)"
    val connection = when {
        status.state == StreamState.ERROR -> ConnectionLine("Can't stream", status.message ?: "Unknown error", Icons.Outlined.ErrorOutline)
        status.state == StreamState.RECOVERING -> ConnectionLine("Restarting the camera", status.message ?: "", Icons.Outlined.Sync)
        live != null -> null
        running -> ConnectionLine("Waiting for your PC", where, Icons.Outlined.Sync, ip)
        else -> ConnectionLine("Ready, no PC yet", where, Icons.Outlined.LinkOff, ip)
    }
    // The PC's virtual camera is on: nobody needs this preview, so skip drawing it unless asked.
    LaunchedEffect(status.pcVirtualCamera, running) { if (!status.pcVirtualCamera || !running) previewAnyway = false }
    val previewOffForCamera = running && status.pcVirtualCamera && !previewAnyway
    val note = when {
        !cameraGranted -> "AirLens needs the camera to show the picture."
        running && config.directMode -> "No preview in direct mode. The PC shows the picture."
        previewOffForCamera -> "Preview is off while the PC's virtual camera is on."
        else -> null
    }
    val noteAction: (@Composable () -> Unit)? = when {
        !cameraGranted -> { { TextButton(onClick = { permissionLauncher.launch(requiredPermissions) }) { Text("Allow camera") } } }
        previewOffForCamera && !config.directMode -> { { TextButton(onClick = { previewAnyway = true }) { Text("Show preview") } } }
        else -> null
    }
    val heatNote = status.heatLowered?.let { lowered ->
        val temp = if (status.batteryTempC.isNaN()) "" else " (%.0f °C)".format(status.batteryTempC)
        "Lowered to $lowered while the phone is hot$temp. It goes back up once it cools down."
    }
    val settingsBadge = remember { !OemPowerGuide.isIgnoringBatteryOptimizations(context) }
    val aspect = config.outputWidth.toFloat() / config.outputHeight

    // Stealth: a tap reveals the controls; every touch keeps them up a little longer.
    LaunchedEffect(stealthActive, revealed, touches) {
        onStealthChanged(stealthActive, !revealed)
        if (revealed) {
            delay(REVEAL_MS)
            revealed = false
        }
    }
    DisposableEffect(Unit) { onDispose { onStealthChanged(false, false) } }

    val sheetState = rememberStandardBottomSheetState(initialValue = SheetValue.PartiallyExpanded, skipHiddenState = true)
    val scaffoldState = rememberBottomSheetScaffoldState(bottomSheetState = sheetState)
    val panelScroll = rememberScrollState()
    var advancedOffset by remember { mutableIntStateOf(0) }
    var quickHeightPx by remember { mutableIntStateOf(0) }
    val density = LocalDensity.current
    val navBar = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()

    BoxWithConstraints(
        Modifier
            .fillMaxSize()
            .pointerInput(Unit) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                    touches++
                }
            },
    ) {
        val landscape = maxWidth > maxHeight
        val openSettings: () -> Unit = {
            scope.launch { if (landscape) panelScroll.animateScrollTo(advancedOffset) else sheetState.expand() }
        }

        @Composable
        fun Quick(modifier: Modifier) = QuickControls(
            modifier = modifier,
            connection = connection,
            presets = presets,
            quality = quality,
            summary = summary,
            heatNote = heatNote,
            running = running,
            onPreset = ::selectPreset,
            onStart = ::start,
            onStop = { StreamingService.stop(context) },
            canDarkScreen = stealthActive,
            onDarkScreen = { revealed = false },
            onAllSettings = openSettings,
        )

        @Composable
        fun Advanced() = AdvancedSettings(
            config = config,
            modes = modes,
            encoderMaxBps = encoderMaxBps,
            running = running,
            status = status,
            update = ::update,
            onApply = { startStream(config) },
            onReport = { showReport = true },
            onLicenses = { showLicenses = true },
            reportEnabled = cameraGranted,
            onFocus = { value -> StreamingService.setFocus(context, value) },
            onLockFocus = { StreamingService.lockFocus(context) },
        )

        @Composable
        fun Finder(modifier: Modifier, bottomInset: androidx.compose.ui.unit.Dp) = Viewfinder(
            modifier = modifier,
            aspect = aspect,
            showPreview = cameraGranted && !dark && !previewOffForCamera,
            note = note,
            noteAction = noteAction,
            onTarget = preview::onSurface,
            live = live,
            rearLenses = rear,
            frontLens = front,
            selectedKey = selectedLens?.key,
            onLens = ::selectLens,
            onFront = { on -> (if (on) front else LensCatalog.defaultLens(rear) ?: rear.firstOrNull())?.let(::selectLens) },
            onSettings = openSettings,
            settingsBadge = settingsBadge,
            bottomInset = bottomInset,
        )

        if (landscape) {
            Row(Modifier.fillMaxSize()) {
                Finder(Modifier.weight(1f).fillMaxHeight(), 0.dp)
                Surface(Modifier.width(380.dp).fillMaxHeight(), color = MaterialTheme.colorScheme.surfaceContainerLow) {
                    Column(
                        Modifier.fillMaxSize().verticalScroll(panelScroll).safeDrawingPadding().padding(vertical = 16.dp),
                        verticalArrangement = Arrangement.spacedBy(24.dp),
                    ) {
                        Quick(Modifier)
                        Box(Modifier.padding(horizontal = 16.dp).onGloballyPositioned { advancedOffset = it.positionInParent().y.toInt() }) {
                            Advanced()
                        }
                    }
                }
            }
        } else {
            // The sheet peeks exactly as far as the quick controls reach, plus its drag handle.
            val peek = with(density) { quickHeightPx.toDp() } + DRAG_HANDLE + navBar
            BottomSheetScaffold(
                scaffoldState = scaffoldState,
                sheetPeekHeight = peek,
                sheetContainerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                containerColor = MaterialTheme.colorScheme.surfaceContainerLowest,
                sheetContent = {
                    Column(
                        Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).navigationBarsPadding().padding(bottom = 24.dp),
                        verticalArrangement = Arrangement.spacedBy(24.dp),
                    ) {
                        Quick(Modifier.onSizeChanged { quickHeightPx = it.height })
                        Box(Modifier.padding(horizontal = 16.dp)) { Advanced() }
                    }
                },
            ) {
                Finder(Modifier.fillMaxSize(), (peek - 24.dp).coerceAtLeast(0.dp))
            }
        }

        if (dark) StealthOverlay(status) { revealed = true }
    }

    if (showReport) ReportDialog(onDismiss = { showReport = false })
    if (showLicenses) LicensesDialog(onDismiss = { showLicenses = false })
}

/** BottomSheetDefaults.DragHandle: 22 dp above and below a 4 dp bar. */
private val DRAG_HANDLE = 48.dp

/** "Best picture · 30 fps · H.265 · 80 Mbps". */
private fun presetSummary(p: QualityPreset, maxBps: Int): String = buildString {
    append(p.tag).append(" · ").append(p.fps).append(" fps")
    if (p.width * 3 == p.height * 4) append(" · 4:3")
    append(if (p.codec == VideoCodec.HEVC) " · H.265" else " · H.264")
    if (maxBps > 0) append(" · ").append(minOf(maxBps, StreamConfig.MAX_SANE_BITRATE) / 1_000_000).append(" Mbps")
}

private fun formatElapsed(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}
