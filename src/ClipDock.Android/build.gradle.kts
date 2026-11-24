plugins {
    id("com.android.application") version "9.2.1"
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.10"
    id("com.chaquo.python") version "17.2.0"
}

android {
    namespace = "app.clipdock.android"
    compileSdk = 37

    defaultConfig {
        applicationId = "app.clipdock.android"
        minSdk = 35
        targetSdk = 37
        versionCode = 2
        versionName = "1.1.0"
        ndk { abiFilters += setOf("arm64-v8a", "x86_64") }
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    val keyFile = providers.environmentVariable("CLIPDOCK_ANDROID_KEYSTORE").orNull
    if (keyFile != null) {
        val releaseKey = signingConfigs.create("distribution") {
            storeFile = file(keyFile)
            storePassword = providers.environmentVariable("CLIPDOCK_ANDROID_KEY_PASSWORD").get()
            keyAlias = "clipdock"
            keyPassword = storePassword
        }
        buildTypes.getByName("release").signingConfig = releaseKey
    }
    testBuildType = providers.gradleProperty("androidTestVariant").getOrElse("debug")
}

chaquopy {
    defaultConfig {
        version = "3.13"
        buildPython("uv", "run", "--no-project", "--python", "3.13", "python")
        pip { install("-r", "requirements.txt") }
    }
    sourceSets.getByName("main") { srcDir("../ClipDock.iOS/Engine") }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.10.01"))
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.11.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test.uiautomator:uiautomator:2.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
}
