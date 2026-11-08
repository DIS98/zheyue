package com.clashdeck.android.api

import com.clashdeck.android.util.NodeFilter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.MediaType.Companion.toMediaType
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class ProxyItem(
    val name: String,
    val type: String,
    val now: String?,
    val all: List<String>,
    val alive: Boolean,
    val delay: Int?,
) {
    val isGroup: Boolean get() = all.isNotEmpty()
}

data class ProxySnapshot(
    val groups: List<ProxyItem>,
    val nodes: Map<String, ProxyItem>,
)

data class RuntimeConfig(
    val mode: String,
    val mixedPort: Int,
    val allowLan: Boolean,
)

data class ConnectionItem(
    val id: String,
    val host: String,
    val destination: String,
    val network: String,
    val rule: String,
    val rulePayload: String,
    val chains: List<String>,
    val upload: Long,
    val download: Long,
    val start: String,
    /** 来源地址。预判要靠它把"自己发的那一次探测"从别的同域名连接里挑出来。 */
    val sourceIP: String = "",
    val destinationPort: Int = 0,
    val inbound: String = "",
)

data class ConnectionsSnapshot(
    val uploadTotal: Long,
    val downloadTotal: Long,
    val items: List<ConnectionItem>,
)

data class RuleItem(
    val type: String,
    val payload: String,
    val proxy: String,
    val size: Int,
)

/** 规则集（rule-provider）状态，对应 Windows 端「规则集管理」。 */
data class RuleProviderInfo(
    val name: String,
    val behavior: String,
    val ruleCount: Int,
    val updatedAt: String,
    val vehicleType: String,
)

/** 规则预判结果：真正命中哪条规则、最终走哪条链路。 */
data class RulePrediction(
    val rule: String,
    val payload: String,
    val chains: List<String>,
) {
    val target: String get() = chains.firstOrNull() ?: "—"
}

data class TrafficSample(val up: Long, val down: Long)

data class LogEntry(val level: String, val payload: String, val at: Long = System.currentTimeMillis())

/**
 * 内核 external-controller 的客户端。
 *
 * UI 的全部实时数据都来自这里，不自己算：
 * 节点延迟、连接链、命中规则都是内核给出的观测值，界面只负责展示。
 */
class ClashApi(private val port: Int, private val secret: String = "") {

    // 控制接口在本机回环上，正常情况下 1ms 内就握手成功。
    // 但内核刚启动时监听尚未就绪，Android 的 VPN/省电策略也可能拖慢本地 socket，
    // 3 秒会把"马上就绪"误判成失败；放宽到 5 秒并允许 OkHttp 自己重试一次。
    private val client = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val base = "http://127.0.0.1:$port"

    private val jsonType = "application/json".toMediaType()

    private fun req(path: String, method: String = "GET", body: String? = null): Request {
        val b = Request.Builder().url("$base$path")
        if (secret.isNotBlank()) b.header("Authorization", "Bearer $secret")
        when (method) {
            "GET" -> b.get()
            "PUT" -> b.put((body ?: "{}").toRequestBody(jsonType))
            "PATCH" -> b.patch((body ?: "{}").toRequestBody(jsonType))
            "DELETE" -> b.delete()
        }
        return b.build()
    }

    private fun execute(path: String, method: String = "GET", body: String? = null): String {
        client.newCall(req(path, method, body)).execute().use { resp: Response ->
            if (!resp.isSuccessful) throw IllegalStateException("HTTP ${resp.code} @ $path")
            return resp.body?.string() ?: ""
        }
    }

    fun isReachable(): Boolean = runCatching {
        execute("/version").isNotBlank()
    }.getOrDefault(false)

    fun version(): String = runCatching {
        JSONObject(execute("/version")).optString("version", "unknown")
    }.getOrDefault("unknown")

