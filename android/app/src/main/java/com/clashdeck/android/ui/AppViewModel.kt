package com.clashdeck.android.ui

import android.app.Application
import android.content.Intent
import android.net.VpnService
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clashdeck.android.api.ClashApi
import com.clashdeck.android.api.ConnectionItem
import com.clashdeck.android.api.ConnectionsSnapshot
import com.clashdeck.android.api.LogEntry
import com.clashdeck.android.api.ProxyItem
import com.clashdeck.android.api.ProxySnapshot
import com.clashdeck.android.api.RuleItem
import com.clashdeck.android.api.RulePrediction
import com.clashdeck.android.api.RuleProviderInfo
import com.clashdeck.android.api.RuntimeConfig
import com.clashdeck.android.BuildConfig
import com.clashdeck.android.api.TrafficSample
import com.clashdeck.android.core.CoreManager
import com.clashdeck.android.core.CoreState
import com.clashdeck.android.core.Native
import com.clashdeck.android.data.DownloadProgress
import com.clashdeck.android.data.AppPaths
import com.clashdeck.android.data.CustomRule
import com.clashdeck.android.data.ProfileInfo
import com.clashdeck.android.data.ProfileRepo
import com.clashdeck.android.data.ProviderRepo
import com.clashdeck.android.data.SettingsRepo
import com.clashdeck.android.data.SubUpdateState
import com.clashdeck.android.data.SubscriptionAutoUpdate
import com.clashdeck.android.data.UpdateInfo
import com.clashdeck.android.data.UpdateRepo
import com.clashdeck.android.util.Errors
import com.clashdeck.android.util.Installer
import com.clashdeck.android.vpn.TunService
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.withContext

/**
 * 界面唯一的业务入口。
 *
 * 数据来源分两种，不要混：
 * - 内核状态（是否连接）：来自 CoreManager，是本进程里的真实状态
 * - 节点/连接/规则/流量：一律问内核的 external-controller，界面不自己推断
 */
class AppViewModel(app: Application) : AndroidViewModel(app) {

    val settings = SettingsRepo(app)
    val repo = ProfileRepo(app, settings)

    private var api = ClashApi(settings.apiPort)

    val coreState: StateFlow<CoreState> = CoreManager.state

    val traffic = MutableStateFlow(TrafficSample(0, 0))
    val trafficHistory = MutableStateFlow<List<TrafficSample>>(emptyList())
    val proxies = MutableStateFlow(ProxySnapshot(emptyList(), emptyMap()))
    val connections = MutableStateFlow(ConnectionsSnapshot(0, 0, emptyList()))
    val rules = MutableStateFlow<List<RuleItem>>(emptyList())
    val providers = MutableStateFlow<List<RuleProviderInfo>>(emptyList())
    val providerBusy = MutableStateFlow<String?>(null)

    /** 规则集读取失败的原因：把「没有 provider」和「读不到」区分开。 */
    val providerError = MutableStateFlow<String?>(null)
    val prediction = MutableStateFlow<RulePrediction?>(null)
    val predicting = MutableStateFlow(false)
    val predictError = MutableStateFlow<String?>(null)
    val customRules = MutableStateFlow<List<CustomRule>>(emptyList())
    val logs = MutableStateFlow<List<LogEntry>>(emptyList())
    val profiles = MutableStateFlow<List<ProfileInfo>>(emptyList())

    /**
     * 当前订阅 id。必须是可观察状态：
     * 切换订阅只改 prefs、不改列表内容，若界面直接读 settings 就不会重组（表现为"切了没反应，退出重进才生效"）。
     */
    val activeProfileId = MutableStateFlow<String?>(null)
    val runtime = MutableStateFlow(RuntimeConfig("rule", 0, false))
    val mode = MutableStateFlow(settings.mode)

    /**
     * 分流模式（智能 / 严格）。
     * 同样走状态流：概览页和配置页各显示一次，若直接读 prefs 就不会重组，
     * 一处改了另一处仍显示旧值——这正是"界面在说谎"那类问题的来源。
     */
    val proxyMode = MutableStateFlow(settings.proxyMode)
    val coreVersion = MutableStateFlow("")
    val toast = MutableStateFlow<String?>(null)
    val busy = MutableStateFlow(false)
    val testingGroups = MutableStateFlow<Set<String>>(emptySet())
    val apiReady = MutableStateFlow(false)

    /** 订阅自动更新状态。后台刷新写的是 SharedPreferences，界面靠 syncSubStatus 对齐。 */
    val subState = MutableStateFlow(SubUpdateState())
    val subBusy = MutableStateFlow(false)

