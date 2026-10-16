package net.silow.airlens.camera

import android.content.Context
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.os.Build
import net.silow.airlens.util.AirLog
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt

enum class LensKind { ULTRA_WIDE, WIDE, TELEPHOTO, FRONT, EXTERNAL }

/**
 * One selectable lens. Phones expose extra lenses in three different ways; this type hides
 * which one applies:
 *
 * | Exposure | How AirLens streams from it |
 * |---|---|
 * | Separate camera ID in `cameraIdList` | `openCamera(cameraId)` |
 * | Physical sub-camera of a logical multi-camera (API 28+) | open the logical camera, `OutputConfiguration.setPhysicalCameraId(physicalId)` |
 * | Only reachable by zooming the logical camera (API 30+) | `CONTROL_ZOOM_RATIO = zoomRatio`; the HAL switches lens |
 */
data class LensOption(
    /** Stable key persisted in [net.silow.airlens.stream.StreamConfig.lensKey]. */
    val key: String,
    val label: String,
    val kind: LensKind,
    val cameraId: String,
    val physicalId: String?,
    val zoomRatio: Float?,
    /** 35 mm-equivalent focal length as computed from HAL data, or 0 when the sensor size is missing. */
    val equivalentFocalMm: Float,
    /** Field of view relative to the main lens (approximate; see [LensCatalog]). */
    val relativeZoom: Float,
    /** Capabilities of the lens itself (physical sizes for sub-cameras; AE ranges of the logical camera). */
    val descriptor: CameraDescriptor,
) {
    val isFront get() = kind == LensKind.FRONT
}

/**
 * Enumerates every lens the camera HAL exposes to third-party apps, sorted ultra-wide → main →
 * telephoto → front → external, and labelled by zoom relative to the main lens.
 *
 * OEMs differ: some list each lens as its own camera ID, some hide the sub-cameras behind a logical
 * camera, some (historically Huawei/EMUI) expose only the main and front cameras to third-party
 * apps. Nothing is assumed; lenses the HAL hides simply do not appear, and the device report says
 * what was found.
 */
object LensCatalog {
    private const val TAG = "AirLens.Lens"
    private const val FULL_FRAME_DIAGONAL_MM = 43.27f
    private const val MAIN_LENS_TARGET_MM = 26f

    fun lenses(context: Context): List<LensOption> {
        val manager = context.getSystemService(CameraManager::class.java)
        val listed = runCatching { manager.cameraIdList.toList() }.getOrDefault(emptyList())
        val raw = mutableListOf<Candidate>()

        for (id in listed) {
            val chars = runCatching { manager.getCameraCharacteristics(id) }.getOrNull() ?: continue
            val descriptor = CameraCapabilities.describe(id, chars) ?: continue
            val isLogical = isLogicalMultiCamera(chars) && chars.physicalCameraIds.isNotEmpty()
            // Some OEMs (the Huawei P30 on EMUI 12) list the physical lenses as ordinary IDs too;
            // those are opened directly, so only the unlisted ones need setPhysicalCameraId.
            val hiddenPhysical = if (isLogical) chars.physicalCameraIds - listed.toSet() else emptySet()

            // The logical camera itself stays selectable: its HAL may do better auto-switching.
            raw += Candidate(id, null, null, descriptor, equivalentFocal(chars), logicalAuto = isLogical)
            zoomOnlyUltraWide(id, chars, descriptor)?.let { raw += it }
            for (pid in hiddenPhysical) {
                val pchars = runCatching { manager.getCameraCharacteristics(pid) }.getOrNull() ?: continue
                // Physical characteristics carry the physical stream sizes; AE ranges come from the
                // logical camera because that is where the capture request goes.
                val pdesc = CameraCapabilities.describe(pid, pchars, fallbackRanges = descriptor.rawFpsRanges) ?: continue
                if (pdesc.textureSizes.isEmpty()) continue
                // Request controls (AF/OIS/NR/edge) are validated against the logical camera.
                val merged = pdesc.copy(
                    facing = descriptor.facing,
                    afModes = descriptor.afModes,
                    oisModes = descriptor.oisModes,
                    noiseReductionModes = descriptor.noiseReductionModes,
                    edgeModes = descriptor.edgeModes,
                )
                raw += Candidate(id, pid, null, merged, equivalentFocal(pchars))
            }
        }
        return label(raw).also { list ->
            AirLog.i(TAG, "lenses: " + list.joinToString { "${it.label} [${it.key}] ${it.equivalentFocalMm.roundToInt()}mm" })
        }
    }

    fun find(context: Context, key: String?): LensOption? {
        val all = lenses(context)
        return all.firstOrNull { it.key == key } ?: defaultLens(all)
    }

    fun defaultLens(all: List<LensOption>): LensOption? =
        all.firstOrNull { it.kind == LensKind.WIDE && it.physicalId == null && it.zoomRatio == null }
            ?: all.firstOrNull { it.kind == LensKind.WIDE }
            ?: all.firstOrNull()

