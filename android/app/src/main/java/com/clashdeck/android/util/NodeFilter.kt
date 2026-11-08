package com.clashdeck.android.util

/**
 * 订阅里的「信息节点」识别。
 *
 * 机场常在 proxies 里塞几条**假节点**，名字本身就是订阅状态，例如实测遇到的：
 *
 *     剩余流量：113.87 GB
 *     套餐到期：2027-02-24
 *     距离下次重置剩余: 18 天
 *     官网: kitty.fo
 *
 * 它们不是真节点：拨上去必然失败，却照样出现在节点列表里。
 * 更麻烦的是它们会进 `url-test` / `fallback` 组的候选池 ——
 * 每次自动测速都在拨一个必然超时的地址，还可能被误选成「最低延迟」，
 * 表现为「自动选择挑中的节点连不上」。
 *
 * 所以过滤要在**两层**都做：
 * - 配置层（RuntimeConfigBuilder）：把它们从 proxies 与组候选里删掉，内核根本看不到；
 * - 展示层（ClashApi 的 /proxies 解析）：万一是老配置没重建，界面也不显示。
 */
object NodeFilter {

    /**
     * 信息节点的关键词。刻意只放「订阅状态」语义的词，
     * 不放「流量」这类可能是真节点卖点的词（例：香港 大流量 01）。
     */
    private val KEYWORDS = listOf(
        "剩余", "已用", "到期", "过期", "有效期", "重置", "距离", "套餐",
        "官网", "网址", "客服", "群组", "公告", "订阅", "余量",
    )

    /**
     * 判定。命中关键词**且**长得像一句话（带冒号或数字）才算信息节点。
     *
     * 为什么加第二个条件：`香港 剩余专线` 这类把关键词当卖点的真节点不该被误杀。
     * 代价是 `剩余IPLC 03` 这种既带关键词又带编号的会被误判 —— 已知局限，
     * 真遇到的机场名可以往 [KEYWORDS] 里加更精确的词来收窄。
     */
    fun isInfo(name: String): Boolean {
        val n = name.trim()
        if (n.isEmpty()) return false
        if (KEYWORDS.none { n.contains(it) }) return false
        return n.contains(":") || n.contains("：") || n.any { it.isDigit() }
    }
}
