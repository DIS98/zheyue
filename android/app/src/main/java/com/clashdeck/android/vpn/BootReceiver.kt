package com.clashdeck.android.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.VpnService
import com.clashdeck.android.data.SettingsRepo

/**
 * 开机自启：只在用户已开启该选项、且系统里已有 VPN 授权时拉起。
 * 没有授权时不能在接收器里弹授权框，直接跳过（用户下次打开 App 再说）。
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        val settings = SettingsRepo(context)
        if (!settings.autoStart) return
        if (VpnService.prepare(context) != null) return
        TunService.start(context)
    }
}
