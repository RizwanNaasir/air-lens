package net.silow.airlens.ui

import android.content.ClipData
import android.content.ClipboardManager
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import net.silow.airlens.camera.CameraCapabilities
import net.silow.airlens.camera.VideoModeOption
import net.silow.airlens.encoder.RatePriority
import net.silow.airlens.gl.ScaleMode
import net.silow.airlens.power.OemPowerGuide
import net.silow.airlens.stream.PowerMode
import net.silow.airlens.stream.StreamConfig
import net.silow.airlens.stream.StreamStatus
import net.silow.airlens.stream.StreamingService
import net.silow.airlens.stream.VideoCodec
import kotlin.math.roundToInt

/**
 * Everything the Quality presets bundle, one setting at a time, plus connection details, live
 * stats and the device report. Shown below the quick controls ("All settings").
 */
@Composable
internal fun AdvancedSettings(
    config: StreamConfig,
    modes: List<VideoModeOption>,
    encoderMaxBps: Int,
    running: Boolean,
    status: StreamStatus,
    update: ((StreamConfig) -> StreamConfig) -> Unit,
    onApply: () -> Unit,
    onReport: () -> Unit,
    reportEnabled: Boolean,
    onFocus: (Float?) -> Unit,
    onLockFocus: () -> Unit,
    onLicenses: () -> Unit,
) {
    val context = LocalContext.current
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("All settings", style = MaterialTheme.typography.titleMedium)
        if (running) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    "Changes here take effect when you apply them.",
                    Modifier.weight(1f),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OutlinedButton(onClick = onApply) { Text("Apply") }
            }
        }
        if (running && status.manualFocusSupported) FocusCard(status.focus, onFocus, onLockFocus)
        VideoCard(config, modes, encoderMaxBps, update)
        OptionsCard(config, modes.firstOrNull { it.matches(config) }, update)
        ConnectionCard(status, config.port)
        if (OemPowerGuide.isHuaweiFamily || !OemPowerGuide.isIgnoringBatteryOptimizations(context)) {
            OemCard(onOpen = { OemPowerGuide.openSettings(context) })
        }
        if (running) StatsCard(status)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = onReport, enabled = reportEnabled) { Text("Device report") }
            OutlinedButton(onClick = onLicenses) { Text("Open-source licenses") }
        }
    }
}

internal fun VideoModeOption.matches(c: StreamConfig) = width == c.outputWidth && height == c.outputHeight && fps == c.fps

@Composable
private fun SectionCard(title: String, content: @Composable () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
            content()
        }
    }
}

