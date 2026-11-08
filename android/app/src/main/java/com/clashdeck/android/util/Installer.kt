package com.clashdeck.android.util

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.app.NotificationCompat
import androidx.core.content.FileProvider
import com.clashdeck.android.MainActivity
import com.clashdeck.android.R
import java.io.File

/** App 是否在前台。由 ClashApp 的 ActivityLifecycleCallbacks 维护。 */
object AppVisible {
    @Volatile
    var isVisible: Boolean = false
}

/**
 * 把下载好的 APK 交给系统安装器。
 *
 * Android 上有两道门，两道的失败方式都是**静默的**，所以必须显式处理：
 * 1. `REQUEST_INSTALL_PACKAGES` + 用户授权「安装未知应用」—— 未授权时 startActivity 会弹出系统页而不是安装
 * 2. Android 10+ 禁止后台应用启动 Activity —— App 在后台时 startActivity 直接被丢掉，不抛异常
 *    （这是 azhon/AppUpdate README 里明确记录的坑）
 */
object Installer {

    /** 镜像下载完成的包，若因权限或后台被拦，界面要能"再点一次安装"。 */
    fun canInstall(context: Context): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            runCatching { context.packageManager.canRequestPackageInstalls() }.getOrDefault(false)
        } else {
            true
        }

    fun openUnknownSourcesSettings(context: Context) {
        val direct = Intent(
            Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
            Uri.parse("package:${context.packageName}"),
        ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val fallback = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (runCatching { context.startActivity(direct) }.isFailure) {
            runCatching { context.startActivity(fallback) }
        }
    }

    /**
     * 尝试拉起安装器。
     * @return null 表示已拉起或已转交通知；非 null 是要显示给用户的原因。
     */
    fun install(context: Context, apk: File, versionName: String): String? {
        if (!apk.exists() || apk.length() == 0L) return "安装包不存在，请重新下载"

        if (!canInstall(context)) {
            openUnknownSourcesSettings(context)
            return "需要先允许「安装未知应用」，已帮你打开设置页；授权后回到这里再点一次安装"
        }

        // 后台启动 Activity 会被系统丢弃，且不抛异常。
        // 所以不在前台时走通知，让用户点一下回前台——这不是保守，是唯一能走通的路径。
        if (!AppVisible.isVisible) {
            notifyReady(context, versionName)
            return "已下载完成。回到折跃点「安装」即可（已发通知提醒）"
        }

        return runCatching {
            val uri = FileProvider.getUriForFile(
                context,
                "${context.packageName}.fileprovider",
                apk,
            )
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        }.exceptionOrNull()?.let { "无法打开安装器：${it.message ?: it.javaClass.simpleName}" }
    }

    private const val CHANNEL_ID = "clashdeck_update_v3"

    /** 历史渠道：启动时删除，避免系统通知设置里出现带旧图标的孤儿条目。 */
    private val LEGACY_CHANNELS = listOf("clashdeck_update", "clashdeck_update_v2")
    private const val NOTIFICATION_ID = 2001

    private fun notifyReady(context: Context, versionName: String) {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            runCatching {
                LEGACY_CHANNELS.forEach { legacy ->
                    if (nm.getNotificationChannel(legacy) != null) {
                        nm.deleteNotificationChannel(legacy)
                    }
                }
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, "应用更新", NotificationManager.IMPORTANCE_DEFAULT),
                )
            }
        }
        val content = PendingIntent.getActivity(
            context,
            2001,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setContentTitle("折跃 $versionName 已下载")
            .setContentText("点击返回应用完成安装")
            .setSmallIcon(R.drawable.ic_stat_mark)
            .setAutoCancel(true)
            .setContentIntent(content)
            .build()
        runCatching { nm.notify(NOTIFICATION_ID, notification) }
    }
}
