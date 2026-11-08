package com.clashdeck.android.ui.pages

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.ui.components.EmptyHint
import com.clashdeck.android.util.DiagLog
import kotlinx.coroutines.delay

/**
 * 日志页：一个入口、两个来源。
 *
 * - **连接日志**：内核通过控制接口推过来的请求级日志（哪条规则命中、走了哪个节点）。连接时才有的实时流。
 * - **内核日志**：落盘的 core.log，包含内核启动每一步**和**应用自己的动作（连接/断开/改模式/换端口）。
 *
 * 分成两个页签而不是两个页面，是因为排查时经常要在两者之间来回对照：
 * 「我 10:33:40 点了连接」→「内核 10:33:40 起监听」→「10:33:41 报端口被占」。
 */
@Composable
fun LogsPage(vm: AppViewModel, mode: Int = 0, onModeChange: (Int) -> Unit = {}) {
    val logs by vm.logs.collectAsStateWithLifecycle()
    val core by vm.coreState.collectAsStateWithLifecycle()

    var level by remember { mutableStateOf("全部") }
    var keyword by remember { mutableStateOf("") }

    var fileLines by remember { mutableStateOf<List<String>>(emptyList()) }
    var fileExists by remember { mutableStateOf(true) }
    var reload by remember { mutableStateOf(0) }
    val clipboard = LocalClipboardManager.current

    // 内核日志：跟着看，但 3 秒一次就够（文件 IO 比网络更该省）
    LaunchedEffect(mode, reload) {
        if (mode != 1) return@LaunchedEffect
        while (true) {
            val (lines, ok) = DiagLog.tail(500)
            fileLines = lines
            fileExists = ok
            delay(3000)
        }
    }

    val filtered = when (level) {
        "全部" -> logs
        "warn" -> logs.filter { it.level.equals("warning", true) || it.level.equals("error", true) }
        "error" -> logs.filter { it.level.equals("error", true) }
        else -> logs.filter { it.level.equals(level, true) }
    }.filter { keyword.isBlank() || it.payload.contains(keyword, ignoreCase = true) }
        .asReversed()

    val fileFiltered = if (keyword.isBlank()) fileLines
    else fileLines.filter { it.contains(keyword, ignoreCase = true) }

    Column(Modifier.fillMaxSize()) {
        // ---------- 来源切换 ----------
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            LogTab("连接日志", mode == 0) { onModeChange(0) }
            LogTab("内核日志", mode == 1) { onModeChange(1) }
            Spacer(Modifier.weight(1f))
            if (mode == 0) {
                Text(
                    "${filtered.size} 条",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                TextButton(onClick = { reload++ }) { Text("刷新") }
            }
        }

        OutlinedTextField(
            value = keyword,
            onValueChange = { keyword = it },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            placeholder = { Text(if (mode == 0) "过滤关键字（域名 / 节点名）" else "过滤关键字（端口 / error / 节点名）") },
            singleLine = true,
        )

        if (mode == 0) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                listOf("全部", "info", "warn", "error").forEach { l ->
                    val selected = level == l
                    Text(
                        l,
                        style = MaterialTheme.typography.labelSmall,
                        color = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier
                            .background(
                                if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant,
                                RoundedCornerShape(20.dp),
                            )
                            .clickable { level = l }
                            .padding(horizontal = 12.dp, vertical = 6.dp),
                    )
                }
            }
        } else {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    if (fileExists) "最近 ${fileLines.size} 行 · 含内核启动与应用动作" else "还没有日志：内核尚未启动过",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                TextButton(
                    onClick = {
                        clipboard.setText(AnnotatedString(DiagLog.redact(fileFiltered.joinToString("\n"))))
                        vm.toast.value = "已复制（已脱敏：IP 与密码已打码）"
                    },
                    enabled = fileFiltered.isNotEmpty(),
                ) { Text("复制") }
                TextButton(
                    onClick = {
                        clipboard.setText(AnnotatedString(fileFiltered.joinToString("\n")))
                        vm.toast.value = "已复制原文（含节点地址，注意分享对象）"
                    },
                    enabled = fileFiltered.isNotEmpty(),
                ) { Text("原文") }
            }
        }

        if (mode == 0) {
            if (filtered.isEmpty()) {
                EmptyHint(
                    if (core.running) "还没有连接日志（内核会推送）" else "未连接。连接后这里会显示每条请求命中的规则与出口"
                )
                return@Column
            }
            LazyColumn(
                Modifier.fillMaxSize().padding(horizontal = 12.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                items(filtered) { entry ->
                    val color = when (entry.level.lowercase()) {
                        "error" -> MaterialTheme.colorScheme.error
                        "warning", "warn" -> Color(0xFFB06A00)
                        "debug" -> MaterialTheme.colorScheme.onSurfaceVariant
                        else -> MaterialTheme.colorScheme.onSurface
                    }
                    Text(
                        entry.payload,
                        style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                        color = color,
                        maxLines = 4,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        } else {
            if (fileFiltered.isEmpty()) {
                EmptyHint(if (fileExists) "日志为空" else "内核还没启动过，或日志已被清理")
                return@Column
            }
            LazyColumn(
                Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.3f)),
                contentPadding = PaddingValues(12.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                items(fileFiltered) { line ->
                    Text(
                        text = line,
                        // 等宽字体视觉上比同号无衬线更"小"，但再往下压（曾用 12sp×0.85＝10.2sp）
                        // 就低于可读下限了；labelSmall(11sp) 是这条线的底
                        style = MaterialTheme.typography.labelSmall.copy(
                            fontFamily = FontFamily.Monospace,
                        ),
                        color = when {
                            line.contains("level=error") -> MaterialTheme.colorScheme.error
                            line.contains("level=warning") -> MaterialTheme.colorScheme.tertiary
                            // 应用自己的动作单独着色：一眼能和内核日志区分开
                            line.contains("[app]") -> MaterialTheme.colorScheme.primary
                            else -> MaterialTheme.colorScheme.onSurfaceVariant
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun LogTab(text: String, selected: Boolean, onClick: () -> Unit) {
    Surface(
        shape = RoundedCornerShape(16.dp),
        color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant,
        onClick = onClick,
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
        )
    }
}
