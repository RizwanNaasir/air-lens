package net.silow.airlens.ui

import android.content.ClipData
import android.content.ClipboardManager
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.FiberManualRecord
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Videocam
import androidx.compose.material.icons.outlined.Cameraswitch
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.DarkMode
import androidx.compose.material.icons.outlined.KeyboardArrowUp
import androidx.compose.material.icons.outlined.Tune
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FilledTonalIconToggleButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import net.silow.airlens.camera.LensKind
import net.silow.airlens.camera.LensOption
import net.silow.airlens.stream.PreviewHub
import net.silow.airlens.stream.QualityPreset

/** Shown in the top bar and as chips while a PC is watching. */
internal class LiveInfo(val elapsed: String, val usb: Boolean, val fps: Int, val mbps: Int, val batteryTempC: Float)

/** The sheet's first line before a PC watches: where things stand, and the address to type in. */
internal class ConnectionLine(val title: String, val detail: String, val icon: ImageVector, val copyText: String? = null)

/** "Wide / Auto / Main / Tele / Front": the same short names as the Windows app. */
internal fun LensOption.shortLabel(): String = when (kind) {
    LensKind.ULTRA_WIDE -> "Wide"
    LensKind.TELEPHOTO -> "Tele"
    else -> label.substringBefore(" (")
}

/**
 * Design B's top part: the picture exactly as the PC gets it (the stream's shape), with the title
 * or live status on top and the lens controls along the bottom edge.
 */
