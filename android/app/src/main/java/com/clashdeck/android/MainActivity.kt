package com.clashdeck.android

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Dashboard
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.Rule
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.Subject
import androidx.compose.material.icons.outlined.SwapVert
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.VerticalDivider
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clashdeck.android.ui.AppViewModel
import com.clashdeck.android.ui.pages.ConfigPage
import com.clashdeck.android.ui.pages.ConnectionsPage
import com.clashdeck.android.ui.pages.LogsPage
import com.clashdeck.android.ui.pages.OverviewPage
import com.clashdeck.android.ui.pages.ProxiesPage
import com.clashdeck.android.ui.pages.RulesPage
import com.clashdeck.android.ui.theme.ClashDeckTheme

class MainActivity : ComponentActivity() {

    private val vm: AppViewModel by viewModels()

    private val vpnPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == RESULT_OK) {
            vm.startVpn()
        } else {
            vm.toast.value = "没有 VPN 授权，无法接管流量"
        }
    }

    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setContent {
            ClashDeckTheme {
                AppRoot(vm = vm, onRequestStart = { startWithPermission() })
            }
        }
    }

    private fun startWithPermission() {
        val intent = vm.vpnPermissionIntent()
        if (intent != null) {
            vpnPermissionLauncher.launch(intent)
        } else {
            vm.startVpn()
        }
    }
}

private data class TabItem(val title: String, val icon: ImageVector)

