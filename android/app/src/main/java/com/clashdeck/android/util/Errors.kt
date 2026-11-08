package com.clashdeck.android.util

import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

/**
 * 把网络异常翻成人话。
 *
 * 为什么需要：这类失败原本把异常原文直接糊到界面上，例如
 * `failed to connect to /10.0.2.2 (port 9) from /10.0.2.15 (port 43728) after 10000ms`。
 * 用户看懂的概率接近零，而它出现的场景（订阅链接失效／手机没网）恰恰最需要一句明确指引。
 *
 * 原则：**能确定原因就说原因 + 该怎么办；不确定就保留原文**，宁可丑也不编。
 */
object Errors {

    fun human(t: Throwable?): String {
        if (t == null) return "未知错误"
        val raw = t.message.orEmpty()
        return when {
            t is UnknownHostException ||
                raw.contains("Unable to resolve host", ignoreCase = true) ->
                "域名解析失败，检查链接是否写错、手机是否联网"

            t is SocketTimeoutException || raw.contains("timeout", ignoreCase = true) ->
                "连接超时，链接可能已失效或当前网络不可达"

            t is ConnectException || raw.contains("failed to connect", ignoreCase = true) ->
                "连不上服务器，链接可能已失效"

            t is SSLException || raw.contains("Cert", ignoreCase = true) ->
                "TLS 证书校验失败，链接可能被劫持或是自签证书"

            raw.contains("HTTP 403") -> "服务器拒绝（HTTP 403），分享码或授权可能已失效"
            raw.contains("HTTP 404") -> "链接不存在（HTTP 404）"

            // 我们自己抛的业务错误本来就是中文，原样透出
            raw.isNotBlank() && raw.any { it.code in 0x4E00..0x9FFF } -> raw
            raw.isNotBlank() -> raw
            else -> t.javaClass.simpleName
        }
    }

    /** 给 IO 类失败留的统一出口，避免各处各写一套 when。 */
    fun humanIo(t: Throwable?): String = human(t).ifBlank { "网络请求失败" }

    @Suppress("unused")
    private fun isIo(t: Throwable) = t is IOException
}
