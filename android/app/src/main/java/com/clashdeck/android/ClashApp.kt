package com.clashdeck.android

import android.app.Activity
import android.app.Application
import android.os.Bundle
import com.clashdeck.android.data.AppPaths
import com.clashdeck.android.util.AppVisible

class ClashApp : Application() {

    override fun onCreate() {
        super.onCreate()
        instance = this
        AppPaths.init(this)

        // 记录前后台状态：Android 10+ 后台应用无法启动 Activity，
        // 升级下载完成时据此决定"直接拉安装器"还是"发通知让用户回来点"。
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            override fun onActivityStarted(activity: Activity) {
                AppVisible.isVisible = true
            }

            override fun onActivityStopped(activity: Activity) {
                AppVisible.isVisible = false
            }

            override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
            override fun onActivityResumed(activity: Activity) = Unit
            override fun onActivityPaused(activity: Activity) = Unit
            override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
            override fun onActivityDestroyed(activity: Activity) = Unit
        })

        // 资源摊平放到后台线程做，别卡启动
        Thread({
            runCatching { AppPaths.ensureAssets(this) }
        }, "clashdeck-assets").start()
    }

    companion object {
        lateinit var instance: ClashApp
            private set
    }
}