    fun proxies(): ProxySnapshot = runCatching {
        val root = JSONObject(execute("/proxies"))
        val obj = root.optJSONObject("proxies") ?: JSONObject()
        val all = mutableListOf<ProxyItem>()
        obj.keys().forEach { key ->
            val o = obj.getJSONObject(key)
            val arr = o.optJSONArray("all")
            val list = if (arr == null) emptyList() else (0 until arr.length()).map { arr.getString(it) }
            val history = o.optJSONArray("history")
            var delay: Int? = null
            if (history != null && history.length() > 0) {
                val last = history.optJSONObject(history.length() - 1)
                val d = last?.optInt("delay", 0) ?: 0
                delay = if (d > 0) d else null
            }
            all += ProxyItem(
                name = key,
                type = o.optString("type"),
                now = o.optString("now").ifBlank { null },
                all = list,
                alive = o.optBoolean("alive", true),
                delay = delay,
            )
        }
        snapshotFrom(all)
    }.getOrDefault(ProxySnapshot(emptyList(), emptyMap()))

    suspend fun select(group: String, name: String) = withContext(Dispatchers.IO) {
        execute("/proxies/${group.escape()}", "PUT", JSONObject().put("name", name).toString())
        Unit
    }

    suspend fun delayOf(name: String, url: String, timeout: Int): Int? = withContext(Dispatchers.IO) {
        runCatching {
            val q = "$base/proxies/${name.escape()}/delay?timeout=$timeout&url=${url.enc()}"
            val b = Request.Builder().url(q)
            if (secret.isNotBlank()) b.header("Authorization", "Bearer $secret")
            client.newCall(b.build()).execute().use { resp ->
                if (!resp.isSuccessful) return@use null
                JSONObject(resp.body?.string() ?: "{}").optInt("delay", 0).takeIf { it > 0 }
            }
        }.getOrNull()
    }

    /**
     * 运行期配置。
     *
     * 刻意**不在失败时返回默认值**：以前失败会返回 `mode = "rule"`，
     * 而轮询会把它写回界面——内核一停，用户刚选的模式就被每 2 秒改回「规则」。
     * 失败就抛，让调用方知道"这次没拿到"。
     */
    fun configs(): RuntimeConfig {
        val o = JSONObject(execute("/configs"))
        return RuntimeConfig(
            mode = o.optString("mode", ""),
            mixedPort = o.optInt("mixed-port", 0),
            allowLan = o.optBoolean("allow-lan", false),
        )
    }

    /**
     * 注意：内核以 cmfa 标签构建时 route 处于 embed 模式，`PATCH /configs` **不会被注册**
     * （mihomo `hub/route/configs.go:28`）。切换模式请用 `Native.setMode`，本方法仅作留档。
     */
    suspend fun setMode(mode: String) = withContext(Dispatchers.IO) {
        execute("/configs", "PATCH", JSONObject().put("mode", mode).toString())
        Unit
    }

    fun connections(): ConnectionsSnapshot = runCatching {
        val o = JSONObject(execute("/connections"))
        val arr = o.optJSONArray("connections") ?: org.json.JSONArray()
        val items = (0 until arr.length()).map { i ->
            val c = arr.getJSONObject(i)
            val meta = c.optJSONObject("metadata") ?: JSONObject()
            val chainsArr = c.optJSONArray("chains")
            val chains = if (chainsArr == null) emptyList()
            else (0 until chainsArr.length()).map { chainsArr.getString(it) }
            ConnectionItem(
                id = c.optString("id"),
                host = meta.optString("host").ifBlank { meta.optString("destinationIP") },
                destination = meta.optString("destinationIP"),
                network = meta.optString("network").uppercase(),
                rule = c.optString("rule"),
                rulePayload = c.optString("rulePayload"),
                chains = chains,
                upload = c.optLong("upload"),
                download = c.optLong("download"),
                start = c.optString("start"),
                sourceIP = meta.optString("sourceIP"),
                destinationPort = meta.optString("destinationPort").toIntOrNull() ?: 0,
                inbound = meta.optString("inboundName"),
            )
        }.sortedByDescending { it.upload + it.download }
        ConnectionsSnapshot(
            uploadTotal = o.optLong("uploadTotal"),
            downloadTotal = o.optLong("downloadTotal"),
            items = items,
        )
    }.getOrDefault(ConnectionsSnapshot(0, 0, emptyList()))

