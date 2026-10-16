package net.silow.airlens.power

import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings

/**
 * Helps the user stop OEM battery managers from killing a long stream.
 *
 * Huawei/Honor (EMUI, HarmonyOS) are the most aggressive: the fix is *Settings → Battery →
 * App launch → AirLens → Manage manually*, with all three toggles on. Other OEMs get the standard
 * battery-optimisation screen. Activity names are best-effort; every candidate is tried in order
 * and the generic app-details screen is the last resort, so a renamed OEM screen never crashes.
 */
object OemPowerGuide {
    val isHuaweiFamily: Boolean
        get() = Build.MANUFACTURER.equals("HUAWEI", ignoreCase = true) ||
            Build.MANUFACTURER.equals("HONOR", ignoreCase = true)

    fun instructions(): String = if (isHuaweiFamily) {
        "EMUI may stop AirLens during long streams. Open Settings → Battery → App launch, find AirLens, " +
            "switch it to \"Manage manually\" and enable Auto-launch, Secondary launch and Run in background. " +
            "For USB, also enable Developer options → \"Allow ADB debugging in charge only mode\"."
    } else {
        "If streaming stops after a while, exclude AirLens from battery optimisation."
    }

    fun isIgnoringBatteryOptimizations(context: Context): Boolean =
        context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(context.packageName)

    /** Opens the most specific settings screen available. Returns false if none could be opened. */
    fun openSettings(context: Context): Boolean {
        for (intent in candidates(context)) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (_: ActivityNotFoundException) {
            } catch (_: SecurityException) {
            }
        }
        return false
    }

    @SuppressLint("BatteryLife")
    private fun candidates(context: Context): List<Intent> = buildList {
        if (isHuaweiFamily) {
            add(component("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"))
            add(component("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"))
        }
        add(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        add(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null)))
    }

    private fun component(pkg: String, cls: String) = Intent().setComponent(ComponentName(pkg, cls))
}