    /** 控制接口最后一次失败原因。界面直接展示——"还没读到"这种话说明不了任何问题。 */
    val apiError = MutableStateFlow<String?>(null)

    // ---------- 应用内升级 ----------

    private val updater = UpdateRepo(app)
    private val providerRepo = ProviderRepo(app, settings)

    /** 服务端声明的新版本；null = 没有更新。 */
    val updateInfo = MutableStateFlow<UpdateInfo?>(null)

    /** 正在检查（用于按钮禁用与文案）。 */
    val updateChecking = MutableStateFlow(false)

    /** 检查/下载/安装的失败原因。启动时的静默检查不写这里。 */
    val updateError = MutableStateFlow<String?>(null)

    /** 用户点了「稍后」。强制更新时忽略这个标记。 */
    val updateDismissed = MutableStateFlow(false)

    /**
     * 上次**成功**检查更新的时刻（0 = 从未成功）。
     * 为什么需要：检查失败时原来什么都不写，界面就显示"当前已是最新"——
     * 用户以为查过了，其实一次都没查成。这个状态只由成功路径写入。
     */
    val updateCheckedAt = MutableStateFlow(settings.lastUpdateCheck)

    /** 下载进度；null = 没在下载。 */
    val updateProgress = MutableStateFlow<DownloadProgress?>(null)

    /** 已下载完成、等待安装的包（下载成功但安装被权限/后台拦下时要能再点一次）。 */
    val updateApk = MutableStateFlow<File?>(null)

    init {
        loadProfiles()
        // 订阅自动更新：进程一起来就重排闹钟，并把后台写进 SP 的结果同步给界面。
        // 后台只在前台服务宿主存在时下载；没有宿主时留的 pending 在这里补做。
        SubscriptionAutoUpdate.arm(app)
        syncSubStatus()
        val sub = subState.value
        if (SubscriptionAutoUpdate.isDue(
                sub.enabled, sub.pending, sub.lastCheckAt, sub.intervalMinutes, System.currentTimeMillis(),
            )
        ) {
            refreshSubscriptions(force = false, silent = true)
        }
        // 订阅下载优先走本机代理（本应用自身流量被排除在 tun 外，直连机场域名多半会超时）
        repo.coreRunning = { CoreManager.state.value.running }
        providerRepo.coreRunning = { CoreManager.state.value.running }
        repo.mixedPort = { settings.mixedPort }
        customRules.value = repo.parseCustomRules()

        // 启动时静默查一次更新（6 小时内不重复查）。
        // 失败不打扰：没网是常态，把错误糊到用户脸上只会让人以为 App 坏了。
        if (System.currentTimeMillis() - settings.lastUpdateCheck > 6 * 60 * 60 * 1000L) {
            checkUpdate(manual = false)
        }

        viewModelScope.launch {
            CoreManager.state.map { it.running }.distinctUntilChanged().collectLatest { running ->
                if (!running) {
                    // 断开后把"运行期才存在"的数据全部清空。
                    // 否则界面会留着上一次的节点列表/连接列表，看起来像"关了但代理还在"。
                    traffic.value = TrafficSample(0, 0)
                    trafficHistory.value = emptyList()
                    proxies.value = ProxySnapshot(emptyList(), emptyMap())
                    connections.value = ConnectionsSnapshot(0, 0, emptyList())
                    providers.value = emptyList()
                    prediction.value = null
                    predictError.value = null
                    coreVersion.value = ""
                    apiError.value = null
                    apiReady.value = false
                    runtime.value = RuntimeConfig("rule", 0, false)
                } else {
                    rebindApi()
                    // 必须用 coroutineScope 包住：collectLatest 只取消"块本身"，
                    // 直接 launch 会挂到外层 viewModelScope 上，连接一次就多一个永不退出的轮询。
                    // 后果是断开后轮询仍在跑，把用户选的模式每 2 秒改回默认值——"切不了"的真正原因。
                    coroutineScope {
                        // 延后几秒再补规则集：连上的瞬间要先把界面数据读出来，
                        // 预下载（可能多个 provider）并发跑会和首次刷新抢 IO/CPU，把 3 秒超时挤爆。
                        launch {
                            kotlinx.coroutines.delay(3000)
                            bootstrapProviders()
                        }
                        launch { followTraffic() }
                        launch { followLogs() }
                        launch { pollLoop() }
                        launch { ensureAutoExit() }
                    }
                }
            }
        }
    }