@Composable
private fun ConnectionCard(status: StreamStatus, configuredPort: Int) {
    val port = if (status.port != 0) status.port else configuredPort
    val addresses = status.addresses.ifEmpty { remember { StreamingService.localAddresses() } }
    SectionCard("Connection") {
        if (addresses.isEmpty()) Text("Wi-Fi: not connected")
        addresses.forEach { Text("Wi-Fi: $it:$port", fontFamily = FontFamily.Monospace) }
        Text("USB: adb forward tcp:$port tcp:$port", fontFamily = FontFamily.Monospace)
        Text(
            "AirLens for Windows sets up USB by itself.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun VideoCard(
    config: StreamConfig,
    modes: List<VideoModeOption>,
    encoderMaxBps: Int,
    update: ((StreamConfig) -> StreamConfig) -> Unit,
) {
    SectionCard("Video") {
        if (modes.isEmpty()) {
            Text("No streamable mode found yet for this lens.")
        } else {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                modes.forEach { mode ->
                    FilterChip(
                        selected = mode.matches(config),
                        onClick = {
                            update {
                                val codec = if (it.codec in mode.codecs) it.codec else mode.codecs.first()
                                it.copy(outputWidth = mode.width, outputHeight = mode.height, fps = mode.fps, codec = codec)
                            }
                        },
                        label = { Text(mode.label) },
                    )
                }
            }
            val current = modes.firstOrNull { it.matches(config) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                VideoCodec.entries.forEach { codec ->
                    FilterChip(
                        selected = config.codec == codec,
                        onClick = { update { it.copy(codec = codec) } },
                        enabled = current == null || codec in current.codecs,
                        label = { Text(if (codec == VideoCodec.H264) "H.264" else "H.265") },
                    )
                }
            }
        }
        // The slider spans the selected encoder's real range (e.g. 60 Mbps H.264 / 80 Mbps H.265 on
        // the P30). "Max" tracks that ceiling automatically when the mode or codec changes.
        val encoderCeiling = encoderMaxBps.takeIf { it > 0 } ?: 60_000_000
        val ceilingMbps = (minOf(encoderCeiling, StreamConfig.MAX_SANE_BITRATE) / 1_000_000).coerceAtLeast(4)
        val isMax = config.bitrateBps == StreamConfig.BITRATE_MAX
        val mbps = if (isMax) ceilingMbps.toFloat() else (config.bitrateBps / 1_000_000f).coerceAtMost(ceilingMbps.toFloat())
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Bitrate: ${mbps.roundToInt()} Mbps" + if (isMax) " (encoder max)" else "", Modifier.weight(1f))
            FilterChip(
                selected = isMax,
                onClick = { update { it.copy(bitrateBps = if (isMax) ceilingMbps * 1_000_000 else StreamConfig.BITRATE_MAX) } },
                label = { Text("Max") },
            )
        }
        Slider(
            value = mbps,
            onValueChange = { v ->
                val chosen = v.roundToInt()
                update { it.copy(bitrateBps = if (chosen >= ceilingMbps) StreamConfig.BITRATE_MAX else chosen * 1_000_000) }
            },
            valueRange = 2f..ceilingMbps.toFloat(),
        )
        Text("When the link or heat can't sustain it")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(
                selected = config.priority == RatePriority.QUALITY,
                onClick = { update { it.copy(priority = RatePriority.QUALITY) } },
                label = { Text("Keep quality (lower fps)") },
            )
            FilterChip(
                selected = config.priority == RatePriority.SMOOTHNESS,
                onClick = { update { it.copy(priority = RatePriority.SMOOTHNESS) } },
                label = { Text("Keep fps") },
            )
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun OptionsCard(config: StreamConfig, mode: VideoModeOption?, update: ((StreamConfig) -> StreamConfig) -> Unit) {
    SectionCard("Picture and screen") {
        LabeledSwitch("Mirror image", config.mirror) { v -> update { it.copy(mirror = v) } }
        LabeledSwitch(
            "Direct mode (lower delay, no preview while live, the PC rotates the picture)",
            config.directMode && mode?.directCapable == true,
            enabled = mode?.directCapable == true,
        ) { v -> update { it.copy(directMode = v) } }
        Text("Framing")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(config.scaleMode == ScaleMode.FILL, { update { it.copy(scaleMode = ScaleMode.FILL) } }, { Text("Fill (crop)") })
            FilterChip(config.scaleMode == ScaleMode.FIT, { update { it.copy(scaleMode = ScaleMode.FIT) } }, { Text("Fit (bars)") })
        }
        Text("Rotation")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(config.rotationLock == null, { update { it.copy(rotationLock = null) } }, { Text("Auto") })
            listOf(0, 90, 180, 270).forEach { deg ->
                FilterChip(config.rotationLock == deg, { update { it.copy(rotationLock = deg) } }, { Text("$deg°") })
            }
        }
        Text("Screen while streaming")
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(config.powerMode == PowerMode.STEALTH, { update { it.copy(powerMode = PowerMode.STEALTH) } }, { Text("Stealth (black)") })
            FilterChip(config.powerMode == PowerMode.SCREEN_OFF, { update { it.copy(powerMode = PowerMode.SCREEN_OFF) } }, { Text("Screen off") })
        }
        Text(
            if (config.powerMode == PowerMode.STEALTH) {
                "Best for Wi-Fi: keeps Wi-Fi in low-latency mode. Black pixels are off on OLED screens."
            } else {
                "Best for USB. Press the power button after starting. Wi-Fi may add latency spikes."
            },
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** Autofocus, or a fixed distance when autofocus keeps hunting. Applies live, no restart. */
@Composable
private fun FocusCard(focus: Float?, onFocus: (Float?) -> Unit, onLockFocus: () -> Unit) {
    SectionCard("Focus") {
        LabeledSwitch("Autofocus", focus == null) { auto -> if (auto) onFocus(null) else onLockFocus() }
        if (focus == null) {
            Text(
                "Autofocus hunting? Turn it off to hold focus where it is now.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        } else {
            var value by remember(focus) { androidx.compose.runtime.mutableFloatStateOf(focus) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Far", style = MaterialTheme.typography.bodySmall)
                Slider(
                    value = value,
                    onValueChange = { value = it },
                    onValueChangeFinished = { onFocus(value) },
                    modifier = Modifier.weight(1f).padding(horizontal = 8.dp),
                )
                Text("Near", style = MaterialTheme.typography.bodySmall)
            }
        }
    }
}

@Composable
private fun LabeledSwitch(label: String, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

@Composable
private fun OemCard(onOpen: () -> Unit) {
    SectionCard("Keep AirLens running") {
        Text(OemPowerGuide.instructions(), style = MaterialTheme.typography.bodySmall)
        OutlinedButton(onClick = onOpen) { Text("Open settings") }
    }
}

@Composable
private fun StatsCard(s: StreamStatus) {
    SectionCard("Live stats") {
        @Composable
        fun line(text: String) = Text(text, fontFamily = FontFamily.Monospace, fontSize = 13.sp)
        line("Lens      ${s.lensLabel ?: "-"}")
        line("Output    ${s.width}x${s.height} @ ${s.fps} fps, ${s.bitrateBps / 1_000_000} of ${s.maxBitrateBps / 1_000_000} Mbps")
        line("Priority  ${if (s.priority == RatePriority.QUALITY) "quality (fps drops first)" else "frame rate (bitrate drops first)"}")
        line("Encoder   ${s.codecName ?: "-"}")
        if (s.encoderFeatures.isNotEmpty()) line("Features  ${s.encoderFeatures.joinToString()}")
        line("Frames    encoded ${s.framesEncoded}, sent ${s.framesSent}, dropped ${s.framesDropped}, GL skipped ${s.glFramesSkipped}")
        line("Pool      ${s.poolOutstanding} frames checked out")
        val temp = if (s.batteryTempC.isNaN()) "-" else "%.1f °C".format(s.batteryTempC)
        line("Thermal   ${s.thermal.name.lowercase()} (battery $temp)")
        line("Wakelock  ${if (s.wakeLockHeld) "held (rotated every 45 min)" else "none"}")
    }
}

/** Black, nearly invisible overlay while streaming in stealth mode; a tap shows the controls. */
@Composable
internal fun StealthOverlay(status: StreamStatus, onTap: () -> Unit) {
    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClick = onTap),
        contentAlignment = Alignment.BottomCenter,
    ) {
        // Nearly invisible at brightness 0; a hint that the app is alive, not a frozen screen.
        Text(
            if (status.clientAddress != null) "AirLens live · tap for controls" else "AirLens waiting for PC · tap for controls",
            color = Color(0xFF202020),
            fontSize = 12.sp,
            modifier = Modifier.padding(32.dp),
        )
    }
}

@Composable
internal fun ReportDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val report by produceState("Collecting…") {
        value = withContext(Dispatchers.Default) { runCatching { CameraCapabilities.report(context) }.getOrElse { "Report failed: $it" } }
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Device report") },
        text = {
            Text(
                report,
                Modifier.verticalScroll(rememberScrollState()),
                fontFamily = FontFamily.Monospace,
                fontSize = 11.sp,
            )
        },
        confirmButton = {
            TextButton(onClick = {
                context.getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("AirLens device report", report))
            }) { Text("Copy") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}