private val tabs = listOf(
    TabItem("概览", Icons.Outlined.Dashboard),
    TabItem("代理", Icons.Outlined.SwapVert),
    TabItem("连接", Icons.Outlined.Link),
    TabItem("规则", Icons.Outlined.Rule),
    TabItem("日志", Icons.Outlined.Subject),
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AppRoot(vm: AppViewModel, onRequestStart: () -> Unit) {
    var tab by remember { mutableIntStateOf(0) }
    // 日志页当前看的来源：0=连接日志 1=内核日志。配置页的入口会把它切到 1。
    var logMode by remember { mutableIntStateOf(0) }
    // 从配置页返回时回到进入前的那一页，而不是一律回概览
    var previousTab by remember { mutableIntStateOf(0) }
    val snackbar = remember { SnackbarHostState() }
    val toast by vm.toast.collectAsStateWithLifecycle()
    val coreState by vm.coreState.collectAsStateWithLifecycle()

    LaunchedEffect(toast) {
        val msg = toast
        if (msg != null) {
            // 失败类提示要留得住：默认 4 秒就消失，用户正在看订阅链接、根本没注意到，
            // 表现就是"点了导入没反应"。这类消息给 10 秒 + 手动关闭。
            val problem = listOf("失败", "错误", "无法", "超时", "不支持").any { msg.contains(it) }
            snackbar.showSnackbar(
                message = msg,
                withDismissAction = problem,
                duration = if (problem) SnackbarDuration.Long else SnackbarDuration.Short,
            )
            vm.consumeToast()
        }
    }

    val showingConfig = tab == 5

    /**
     * 宽度断点。取窗口宽度（多窗口/分屏下就是窗口，不取物理屏宽）。
     *
     * 为什么要分档：单列内容铺满 12 寸会把一行字拉成整条，阅读要来回扫视；
     * 但一刀切限到 560dp 又是另一个极端 —— 平板上只占中间一小块，两侧大片空白，
     * 这正是"平板上一小块屏"的成因。所以按宽度给三档上限，并用侧栏吃掉横向空间。
     */
    val widthDp = LocalConfiguration.current.screenWidthDp
    val wide = widthDp >= 600
    val contentMax = when {
        widthDp >= 1200 -> 1080.dp
        widthDp >= 840 -> 900.dp
        widthDp >= 600 -> 720.dp
        else -> 560.dp
    }

    // 左滑/右滑（系统返回手势）先回上一页；只有已经在概览页时才退出应用。
    // 不接管的话，单 Activity 没有"上一页"，右滑就直接退回桌面。
    BackHandler(enabled = tab != 0) {
        tab = if (tab == 5) previousTab else 0
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        // 品牌名只出现在"家"的位置（概览页），其余页显示页面名
                        text = when {
                            showingConfig -> "配置"
                            tab == 0 -> "折跃"
                            else -> tabs.getOrNull(tab)?.title ?: "折跃"
                        },
                        style = MaterialTheme.typography.titleLarge,
                    )
                },
                navigationIcon = {
                    if (showingConfig) {
                        IconButton(onClick = { tab = previousTab }) {
                            Icon(Icons.Outlined.ArrowBack, contentDescription = "返回")
                        }
                    }
                },
                actions = {
                    if (!showingConfig) {
                        IconButton(onClick = {
                            previousTab = tab
                            tab = 5
                        }) {
                            Icon(Icons.Outlined.Settings, contentDescription = "配置")
                        }
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.background,
                    scrolledContainerColor = MaterialTheme.colorScheme.background,
                ),
            )
        },
        bottomBar = {
            // 宽屏用左侧 NavigationRail 替换底栏：底栏在 10 寸宽屏上只占一条边，
            // 却把六个标签横向摊开一号字大小的目标，反而不如竖排。
            if (!showingConfig && !wide) {
                Column {
                    // 用一条极淡的分隔线代替 elevation：深色下 elevation 会泛白，线更稳
                    HorizontalDivider(
                        thickness = 1.dp,
                        color = MaterialTheme.colorScheme.outlineVariant,
                    )
                    NavigationBar(
                        containerColor = MaterialTheme.colorScheme.surface,
                        tonalElevation = 0.dp,
                    ) {
                        tabs.forEachIndexed { index, item ->
                            NavigationBarItem(
                                selected = tab == index,
                                onClick = { tab = index },
                                icon = {
                                    Icon(
                                        item.icon,
                                        contentDescription = item.title,
                                        modifier = Modifier.size(22.dp),
                                    )
                                },
                                label = {
                                    Text(item.title, style = MaterialTheme.typography.labelSmall)
                                },
                                // 选中态用实心胶囊 + 反色图标：比默认浅色指示块更"立"，
                                // 也更接近 Shadowrocket / Stash 这类工具的观感
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = MaterialTheme.colorScheme.onPrimary,
                                    indicatorColor = MaterialTheme.colorScheme.primary,
                                    selectedTextColor = MaterialTheme.colorScheme.primary,
                                    unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant,
                                    unselectedTextColor = MaterialTheme.colorScheme.onSurfaceVariant,
                                ),
                            )
                        }
                    }
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        Row(Modifier.fillMaxSize().padding(padding)) {
            if (wide && !showingConfig) {
                NavigationRail(containerColor = MaterialTheme.colorScheme.surface) {
                    tabs.forEachIndexed { index, item ->
                        NavigationRailItem(
                            selected = tab == index,
                            onClick = { tab = index },
                            icon = {
                                Icon(
                                    item.icon,
                                    contentDescription = item.title,
                                    modifier = Modifier.size(22.dp),
                                )
                            },
                            label = { Text(item.title, style = MaterialTheme.typography.labelSmall) },
                        )
                    }
                }
                VerticalDivider(
                    thickness = 1.dp,
                    color = MaterialTheme.colorScheme.outlineVariant,
                )
            }
            Box(
                Modifier.weight(1f).fillMaxHeight(),
                contentAlignment = Alignment.TopCenter,
            ) {
                // 只做淡入淡出：切页是"同一层内容替换"，横向位移会让人误以为存在层级关系。
                // 上限跟着断点走：平板上 560dp 太小，铺满又太长，中间三档是折中。
                AnimatedContent(
                    targetState = tab,
                    transitionSpec = { fadeIn(tween(160)) togetherWith fadeOut(tween(100)) },
                    modifier = Modifier.widthIn(max = contentMax).fillMaxSize(),
                    label = "tab",
                ) { current ->
                    when {
                        current == 0 -> OverviewPage(vm, onRequestStart)
                        current == 1 -> ProxiesPage(vm)
                        current == 2 -> ConnectionsPage(vm)
                        current == 3 -> RulesPage(vm)
                        current == 4 -> LogsPage(vm, logMode) { logMode = it }
                        // 配置页的「内核日志 · 查看」直接跳到日志页的内核页签
                        else -> ConfigPage(vm, onOpenCoreLog = { logMode = 1; tab = 4 })
                    }
                }
            }
        }
    }
}
