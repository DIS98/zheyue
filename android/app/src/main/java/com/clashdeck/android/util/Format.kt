package com.clashdeck.android.util

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

fun formatBytes(bytes: Long): String {
    if (bytes < 0) return "-"
    if (bytes < 1024) return "$bytes B"
    val kb = bytes / 1024.0
    if (kb < 1024) return String.format(Locale.US, "%.1f KB", kb)
    val mb = kb / 1024.0
    if (mb < 1024) return String.format(Locale.US, "%.1f MB", mb)
    val gb = mb / 1024.0
    if (gb < 1024) return String.format(Locale.US, "%.2f GB", gb)
    return String.format(Locale.US, "%.2f TB", gb / 1024.0)
}

fun formatSpeed(bytesPerSec: Long): String = when {
    bytesPerSec < 1024 -> "$bytesPerSec B/s"
    bytesPerSec < 1024 * 1024 -> String.format(Locale.US, "%.1f KB/s", bytesPerSec / 1024.0)
    else -> String.format(Locale.US, "%.2f MB/s", bytesPerSec / 1024.0 / 1024.0)
}

/**
 * 时间格式化。
 *
 * 不用共享的 SimpleDateFormat：它线程不安全，多线程同时 format 会算出错乱结果甚至抛异常。
 * DateTimeFormatter 是不可变对象，跨线程共用安全。
 */
private val dateFmt: DateTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm", Locale.getDefault())

fun formatTime(millis: Long): String {
    if (millis <= 0) return "—"
    return Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()).format(dateFmt)
}

/**
 * 到期文案的唯一实现，两个页面共用。
 *
 * 之前两处各写一套 `days <= 0 → 已过期`，而 `daysLeft` 是整除：
 * **还剩 3 小时也被算成 0 天 → 显示"已过期"**。用户明明还有余额，界面却说没了。
 * 这里把三种状态分开：长期有效 / 已过期 / 剩 N 天 / 剩 N 小时（不足一天按小时，向上取整）。
 */
fun expiryLabel(expireAtMillis: Long, now: Long = System.currentTimeMillis()): String {
    if (expireAtMillis <= 0) return "长期有效"
    val diff = expireAtMillis - now
    if (diff <= 0) return "已过期"
    val days = diff / 86_400_000L
    if (days >= 1) return "剩 $days 天"
    val hours = (diff + 3_599_999L) / 3_600_000L
    return "剩 $hours 小时"
}

