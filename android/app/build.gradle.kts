import java.util.Properties

plugins { id("com.android.application") }
dependencies {
    implementation("org.yaml:snakeyaml:2.5")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
android {
    namespace = "io.regionlauncher.android"
    compileSdk = 37
    defaultConfig {
        applicationId = "io.regionlauncher.android"
        minSdk = 33
        targetSdk = 37
        versionCode = 1
        versionName = "0.1.0-dev"
    }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    val signingFile=rootProject.file(".private/signing.properties")
    if(signingFile.exists()) {
        val keys=Properties().apply { signingFile.inputStream().use { load(it) } }
        signingConfigs.create("localRelease") {
            storeFile=rootProject.file(".private/release.jks")
            storePassword=keys.getProperty("password")
            keyAlias="region-launcher"
            keyPassword=keys.getProperty("password")
        }
    }
    buildTypes { getByName("release") { isMinifyEnabled = false; signingConfig=signingConfigs.findByName("localRelease") } }
    packaging { jniLibs { useLegacyPackaging = true } }
}
