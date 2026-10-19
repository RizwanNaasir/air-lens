plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.compose.compiler)
}

// The release workflow passes the tag as -PappVersion=0.1.2; local builds use this default.
val appVersion = (findProperty("appVersion") as String?) ?: "0.1.1"
// 0.1.2 -> 102, 1.4.0 -> 10400: grows with every release, as Android requires for an update.
val appVersionCode = appVersion.split('.').map { it.toInt() }.let { p -> p[0] * 10000 + p.getOrElse(1) { 0 } * 100 + p.getOrElse(2) { 0 } }

// One signing key for every build anyone installs, or Android refuses to update from one version to
// the next. The release workflow provides it from repository secrets; without it, the local debug key.
val signingKeyFile = System.getenv("AIRLENS_KEYSTORE_FILE")?.let { file(it) }?.takeIf { it.exists() }

android {
    namespace = "net.silow.airlens"
    compileSdk = 36

    defaultConfig {
        applicationId = "net.silow.airlens"
        // 29 gives us KEY_MAX_B_FRAMES, KEY_PREPEND_HEADER_TO_SYNC_FRAMES, the thermal status API and
        // the low-latency Wi-Fi lock. No 4K60-capable phone ships an older release.
        minSdk = 29
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersion
    }

    signingConfigs {
        if (signingKeyFile != null) {
            create("airlens") {
                storeFile = signingKeyFile
                storePassword = System.getenv("AIRLENS_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("AIRLENS_KEY_ALIAS")
                keyPassword = System.getenv("AIRLENS_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        // The APK that ships is the debug build type (no R8 shrinking to verify yet), signed with the
        // shared key when one is given.
        debug {
            if (signingKeyFile != null) signingConfig = signingConfigs.getByName("airlens")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    testOptions {
        // The networking and queue code is plain JVM code that only logs through android.util.Log.
        // Returning defaults for un-mocked framework calls lets those classes run in local tests.
        unitTests.isReturnDefaultValues = true
    }

    packaging {
        // Duplicate licence copies some jars carry. The Apache License 2.0 that covers every bundled
        // library ships once, readable in the app: res/raw/apache_license_2_0.txt (OpenSourceLicenses.kt).
        resources { excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    val composeBom = platform(libs.androidx.compose.bom)
    implementation(composeBom)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.kotlinx.coroutines.android)

    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons)
    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
    testImplementation(libs.org.json)
}
