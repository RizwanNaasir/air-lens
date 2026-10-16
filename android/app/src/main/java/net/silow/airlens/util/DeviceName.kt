package net.silow.airlens.util

import android.content.Context
import android.os.Build
import android.provider.Settings
import java.util.concurrent.TimeUnit

/**
 * The name people know the phone by, for the PC to show ("HUAWEI P30", not "ELE-L29").
 *
 * Vendors keep the marketing name in their own system property: Huawei/Honor
 * `ro.config.marketing_name`, Xiaomi `ro.product.marketname`, Oppo/OnePlus/Realme
 * `ro.vendor.oplus.market.name`, Vivo `ro.vivo.market.name`. Pixel and Samsung put it in the
 * device-name setting instead. Read with `getprop`, which needs no permission.
 */
object DeviceName {
    private val marketingProps = listOf(
        "ro.config.marketing_name",
        "ro.product.marketname",
        "ro.vendor.oplus.market.name",
        "ro.vivo.market.name",
    )

    @Volatile private var cached: String? = null

    fun get(context: Context): String = cached ?: resolve(context).also { cached = it }

    private fun resolve(context: Context): String {
        marketingProps.firstNotNullOfOrNull { prop -> getprop(prop)?.takeIf { it.isNotBlank() } }?.let { return it }
        val setting = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)
        if (!setting.isNullOrBlank() && !setting.equals(Build.MODEL, ignoreCase = true)) return setting
        val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
        return if (Build.MODEL.startsWith(maker, ignoreCase = true)) Build.MODEL else "$maker ${Build.MODEL}"
    }

    private fun getprop(name: String): String? = runCatching {
        val p = ProcessBuilder("getprop", name).redirectErrorStream(true).start()
        val out = p.inputStream.bufferedReader().use { it.readText() }.trim()
        if (!p.waitFor(500, TimeUnit.MILLISECONDS)) p.destroy()
        out
    }.getOrNull()
}