    suspend fun closeConnection(id: String) = withContext(Dispatchers.IO) {
        runCatching { execute("/connections/${id.escape()}", "DELETE") }
        Unit
    }

    suspend fun closeAllConnections() = withContext(Dispatchers.IO) {
        runCatching { execute("/connections", "DELETE") }
        Unit
    }

    fun rules(): List<RuleItem> = runCatching {
        val o = JSONObject(execute("/rules"))
        val arr = o.optJSONArray("rules") ?: org.json.JSONArray()
        (0 until arr.length()).map { i ->
            val r = arr.getJSONObject(i)
            RuleItem(
                type = r.optString("type"),
                payload = r.optString("payload"),
                proxy = r.optString("proxy"),
                size = r.optInt("size", -1),
            )
        }
    }.getOrDefault(emptyList())

    // ---------- 规则集管理 ----------

    /**
     * 规则集列表。
     *
     * 刻意不在内部吞异常：调用方需要区分「这份配置没有 provider」和「接口读失败」——
     * 前者是空列表，后者必须让用户看到原因。吞掉之后两种情况长得一样，排查全靠猜。
     */
    suspend fun ruleProviders(): List<RuleProviderInfo> = withContext(Dispatchers.IO) {
        val root = JSONObject(execute("/providers/rules"))
        val obj = root.optJSONObject("providers") ?: JSONObject()
        obj.keys().asSequence().map { key ->
            val o = obj.getJSONObject(key)
            RuleProviderInfo(
                name = key,
                behavior = o.optString("behavior"),
                ruleCount = o.optInt("ruleCount"),
                updatedAt = o.optString("updatedAt"),
                vehicleType = o.optString("vehicleType"),
            )
        }.sortedBy { it.name }.toList()
    }

    suspend fun updateRuleProvider(name: String): Boolean = withContext(Dispatchers.IO) {
        runCatching { execute("/providers/rules/${name.escape()}", "PUT") }.isSuccess
    }

    // ---------- 规则预判 ----------

    /**
     * 输入域名 → 内核真连一次 → 读回实际命中的规则与链路。
     *
     * 刻意不在本地做规则匹配：GEOSITE / RULE-SET / 逻辑规则在本地重算必然与内核不一致，
     * 给出的会是"推断"而不是"观测"。这里借用本机混合端口发一个请求，再从连接列表里读内核的判定。
     */
    suspend fun predict(domain: String, mixedPort: Int): RulePrediction? = withContext(Dispatchers.IO) {
        if (mixedPort <= 0) return@withContext null
        val proxyClient = OkHttpClient.Builder()
            .proxy(java.net.Proxy(java.net.Proxy.Type.HTTP, java.net.InetSocketAddress("127.0.0.1", mixedPort)))
            .connectTimeout(4, TimeUnit.SECONDS)
            .readTimeout(4, TimeUnit.SECONDS)
            .build()

        var result: RulePrediction? = null
        var probeCode: Int? = null
        var probeError: String? = null
        kotlinx.coroutines.coroutineScope {
            val probe = async {
                runCatching {
                    proxyClient.newCall(Request.Builder().url("http://$domain/").build())
                        .execute().use { it.code }
                }
            }
            try {
                kotlinx.coroutines.withTimeoutOrNull(6000) {
                    repeat(40) {
                        // 同一域名可能同时存在别的连接（系统服务、TUN 里按 IP 建连的旧连接），
                        // 它们的 host 相同但命中规则不同 —— 直接取第一条会读到**别人**的规则，
                        // 表现是"严格模式下明明走代理的域名，预判却说直连"（实测踩到）。
                        // 探测请求来自本机环回口，所以优先认 sourceIP 为 127.x 的那条。
                        val hits = connections().items.filter {
                            it.host.equals(domain, ignoreCase = true) ||
                                it.host.endsWith(".$domain", ignoreCase = true)
                        }
                        val hit = hits.firstOrNull { it.sourceIP.startsWith("127.") } ?: hits.firstOrNull()
                        if (hit != null) {
                            result = RulePrediction(hit.rule, hit.rulePayload, hit.chains)
                            return@withTimeoutOrNull
                        }
                        delay(150)
                    }
                }
            } finally {
                // 没观测到时，要用探测自身的结果解释"为什么"：
                // 连接只存活几毫秒（拨号被拒）时是抓不到记录的，此时说"域名不存在"是误导。
                if (result == null) {
                    runCatching {
                        val r = probe.await()
                        probeCode = r.getOrNull()
                        probeError = r.exceptionOrNull()?.let { it.message ?: it::class.simpleName }
                    }
                    probe.cancel()
                }
            }
        }
        if (result == null) {
            val why = when {
                probeError != null -> "探测请求失败：$probeError"
                probeCode != null -> "探测请求返回 HTTP $probeCode（多为节点不可用或域名解析失败）"
                else -> "探测请求没有返回"
            }
            throw IllegalStateException("没有读到命中规则（$why）")
        }
        result
    }

