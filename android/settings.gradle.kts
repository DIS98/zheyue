pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        mavenCentral()
        maven("https://maven.aliyun.com/repository/public")
    }
}

// 产品名是折跃 / Zheyue；这里只影响 IDE 显示与 Gradle 日志，不影响包名与产物路径
rootProject.name = "Zheyue"
include(":app")