    private class Candidate(
        val cameraId: String,
        val physicalId: String?,
        val zoomRatio: Float?,
        val descriptor: CameraDescriptor,
        val equivalentFocalMm: Float,
        val logicalAuto: Boolean = false,
    )

    private fun label(raw: List<Candidate>): List<LensOption> {
        val back = raw.filter { it.descriptor.facing == Facing.BACK && it.zoomRatio == null && it.equivalentFocalMm > 0f }
        val mainFocal = back.minByOrNull { abs(it.equivalentFocalMm - MAIN_LENS_TARGET_MM) }?.equivalentFocalMm ?: 0f

        val options = raw.map { c ->
            val relative = when {
                c.zoomRatio != null -> c.zoomRatio
                mainFocal > 0f && c.equivalentFocalMm > 0f -> c.equivalentFocalMm / mainFocal
                else -> 1f
            }
            val kind = when (c.descriptor.facing) {
                Facing.FRONT -> LensKind.FRONT
                Facing.EXTERNAL -> LensKind.EXTERNAL
                Facing.BACK -> when {
                    relative < 0.8f -> LensKind.ULTRA_WIDE
                    relative > 1.5f -> LensKind.TELEPHOTO
                    else -> LensKind.WIDE
                }
            }
            // Named by kind only. Zoom factors computed from HAL data are unreliable on some phones
            // (the P30 reports sensor sizes that give 0.4x/2.1x for its marketed 0.6x/3x lenses),
            // so they are only used to tell two lenses of the same kind apart.
            val name = when {
                c.logicalAuto -> "Auto (multi-lens)"
                kind == LensKind.ULTRA_WIDE -> "Ultra-wide"
                kind == LensKind.TELEPHOTO -> "Telephoto"
                kind == LensKind.WIDE -> if (abs(relative - 1f) < 0.05f) "Main" else "Wide"
                kind == LensKind.FRONT -> "Front"
                else -> "External"
            }
            LensOption(
                key = buildString {
                    append(c.cameraId)
                    c.physicalId?.let { append('/').append(it) }
                    c.zoomRatio?.let { append('@').append(it) }
                },
                label = name,
                kind = kind,
                cameraId = c.cameraId,
                physicalId = c.physicalId,
                zoomRatio = c.zoomRatio,
                equivalentFocalMm = c.equivalentFocalMm,
                relativeZoom = relative,
                descriptor = c.descriptor,
            )
        }
        val order = listOf(LensKind.ULTRA_WIDE, LensKind.WIDE, LensKind.TELEPHOTO, LensKind.FRONT, LensKind.EXTERNAL)
        // A zoom-ratio "lens" only stands in for an ultra-wide the app cannot open directly.
        val hasRealUltraWide = options.any { it.kind == LensKind.ULTRA_WIDE && it.zoomRatio == null }
        return options
            .filterNot { hasRealUltraWide && it.zoomRatio != null }
            .sortedWith(compareBy<LensOption> { order.indexOf(it.kind) }.thenBy { it.equivalentFocalMm })
            .disambiguate()
    }

    /** Lenses sharing a label get their approximate zoom (back) or camera key (others) appended. */
    private fun List<LensOption>.disambiguate(): List<LensOption> {
        val counts = groupingBy { it.label }.eachCount()
        return map {
            when {
                (counts[it.label] ?: 0) <= 1 -> it
                it.descriptor.facing == Facing.BACK -> it.copy(label = "${it.label} ~${formatZoom(it.relativeZoom)}×")
                else -> it.copy(label = "${it.label} (${it.key})")
            }
        }
    }

    /**
     * API 30+: logical cameras whose zoom range goes below 1× switch to an ultra-wide lens the app
     * cannot address directly. Offer that as a lens. Telephoto thresholds are HAL-private, so tele
     * is not synthesised this way.
     */
    private fun zoomOnlyUltraWide(id: String, chars: CameraCharacteristics, descriptor: CameraDescriptor): Candidate? {
        if (Build.VERSION.SDK_INT < 30 || descriptor.facing != Facing.BACK) return null
        val range = chars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE) ?: return null
        if (range.lower >= 0.95f) return null
        return Candidate(id, null, range.lower, descriptor, equivalentFocal(chars) * range.lower)
    }

    private fun isLogicalMultiCamera(chars: CameraCharacteristics): Boolean =
        chars.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)
            ?.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA) == true

    /** focal × (full-frame diagonal / sensor diagonal). Uses the shortest focal length on zoom lenses. */
    fun equivalentFocal(chars: CameraCharacteristics): Float {
        val focal = chars.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.minOrNull() ?: return 0f
        val size = chars.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE) ?: return 0f
        val diagonal = sqrt(size.width * size.width + size.height * size.height)
        return if (diagonal > 0f) focal * FULL_FRAME_DIAGONAL_MM / diagonal else 0f
    }

    private fun formatZoom(z: Float): String {
        val rounded = (z * 10).roundToInt() / 10f
        return if (rounded % 1f == 0f) rounded.toInt().toString() else rounded.toString()
    }
}
