package net.silow.airlens

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import net.silow.airlens.util.AirLog

class AirLensApp : Application() {
    override fun onCreate() {
        super.onCreate()
        AirLog.init(this)
        val channel = NotificationChannel(
            CHANNEL_STREAMING,
            getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW, // no sound: it appears every time streaming starts
        ).apply { description = getString(R.string.notification_channel_description) }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    companion object {
        const val CHANNEL_STREAMING = "streaming"
    }
}
