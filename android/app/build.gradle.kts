import java.util.Properties

import java.text.SimpleDateFormat
import java.util.Date

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.clashdeck.android"
    compileSdk = 35

    // 构建时间写进 BuildConfig：出问题时要能对上"这一版到底是什么时候打的包"
    // ---------- 版本号：唯一真源是仓库根的 version.txt ----------
    // 显示版一律三段（Windows 口径）；versionCode 是平台派生物，由显示版按固定公式算出：
    //   MAJOR * 1_000_000 + MINOR * 1_000 + PATCH
    // 不允许单独手改 —— 手改必然出现"界面写 1.0.23、系统里是 23"这种脱节，
    // 而它不会报错，只会在升级判断上出错（系统按 versionCode 比大小）。
    val appVersion = rootProject.file("version.txt").readText().trim()
    val versionParts = appVersion.split(".")
    require(versionParts.size == 3) { "version.txt 必须是三段式 MAJOR.MINOR.PATCH，当前：$appVersion" }
    val (verMajor, verMinor, verPatch) = versionParts.map { it.toInt() }
    val appVersionCode = verMajor * 1_000_000 + verMinor * 1_000 + verPatch

    val buildTime = SimpleDateFormat("yyyy-MM-dd HH:mm").format(Date())

    defaultConfig {
        applicationId = "com.clashdeck.android"
        minSdk = 26
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
        buildConfigField("String", "BUILD_TIME", "\"$buildTime\"")

        // 应用内升级的两个常量。
        // UPDATE_BASE 是分发根地址；该地址对**每个请求**都要求 ticket（不带就 403），
        // 所以这里一起编进包里，UpdateRepo 拼 URL 时统一带上。
        // 注意：重装/换站点后要同步改这两行，否则站内升级会以 403 失败。
        // 指向分享页本站（2026-11-07 回迁）：腾讯云云通道已废弃，不再有 /zheyue-android/ 路径前缀。
        // Range 续传由站点上的 serve_range.py 提供；不支持时 UpdateRepo 自动退回整包重下。
        buildConfigField("String", "UPDATE_BASE", "\"https://updates.example.com"")
        buildConfigField("String", "UPDATE_ticket", "\"<REDACTED>\"")

        ndk {
            // 交付包只带 arm64（手机）；加 -PwithX86 时把 x86_64 也打进去，用于模拟器验证
            abiFilters += if (project.hasProperty("withX86")) {
                listOf("arm64-v8a", "x86_64")
            } else {
                listOf("arm64-v8a")
            }
        }
    }

    /**
     * 签名口令**不进仓库**。
     *
     * 原来 storePassword / keyPassword 是明文写在这里（committed），等于口令跟源码一起
     * 流到任何拿到仓库的机器上；README 里还写了一遍。keystore 文件本身被 .gitignore 排除，
     * 但"文件不进库 + 口令进库"两者加起来，防护等于零。
     *
     * 现在从 `keystore/keystore.properties` 读（该目录整体不入库）。
     * 文件缺失时**构建直接失败并给出指引**，而不是悄悄退回某个默认口令——
     * 后者会让某台机器用不同口令签出包，覆盖安装时报"签名不一致"，很难查。
     */
    val keystoreProps = Properties().apply {
        val f = rootProject.file("keystore/keystore.properties")
        if (f.exists()) f.inputStream().use { load(it) }
    }

    signingConfigs {
        create("self") {
            storeFile = file("../keystore/clashdeck.jks")
            storePassword = keystoreProps.getProperty("storePassword")
                ?: error("缺少 keystore/keystore.properties 的 storePassword；该文件不入库，内容见 README §三")
            keyAlias = keystoreProps.getProperty("keyAlias") ?: "clashdeck"
            keyPassword = keystoreProps.getProperty("keyPassword")
                ?: keystoreProps.getProperty("storePassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            isShrinkResources = false
            signingConfig = signingConfigs.getByName("self")
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        debug {
            signingConfig = signingConfigs.getByName("self")
            // 调试包指向本机测试源（模拟器用 10.0.2.2 访问宿主机）。
            // 目的：能在不改线上站点的情况下，完整跑「检查→下载→校验→安装」。
            // release 包仍然用 defaultConfig 里的真实站点。
            buildConfigField("String", "UPDATE_BASE", "\"http://10.0.2.2:18080\"")
            buildConfigField("String", "UPDATE_ticket", "\"TESTCODE\"")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    // Go 的 c-shared 库必须解压到 nativeLibraryDir 才能被 System.loadLibrary 找到
    packaging {
        jniLibs.useLegacyPackaging = true
        resources.excludes += setOf(
            "/META-INF/{AL2.0,LGPL2.1}",
            "/META-INF/DEPENDENCIES",
            "/META-INF/INDEX.LIST"
        )
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.09.03"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.6")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.yaml:snakeyaml:2.2")

    debugImplementation("androidx.compose.ui:ui-tooling")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.yaml:snakeyaml:2.2")
}

// 单测可以拿真实订阅跑（不把含凭据的订阅放进仓库）：
//   gradle :app:testReleaseUnitTest -PtestProfile="./"
tasks.withType<Test>().configureEach {
    (project.findProperty("testProfile") as String?)?.let {
        systemProperty("clashdeck.testProfile", it)
    }
    testLogging { showStandardStreams = true }
}
