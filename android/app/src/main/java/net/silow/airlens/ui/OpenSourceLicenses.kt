package net.silow.airlens.ui

import android.content.Context
import android.view.WindowManager
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.window.DialogWindowProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import net.silow.airlens.R

/** One open-source project bundled in the APK. */
internal data class OpenSourceProject(
    val name: String,
    val parts: String,
    val copyright: String,
    val url: String,
)

/**
 * Every project the APK bundles (`./gradlew :app:dependencies --configuration debugRuntimeClasspath`).
 * All of them are under the Apache License 2.0, whose full text ships as
 * `res/raw/apache_license_2_0.txt` (Google's copy, from the AndroidX AARs). None of them carries
 * a NOTICE file. Add a line here when a dependency from a new project comes in.
 */
internal val bundledOpenSource = listOf(
    OpenSourceProject(
        "Android Jetpack (AndroidX)",
        "Activity, Annotation, Collection, Core, Lifecycle, SavedState, Startup, Window and the libraries they use",
        "Copyright The Android Open Source Project",
        "https://developer.android.com/jetpack/androidx",
    ),
    OpenSourceProject(
        "Jetpack Compose",
        "Runtime, UI, Foundation, Animation, Material, Material 3 and Material Icons",
        "Copyright The Android Open Source Project",
        "https://developer.android.com/compose",
    ),
    OpenSourceProject(
        "Kotlin standard library",
        "kotlin-stdlib",
        "Copyright JetBrains s.r.o. and Kotlin Programming Language contributors",
        "https://github.com/JetBrains/kotlin",
    ),
    OpenSourceProject(
        "kotlinx.coroutines",
        "kotlinx-coroutines-core and kotlinx-coroutines-android",
        "Copyright JetBrains s.r.o. and contributors",
        "https://github.com/Kotlin/kotlinx.coroutines",
    ),
    OpenSourceProject(
        "kotlinx.serialization",
        "kotlinx-serialization-core",
        "Copyright JetBrains s.r.o. and contributors",
        "https://github.com/Kotlin/kotlinx.serialization",
    ),
    OpenSourceProject(
        "JetBrains Java Annotations",
        "org.jetbrains:annotations",
        "Copyright JetBrains s.r.o.",
        "https://github.com/JetBrains/java-annotations",
    ),
    OpenSourceProject(
        "JSpecify",
        "org.jspecify:jspecify",
        "Copyright The JSpecify Authors",
        "https://jspecify.dev",
    ),
    OpenSourceProject(
        "Guava ListenableFuture",
        "com.google.guava:listenablefuture",
        "Copyright The Guava Authors",
        "https://github.com/google/guava",
    ),
)

internal fun apacheLicenseText(context: Context): String =
    context.resources.openRawResource(R.raw.apache_license_2_0).bufferedReader().use { it.readText() }

/** Full-screen list of the bundled projects, then the Apache License 2.0 in full. */
@Composable
internal fun LicensesDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val license by produceState("") {
        value = withContext(Dispatchers.IO) { runCatching { apacheLicenseText(context) }.getOrDefault("") }
    }
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        // Cover the whole screen, the camera cut-out too, as the activity behind does; otherwise a
        // landscape strip of the app shows beside the dialog. The content stays in the safe area.
        val window = (LocalView.current.parent as? DialogWindowProvider)?.window
        SideEffect {
            window?.let { w ->
                w.attributes = w.attributes.apply {
                    layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
                }
            }
        }
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize().safeDrawingPadding()) {
                Row(
                    Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp, top = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("Open-source licenses", Modifier.weight(1f), style = MaterialTheme.typography.titleLarge)
                    TextButton(onClick = onDismiss) { Text("Close") }
                }
                LazyColumn(
                    contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 24.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    item {
                        Text(
                            "AirLens is built with these open-source projects. All of them are under " +
                                "the Apache License 2.0, printed in full below.",
                            Modifier.padding(horizontal = 4.dp),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    items(bundledOpenSource) { project ->
                        Card(
                            modifier = Modifier.fillMaxWidth(),
                            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
                        ) {
                            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                Text(project.name, style = MaterialTheme.typography.titleSmall)
                                Text(project.parts, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(project.copyright, style = MaterialTheme.typography.bodySmall)
                                Text("Apache License 2.0 · ${project.url}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
                            }
                        }
                    }
                    item {
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 8.dp)) {
                            Text("Apache License 2.0", style = MaterialTheme.typography.titleMedium)
                            Text(license, fontFamily = FontFamily.Monospace, fontSize = 11.sp, lineHeight = 15.sp)
                        }
                    }
                }
            }
        }
    }
}
