package com.clashdeck.android.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.core.app.ServiceCompat
import com.clashdeck.android.MainActivity
import com.clashdeck.android.R
import com.clashdeck.android.core.CoreManager
import com.clashdeck.android.core.CoreStatus
import com.clashdeck.android.core.Native
import com.clashdeck.android.data.AppPaths
import com.clashdeck.android.data.ProfileRepo
import com.clashdeck.android.data.SettingsRepo
import com.clashdeck.android.util.DiagLog
import com.clashdeck.android.util.formatSpeed
import org.json.JSONObject

/**
 * VPN 服务。三件事：建 tun、把 fd 交给内核、把内核状态同步给 UI。
 *
 * 几个刻意的决定（都有代价，写在这里避免下次重新踩）：
 * 1. tun 由系统 VpnService 建，fd 通过 detachFd() 转移给内核 —— 内核拿到的就是系统给的那个 tun，
 *    不需要 root，也不需要额外进程。
 * 2. addDisallowedApplication(自己) 让本应用的 socket 绕过 tun。这样内核的出站连接天然不会回环，
 *    不需要在 Go 侧实现 socket protect 回调。
 * 3. 启动放在工作线程：内核解析配置（含规则集）可能耗时数秒，放在主线程就是 ANR。
 * 4. fd 所有权：startCore 成功后归内核，本类不再关闭；失败或未启动时才由这里关闭。
 */
class TunService : VpnService() {

    companion object {
        private const val TAG = "TunService"

        const val ACTION_START = "com.clashdeck.android.action.START"
        const val ACTION_STOP = "com.clashdeck.android.action.STOP"

        /** 订阅定时刷新：只对已在前台运行的服务投递，不新建服务、不重建 tun。 */
        const val ACTION_REFRESH_SUBSCRIPTION = "com.clashdeck.android.action.REFRESH_SUBSCRIPTION"

        // v3：换图标必须换渠道 id。渠道创建后图标会被系统锁定，改 drawable 不生效。
        private const val CHANNEL_ID = "clashdeck_vpn_v4"

        /** 历史渠道 id：改名/换图标时留在这里，启动时删除，避免系统设置里出现重复条目。 */
        private val LEGACY_CHANNELS = listOf("clashdeck_vpn", "clashdeck_vpn_v2", "clashdeck_vpn_v3")
        private const val NOTIFICATION_ID = 1001

        private const val ADDRESS_V4 = "172.19.0.1"
        private const val ADDRESS_V6 = "fdfe:dcba:9876::1"
        private const val DNS_ADDRESS = "172.19.0.2"
        private const val MTU = 9000

        /**
         * DNS 劫持网段用 0.0.0.0（任意地址的 53 端口），与官方实现一致。
         * 只写 DNS_ADDRESS 时，硬编码 8.8.8.8 之类 DNS 的应用会绕过劫持，表现为"个别 App 没网"。
         */
        private const val DNS_HIJACK = "0.0.0.0"

        /** 与下方 addAddress 的网段必须严格一致，否则 tun 收不到包（表现为"连上了但没网"）。 */
        private const val GATEWAY = "172.19.0.1/30,fdfe:dcba:9876::1/126"

        @Volatile
        var instance: TunService? = null
            private set

        fun start(context: android.content.Context) {
            val intent = Intent(context, TunService::class.java).setAction(ACTION_START)
            context.startForegroundService(intent)
        }

        /**
         * 停止。
         *
         * instance 为 null 时不能什么都不做：服务可能已因系统回收被销毁，
         * 而进程内的 [CoreManager] 仍停在 RUNNING（silent teardown 不写状态）。
         * 那样用户点"断开"没有任何反应，界面还显示已连接 —— 只能强杀应用。
         * 这里兜底把状态和残留服务一起清掉。
         */
        fun stop(context: Context) {
            val inst = instance
            if (inst != null) {
                inst.requestStop()
                return
            }
            CoreManager.markStopped()
            runCatching { context.stopService(Intent(context, TunService::class.java)) }
        }

        val connected: Boolean get() = instance?.established == true
    }

    @Volatile
    private var established = false
    private var coreStarted = false
    private var tunFd = -1
    private var startThread: Thread? = null
    private var monitorThread: Thread? = null

    /** 启动流程进行中被要求停止。见 requestStop 与 doStart 尾部的双重检查。 */
    @Volatile
    private var stopRequested = false

    /**
     * 启动看门狗。
     *
     * 为什么需要：doStart() 里 Native.start() 是**同步阻塞**调用，一旦 Go 侧卡住
     * （tun 读不到 fd、DNS 初始化异常等），既不返回也不抛错，界面就会永远停在"连接中"：
     * 按钮还在，点它只是再停一次，用户唯一的出路是强杀应用。
     * 这里在 25 秒后兜底判定失败并清理，至少让状态机回到 ERROR 并给出原因。
     * 25 秒是刻意放宽的：正常冷启动 2-5 秒，低端机 + 首次解压资产也在 10 秒内。
     */
    private var startWatchdog: Thread? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        instance = this

