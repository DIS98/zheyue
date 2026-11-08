package com.clashdeck.android.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.util.formatBytes

/**
 * 发现新版本时的横幅。
 *
 * 三个状态，界面必须能分清（否则用户会以为"点了没反应"）：
 * 1. 待更新 —— 显示版本、大小、更新说明、立即更新
 * 2. 下载中 —— 显示进度条与已下载/总量
 * 3. 已下载待安装 —— 按钮变成「安装」，因为安装确认框必须由系统弹，用户得再点一次
 */
@Composable
fun UpdateBanner(vm: AppViewModel, modifier: Modifier = Modifier) {
    val info by vm.updateInfo.collectAsStateWithLifecycle()
    val dismissed by vm.updateDismissed.collectAsStateWithLifecycle()
    val progress by vm.updateProgress.collectAsStateWithLifecycle()
    val apk by vm.updateApk.collectAsStateWithLifecycle()
    val error by vm.updateError.collectAsStateWithLifecycle()
    val checking by vm.updateChecking.collectAsStateWithLifecycle()

    val item = info ?: return
    if (dismissed && !item.forced) return

    val accent = MaterialTheme.colorScheme.primary

    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(20.dp),
        color = accent.copy(alpha = 0.10f),
    ) {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.SystemUpdate,
                    contentDescription = null,
                    tint = accent,
                    modifier = Modifier.size(20.dp),
                )
                Spacer(Modifier.width(8.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        "新版本 ${item.versionName}",
                        style = MaterialTheme.typography.titleSmall,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    Text(
                        buildString {
                            if (item.size > 0) append(formatBytes(item.size)) else append("大小未知")
                            if (item.publishedAt.isNotBlank()) append(" · ").append(item.publishedAt)
                            if (item.forced) append(" · 必须更新")
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            if (item.notes.isNotEmpty()) {
                Spacer(Modifier.height(10.dp))
                item.notes.take(4).forEach { line ->
                    Text(
                        "· $line",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            val p = progress
            if (p != null) {
                Spacer(Modifier.height(12.dp))
                // 自绘进度条：Material3 的 LinearProgressIndicator 在不同版本里 progress 参数形态不一致，
                // 这里只需要一根条，自绘既省事也不会因升级 compose 版本而编译失败。
                Box(
                    Modifier
                        .fillMaxWidth()
                        .height(6.dp)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.surfaceVariant),
                ) {
                    Box(
                        Modifier
                            .fillMaxWidth(p.fraction)
                            .fillMaxHeight()
                            .background(accent),
                    )
                }
                Spacer(Modifier.height(6.dp))
                Text(
                    if (p.total > 0) {
                        "正在下载 ${(p.fraction * 100).toInt()}% · ${formatBytes(p.written)} / ${formatBytes(p.total)}"
                    } else {
                        "正在下载 ${formatBytes(p.written)}"
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                Spacer(Modifier.height(12.dp))
                Row(
                    Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Button(
                        onClick = { if (apk != null) vm.installDownloaded() else vm.startUpdate() },
                        enabled = !checking,
                    ) {
                        Text(if (apk != null) "安装 ${item.versionName}" else "立即更新")
                    }
                    if (!item.forced) {
                        TextButton(onClick = { vm.dismissUpdate() }) { Text("稍后") }
                    }
                    Spacer(Modifier.weight(1f))
                    if (apk == null) {
                        TextButton(onClick = { vm.checkUpdate(true) }, enabled = !checking) {
                            Text(if (checking) "检查中…" else "重新检查")
                        }
                    }
                }
            }

            error?.let {
                Spacer(Modifier.height(8.dp))
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
            }
        }
    }
}