    private fun rebindApi() {
        // 配置里的端口（默认 9090）被别的程序占用时，内核会自动换一个空闲端口。
        // 所以这里必须问内核要"实际在听哪个端口"，不能假设还是设置里那个值。
        val actual = runCatching { Native.apiPort() }.getOrDefault(0)
        api = ClashApi(if (actual > 0) actual else settings.apiPort)
    }

    private fun loadProfiles() {
        profiles.value = repo.list()
        activeProfileId.value = repo.active()?.id
    }

    private suspend fun pollLoop() {
        // 以"内核是否在跑"为循环条件，而不是 viewModelScope 是否存活（后者恒为 true）
        //
        // 容错策略：内核刚启动的头一两秒，控制端可能还没就绪（ReCreateServer 是异步监听）。
        // 所以用"连续失败"计数——偶发失败不打扰用户，连续失败才认为真的读不到。
        // 之前一失败就写 apiError，导致每次连接都必然闪一条红字，看着像坏了。
        var failures = 0
        while (CoreManager.state.value.running) {
            val result = runCatching { refreshOnce() }
            if (result.isSuccess) {
                failures = 0
                apiError.value = null
            } else {
                failures++
                if (failures >= 3) {
                    apiError.value = describeApiError(result.exceptionOrNull())
                }
            }
            delay(if (failures > 0) 1200 else 2000)
        }
    }

    /**
     * 每次连接把出口复位到订阅的「自动选择」组。
     *
     * 配置层已经做完主要工作：preferAutoGroup() 把 url-test 组放到 select 组候选首位，
     * profile.store-selected=false 让内核不写也不读选中缓存 —— 冷启动与重连都回退到
     * proxies[0]，也就是自动选择组。所以这里不再需要测速、判断或死节点救场。
     *
     * 只剩 GLOBAL 必须管：它是内核内置组，不在订阅 YAML 里，配置层够不着。
     * 分流目标组一并复位，兜住订阅里显式写了 default-selected 之类的特例。
     * url-test 自己按延迟维护健康节点，一轮足够。
     */
    private suspend fun ensureAutoExit() {
        // 等内核就绪并跑完首轮 url-test 测速：过早读快照会拿到空列表
        delay(5000)
        var waited = 0
        while (CoreManager.state.value.running && !apiReady.value && waited < 30) {
            delay(1000)
            waited++
        }
        if (!CoreManager.state.value.running || !apiReady.value) return

        val snap = proxies.value
        val auto = snap.groups.firstOrNull { g ->
            g.name != "GLOBAL" &&
                (g.type.equals("URLTest", ignoreCase = true) || g.type.equals("Fallback", ignoreCase = true))
        } ?: return
        val main = snap.groups.firstOrNull {
            it.name != "GLOBAL" && it.type.equals("Selector", ignoreCase = true)
        }
        listOfNotNull(
            snap.groups.firstOrNull { it.name == "GLOBAL" },
            main,
        ).filter { it.all.contains(auto.name) && it.now != auto.name }
            .forEach { g -> runCatching { api.select(g.name, auto.name) } }
        runCatching { refreshOnce() }
    }

    /** 把网络异常翻译成用户能理解、且能据此行动的说法。 */
    private fun describeApiError(t: Throwable?): String = when (t) {
        null -> "未知错误"
        is java.net.SocketTimeoutException ->
            "控制接口无响应（内核可能仍在启动或已卡住）。可点「重试」；若一直这样，断开后重连一次"
        is java.net.ConnectException ->
            "控制接口没有监听（内核可能已退出）。断开后重新连接即可"
        is java.io.IOException ->
            "读取控制接口出错：${t.message ?: "无详细信息"}"
        else -> "${t.javaClass.simpleName}: ${t.message ?: "无详细信息"}"
    }

    /** 界面上的「重试」：立刻打一次，不等下一个轮询周期。 */
    fun retryApiNow() {
        viewModelScope.launch {
            apiError.value = null
            runCatching { refreshOnce() }
                .onFailure { apiError.value = describeApiError(it) }
        }
    }

    private suspend fun refreshOnce() = withContext(Dispatchers.IO) {
        val snapshot = api.proxies()
        if (snapshot.groups.isNotEmpty() || snapshot.nodes.isNotEmpty()) {
            proxies.value = snapshot
            apiReady.value = true
        }
        connections.value = api.connections()
        val cfg = api.configs()
        runtime.value = cfg
        if (cfg.mode.isNotBlank()) mode.value = cfg.mode
        if (coreVersion.value.isBlank()) coreVersion.value = api.version()
        if (rules.value.isEmpty()) rules.value = api.rules()
    }

