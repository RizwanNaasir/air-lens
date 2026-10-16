package net.silow.airlens.util

import android.annotation.SuppressLint
import android.content.Context
import android.provider.Settings
import java.security.MessageDigest

/**
 * A stable id for this phone, so the PC recognises it on Wi-Fi after meeting it over USB, and
 * after DHCP hands it a new address. The first 16 hex digits of SHA-256 over the Android ID (which
 * is already scoped to this app's signing key), so the Android ID itself never leaves the phone.
 */
object DeviceId {
    @Volatile private var cached: String? = null

    @SuppressLint("HardwareIds") // hashed before use, see above
    fun get(context: Context): String = cached ?: run {
        val androidId = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown"
        of(androidId).also { cached = it }
    }

    fun of(androidId: String): String =
        MessageDigest.getInstance("SHA-256").digest("airlens:$androidId".toByteArray())
            .take(8).joinToString("") { "%02x".format(it) }
}
