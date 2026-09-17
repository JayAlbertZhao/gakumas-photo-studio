plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "org.digital_kotone.arphoto"
    compileSdk = 36

    defaultConfig {
        applicationId = "org.digital_kotone.arphoto"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.06.01"))
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.compose.material3:material3")
    // 4.25 is the newest reviewed line here that does not require compileSdk 37.
    implementation("io.github.sceneview:arsceneview:4.25.0")
}
