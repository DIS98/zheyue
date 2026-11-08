package com.clashdeck.android.ui.pages

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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.api.ProxyItem
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.ui.components.DelayBadge
import com.clashdeck.android.ui.components.EmptyHint
import com.clashdeck.android.ui.components.SectionCard

@Composable
fun ProxiesPage(vm: AppViewModel) {
    // 840dp 起用两列节点。改动只影响列表排布，不涉及数据与选择逻辑。
    val wideList = LocalConfiguration.current.screenWidthDp >= 840
    val snapshot by vm.proxies.collectAsStateWithLifecycle()
    val core by vm.coreState.collectAsStateWithLifecycle()
    val testing by vm.testingGroups.collectAsStateWithLifecycle()
    val apiError by vm.apiError.collectAsStateWithLifecycle()
    val expanded = remember { mutableStateMapOf<String, Boolean>() }
    var search by remember { mutableStateOf("") }
    var sortByDelay by remember { mutableStateOf(true) }

    if (snapshot.groups.isEmpty()) {
        Column(
            Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            EmptyHint(
                when {
                    apiError != null -> apiError!!
                    core.running -> "内核已连接，正在读取策略组…"
                    else -> "未连接。先在概览页打开开关"
                }
            )
            // 读不到时给一个"立刻再试一次"的入口：
            // 只说"失败"而不给动作，用户只能干等或反复杀进程。
            if (apiError != null) {
                TextButton(onClick = { vm.retryApiNow() }) { Text("重试") }
            }
        }
        return
    }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = search,
            onValueChange = { search = it },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            placeholder = { Text("过滤节点名，如 hk / 日本 / 02") },
            singleLine = true,
        )

        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 2.dp),
            horizontalArrangement = Arrangement.End,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                "排序",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.size(8.dp))
            SortPill("延迟", sortByDelay) { sortByDelay = true }
            Spacer(Modifier.size(6.dp))
            SortPill("名称", !sortByDelay) { sortByDelay = false }
        }

        LazyColumn(
            Modifier.fillMaxSize().padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            items(snapshot.groups, key = { it.name }) { group ->
                val isExpanded = expanded[group.name] ?: (snapshot.groups.indexOf(group) == 0)
                val nodes = group.all
                    .filter { search.isBlank() || it.contains(search, ignoreCase = true) }
                    .map { name -> snapshot.nodes[name] ?: ProxyItem(name, "", null, emptyList(), true, null) }
                    .let { list ->
                        if (sortByDelay) list.sortedWith(compareBy(nullsLast()) { it.delay })
                        else list.sortedBy { it.name.lowercase() }
                    }

                SectionCard(
                    title = group.name,
                    subtitle = "${group.type} · ${group.all.size} 个节点",
                    trailing = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (testing.contains(group.name)) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(16.dp),
                                    strokeWidth = 2.dp,
                                )
                                Spacer(Modifier.size(8.dp))
                            }
                            TextButton(onClick = { vm.testGroup(group) }) { Text("测速") }
                        }
                    },
                ) {
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable { expanded[group.name] = !isExpanded },
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                "当前：${group.now ?: "未选择"}",
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        DelayBadge(snapshot.nodes[group.now]?.delay)
                        Icon(
                            if (isExpanded) Icons.Filled.ExpandLess else Icons.Filled.ExpandMore,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    if (isExpanded) {
                        Spacer(Modifier.height(4.dp))
                        if (wideList) {
                            // 宽屏改两列：单列时节点名贴左、延迟徽章贴右，中间隔好几百 dp，
                            // 眼睛每读一行要横跳一次；两列后行内距离回到手机尺度，
                            // 304 个节点的纵向滚动也直接减半。
                            nodes.chunked(2).forEach { pair ->
                                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                                    pair.forEach { node ->
                                        NodeRow(
                                            node = node,
                                            selected = node.name == group.now,
                                            modifier = Modifier.weight(1f),
                                            onSelect = { vm.selectProxy(group.name, node.name) },
                                            onTest = { vm.testNode(node.name) },
                                        )
                                    }
                                    // 奇数个时补一个空位，避免最后一行唯一节点被拉成整行宽
                                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                                }
                            }
                        } else {
                            nodes.forEach { node ->
                                NodeRow(
                                    node = node,
                                    selected = node.name == group.now,
                                    modifier = Modifier.fillMaxWidth(),
                                    onSelect = { vm.selectProxy(group.name, node.name) },
                                    onTest = { vm.testNode(node.name) },
                                )
                            }
                        }
                        if (nodes.isEmpty()) {
                            EmptyHint("没有匹配的节点")
                        }
                    }
                }
            }
            item { Spacer(Modifier.height(12.dp)) }
        }
    }
}

/**
 * 单个节点行。抽出来是为了窄屏（整行）与宽屏（半行）复用同一份渲染，
 * 避免两处各写一遍、以后改样式只改一半。
 */
@Composable
private fun NodeRow(
    node: ProxyItem,
    selected: Boolean,
    modifier: Modifier,
    onSelect: () -> Unit,
    onTest: () -> Unit,
) {
    Row(
        modifier
            .clickable { onSelect() }
            .padding(vertical = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (selected) {
            Icon(
                Icons.Filled.Check,
                contentDescription = null,
                modifier = Modifier.size(16.dp),
                tint = MaterialTheme.colorScheme.primary,
            )
            Spacer(Modifier.size(6.dp))
        } else {
            Spacer(Modifier.size(22.dp))
        }
        Text(
            node.name,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.weight(1f),
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            color = if (selected) MaterialTheme.colorScheme.primary
            else MaterialTheme.colorScheme.onSurface,
        )
        Spacer(Modifier.size(8.dp))
        Row(Modifier.clickable(enabled = !node.isGroup) { onTest() }) {
            DelayBadge(node.delay)
        }
    }
}

@Composable
private fun SortPill(text: String, selected: Boolean, onClick: () -> Unit) {
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