    /** 内核每 1s 推一次上下行速率。 */    fun trafficFlow(): Flow<TrafficSample> = callbackFlow {
        val request = Request.Builder().url("ws://127.0.0.1:$port/traffic").apply {
            if (secret.isNotBlank()) header("Authorization", "Bearer $secret")
        }.build()

        val listener = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching {
                    val o = JSONObject(text)
                    trySend(TrafficSample(o.optLong("up"), o.optLong("down")))
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: okhttp3.Response?) {
                close(t)
            }
        }
        val ws = client.newWebSocket(request, listener)
        awaitClose { runCatching { ws.close(1000, null) } }
    }.flowOn(Dispatchers.IO)

    fun logFlow(level: String = "info"): Flow<LogEntry> = callbackFlow {
        val request = Request.Builder().url("ws://127.0.0.1:$port/logs?level=$level").apply {
            if (secret.isNotBlank()) header("Authorization", "Bearer $secret")
        }.build()

        val listener = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching {
                    val o = JSONObject(text)
                    trySend(LogEntry(o.optString("type", "info"), o.optString("payload")))
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: okhttp3.Response?) {
                close(t)
            }
        }
        val ws = client.newWebSocket(request, listener)
        awaitClose { runCatching { ws.close(1000, null) } }
    }.flowOn(Dispatchers.IO)

    private fun String.escape(): String = java.net.URLEncoder.encode(this, "UTF-8")

    private fun String.enc(): String = java.net.URLEncoder.encode(this, "UTF-8")
}

/**
 * 从 /proxies 的原始条目里挑出该给界面看的。
 *
 * 抽成纯函数是为了能直接断言：这是"过滤有没有生效"的唯一判据，
 * 靠肉眼看列表证明不了（少一条不少一条看不出来）。
 */
internal fun snapshotFrom(all: List<ProxyItem>): ProxySnapshot {
    // 信息节点（"剩余流量：…"这类）不进界面：它们不是真节点，拨了必然失败，
    // 留在列表里只会让人以为"节点是坏的"。配置层已删（见 RuntimeConfigBuilder.stripInfoNodes），
    // 这里再滤一次是为了兼容"改版前就已连上、没重建配置"的会话。
    val visible = all.filter { !NodeFilter.isInfo(it.name) }
    val groups = visible.filter { it.isGroup }.map { g ->
        g.copy(
            all = g.all.filter { !NodeFilter.isInfo(it) },
            now = g.now?.takeIf { !NodeFilter.isInfo(it) },
        )
    }
    return ProxySnapshot(groups = groups, nodes = visible.associateBy { it.name })
}