        // 系统在进程被杀后重启服务时 intent 为 null（服务粘性的典型场景）。
        // 这不是用户点的"连接"——照常启动内核会让人觉得"一打开 App 就自动连上了"。
        // 这里做彻底收尾：连内核一起停，避免留下"VPN 还开着但界面说没连"的僵尸态。
        if (intent == null) {
            DiagLog.w("conn", "系统重启了服务（intent 为空），按设计不自动连接")
            Log.i(TAG, "onStartCommand: null intent (system restart) -> teardown, not auto-connect")
            CoreManager.markStopped()
            teardown(silent = true)
            return START_NOT_STICKY
        }
        Log.i(TAG, "onStartCommand: action=${intent.action} established=$established")

        when (intent.action) {
            ACTION_STOP -> {
                requestStop()
                return START_NOT_STICKY
            }
            ACTION_REFRESH_SUBSCRIPTION -> {
                // 已经在跑的服务才能接这个动作：后台 Receiver 无法自己拉起长耗时下载。
                // 刷新走独立工作线程，绝不碰 doStart/teardown，连接不会因为订阅更新被拆掉。
                if (established) {
                    Thread({
                        kotlinx.coroutines.runBlocking {
                            com.clashdeck.android.data.SubscriptionAutoUpdate.refresh(
                                this@TunService,
                                force = false,
                            )
                        }
                    }, "clashdeck-sub-refresh").apply { isDaemon = true; start() }
                }
                return START_NOT_STICKY
            }
        }

        if (established) return START_NOT_STICKY

        startForegroundCompat(getString(R.string.notif_connecting))
        CoreManager.markStarting()