    private suspend fun followTraffic() {
        runCatching {
            api.trafficFlow().collect { s ->
                traffic.value = s
                val next = trafficHistory.value + s
                trafficHistory.value = if (next.size > 90) next.takeLast(90) else next
            }
        }
    }

    private suspend fun followLogs() {
        runCatching {
            api.logFlow(settings.logLevel).collect { entry ->
                val next = logs.value + entry
                logs.value = if (next.size > 800) next.takeLast(800) else next
            }
        }
    }

    // ---------- 连接控制 ----------

    fun vpnPermissionIntent(): Intent? = VpnService.prepare(getApplication())

    fun startVpn() {
        // 以持久化设置为准，并用它矫正界面状态。
        // 反过来（settings.mode = mode.value）会把界面里的旧值盖到用户的选择上——
        // 表现就是"断开时选了直连，一连上又变回规则"。
        mode.value = settings.mode
        rebindApi()
        TunService.start(getApplication())
    }

    fun stopVpn() {
        TunService.stop(getApplication())
    }

    /**
     * 重连（换模式、换配置、改端口都走这里）。
     *
     * 两个坑都在这里修掉：
     *
     * ① **不能被并发触发**。7 个入口都会调它（切模式、切分流、换订阅、改端口、改测速地址、
     *    改详细日志、增删自定义规则）。用户快速点两个开关就会两次并发重启：
     *    第二次的 `startCore` 会被 Go 侧以 `core already running` 拒绝，
     *    而拒绝会走"启动失败"分支去 teardown —— **把刚刚连好的内核拆掉**，
     *    界面还会弹一句看不懂的 "core already running"。实测路径就是"连上又断、还报错"。
     *    这里用一把互斥 + 一个 pending 标记：同一时刻只有一个重启在跑，
     *    排队期间的新请求合并成"跑完再跑一次"，不丢请求也不叠加。
     *
     * ② 等待要等状态真正离开 RUNNING（而不是睡固定 1.5 秒），慢机上才不会撞端口。
     */
    private val restartMutex = Mutex()
    private var restartPending = false

    fun restartVpn() {
        // 重启进行中的点击**必须记账**，不能直接 return。
        // 重启要 2–3 秒（stop → 等退出 → 400ms → start），这段时间 CoreManager 已经不在 RUNNING，
        // 老写法会在第一行就 return —— 用户在窗口内切的分流/模式被静默丢弃，
        // 界面显示已选中、实际没生效（又一次"界面在说谎"）。
        if (restartMutex.isLocked) {
            restartPending = true
            return
        }
        if (!CoreManager.state.value.running) return
        viewModelScope.launch {
            if (!restartMutex.tryLock()) {
                restartPending = true
                return@launch
            }
            try {
                do {
                    restartPending = false
                    performRestart()
                } while (restartPending)
            } finally {
                restartMutex.unlock()
            }
        }
    }

    private suspend fun performRestart() {
        TunService.stop(getApplication())
        withTimeoutOrNull(8_000) {
            while (CoreManager.state.value.running) delay(100)
        }
        // 端口释放比状态翻牌略晚一点，给一小段静默期，避免 bind 撞车
        delay(400)
        // 重启会让内核重新解析规则（自定义规则、订阅更新、模式切换都会改规则表），
        // 而 pollLoop 只在 rules 为空时才重拉 —— 不清空就会出现"加了规则，规则页里看不到"：
        // 界面显示旧列表、实际已生效，是典型的"界面在说谎"。
        rules.value = emptyList()
        TunService.start(getApplication())
    }

    fun setMode(newMode: String) {
        mode.value = newMode
        settings.mode = newMode
        com.clashdeck.android.util.DiagLog.i(
            "mode",
            "切换为 $newMode（内核运行中=${CoreManager.state.value.running}）",
        )
        if (!CoreManager.state.value.running) {
            // 未连接时切换是"预置"行为。刻意不弹提示：
            // 用户连着点几个模式会被弹窗糊脸，而状态卡里已经写着"按「直连」接管流量"。
            return
        }
        // 走内核内部 API：embed 模式下 PATCH /configs 是关闭的（见 Native.setMode 注释）
        val rc = runCatching {
            if (Native.ensureLoaded()) Native.setMode(newMode) else -1
        }.getOrDefault(-1)
        if (rc != 0) {
            val detail = runCatching { Native.lastError() }.getOrNull()
            toast.value = "切换模式失败：" + (detail?.takeIf { it.isNotBlank() } ?: "code=$rc")
        }
    }