@Composable
internal fun Viewfinder(
    modifier: Modifier,
    aspect: Float,
    showPreview: Boolean,
    note: String?,
    noteAction: (@Composable () -> Unit)?,
    onTarget: (PreviewHub.Target?) -> Unit,
    live: LiveInfo?,
    rearLenses: List<LensOption>,
    frontLens: LensOption?,
    selectedKey: String?,
    onLens: (LensOption) -> Unit,
    onFront: (Boolean) -> Unit,
    onSettings: () -> Unit,
    settingsBadge: Boolean,
    bottomInset: Dp,
) {
    Box(modifier.background(MaterialTheme.colorScheme.surfaceContainerLowest)) {
        Box(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .padding(top = if (live != null) 104.dp else 64.dp, bottom = bottomInset + 72.dp),
            contentAlignment = Alignment.Center,
        ) {
            Box(Modifier.aspectRatio(aspect).background(Color.Black)) {
                if (showPreview) CameraPreview(Modifier.fillMaxSize(), onTarget)
                ThirdsGrid(Modifier.fillMaxSize())
                if (note != null) {
                    Column(
                        Modifier.align(Alignment.Center).padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(note, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center)
                        noteAction?.invoke()
                    }
                }
            }
        }

        Row(
            Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .height(64.dp)
                .padding(start = 16.dp, end = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (live != null) {
                LiveBadge()
                Text("On your PC · ${live.elapsed}", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            } else {
                Text("AirLens", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            }
            BadgedBox(badge = { if (settingsBadge) Badge() }) {
                FilledTonalIconButton(onClick = onSettings) { Icon(Icons.Outlined.Tune, contentDescription = "All settings") }
            }
        }

        if (live != null) {
            Row(
                Modifier.statusBarsPadding().padding(top = 64.dp, start = 16.dp, end = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                StatChip(if (live.usb) "USB" else "Wi-Fi", if (live.usb) Icons.Outlined.Usb else Icons.Outlined.Wifi)
                StatChip("${live.fps} fps")
                StatChip("${live.mbps} Mbps")
                if (!live.batteryTempC.isNaN()) StatChip("%.0f °C".format(live.batteryTempC))
            }
        }

        Row(
            Modifier.align(Alignment.BottomCenter).padding(bottom = bottomInset + 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (rearLenses.isNotEmpty()) LensPill(rearLenses, selectedKey, onLens)
            if (frontLens != null) {
                FilledTonalIconToggleButton(
                    checked = frontLens.key == selectedKey,
                    onCheckedChange = onFront,
                    modifier = Modifier.size(48.dp),
                ) { Icon(Icons.Outlined.Cameraswitch, contentDescription = "Front camera") }
            }
        }
    }
}

@Composable
private fun LensPill(lenses: List<LensOption>, selectedKey: String?, onLens: (LensOption) -> Unit) {
    Surface(shape = CircleShape, color = MaterialTheme.colorScheme.surfaceContainer) {
        Row(Modifier.padding(4.dp), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
            lenses.forEach { lens ->
                val modifier = Modifier.height(40.dp).semantics { selected = lens.key == selectedKey }
                val padding = PaddingValues(horizontal = 14.dp)
                if (lens.key == selectedKey) {
                    FilledTonalButton(onClick = {}, modifier = modifier, contentPadding = padding) { Text(lens.shortLabel()) }
                } else {
                    TextButton(onClick = { onLens(lens) }, modifier = modifier, contentPadding = padding) {
                        Text(lens.shortLabel(), color = MaterialTheme.colorScheme.onSurface)
                    }
                }
            }
        }
    }
}

@Composable
private fun LiveBadge() {
    Surface(shape = CircleShape, color = MaterialTheme.colorScheme.error) {
        Row(Modifier.padding(horizontal = 8.dp, vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.FiberManualRecord, contentDescription = null, Modifier.size(12.dp), tint = MaterialTheme.colorScheme.onError)
            Spacer(Modifier.width(4.dp))
            Text("LIVE", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onError)
        }
    }
}

@Composable
private fun StatChip(text: String, icon: ImageVector? = null) {
    Surface(shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.surfaceContainer) {
        Row(Modifier.height(32.dp).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            if (icon != null) {
                Icon(icon, contentDescription = null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(6.dp))
            }
            Text(text, style = MaterialTheme.typography.labelLarge)
        }
    }
}

/** Rule-of-thirds lines: a framing aid, faint enough not to compete with the picture. */
@Composable
private fun ThirdsGrid(modifier: Modifier) {
    val color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.45f)
    Canvas(modifier) {
        val stroke = 1.dp.toPx()
        for (i in 1..2) {
            val x = size.width * i / 3
            val y = size.height * i / 3
            drawLine(color, Offset(x, 0f), Offset(x, size.height), stroke)
            drawLine(color, Offset(0f, y), Offset(size.width, y), stroke)
        }
    }
}

/**
 * The sheet's always-visible part: connection, the Quality presets, and the main action. What it
 * leaves out lives in "All settings" below it.
 */
@Composable
internal fun QuickControls(
    modifier: Modifier,
    connection: ConnectionLine?,
    presets: List<QualityPreset>,
    quality: String,
    summary: String,
    heatNote: String?,
    running: Boolean,
    onPreset: (QualityPreset) -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit,
    canDarkScreen: Boolean,
    onDarkScreen: () -> Unit,
    onAllSettings: () -> Unit,
) {
    val context = LocalContext.current
    Column(modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        if (connection != null) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(connection.icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Column(Modifier.weight(1f)) {
                    Text(connection.title, style = MaterialTheme.typography.titleMedium)
                    Text(connection.detail, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                val copy = connection.copyText
                if (copy != null) {
                    IconButton(onClick = {
                        context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("AirLens address", copy))
                    }) { Icon(Icons.Outlined.ContentCopy, contentDescription = "Copy IP address") }
                }
            }
        }

        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Quality", style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
            if (presets.isEmpty()) {
                Text("Looking for this lens's modes…", style = MaterialTheme.typography.bodyMedium)
            } else {
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    presets.forEachIndexed { i, p ->
                        SegmentedButton(
                            selected = p.id == quality,
                            onClick = { onPreset(p) },
                            shape = SegmentedButtonDefaults.itemShape(i, presets.size),
                        ) { Text(p.label) }
                    }
                }
            }
            if (summary.isNotEmpty()) {
                Text(summary, Modifier.padding(horizontal = 4.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (heatNote != null) {
                Text(heatNote, Modifier.padding(horizontal = 4.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            if (running) {
                Text(
                    "A new quality restarts the stream for about a second.",
                    Modifier.padding(horizontal = 4.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            ExtendedFloatingActionButton(
                onClick = if (running) onStop else onStart,
                icon = { Icon(if (running) Icons.Filled.Stop else Icons.Filled.Videocam, contentDescription = null) },
                text = { Text(if (running) "Stop streaming" else "Start streaming") },
            )
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
            if (canDarkScreen) {
                TextButton(onClick = onDarkScreen) {
                    Icon(Icons.Outlined.DarkMode, contentDescription = null, Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Dark screen")
                }
            }
            TextButton(onClick = onAllSettings) {
                Icon(Icons.Outlined.KeyboardArrowUp, contentDescription = null, Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text("All settings")
            }
        }
    }
}
