package net.silow.airlens.ui

import android.os.Build
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext

/**
 * Stock Material 3: the wallpaper-based dynamic scheme on Android 12+, the baseline scheme before
 * that (the P30 runs Android 10).
 *
 * Always dark: the app is used next to a running camera, often in a dim room, and a bright UI
 * would light the subject and heat an OLED panel for nothing.
 */
@Composable
fun AirLensTheme(content: @Composable () -> Unit) {
    val colors = if (Build.VERSION.SDK_INT >= 31) dynamicDarkColorScheme(LocalContext.current) else darkColorScheme()
    MaterialTheme(colorScheme = colors) {
        // A root Surface sets the default content colour; without it, text outside cards is black.
        Surface(color = colors.background, contentColor = colors.onBackground, content = content)
    }
}