    fun selectProxy(group: String, node: String) {
        viewModelScope.launch {
            runCatching { api.select(group, node) }
                .onFailure { toast.value = "切换失败：${it.message}" }
            delay(150)
            runCatching { refreshOnce() }
        }
    }

    fun testNode(node: String) {
        viewModelScope.launch {
            val d = runCatching { api.delayOf(node, settings.testUrl, settings.testTimeout) }.getOrNull()
            toast.value = if (d != null) "$node：$d ms" else "$node：超时"
            runCatching { refreshOnce() }
        }
    }

    fun testGroup(group: ProxyItem) {
        viewModelScope.launch {
            testingGroups.value = testingGroups.value + group.name
            try {
                group.all.chunked(8).forEach { chunk ->
                    coroutineScope {
                        chunk.map { name ->
                            async { runCatching { api.delayOf(name, settings.testUrl, settings.testTimeout) } }
                        }.awaitAll()
                    }
                }
                runCatching { refreshOnce() }
            } finally {
                testingGroups.value = testingGroups.value - group.name
            }
        }
    }

    fun closeConnection(item: ConnectionItem) {
        viewModelScope.launch {
            api.closeConnection(item.id)
            delay(200)
            runCatching { refreshOnce() }
        }
    }

    fun closeAllConnections() {
        viewModelScope.launch {
            api.closeAllConnections()
            delay(300)
            runCatching { refreshOnce() }
        }
    }

    fun reloadRules() {
        viewModelScope.launch {
            runCatching { rules.value = api.rules() }
                .onFailure { toast.value = "读取规则失败：${it.message}" }
            refreshProvidersOnce()
        }
    }

    // ---------- 规则集管理 ----------

    fun loadProviders() {
        viewModelScope.launch { refreshProvidersOnce() }
    }

    private suspend fun refreshProvidersOnce() {
        runCatching { api.ruleProviders() }
            .onSuccess {
                providers.value = it
                providerError.value = null
            }
            .onFailure {
                providerError.value = it.message ?: it.javaClass.simpleName
            }
    }

    /**
     * 连上之后把规则集补齐。
     *
     * 顺序不是随意的：
     * 1. 先把缺的文件抓到工作目录（内核自己下载会因为"规则集未生效→落代理"而失败，见 ProviderRepo 注释）
     * 2. 再让内核 PUT 重载——它会读我们刚放好的本地文件，条目数立刻就该有
     * 3. 然后短轮询：条数就绪需要一点时间，界面不该停在"0 条"
     */
    private suspend fun bootstrapProviders() {
        val updated = runCatching {
            providerRepo.prefetch(AppPaths.runtimeConfig) { name -> providerBusy.value = name }
        }.getOrDefault(emptyList())
        providerBusy.value = null

        updated.forEach { runCatching { api.updateRuleProvider(it) } }

        var tries = 0
        while (tries < 6) {
            refreshProvidersOnce()
            val list = providers.value
            if (list.isNotEmpty() && list.all { it.ruleCount > 0 }) return
            delay(2000)
            tries++
        }
    }

    fun updateProvider(name: String) {
        viewModelScope.launch {
            providerBusy.value = name
            // 先由 App 自己抓（先代理后直连），再让内核重载本地文件
            val fetched = runCatching {
                providerRepo.prefetch(AppPaths.runtimeConfig, force = true, only = name)
            }.getOrDefault(emptyList())
            val ok = api.updateRuleProvider(name)
            providerBusy.value = null
            refreshProvidersOnce()
            runCatching { rules.value = api.rules() }

            val p = providers.value.firstOrNull { it.name == name }
            toast.value = when {
                p != null && p.ruleCount > 0 -> "已更新：${p.name}（${p.ruleCount} 条）"
                fetched.isEmpty() -> "更新失败：$name（下载被拦截）"
                else -> "已重新下载，但内核读到的条目为 0：$name"
            }
        }
    }

    fun updateAllProviders() {
        viewModelScope.launch {
            providerBusy.value = "ALL"
            // force=true：用户点"更新"就是要重新下载，不能因为本地已有文件就跳过
            val fetched = runCatching {
                providerRepo.prefetch(AppPaths.runtimeConfig, force = true) { }
            }.getOrDefault(emptyList())

            var okCount = 0
            var failCount = 0
            providers.value.forEach { p ->
                // 预下载成功的直接重载；没下到的让内核自己试一次（可能它那侧网络更通）
                if (api.updateRuleProvider(p.name)) okCount++ else failCount++
            }
            providerBusy.value = null
            refreshProvidersOnce()
            runCatching { rules.value = api.rules() }

            val empty = providers.value.count { it.ruleCount == 0 }
            toast.value = buildString {
                append("规则集更新：成功 $okCount，失败 $failCount")
                if (fetched.isNotEmpty()) append("；已重新下载 ${fetched.size} 个")
                if (empty > 0) append("；仍有 $empty 个为空（多为下载被拦截）")
            }
        }
    }

