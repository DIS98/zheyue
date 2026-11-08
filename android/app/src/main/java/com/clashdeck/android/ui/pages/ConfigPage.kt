package com.clashdeck.android.ui.pages

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.foundation.Image
import androidx.compose.ui.res.painterResource
import androidx.compose.foundation.layout.width
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import com.clashdeck.android.BuildConfig
import com.clashdeck.android.R
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.data.AppPaths
import com.clashdeck.android.data.ProfileInfo
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.ui.components.EmptyHint
import com.clashdeck.android.ui.components.KvRow
import com.clashdeck.android.ui.components.SectionCard
import com.clashdeck.android.util.expiryLabel
import com.clashdeck.android.util.formatBytes
import com.clashdeck.android.util.formatTime

@Composable
fun ConfigPage(vm: AppViewModel, onOpenCoreLog: () -> Unit = {}) {
    val profiles by vm.profiles.collectAsStateWithLifecycle()
    val busy by vm.busy.collectAsStateWithLifecycle()
    val coreVersion by vm.coreVersion.collectAsStateWithLifecycle()

    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var showPaste by remember { mutableStateOf(false) }
    var pasteText by remember { mutableStateOf("") }
    var toDelete by remember { mutableStateOf<ProfileInfo?>(null) }

    var mixedPort by remember { mutableStateOf(vm.settings.mixedPort.toString()) }
    var apiPort by remember { mutableStateOf(vm.settings.apiPort.toString()) }
    var testUrl by remember { mutableStateOf(vm.settings.testUrl) }
    var timeout by remember { mutableStateOf(vm.settings.testTimeout.toString()) }
    var autoStart by remember { mutableStateOf(vm.settings.autoStart) }
    var builtinRules by remember { mutableStateOf(vm.settings.builtinRules) }

    val activeId by vm.activeProfileId.collectAsStateWithLifecycle()

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        // ---------- 订阅列表 ----------
        SectionCard(
            title = "订阅与配置",
            subtitle = if (profiles.isEmpty()) "还没有配置" else "共 ${profiles.size} 份，点一下切换为当前配置",
            trailing = {
                if (busy) CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
            },
        ) {
            if (profiles.isEmpty()) {
                EmptyHint("在下方导入订阅链接，或直接粘贴 YAML")
            }
            profiles.forEach { profile ->
                ProfileRow(
                    profile = profile,
                    active = profile.id == (activeId ?: profiles.firstOrNull()?.id),
                    onActivate = { vm.activateProfile(profile) },
                    onUpdate = { vm.updateProfile(profile) },
                    onDelete = { toDelete = profile },
                )
            }
        }

        // ---------- 导入 ----------
        SectionCard(title = "导入订阅", subtitle = "支持 Clash / mihomo 的 YAML 链接，也支持 base64 订阅") {
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                label = { Text("名称（可留空）") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(
                value = url,
                onValueChange = { url = it },
                label = { Text("订阅链接") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                Button(
                    onClick = {
                        vm.importFromUrl(name, url)
                        url = ""
                        name = ""
                    },
                    enabled = url.startsWith("http") && !busy,
                ) { Text("导入") }
                TextButton(onClick = { showPaste = true }) { Text("粘贴 YAML") }
            }
        }

        // ---------- 端口 ----------
        SectionCard(title = "端口", subtitle = "改完会重启内核生效") {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = mixedPort,
                    onValueChange = { mixedPort = it.filter { c -> c.isDigit() } },
                    label = { Text("混合端口") },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                OutlinedTextField(
                    value = apiPort,
                    onValueChange = { apiPort = it.filter { c -> c.isDigit() } },
                    label = { Text("控制端口") },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
            }
            Spacer(Modifier.height(10.dp))
            Button(
                onClick = {
                    val m = mixedPort.toIntOrNull()
                    val a = apiPort.toIntOrNull()
                    if (m != null && a != null && m in 1024..65535 && a in 1024..65535 && m != a) {
                        vm.savePorts(m, a)
                    } else {
                        vm.toast.value = "端口要在 1024-65535 之间且不能相同"
                    }
                },
            ) { Text("保存端口") }
        }

        // ---------- 测速 ----------
        SectionCard(title = "测速", subtitle = "改完立即生效，下次测速就用新地址") {
            OutlinedTextField(
                value = testUrl,
                onValueChange = { testUrl = it },
                label = { Text("测速地址") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(
                value = timeout,
                onValueChange = { timeout = it.filter { c -> c.isDigit() } },
                label = { Text("超时（毫秒）") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(10.dp))
            Button(
                onClick = {
                    val raw = timeout.toIntOrNull() ?: 5000
                    val t = raw.coerceIn(1000, 30000)
                    if (!testUrl.startsWith("http")) {
                        vm.toast.value = "测速地址要 http(s) 开头"
                    } else {
                        vm.saveTestTarget(testUrl, t)
                        timeout = t.toString()
                        vm.toast.value = "已保存测速设置"
                    }
                },
            ) { Text("保存测速设置") }
        }

        // ---------- 后台保活 ----------
        SectionCard(
            title = "后台保活",
            subtitle = "安卓会限制后台应用，不设置的话连接可能几分钟后被系统掐掉",
        ) {
            val ctx = LocalContext.current
            val ignoring = remember {
                val pm = ctx.getSystemService(Context.POWER_SERVICE) as? PowerManager
                pm?.isIgnoringBatteryOptimizations(ctx.packageName) ?: false
            }

            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    if (ignoring) "已加入电池优化白名单" else "尚未加入白名单",
                    style = MaterialTheme.typography.bodyMedium,
                    color = if (ignoring) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                )
                Button(onClick = { openBatterySettings(ctx) }) { Text("去设置") }
            }

            Spacer(Modifier.height(8.dp))
            Text(
                "国产系统还需要手动加一层：\n" +
                    "小米：设置 → 应用设置 → 应用管理 → 折跃 → 省电策略 → 无限制\n" +
                    "华为/荣耀：设置 → 电池 → 启动管理 → 改为手动管理，三项全开\n" +
                    "OPPO / vivo：设置 → 电池 → 应用耗电管理 → 允许后台运行",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        // ---------- 分流 ----------
        // 分流模式（智能 / 严格）的开关放在概览页，紧跟"规则/全局/直连"——
        // 那是用户每次连接都会看到的地方，没必要让他为切一个模式多跳一层。
        // 这里只留内置规则开关：它决定"有没有规则可用"，与模式是两件事。
        SectionCard(
            title = "内置分流规则",
            subtitle = "15 个规则集随包分发，不依赖外网。分流模式在「概览」页切换",
        ) {
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("启用内置规则", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "关闭后只用订阅自带规则，严格模式会变得很严",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Switch(
                    checked = builtinRules,
                    onCheckedChange = {
                        builtinRules = it
                        vm.setBuiltinRules(it)
                    },
                )
            }
        }

        // ---------- 常驻 ----------
        SectionCard(title = "常驻") {
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("开机自启", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "需要系统里已有 VPN 授权，未授权时不会弹窗",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Switch(
                    checked = autoStart,
                    onCheckedChange = {
                        autoStart = it
                        vm.setAutoStart(it)
                    },
                )
            }
        }

        // ---------- 诊断 ----------
        SectionCard(
            title = "诊断",
            subtitle = "日志是排查问题的唯一依据，出问题先看这里",
        ) {
            var debugLog by remember { mutableStateOf(vm.settings.logLevel == "debug") }

            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("详细日志", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        if (debugLog) "已开启：记录每条连接与匹配过程（日志增长很快，排查完建议关掉）"
                        else "只记录启动、规则与错误。排查疑难问题时再打开",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Switch(
                    checked = debugLog,
                    onCheckedChange = {
                        debugLog = it
                        vm.setVerboseLog(it)
                    },
                )
            }

            Spacer(Modifier.height(10.dp))

            // 控制接口挂掉时，日志页是唯一还能看的信息源
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("查看日志", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "连接日志（实时请求）与内核日志（启动与错误）",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(onClick = onOpenCoreLog) { Text("打开") }
            }
        }

        // ---------- 关于 ----------
        SectionCard(
            title = "关于",
            subtitle = "发布版本 ${BuildConfig.VERSION_NAME}",
            trailing = {
                val clipboard = LocalClipboardManager.current
                TextButton(onClick = {
                    clipboard.setText(AnnotatedString(versionReport(coreVersion)))
                    vm.toast.value = "版本信息已复制"
                }) { Text("复制") }
            },
        ) {
            // 品牌标识：直接复用通知大图标那份矢量（与启动图标同源的双箭头标记），
            // 这样"应用图标 / 通知图标 / 关于页"三处是同一个视觉来源，不会各画一版然后漂移。
            Row(verticalAlignment = Alignment.CenterVertically) {
                Image(
                    painter = painterResource(R.drawable.ic_notif_large),
                    contentDescription = null,
                    modifier = Modifier.size(36.dp),
                )
                Spacer(Modifier.width(12.dp))
                Column {
                    Text("折跃", style = MaterialTheme.typography.titleMedium)
                    Text(
                        "自用代理内核控制台",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Spacer(Modifier.height(10.dp))
            HorizontalDivider(thickness = 1.dp, color = MaterialTheme.colorScheme.outlineVariant)
            Spacer(Modifier.height(4.dp))

            KvRow("发布版本", BuildConfig.VERSION_NAME)
            KvRow("构建时间", BuildConfig.BUILD_TIME)
            KvRow(
                "内核版本",
                if (coreVersion.isBlank()) "mihomo（未连接时读不到）" else "mihomo $coreVersion",
            )
            val updInfo by vm.updateInfo.collectAsStateWithLifecycle()
            val updChecking by vm.updateChecking.collectAsStateWithLifecycle()
            val updProgress by vm.updateProgress.collectAsStateWithLifecycle()
            val updError by vm.updateError.collectAsStateWithLifecycle()
            val checkedAt by vm.updateCheckedAt.collectAsStateWithLifecycle()

            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("应用内升级", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        when {
                            updProgress != null -> "正在下载 ${(updProgress!!.fraction * 100).toInt()}%"
                            updInfo != null -> "有新版本 ${updInfo!!.versionName}，回概览页更新"
                            updError != null -> updError!!
                            updChecking -> "正在检查…"
                            checkedAt <= 0L -> "还没检查过，点右侧「检查更新」"
                            else -> "当前已是最新（上次检查 ${formatTime(checkedAt)}）"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(
                    onClick = { vm.checkUpdate(true) },
                    enabled = !updChecking && updProgress == null,
                ) { Text(if (updChecking) "检查中…" else "检查更新") }
            }

            KvRow("工作目录", AppPaths.home.absolutePath)
            // 不能写死 arm64-v8a：模拟器上跑的是 x86_64 的 so，
            // 显示错的架构会让人按错的 ABI 去排查（实测在模拟器上就被这行误导过）。
            KvRow("运行库", "libclash.so (${android.os.Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown"})")
        }

        Spacer(Modifier.height(20.dp))
    }

    if (showPaste) {
        AlertDialog(
            onDismissRequest = { showPaste = false },
            title = { Text("粘贴 Clash 配置") },
            text = {
                OutlinedTextField(
                    value = pasteText,
                    onValueChange = { pasteText = it },
                    label = { Text("YAML 内容") },
                    modifier = Modifier.fillMaxWidth().height(220.dp),
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    vm.importFromText(name.ifBlank { "粘贴配置" }, pasteText)
                    pasteText = ""
                    showPaste = false
                }) { Text("导入") }
            },
            dismissButton = { TextButton(onClick = { showPaste = false }) { Text("取消") } },
        )
    }

    toDelete?.let { profile ->
        AlertDialog(
            onDismissRequest = { toDelete = null },
            title = { Text("删除配置") },
            text = { Text("确定删除「${profile.name}」？文件会一起删掉。") },
            confirmButton = {
                TextButton(onClick = {
                    vm.deleteProfile(profile)
                    toDelete = null
                }) { Text("删除") }
            },
            dismissButton = { TextButton(onClick = { toDelete = null }) { Text("取消") } },
        )
    }
}

@Composable
private fun ProfileRow(
    profile: ProfileInfo,
    active: Boolean,
    onActivate: () -> Unit,
    onUpdate: () -> Unit,
    onDelete: () -> Unit,
) {
    Column(
        Modifier
            .fillMaxWidth()
            .clickable { onActivate() }
            .padding(vertical = 8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Spacer(
                Modifier
                    .size(8.dp)
                    .background(
                        if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
                        CircleShape,
                    ),
            )
            Spacer(Modifier.size(8.dp))
            Text(
                profile.name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
                color = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
            )
            if (profile.url != null) {
                TextButton(onClick = onUpdate) { Text("更新") }
            }
            TextButton(onClick = onDelete) { Text("删除") }
        }
        Text(
            buildString {
                append(formatTime(profile.updatedAt))
                append(" · ")
                append(formatBytes(profile.size))
                profile.userInfo?.let { info ->
                    if (info.hasQuota) {
                        append(" · 已用 ")
                        append(formatBytes(info.used))
                        append("/")
                        append(formatBytes(info.total))
                        if (info.expire > 0) append(" · ").append(expiryLabel(info.expireAtMillis))
                    }
                }
                if (profile.url == null) append(" · 本地")
            },
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            fontFamily = FontFamily.Default,
        )
    }
}

/** 先尝试直接申请本应用忽略电池优化；部分 ROM 没有这个页面，退回到列表页。 */
private fun openBatterySettings(ctx: Context) {
    val direct = Intent(
        Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
        Uri.parse("package:" + ctx.packageName),
    ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    val fallback = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    if (runCatching { ctx.startActivity(direct) }.isFailure) {
        runCatching { ctx.startActivity(fallback) }
    }
}

/** 一次性给出定位问题需要的全部信息，用户复制即可发过来。 */
private fun versionReport(coreVersion: String): String = buildString {
    append("折跃 ").append(BuildConfig.VERSION_NAME)
    append(" (构建号 ").append(BuildConfig.VERSION_CODE).append(")\n")
    append("构建 ").append(BuildConfig.BUILD_TIME).append('\n')
    append("内核 mihomo ").append(coreVersion.ifBlank { "未连接" }).append('\n')
    append("包名 ").append(BuildConfig.APPLICATION_ID).append('\n')
    append("Android ").append(android.os.Build.VERSION.RELEASE)
    append(" / API ").append(android.os.Build.VERSION.SDK_INT).append('\n')
    append(android.os.Build.MANUFACTURER).append(' ').append(android.os.Build.MODEL)
}

