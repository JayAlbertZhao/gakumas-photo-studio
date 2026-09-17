plugins {
    id("com.android.library")
}

android {
    namespace = "org.digital_kotone.arphoto.toolkit"
    compileSdk = 36

    defaultConfig { minSdk = 29 }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    api("androidx.activity:activity:1.11.0")
    api("io.github.sceneview:arsceneview:4.25.0")
}