    // ---------- 规则预判 ----------

    fun predictRule(rawInput: String) {
        val domain = rawInput.trim()
            .removePrefix("http://").removePrefix("https://")
            .substringBefore("/")
        if (domain.isBlank()) return
        viewModelScope.launch {
            predicting.value = true
            predictError.value = null
            prediction.value = null
            try {
                val result = api.predict(domain, settings.mixedPort)
                if (result == null) {
                    predictError.value = "内核未运行，先连接再预判"
                } else {
                    prediction.value = result
                }
            } catch (t: Throwable) {
                predictError.value = t.message ?: "预判失败"
            } finally {
                predicting.value = false
            }
        }
    }

    fun clearPrediction() {
        prediction.value = null
        predictError.value = null
    }

    // ---------- 配置管理 ----------

    fun importFromUrl(name: String, url: String) {
        viewModelScope.launch {
            busy.value = true
            try {
                val p = repo.importFromUrl(name, url)
                repo.setActive(p.id)
                loadProfiles()
                toast.value = "已导入：${p.name}"
                afterProfileChanged()
            } catch (t: Throwable) {
                toast.value = "导入失败：${Errors.human(t)}"
            } finally {
                busy.value = false
            }
        }
    }

    fun importFromText(name: String, text: String) {
        viewModelScope.launch {
            busy.value = true
            try {
                val p = repo.importFromText(name, text)
                repo.setActive(p.id)
                loadProfiles()
                toast.value = "已导入：${p.name}"
                afterProfileChanged()
            } catch (t: Throwable) {
                toast.value = "导入失败：${Errors.human(t)}"
            } finally {
                busy.value = false
            }
        }
    }

    fun updateProfile(profile: ProfileInfo) {
        viewModelScope.launch {
            busy.value = true
            try {
                val (updated, changed) = repo.update(profile)
                loadProfiles()
                toast.value = if (changed) "已更新：${updated.name}" else "内容没有变化：${updated.name}"
                // 流量头每次都变，节点表可能一个字没改；没变化就不拆正在连的内核。
                if (changed && repo.active()?.id == updated.id) restartVpn()
            } catch (t: Throwable) {
                toast.value = "更新失败：" + com.clashdeck.android.util.DiagLog.redact(Errors.human(t))
            } finally {
                busy.value = false
            }
        }
    }

    // ---------- 订阅自动更新 ----------

    /** 后台刷新写的是 SP，StateFlow 不会自动跟着变；配置页可见时定时调它对齐。 */
    fun syncSubStatus() {
        subState.value = SubscriptionAutoUpdate.readState(getApplication())
    }

    fun setSubAutoEnabled(enabled: Boolean) {
        settings.subAutoEnabled = enabled
        if (enabled) {
            SubscriptionAutoUpdate.arm(getApplication())
            // 排程没落地就回滚开关，避免界面写着「已开启」却永远不会触发。
            if (settings.subNextAt <= 0L) {
                settings.subAutoEnabled = false
                toast.value = "系统没有可用的闹钟服务，自动更新未开启"
            }
        } else {
            settings.subPending = false
            SubscriptionAutoUpdate.cancel(getApplication())
        }
        syncSubStatus()
    }

    /** @param minutes 1–10080；越界直接拒绝，不做静默钳制，否则界面显示的数字会和实际排程不一致。 */
    fun setSubIntervalMinutes(minutes: Int) {
        if (minutes !in SettingsRepo.SUB_INTERVAL_MIN..SettingsRepo.SUB_INTERVAL_MAX) {
            toast.value = "间隔要在 ${SettingsRepo.SUB_INTERVAL_MIN}–${SettingsRepo.SUB_INTERVAL_MAX} 分钟之间"
            return
        }
        val old = settings.subIntervalMinutes
        settings.subIntervalMinutes = minutes
        SubscriptionAutoUpdate.arm(getApplication())
        // 排程没落地就不能让界面显示一个并不存在的新间隔。
        if (settings.subNextAt <= 0L) {
            settings.subIntervalMinutes = old
            // 先用旧值重排一次：否则「开关已开启」但下一次闹钟是空的。
            SubscriptionAutoUpdate.arm(getApplication())
            if (settings.subNextAt <= 0L) {
                // 闹钟服务彻底不可用，开关不能悬空。
                settings.subAutoEnabled = false
                SubscriptionAutoUpdate.cancel(getApplication())
            }
            toast.value = "系统没有可用的闹钟服务，间隔未改动"
        }
        syncSubStatus()
    }

