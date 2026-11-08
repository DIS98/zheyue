package com.clashdeck.android.util

import android.util.Log
import com.clashdeck.android.data.AppPaths
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 诊断日志。
 *
 * 为什么应用事件要写进**内核那个 core.log**，而不是单独建文件：
 * 排障时最有价值的是「用户做了什么 → 内核紧接着发生了什么」这条因果链。
 * 分成两个文件就只能靠时间戳脑补对齐，尤其是连接/断开这种秒级操作。
 * 所以两边都写同一个文件，格式也统一成 logrus 那种 `time= level= msg=`，日志页按顺序读出来就是完整时间线。
 */
object DiagLog {

    /** 与内核侧保持一致的上限，超了就清空重来，避免长期运行吃掉存储。 */
    private const val MAX_BYTES = 4L * 1024 * 1024

    private val lock = Any()
    private val stamp = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US)

    fun i(tag: String, msg: String) = write("info", tag, msg)

    fun w(tag: String, msg: String) = write("warning", tag, msg)

    fun e(tag: String, msg: String) = write("error", tag, msg)

    private fun write(level: String, tag: String, msg: String) {
        val safe = msg.replace("\"", "'").replace("\n", " ")
        val line = "time=\"${stamp.format(Date())}\" level=$level msg=\"[app] $tag: $safe\"\n"
        runCatching {
            synchronized(lock) {
                val f = File(AppPaths.home, "core.log")
                if (f.exists() && f.length() > MAX_BYTES) f.delete()
                f.appendText(line)
            }
        }.onFailure { Log.w("DiagLog", "write failed: ${it.message}") }
    }

    /** 读尾部若干行。日志页与「复制」共用，保证两边看到的内容一致。 */
    suspend fun tail(maxLines: Int = 500): Pair<List<String>, Boolean> = withContext(Dispatchers.IO) {
        val f = File(AppPaths.home, "core.log")
        if (!f.exists()) return@withContext emptyList<String>() to false
        val lines = runCatching {
            // 从尾部读，避免把整个文件读进内存
            f.useLines { seq -> seq.toList().takeLast(maxLines) }
        }.getOrElse { emptyList() }
        lines to true
    }

    /**
     * 分享前脱敏。
     *
     * 日志里天然带有节点服务器地址、DNS 服务器、内网 IP——这些东西发出去等于把你的机场交代了。
     * 这里只做「保留结构、抹掉身份」：IPv4 留前两段、IPv6 留第一组、密码类字段整体打码。
     * 域名不打码（它是判断命中了哪条规则的关键线索），所以页面会同时提供「原文」按钮给确实需要的人。
     */
    fun redact(text: String): String {
        var s = text
        s = Regex("""\b(\d{1,3}\.\d{1,3})\.\d{1,3}\.\d{1,3}\b""")
            .replace(s) { "${it.groupValues[1]}.*.*" }
        s = Regex("""\b([0-9a-fA-F]{1,4})(:[0-9a-fA-F]{0,4}){3,7}\b""")
            .replace(s) { "${it.groupValues[1]}:****" }
        // 键值型凭据：password / token 之外，还要覆盖分享码与常见鉴权参数。
        // 踩过的坑：本站下载链接是 ，原规则里没有这个词，
        // 于是"点复制发给别人排查"会把等于公开下载权限的分享码一起发出去。
        s = Regex(
            """(?i)(password|passwd|pwd|token|secret|uuid|ticket|access[_-]?key|api[_-]?key|auth|sig|signature|key)=([^\s&"',}]+)""",
        ).replace(s) { "${it.groupValues[1]}=***" }
        s = Regex(
            """(?i)(password|passwd|pwd|token|secret|uuid)(["']?\s*[:=]\s*["']?)([^\s"',}]+)""",
        ).replace(s) { "${it.groupValues[1]}${it.groupValues[2]}***" }
        // URL 里的 user:pass@ —— 既不是 key=value 也不含 password 字样，只能按形状匹配
        s = Regex("""(?<=://)([^:/@\s]+):([^@/\s]+)@""").replace(s) { "${it.groupValues[1]}:***@" }
        return s
    }
}