        stopRequested = false
        startThread?.interrupt()
        startThread = Thread({ doStart() }, "clashdeck-core-start").apply { start() }
        armStartupWatchdog()
        // 不粘性：进程被杀后由系统静默重启一个"看似连着其实没有"的服务，比直接停掉更难排查
        return START_NOT_STICKY
    }

    private fun doStart() {
        try {
            if (!Native.ensureLoaded()) {
                error("内核库加载失败（${Native.loadFailure ?: "unknown"}）")
            }

            val ctx = applicationContext
            AppPaths.init(ctx)
            AppPaths.ensureAssets(ctx)

            val settings = SettingsRepo(ctx)
            val repo = ProfileRepo(ctx, settings)
            val profile = repo.active() ?: error("还没有配置，先到「配置」页导入订阅")
            val cfgFile = repo.buildRuntimeConfig(profile)
            // 记下"用户点了连接"这一刻和当时的上下文，后面内核日志可以据此对齐
            DiagLog.i("conn", "开始连接 profile=${profile.name} mode=${settings.mode}")

            val builder = Builder()
                .setSession(getString(R.string.app_name))
                .setMtu(MTU)
                .addAddress(ADDRESS_V4, 30)
                .addAddress(ADDRESS_V6, 126)
                .addRoute("0.0.0.0", 0)
                .addRoute("::", 0)
                .addDnsServer(DNS_ADDRESS)
                .setBlocking(false)
            // 这里必须接管 IPv6（分配地址 + 路由 ::/0），而内核侧也必须开启 ipv6（见 RuntimeConfigBuilder）。
            // 两个方向都不能省：
            //  - 不接管 → v6 流量绕过代理直接出去，等于泄露；
            //  - 接管但内核关着 v6 → 系统送进来的 v6 连接全部失败，
            //    日志刷满 `dns resolve failed: ip version error`（实测 74 条），应用只能退回 v4。

            // 本应用自身绕过 tun：内核出站连接因此不会回环
            runCatching { builder.addDisallowedApplication(packageName) }
                .onFailure { Log.w(TAG, "addDisallowedApplication failed: ${it.message}") }

            val pfd = builder.establish() ?: error("VPN 未授权或建立失败")
            tunFd = pfd.detachFd()

            val rc = Native.startCore(
                AppPaths.home.absolutePath,
                cfgFile.absolutePath,
                tunFd,
                "gvisor",
                GATEWAY,
                DNS_HIJACK,
            )
            if (rc != 0) {
                val msg = runCatching { Native.lastError() }.getOrNull().orEmpty()
                error(msg.ifBlank { "内核启动失败（code=$rc）" })
            }

            DiagLog.i("conn", "内核已就绪 apiPort=${runCatching { Native.apiPort() }.getOrDefault(0)}")
            coreStarted = true
            established = true
            startWatchdog?.interrupt()
            CoreManager.markRunning()
            // 本次启动已用最新订阅原文重建 runtime.yaml，后台挂着的「待重载」到此消费掉。
            settings.subPendingReload = false

            // 启动期间用户点了断开：内核已经起来了，必须立刻回收，
            // 否则会出现"界面未连接、实际流量还在走代理"（隐私级）。
            // 这段检查放在 markRunning 之后：状态机先落到"已连接"，
            // teardown 再把它推回"已停止"，顺序反了会留下 RUNNING 的残影。
            if (stopRequested) {
                DiagLog.w("conn", "启动完成前已请求停止，立即回收内核")
                teardown(silent = false)
                return
            }
            updateNotification("已连接", null, 0, 0)
            // 同上：内核可能换了端口，通知栏这侧也要用实际值
            val apiPort = runCatching { Native.apiPort() }.getOrDefault(0)
                .takeIf { it > 0 } ?: settings.apiPort
            startMonitor(apiPort)
        } catch (t: Throwable) {
            // "已经在跑"不是失败：说明另一个启动流程已经成功。
            // 原来这里一律 teardown，等于**把刚连好的内核亲手拆掉**，
            // 表现为"连上又断 + 界面弹出 core already running"（并发重启时必现）。
            val msg = t.message.orEmpty()
            if (msg.contains("already running", ignoreCase = true)) {
                Log.i(TAG, "core already running, treat as success")
                DiagLog.w("conn", "内核已在运行，跳过本次重复启动")
                established = true
                coreStarted = true
                startWatchdog?.interrupt()
                CoreManager.markRunning()
                return
            }
            Log.e(TAG, "start failed", t)
            DiagLog.e("conn", "连接失败: ${t.javaClass.simpleName}: ${t.message}")
            CoreManager.markError(t.message ?: "启动失败")
            teardown(silent = true)
            stopSelf()
        }
    }

    fun requestStop() {
        // 先立标志再动手。理由见 doStart 里 stopRequested 的注释：
        // 启动是阻塞调用，`stopCore` 可能抢在 `startCore` 之前拿到锁（此时内核还没 running，
        // 于是直接返回 nil 什么都不做），随后 startCore 成功 —— **用户点了断开，内核反而起来了**。
        // 界面显示"未连接"，实际 tun 还在接管流量，这是隐私级问题，不是体验问题。
        stopRequested = true
        startThread?.interrupt()
        Thread({
            CoreManager.markStopping()
            teardown(silent = false)
        }, "clashdeck-core-stop").start()
    }

    private fun teardown(silent: Boolean) {
        monitorThread?.interrupt()
        monitorThread = null

        if (coreStarted) {
            runCatching { Native.stopCore() }
                .onFailure { Log.w(TAG, "stopCore failed: ${it.message}") }
            coreStarted = false
            // fd 已被内核接管并随内核关闭，这里不再碰它
        } else if (tunFd >= 0) {
            runCatching { ParcelFileDescriptor.adoptFd(tunFd).close() }
            tunFd = -1
        }

        established = false
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
        startWatchdog?.interrupt()
        if (!silent) CoreManager.markStopped()
    }

    override fun onRevoke() {
        // 系统撤销授权（另一个 VPN 启动、用户在系统设置里断开）
        CoreManager.markStopped()
        teardown(silent = true)
        super.onRevoke()
    }

    override fun onDestroy() {
        if (established || coreStarted) teardown(silent = true)
        if (instance === this) instance = null
        super.onDestroy()
    }

    // ---------- 通知 ----------

    private fun startForegroundCompat(text: String) {
        ensureChannel()
        val type = if (Build.VERSION.SDK_INT >= 34) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, buildNotification(text, null, 0, 0), type)
    }

    private fun updateNotification(title: String, node: String?, up: Long, down: Long) {
        val nm = getSystemService(NotificationManager::class.java)
        runCatching { nm.notify(NOTIFICATION_ID, buildNotification(title, node, up, down)) }
    }

    /**
     * 通知的大图标（品牌标记）。
     *
     * 不设它的时候，ColorOS / HyperOS 这类 ROM 会拿**应用图标**去画左侧那个方块；
     * 应用图标一旦被系统缓存住（覆盖安装后常见），用户就一直看到上一版的 logo。
     * 显式给 largeIcon 之后系统优先用它，等于把这一格的控制权拿回来。
     */
    private val notifLargeIcon: Bitmap by lazy {
        val size = (48 * resources.displayMetrics.density).toInt().coerceIn(96, 288)
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bmp)
        ContextCompat.getDrawable(this, R.drawable.ic_notif_large)?.let {
            it.setBounds(0, 0, size, size)
            it.draw(canvas)
        }
        bmp
    }

    private fun buildNotification(
        title: String,
        node: String?,
        up: Long,
        down: Long,
    ): Notification {
        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, TunService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        val speedLine = "↑ ${formatSpeed(up)}   ↓ ${formatSpeed(down)}"
        val summary = if (node.isNullOrBlank()) speedLine else "$node · $speedLine"
        val expanded = buildString {
            if (!node.isNullOrBlank()) append("节点：").append(node).append('\n')
            append(speedLine)
        }

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(summary)
            .setStyle(NotificationCompat.BigTextStyle().bigText(expanded))
            .setSmallIcon(R.drawable.ic_stat_mark)
            .setLargeIcon(notifLargeIcon)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(contentIntent)
            .addAction(0, getString(R.string.action_stop), stopIntent)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    // ---------- 通知栏状态刷新 ----------

    /**
     * 每 3 秒读一次内核控制接口，更新"当前节点 + 上下行速度"。
     * 刻意用 HTTP 轮询而不是订阅：这里只需要一个粗粒度的展示值，
     * 而轮询失败也不会影响 VPN 本身（出错直接跳过这一轮）。
     */
    private fun armStartupWatchdog() {
        startWatchdog?.interrupt()
        startWatchdog = Thread({
            try {
                Thread.sleep(25_000)
            } catch (_: InterruptedException) {
                return@Thread
            }
            if (established) return@Thread
            if (CoreManager.state.value.status != CoreStatus.STARTING) return@Thread
            Log.e(TAG, "startup watchdog fired: still STARTING after 25s")
            DiagLog.e("conn", "启动看门狗：25 秒仍未就绪，判定失败并清理")
            CoreManager.markError("内核启动超时（25 秒未就绪），已中止。可重试；若反复出现请看内核日志")
            teardown(silent = true)
            stopSelf()
        }, "clashdeck-start-watchdog").apply { isDaemon = true; start() }
    }

    private fun startMonitor(port: Int) {
        monitorThread?.interrupt()
        monitorThread = Thread({
            var lastUp = -1L
            var lastDown = -1L
            var lastAt = 0L
            while (established && !Thread.currentThread().isInterrupted) {
                val body = httpGet(port, "/connections")
                if (body != null) {
                    runCatching {
                        val o = JSONObject(body)
                        val up = o.optLong("uploadTotal")
                        val down = o.optLong("downloadTotal")
                        val now = System.currentTimeMillis()
                        var upSpeed = 0L
                        var downSpeed = 0L
                        if (lastUp >= 0 && now > lastAt) {
                            val dt = ((now - lastAt) / 1000.0).coerceAtLeast(0.5)
                            upSpeed = (((up - lastUp) / dt).toLong()).coerceAtLeast(0L)
                            downSpeed = (((down - lastDown) / dt).toLong()).coerceAtLeast(0L)
                        }
                        lastUp = up
                        lastDown = down
                        lastAt = now
                        updateNotification("已连接", currentProxy(port), upSpeed, downSpeed)
                    }
                }
                try {
                    Thread.sleep(3000)
                } catch (_: InterruptedException) {
                    break
                }
            }
        }, "clashdeck-notif").apply { isDaemon = true; start() }
    }

    private fun httpGet(port: Int, path: String): String? = runCatching {
        val conn = java.net.URL("http://127.0.0.1:$port$path").openConnection() as java.net.HttpURLConnection
        conn.connectTimeout = 1500
        conn.readTimeout = 1500
        val body = conn.inputStream.bufferedReader().use { it.readText() }
        conn.disconnect()
        body
    }.getOrNull()

    /** 主策略组当前选中的节点；没有策略组时返回 null。 */
    private fun currentProxy(port: Int): String? {
        val body = httpGet(port, "/proxies") ?: return null
        val obj = runCatching { JSONObject(body).optJSONObject("proxies") }.getOrNull() ?: return null
        val groups = obj.keys().asSequence()
            .filter { it != "GLOBAL" }
            .mapNotNull { key ->
                val o = obj.optJSONObject(key) ?: return@mapNotNull null
                val now = o.optString("now")
                if (o.optJSONArray("all") != null && now.isNotBlank()) key to now else null
            }
            .toList()
        return groups.firstOrNull { it.first == "节点选择" }?.second
            ?: groups.firstOrNull { it.first == "PROXY" }?.second
            ?: groups.firstOrNull()?.second
    }

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT < 26) return
        val nm = getSystemService(NotificationManager::class.java)
        // 清掉改图标之前留下的旧渠道（它们带着旧 logo，会作为孤儿留在系统通知设置里）
        LEGACY_CHANNELS.forEach { legacy ->
            if (nm.getNotificationChannel(legacy) != null) {
                runCatching { nm.deleteNotificationChannel(legacy) }
            }
        }

        if (nm.getNotificationChannel(CHANNEL_ID) != null) return
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_LOW,
            ).apply { setShowBadge(false) }
        )
    }
}