    /** force=true 用于界面上的「立刻检查一次」：不受总开关与退避限制。 */
    fun refreshSubscriptions(force: Boolean = false, silent: Boolean = false) {
        if (subBusy.value) return
        subBusy.value = true
        viewModelScope.launch {
            val result = withContext(Dispatchers.IO) {
                SubscriptionAutoUpdate.refresh(getApplication(), force)
            }
            loadProfiles()
            syncSubStatus()
            if (!silent) {
                toast.value = when {
                    result.error != null -> "订阅检查失败：${result.error}"
                    result.skipped -> "订阅自动更新未开启"
                    result.changed && !result.reloaded -> "订阅已更新，下次连接生效"
                    result.changed -> "订阅已更新并生效"
                    else -> "订阅没有变化"
                }
            }
            subBusy.value = false
        }
    }

    fun deleteProfile(profile: ProfileInfo) {
        viewModelScope.launch {
            val wasActive = repo.active()?.id == profile.id
            repo.delete(profile)
            loadProfiles()
            toast.value = "已删除：${profile.name}"
            if (wasActive) {
                if (CoreManager.state.value.running) stopVpn()
            }
        }
    }

    fun activateProfile(profile: ProfileInfo) {
        repo.setActive(profile.id)
        loadProfiles()
        if (CoreManager.state.value.running) restartVpn()
    }

    fun renameProfile(profile: ProfileInfo, newName: String) {
        if (newName.isBlank()) return
        repo.rename(profile, newName.trim())
        loadProfiles()
    }

    private fun afterProfileChanged() {
        if (CoreManager.state.value.running) restartVpn()
    }

    /**
     * 手动加一条规则：把这个域名钉成直连或走某个策略组。
     * 写入后置于规则表最前（内核从上往下匹配），改了就跑重启让内核重新加载。
     */
    /**
     * 添加自定义规则。
     *
     * @param type   DOMAIN-SUFFIX / DOMAIN / DOMAIN-KEYWORD；输入是 IP 或网段时会被自动改成 IP-CIDR
     * @param action proxy（走代理组）/ direct（直连）
     */
    fun addCustomRule(input: String, type: String, action: String) {
        val raw = input.trim()
            .removePrefix("http://").removePrefix("https://")
            .substringBefore("/")
            .substringBefore("?")
            .trim()
            .lowercase()
        if (raw.isBlank()) {
            toast.value = "请输入域名或 IP"
            return
        }
        // 输入是 IP/网段时忽略用户选的匹配方式：域名类规则对 IP 无效，硬套只会静默不生效
        val looksIp = raw.firstOrNull()?.isDigit() == true ||
            raw.contains(":") ||
            (raw.contains("/") && raw.substringBefore("/").firstOrNull()?.isDigit() == true)
        val finalType = if (looksIp) CustomRule.IP_CIDR else type
        val value = when {
            finalType != CustomRule.IP_CIDR -> raw
            raw.contains("/") -> raw
            raw.contains(":") -> "$raw/128"
            else -> "$raw/32"
        }

        val rule = CustomRule(finalType, value, action)
        val rules = repo.parseCustomRules()
        if (rules.any { it.type == rule.type && it.value == rule.value }) {
            toast.value = "这条规则已经存在"
            return
        }
        repo.saveCustomRules(rules + rule)
        customRules.value = repo.parseCustomRules()
        toast.value = "已添加：${rule.label()}"
        if (CoreManager.state.value.running) restartVpn()
    }

    fun removeCustomRule(rule: CustomRule) {
        val rules = repo.parseCustomRules().filterNot { it.type == rule.type && it.value == rule.value }
        repo.saveCustomRules(rules)
        customRules.value = rules
        if (CoreManager.state.value.running) restartVpn()
    }

    // ---------- 设置 ----------

    /** 改端口需要重建运行期配置：混合端口写在 yaml 里，不重连不生效。 */
    fun savePorts(mixed: Int, apiPort: Int) {
        val needsRestart = settings.mixedPort != mixed || settings.apiPort != apiPort
        settings.mixedPort = mixed
        settings.apiPort = apiPort
        rebindApi()
        toast.value = "端口已保存" + if (needsRestart) "，重新连接后生效" else ""
        if (needsRestart && CoreManager.state.value.running) restartVpn()
    }

