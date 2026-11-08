package com.clashdeck.android.ui.pages

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.background
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.api.RuleItem
import com.clashdeck.android.api.RuleProviderInfo
import com.clashdeck.android.data.CustomRule
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.ui.components.EmptyHint
import com.clashdeck.android.ui.components.KvRow
import com.clashdeck.android.ui.components.SectionCard
import com.clashdeck.android.util.formatBytes

@Composable
fun ConnectionsPage(vm: AppViewModel) {
    val snap by vm.connections.collectAsStateWithLifecycle()
    val core by vm.coreState.collectAsStateWithLifecycle()

    Column(Modifier.fillMaxSize()) {
        SectionCard(
            title = "活动连接",
            subtitle = "共 ${snap.items.size} 条 · ↑ ${formatBytes(snap.uploadTotal)} ↓ ${formatBytes(snap.downloadTotal)}",
            trailing = {
                TextButton(onClick = { vm.closeAllConnections() }) { Text("全部断开") }
            },
            modifier = Modifier.padding(16.dp),
        ) {
            if (snap.items.isEmpty()) {
                EmptyHint(if (core.running) "当前没有连接" else "未连接")
            }
        }

        LazyColumn(
            Modifier.fillMaxSize().padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(snap.items, key = { it.id }) { item ->
                SectionCard {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                item.host.ifBlank { item.destination },
                                style = MaterialTheme.typography.bodyMedium,
                                fontWeight = FontWeight.Medium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                "${item.network} · ${item.rule}${if (item.rulePayload.isNotBlank()) "(${item.rulePayload})" else ""}",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            if (item.chains.isNotEmpty()) {
                                Text(
                                    item.chains.joinToString(" → "),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.primary,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                            Text(
                                "↑ ${formatBytes(item.upload)}  ↓ ${formatBytes(item.download)}",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        IconButton(onClick = { vm.closeConnection(item) }) {
                            Icon(
                                Icons.Filled.Close,
                                contentDescription = "断开",
                                modifier = Modifier.size(18.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
            item { Spacer(Modifier.height(12.dp)) }
        }
    }
}

@Composable
fun RulesPage(vm: AppViewModel) {
    // 840dp 起规则列表排两列。与代理页节点同一条断点，两页观感一致。
    val wideRows = LocalConfiguration.current.screenWidthDp >= 840
    val rules by vm.rules.collectAsStateWithLifecycle()
    val providers by vm.providers.collectAsStateWithLifecycle()
    val providerBusy by vm.providerBusy.collectAsStateWithLifecycle()
    val providerError by vm.providerError.collectAsStateWithLifecycle()
    val prediction by vm.prediction.collectAsStateWithLifecycle()
    val predicting by vm.predicting.collectAsStateWithLifecycle()
    val predictError by vm.predictError.collectAsStateWithLifecycle()
    val core by vm.coreState.collectAsStateWithLifecycle()

    var search by remember { mutableStateOf("") }
    var domainInput by remember { mutableStateOf("") }

    val filtered = if (search.isBlank()) rules
    else rules.filter {
        it.payload.contains(search, ignoreCase = true) || it.proxy.contains(search, ignoreCase = true)
    }.sortedByDescending { it.size }

    // 规则集是"连上之后才慢慢下好"的东西：只加载一次会永远停在 0 条。
    // 页面在的时候每 5 秒拉一次，离开自动停（LaunchedEffect 随页面销毁取消）。
    LaunchedEffect(core.running) {
        while (core.running) {
            vm.loadProviders()
            kotlinx.coroutines.delay(5000)
        }
    }

    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        // ---------- 规则预判 ----------
        item {
            SectionCard(
                title = "规则预判",
                subtitle = "输入域名，让内核真连一次，读实际命中的规则",
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = domainInput,
                        onValueChange = { domainInput = it },
                        modifier = Modifier.weight(1f),
                        placeholder = { Text("例如 google.com") },
                        singleLine = true,
                    )
                    Spacer(Modifier.width(8.dp))
                    Button(
                        onClick = { vm.predictRule(domainInput) },
                        enabled = !predicting && domainInput.isNotBlank(),
                    ) { Text("预判") }
                }

                if (predicting) {
                    Spacer(Modifier.height(10.dp))
                    Text(
                        "正在通过代理发起一次连接…",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                prediction?.let { r ->
                    Spacer(Modifier.height(10.dp))
                    KvRow("命中规则", if (r.payload.isBlank()) r.rule else "${r.rule} · ${r.payload}")
                    KvRow("最终出口", r.target)
                    KvRow("完整链路", r.chains.joinToString(" → ").ifBlank { "—" })
                }
                predictError?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                }
            }
        }

        // ---------- 自定义规则 ----------
        item {
            val customRules by vm.customRules.collectAsStateWithLifecycle()
            val groups = vm.proxies.collectAsStateWithLifecycle().value.groups
            // 优先指向「可手动选择」的 Selector 组（如 节点选择）：
            // url-test 组（如 自动选择）是按延迟自动挑的，把域名钉给它等于把选择权交回给测速结果，
            // 与"我手动指定走代理"的意图不符。
            val proxyTarget = (groups.firstOrNull { it.name != "GLOBAL" && it.type == "Selector" }
                ?: groups.firstOrNull { it.name != "GLOBAL" })?.name ?: "PROXY"

            var ruleInput by remember { mutableStateOf("") }
            var ruleType by remember { mutableStateOf(CustomRule.DOMAIN_SUFFIX) }
            var useProxy by remember { mutableStateOf(true) }

            SectionCard(
                title = "自定义规则",
                subtitle = "把一个域名钉成直连或走代理，优先级高于订阅与内置规则",
            ) {
                OutlinedTextField(
                    value = ruleInput,
                    onValueChange = { ruleInput = it },
                    modifier = Modifier.fillMaxWidth(),
                    placeholder = { Text("example.com 或 10.0.0.0/8") },
                    singleLine = true,
                )

                Spacer(Modifier.height(10.dp))

                // 匹配方式：手机上不该让人记 DOMAIN-SUFFIX / DOMAIN-KEYWORD 这些写法，
                // 用三个能读懂的说法代替，选完在下面给出对应的例子。
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "匹配",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.width(36.dp),
                    )
                    CustomRule.PICKABLE.forEach { t ->
                        RulePill(CustomRule.typeLabel(t), ruleType == t) { ruleType = t }
                        Spacer(Modifier.width(6.dp))
                    }
                }

                Spacer(Modifier.height(4.dp))
                Text(
                    CustomRule.typeHint(ruleType),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                Spacer(Modifier.height(8.dp))

                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "动作",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.width(36.dp),
                    )
                    RulePill("代理", useProxy) { useProxy = true }
                    Spacer(Modifier.width(6.dp))
                    RulePill("直连", !useProxy) { useProxy = false }
                    Spacer(Modifier.weight(1f))
                    Button(
                        onClick = {
                            vm.addCustomRule(
                                ruleInput,
                                ruleType,
                                if (useProxy) CustomRule.PROXY else CustomRule.DIRECT,
                            )
                            ruleInput = ""
                        },
                        enabled = ruleInput.isNotBlank(),
                    ) { Text("添加") }
                }

                Spacer(Modifier.height(4.dp))
                Text(
                    if (useProxy) "走代理时使用「$proxyTarget」，可在「代理」页切换节点"
                    else "直连：不走任何节点",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                Spacer(Modifier.height(10.dp))
                if (customRules.isEmpty()) {
                    Text(
                        "还没有自定义规则",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else {
                    Text(
                        "已添加 ${customRules.size} 条",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    customRules.forEach { rule ->
                        Row(
                            Modifier.fillMaxWidth().padding(top = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(
                                    rule.value,
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontFamily = FontFamily.Monospace,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                Text(
                                    "${CustomRule.typeLabel(rule.type)} · " +
                                        if (rule.action == CustomRule.DIRECT) "直连" else "代理",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            TextButton(onClick = { vm.removeCustomRule(rule) }) { Text("删除") }
                        }
                    }
                }
            }
        }

        // ---------- 搜索 + 统计 ----------
        item {
            OutlinedTextField(
                value = search,
                onValueChange = { search = it },
                modifier = Modifier.fillMaxWidth(),
                placeholder = { Text("搜索域名 / 策略") },
                singleLine = true,
            )
        }
        item {
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                val providerRules = providers.sumOf { it.ruleCount }
                Text(
                    // 未连接时内核没起，规则必然是 0 条 —— 直接写"0 条"会让人以为配置坏了
                    if (!core.running && rules.isEmpty()) {
                        "未连接 · 连接后显示规则与规则集"
                    } else buildString {
                        append("配置内规则 ${rules.size} 条")
                        if (providers.isNotEmpty()) {
                            append(" · 规则集 ${providers.size} 个共 $providerRules 条")
                        }
                        if (search.isNotBlank()) append(" · 匹配 ${filtered.size} 条")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                TextButton(onClick = { vm.reloadRules() }) { Text("刷新") }
            }
        }

        if (filtered.isEmpty()) {
            item { EmptyHint(if (core.running) "没有规则或没有匹配项" else "未连接") }
        } else if (wideRows) {
            // 宽屏两列。规则行的结构是"规则名贴左、去向贴右"，单列在 900dp 卡片里
            // 两者相距约 700dp，读一行要横跳一次；两列后行内距离回到手机尺度。
            items(filtered.chunked(2)) { pair ->
                // 两列之间要留缝：规则行的去向文字是右对齐的，不加间距会和右列的规则名粘成
                // 一行（实测出现过 "DIRECTRuleSet,cd-ads"）。
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                    pair.forEach { rule ->
                        RuleRow(rule, Modifier.weight(1f))
                    }
                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        } else {
            items(filtered) { rule ->
                RuleRow(rule, Modifier.fillMaxWidth())
            }
        }

        // ---------- 规则集管理 ----------
        item {
            Spacer(Modifier.height(4.dp))
            SectionCard(
                title = "规则集",
                subtitle = when {
                    providers.isNotEmpty() -> {
                        val ready = providers.count { it.ruleCount > 0 }
                        val total = providers.sumOf { it.ruleCount }
                        "已就绪 $ready/${providers.size} 个 · 共 $total 条"
                    }
                    core.running -> "该配置没有 rule-providers"
                    else -> "连接后显示"
                },
                trailing = {
                    TextButton(
                        onClick = { vm.updateAllProviders() },
                        enabled = providerBusy == null && providers.isNotEmpty(),
                    ) { Text(if (providerBusy == "ALL") "更新中…" else "全部更新") }
                },
            ) {
                if (providers.isEmpty()) {
                    EmptyHint(
                        when {
                            !core.running -> "连接后显示规则集状态"
                            providerError != null -> "读取规则集失败：$providerError"
                            else -> "这份配置里没有 rule-providers（规则集）"
                        }
                    )
                } else {
                    if (wideRows) {
                        // 宽屏两列：规则集行是"名称+明细贴左、更新按钮贴右"，
                        // 单列在 900dp 卡片里两者相距 700dp 以上，与规则条目同病。
                        providers.chunked(2).forEach { pair ->
                            Row(
                                Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(24.dp),
                            ) {
                                pair.forEach { p ->
                                    ProviderRow(
                                        p = p,
                                        busy = providerBusy == p.name,
                                        enabled = providerBusy == null,
                                        modifier = Modifier.weight(1f),
                                        onUpdate = { vm.updateProvider(p.name) },
                                    )
                                }
                                if (pair.size == 1) Spacer(Modifier.weight(1f))
                            }
                        }
                    } else {
                        providers.forEach { p ->
                            ProviderRow(
                                p = p,
                                busy = providerBusy == p.name,
                                enabled = providerBusy == null,
                                modifier = Modifier.fillMaxWidth(),
                                onUpdate = { vm.updateProvider(p.name) },
                            )
                        }
                    }
                }
            }
        }

        item { Spacer(Modifier.height(12.dp)) }
    }
}

/**
 * 内核给的 updatedAt 是 UTC 的 RFC3339（如 2026-10-27T18:43:28.156Z）。
 * 必须换算到本地时区再显示——直接截字符串会给用户一个差 8 小时的时间。
 */
private fun shortTime(raw: String): String {
    if (raw.isBlank() || raw.startsWith("0001")) return "未记录"
    return runCatching {
        java.time.OffsetDateTime.parse(raw)
            .atZoneSameInstant(java.time.ZoneId.systemDefault())
            .format(java.time.format.DateTimeFormatter.ofPattern("MM-dd HH:mm"))
    }.getOrElse {
        // 解析不了就老实截断，别显示整串 ISO
        raw.replace("T", " ").take(16)
    }
}



@Composable
private fun RulePill(text: String, selected: Boolean, onClick: () -> Unit) {
    Surface(
        shape = RoundedCornerShape(14.dp),
        color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant,
        onClick = onClick,
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 5.dp),
        )
    }
}

/**
 * 单条规则行。窄屏整行、宽屏半行共用一份渲染。
 * 规则名用等宽字体，因为 `RuleSet,cd-telegram` 这类值对齐后更容易扫读。
 */
@Composable
private fun RuleRow(rule: RuleItem, modifier: Modifier) {
    Row(
        modifier.padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            "${rule.type},${rule.payload}",
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Text(
            rule.proxy,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.primary,
            maxLines = 1,
        )
        if (rule.size >= 0) {
            Text(
                "  ${rule.size}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** 单条规则集行。宽屏半行、窄屏整行共用一份渲染。 */
@Composable
private fun ProviderRow(
    p: RuleProviderInfo,
    busy: Boolean,
    enabled: Boolean,
    modifier: Modifier,
    onUpdate: () -> Unit,
) {
    Row(
        modifier.padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    p.name,
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (p.ruleCount == 0) {
                    Spacer(Modifier.width(6.dp))
                    Text(
                        "未下载",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
            Text(
                if (p.ruleCount > 0) {
                    "${p.ruleCount} 条 · ${p.behavior.ifBlank { p.vehicleType }} · ${shortTime(p.updatedAt)}"
                } else {
                    "0 条 · 下载未成功，点右侧「更新」重试"
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (p.ruleCount > 0) MaterialTheme.colorScheme.onSurfaceVariant
                else MaterialTheme.colorScheme.error.copy(alpha = 0.85f),
            )
        }
        TextButton(onClick = onUpdate, enabled = enabled) {
            Text(if (busy) "更新中" else "更新")
        }
    }
}
