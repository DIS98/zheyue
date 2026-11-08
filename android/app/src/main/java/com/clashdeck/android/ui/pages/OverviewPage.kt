package com.clashdeck.android.ui.pages

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.LocalIndication
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.api.ProxyItem
import com.clashdeck.android.api.TrafficSample
import com.clashdeck.android.core.CoreStatus
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.data.SettingsRepo
import com.clashdeck.android.ui.components.DelayBadge
import com.clashdeck.android.ui.components.EmptyHint
import com.clashdeck.android.ui.components.KvRow
import com.clashdeck.android.ui.components.SectionCard
import com.clashdeck.android.ui.components.UpdateBanner
import com.clashdeck.android.ui.theme.DownColor
import com.clashdeck.android.ui.theme.UpColor
import com.clashdeck.android.util.expiryLabel
import com.clashdeck.android.util.formatBytes
import com.clashdeck.android.util.formatSpeed
import com.clashdeck.android.util.formatTime

@Composable
fun OverviewPage(vm: AppViewModel, onRequestStart: () -> Unit) {
    val core by vm.coreState.collectAsStateWithLifecycle()
    val traffic by vm.traffic.collectAsStateWithLifecycle()
    val history by vm.trafficHistory.collectAsStateWithLifecycle()
    val profiles by vm.profiles.collectAsStateWithLifecycle()
    val proxies by vm.proxies.collectAsStateWithLifecycle()
    val mode by vm.mode.collectAsStateWithLifecycle()
    val proxyMode by vm.proxyMode.collectAsStateWithLifecycle()
    val connections by vm.connections.collectAsStateWithLifecycle()

    val activeId by vm.activeProfileId.collectAsStateWithLifecycle()
    val activeProfile = profiles.firstOrNull { it.id == activeId } ?: profiles.firstOrNull()

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        // 有新版时置顶：这是用户最需要看到的一件事，不该藏在配置页里
        UpdateBanner(vm)

        StatusCard(
            status = core.status,
            message = core.message,
            mode = mode,
            proxyMode = proxyMode,
            groups = proxies.groups,
            nodes = proxies.nodes,
            onModeChange = { vm.setMode(it) },
            onProxyModeChange = { vm.setProxyMode(it) },
            onToggle = {
                if (core.running) vm.stopVpn() else onRequestStart()
            },
        )

        SectionCard(title = "实时流量", subtitle = "上/下行速率，最近 90 秒") {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                SpeedBlock("上行", formatSpeed(traffic.up), UpColor)
                SpeedBlock("下行", formatSpeed(traffic.down), DownColor)
                Column(horizontalAlignment = Alignment.End) {
                    Text(
                        "本次连接",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        "↑ ${formatBytes(connections.uploadTotal)}  ↓ ${formatBytes(connections.downloadTotal)}",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            Spacer(Modifier.height(12.dp))
            TrafficChart(history, Modifier.fillMaxWidth().height(76.dp))
        }

        SectionCard(
            title = "当前配置",
            subtitle = activeProfile?.name ?: "还没有导入订阅",
        ) {
            if (activeProfile == null) {
                EmptyHint("到「配置」页导入一份 Clash 订阅或粘贴 YAML")
            } else {
                val info = activeProfile.userInfo
                if (info != null && info.hasQuota) {
                    val ratio = (info.used.toFloat() / info.total.toFloat()).coerceIn(0f, 1f)
                    val danger = ratio > 0.9f
                    LinearProgressIndicator(
                        progress = { ratio },
                        modifier = Modifier.fillMaxWidth().height(6.dp),
                        color = if (danger) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                        trackColor = MaterialTheme.colorScheme.surfaceVariant,
                    )
                    Spacer(Modifier.height(8.dp))
                    KvRow("已用 / 总量", "${formatBytes(info.used)} / ${formatBytes(info.total)}")
                    KvRow(
                        "到期",
                        if (info.expire <= 0) "长期有效"
                        else "${formatTime(info.expireAtMillis)}（${expiryLabel(info.expireAtMillis)}）",
                    )
                } else {
                    KvRow("订阅用量", "该订阅未返回流量信息")
                }
                KvRow("上次更新", formatTime(activeProfile.updatedAt))
                KvRow("配置文件大小", formatBytes(activeProfile.size))
            }
        }

        SectionCard(
            title = "策略组",
            subtitle = if (proxies.groups.isEmpty()) "连接后显示" else "共 ${proxies.groups.size} 组",
        ) {
            if (proxies.groups.isEmpty()) {
                EmptyHint(if (core.running) "正在读取内核数据…" else "未连接")
            } else {
                val mainGroups = proxies.groups.filter { it.name != "GLOBAL" }.ifEmpty { proxies.groups }
                mainGroups.take(6).forEach { group ->
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                group.name,
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                group.now ?: "未选择",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        group.now?.let { now ->
                            DelayBadge(proxies.nodes[now]?.delay)
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(8.dp))
    }
}

@Composable
private fun StatusCard(
    status: CoreStatus,
    message: String?,
    mode: String,
    proxyMode: String,
    groups: List<ProxyItem>,
    nodes: Map<String, ProxyItem>,
    onModeChange: (String) -> Unit,
    onProxyModeChange: (String) -> Unit,
    onToggle: () -> Unit,
) {
    val running = status == CoreStatus.RUNNING
    val pending = status == CoreStatus.STARTING || status == CoreStatus.STOPPING

    val label = when (status) {
        CoreStatus.STOPPED -> "未连接"
        CoreStatus.STARTING -> "正在连接"
        CoreStatus.RUNNING -> "已连接"
        CoreStatus.STOPPING -> "正在断开"
        CoreStatus.ERROR -> "连接失败"
    }
    val accent = when {
        running -> MaterialTheme.colorScheme.primary
        status == CoreStatus.ERROR -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }

    // 环与主按钮共用同一组渐变：青 → 靛 → 青，首尾同色才能无缝绕一圈
    val ringStops = listOf(
        MaterialTheme.colorScheme.secondary,
        MaterialTheme.colorScheme.primary,
        MaterialTheme.colorScheme.secondary,
    )
    val buttonBrush: Brush = if (running) {
        Brush.linearGradient(listOf(MaterialTheme.colorScheme.primary, MaterialTheme.colorScheme.secondary))
    } else {
        SolidColor(MaterialTheme.colorScheme.surfaceVariant)
    }

    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.94f else 1f, label = "powerScale")

    Surface(
        shape = RoundedCornerShape(28.dp),
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp, vertical = 24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(7.dp)
                        .background(accent, CircleShape)
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    label,
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }

            Spacer(Modifier.height(20.dp))

            // 外圈是静态装饰环，内圈才是按钮。
            // 内圈必须 clip 在 clickable 之前，否则水波纹会溢出成方形——这就是"按下出现方框"的原因。
            // 高度必须固定：连接时多出来的两层柔光会把容器撑高 44dp，
            // 下面的模式药丸随之下移 115px，用户按原位置点就落空（表现为"切不了"）。
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(186.dp),
                contentAlignment = Alignment.Center,
            ) {
                // 不用 blur：BlurEffect 要 API 31+，低版本会直接不生效。
                if (running) {
                    Box(Modifier.size(186.dp).background(accent.copy(alpha = 0.06f), CircleShape))
                    Box(Modifier.size(160.dp).background(accent.copy(alpha = 0.09f), CircleShape))
                }
                Canvas(Modifier.size(144.dp)) {
                    val ring = Stroke(width = 2.4.dp.toPx(), cap = StrokeCap.Round)
                    if (running) {
                        // 连上时是完整的渐变环，像一圈"通道"
                        drawArc(
                            brush = Brush.sweepGradient(ringStops),
                            startAngle = -90f,
                            sweepAngle = 360f,
                            useCenter = false,
                            style = ring,
                        )
                    } else {
                        // 未连时只有 300° 的暗环，留个缺口表示"未闭合"
                        drawArc(
                            color = accent.copy(alpha = 0.32f),
                            startAngle = -90f,
                            sweepAngle = 300f,
                            useCenter = false,
                            style = ring,
                        )
                    }
                }
                Box(
                    Modifier
                        .size(110.dp)
                        .scale(scale)
                        .clip(CircleShape)
                        .background(buttonBrush)
                        .then(
                            if (running) Modifier
                            else Modifier.border(
                                1.dp,
                                MaterialTheme.colorScheme.outline.copy(alpha = 0.45f),
                                CircleShape,
                            )
                        )
                        .clickable(
                            interactionSource = interaction,
                            indication = LocalIndication.current,
                            enabled = !pending,
                        ) { onToggle() },
                    contentAlignment = Alignment.Center,
                ) {
                    if (pending) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(32.dp),
                            strokeWidth = 2.5.dp,
                            color = if (running) MaterialTheme.colorScheme.onPrimary
                            else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    } else {
                        Icon(
                            Icons.Filled.PowerSettingsNew,
                            contentDescription = if (running) "断开连接" else "建立连接",
                            modifier = Modifier.size(46.dp),
                            tint = if (running) MaterialTheme.colorScheme.onPrimary
                            else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            Spacer(Modifier.height(18.dp))

            Text(
                text = when {
                    pending -> "请稍候"
                    running -> "点击断开"
                    else -> "点击连接，按「${modeLabel(mode)}」接管全部流量"
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            if (status == CoreStatus.ERROR && !message.isNullOrBlank()) {
                Spacer(Modifier.height(8.dp))
                Surface(
                    shape = RoundedCornerShape(12.dp),
                    color = MaterialTheme.colorScheme.error.copy(alpha = 0.10f),
                ) {
                    Text(
                        message,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                    )
                }
            }

            // 当前出口：连上之后用户最想知道"我现在走的是哪个节点"。
            // 以前要去「代理」页翻策略组，多两次点击——实测自己用的时候每次都得翻。
            // 高度固定 16dp（未连接时占位），出现/消失都不会把下面的模式药丸顶走。
            Box(Modifier.height(16.dp), contentAlignment = Alignment.Center) {
                // 出口必须显示规则真正指向的组：配置里第一个 type=select（/proxies 里叫 Selector）。
                // 取「第一个非 GLOBAL 组」会命中 url-test 的「自动选择」——它只测速、不参与分流，
                // 把它标成当前出口等于告诉用户一个根本没在走的节点。口径与 DataPages 自定义规则一致。
                val mainGroup = groups.firstOrNull { it.name != "GLOBAL" && it.type.equals("Selector", ignoreCase = true) }
                    ?: groups.firstOrNull { it.name != "GLOBAL" }
                    ?: groups.firstOrNull()
                val nodeLabel = mainGroup?.now
                if (running && !nodeLabel.isNullOrBlank()) {
                    val delay = nodes[nodeLabel]?.delay
                    val suffix = if (delay != null && delay > 0) " · $delay ms" else ""
                    Text(
                        "当前出口 ${mainGroup.name} / $nodeLabel$suffix",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(horizontal = 8.dp),
                    )
                }
            }

            Spacer(Modifier.height(14.dp))
            ModeSegmented(mode = mode, onChange = onModeChange)

            // 分流模式：只在「规则」下才有意义 —— 全局/直连会直接盖过所有规则判定，
            // 那种情况下给出这个按钮就是"点了没用"。所以它跟着模式出现和消失。
            // 位置在分段控件**下方**：这一行出现/消失只影响卡片底边，
            // 不会把上面的模式药丸顶走（避免重演"按钮下移、点不中"的老问题）。
            if (mode == "rule") {
                Spacer(Modifier.height(10.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "分流",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Spacer(Modifier.width(8.dp))
                    ProxyModePill("智能模式", proxyMode != SettingsRepo.MODE_STRICT) {
                        onProxyModeChange(SettingsRepo.MODE_SMART)
                    }
                    Spacer(Modifier.width(6.dp))
                    ProxyModePill("严格模式", proxyMode == SettingsRepo.MODE_STRICT) {
                        onProxyModeChange(SettingsRepo.MODE_STRICT)
                    }
                }
            }
        }
    }
}

/** 三段式分段控件。自己搭而不是用 Chip：Chip 的圆角与间距在三段并排时对不齐。 */
@Composable
private fun ModeSegmented(mode: String, onChange: (String) -> Unit) {
    val options = listOf("rule" to "规则", "global" to "全局", "direct" to "直连")
    Row(
        Modifier
            .clip(RoundedCornerShape(24.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .padding(4.dp),
    ) {
        options.forEach { (value, text) ->
            val selected = mode == value
            Surface(
                onClick = { if (!selected) onChange(value) },
                shape = RoundedCornerShape(20.dp),
                color = if (selected) MaterialTheme.colorScheme.surface else Color.Transparent,
                shadowElevation = if (selected) 1.dp else 0.dp,
            ) {
                Text(
                    text,
                    style = MaterialTheme.typography.labelSmall,
                    color = if (selected) MaterialTheme.colorScheme.onSurface
                    else MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 22.dp, vertical = 9.dp),
                )
            }
        }
    }
}

/**
 * 分流模式小胶囊（智能 / 严格）。
 * 比第二排分段控件更轻：只有两个值，且不能抢走主按钮的视觉地位。
 * 最小点击高度按 32dp 留，避免"看起来能点但点不中"。
 */
@Composable
private fun ProxyModePill(text: String, selected: Boolean, onClick: () -> Unit) {
    Surface(
        shape = RoundedCornerShape(14.dp),
        color = if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.14f)
        else MaterialTheme.colorScheme.surfaceVariant,
        border = if (selected) BorderStroke(1.dp, MaterialTheme.colorScheme.primary) else null,
        onClick = onClick,
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = if (selected) MaterialTheme.colorScheme.primary
            else MaterialTheme.colorScheme.onSurfaceVariant,
            // 竖向 10dp ≈ 文字 11sp + 20dp = 触控高度约 38dp；再小就低于 40dp 的可点下限
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
        )
    }
}

@Composable
private fun SpeedBlock(label: String, value: String, color: Color) {
    Column {
        Text(label, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.titleMedium, color = color, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun TrafficChart(history: List<TrafficSample>, modifier: Modifier = Modifier) {
    val gridColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)
    Canvas(modifier) {
        drawLine(
            color = gridColor,
            start = Offset(0f, size.height),
            end = Offset(size.width, size.height),
            strokeWidth = 1f,
        )
        if (history.size < 2) return@Canvas

        val maxValue = history.maxOf { maxOf(it.up, it.down) }.coerceAtLeast(1024L).toFloat()
        val stepX = size.width / (history.size - 1).toFloat()

        fun build(selector: (TrafficSample) -> Long): Path {
            val path = Path()
            history.forEachIndexed { index, sample ->
                val x = index * stepX
                val y = size.height - (selector(sample) / maxValue) * (size.height - 2f)
                if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
            }
            return path
        }

        fun area(selector: (TrafficSample) -> Long): Path {
            val path = build(selector)
            path.lineTo(size.width, size.height)
            path.lineTo(0f, size.height)
            path.close()
            return path
        }

        // 先铺面积渐变再描边，线条才不会被填充盖住
        drawPath(
            area { it.down },
            brush = androidx.compose.ui.graphics.Brush.verticalGradient(
                listOf(DownColor.copy(alpha = 0.22f), Color.Transparent),
                startY = 0f,
                endY = size.height,
            ),
        )
        drawPath(
            area { it.up },
            brush = androidx.compose.ui.graphics.Brush.verticalGradient(
                listOf(UpColor.copy(alpha = 0.18f), Color.Transparent),
                startY = 0f,
                endY = size.height,
            ),
        )
        drawPath(build { it.down }, color = DownColor, style = Stroke(width = 2.2f))
        drawPath(build { it.up }, color = UpColor, style = Stroke(width = 2.2f))
    }
}

/** 模式的中文名。界面多处要用，避免各写一份。 */
private fun modeLabel(mode: String): String = when (mode) {
    "global" -> "全局"
    "direct" -> "直连"
    else -> "规则"
}