    /** 测速地址与超时只影响之后的测速请求，不动内核，因此不需要重连。 */
    fun saveTestTarget(url: String, timeoutMs: Int) {
        settings.testUrl = url
        settings.testTimeout = timeoutMs
    }

    fun setAutoStart(enabled: Boolean) {
        settings.autoStart = enabled
    }

    /**
     * 详细日志开关。
     *
     * 日志级别写在运行期配置里（log-level），所以必须重建配置并重连才生效——
     * 界面开关不重连就"看着打开了、其实没变"，那是最误导人的状态。
     */
    fun setVerboseLog(enabled: Boolean) {
        settings.logLevel = if (enabled) "debug" else "info"
        toast.value = if (enabled) "已开启详细日志，重新连接后生效" else "已关闭详细日志，重新连接后生效"
        if (CoreManager.state.value.running) restartVpn()
    }

    /** 内置分流开关。切换后必须重建运行期配置，否则要等下次连接才生效。 */
    fun setBuiltinRules(enabled: Boolean) {
        settings.builtinRules = enabled
        toast.value = if (enabled) "已启用内置分流，重新连接后生效" else "已关闭内置分流，重新连接后生效"
        if (CoreManager.state.value.running) restartVpn()
    }

    /**
     * 分流模式（智能 / 严格），与 Windows 端同一口径。
     *
     * 这是影响面最大的一个开关：严格模式下所有没被规则命中的流量都走直连，
     * 所以切换后要重连，并且明确告诉用户代价。
     */
    fun setProxyMode(mode: String) {
        val strict = mode != SettingsRepo.MODE_SMART
        if ((settings.proxyMode == SettingsRepo.MODE_STRICT) == strict) return
        settings.proxyMode = if (strict) SettingsRepo.MODE_STRICT else SettingsRepo.MODE_SMART
        proxyMode.value = settings.proxyMode
        toast.value = if (strict) {
            "已切换为严格模式：只放行列出的服务，其余直连。重新连接后生效"
        } else {
            "已切换为智能模式：没命中的流量走代理。重新连接后生效"
        }
        if (CoreManager.state.value.running) restartVpn()
    }

    // ---------- 应用内升级 ----------

    /**
     * 检查更新。
     *
     * @param manual 用户主动点的（要给出"已是最新"/失败原因）；false 为启动时静默检查
     */
    fun checkUpdate(manual: Boolean) {
        if (updateChecking.value || updateProgress.value != null) return
        viewModelScope.launch {
            updateChecking.value = true
            if (manual) updateError.value = null
            runCatching { updater.checkForUpdate() }
                .onSuccess { info ->
                    updateInfo.value = info
                    updateDismissed.value = false
                    settings.lastUpdateCheck = System.currentTimeMillis()
                    updateCheckedAt.value = settings.lastUpdateCheck
                    if (info == null) {
                        updateError.value = null
                        if (manual) toast.value = "已是最新版本 ${BuildConfig.VERSION_NAME}"
                    }
                }
                .onFailure { e ->
                    if (manual) {
                        val msg = e.message ?: "检查更新失败"
                        updateError.value = msg
                        toast.value = msg
                    }
                }
            updateChecking.value = false
        }
    }

    /** 下载并尝试拉起安装器。 */
    fun startUpdate() {
        val info = updateInfo.value ?: return
        if (updateProgress.value != null) return
        updateError.value = null
        viewModelScope.launch {
            updateProgress.value = DownloadProgress(0, info.size)
            var lastPercent = -1
            val file = runCatching {
                updater.download(info) { written, total ->
                    // 按百分比节流：41 MB 会产生几千次回调，每次都更新 StateFlow 会拖慢主线程
                    val pct = if (total > 0) (written * 100 / total).toInt() else -1
                    if (pct != lastPercent) {
                        lastPercent = pct
                        updateProgress.value = DownloadProgress(written, total)
                    }
                }
            }.onFailure { e ->
                updateProgress.value = null
                updateError.value = e.message ?: "下载失败"
            }.getOrNull() ?: return@launch

            updateProgress.value = null
            updateApk.value = file
            Installer.install(getApplication(), file, info.versionName)?.let { reason ->
                updateError.value = reason
            }
        }
    }

    /** 已下载但没装成（未授权/在后台）时，用户手动再点一次。 */
    fun installDownloaded() {
        val info = updateInfo.value ?: return
        val apk = updateApk.value ?: return
        updateError.value = Installer.install(getApplication(), apk, info.versionName)
    }

    fun dismissUpdate() {
        if (updateInfo.value?.forced == true) return
        updateDismissed.value = true
    }

    fun consumeToast() {
        toast.value = null
    }
}
