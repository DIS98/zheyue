package com.clashdeck.android.core

/**
 * 内核 JNI 绑定。
 *
 * 符号对应 core-go/jni.c 里的实现，实现再调用 Go 导出的纯 C 函数。
 * 库名 libclash.so，由 core-go 用 `go build -buildmode=c-shared` 产出，放在 jniLibs/arm64-v8a。
 */
object Native {

    @Volatile
    private var loaded = false
    private var loadError: String? = null

    fun ensureLoaded(): Boolean {
        if (loaded) return true
        synchronized(this) {
            if (loaded) return true
            return try {
                System.loadLibrary("clash")
                loaded = true
                true
            } catch (t: Throwable) {
                loadError = "${t.javaClass.simpleName}: ${t.message}"
                false
            }
        }
    }

    val loadFailure: String? get() = loadError

    /**
     * 启动内核。
     * @param homeDir 内核工作目录（geosite/geoip/ruleset 都在这里）
     * @param configPath 运行期配置 yaml
     * @param fd VpnService 建立后 detachFd() 得到的 tun 文件描述符
     * @return 0 成功，非 0 失败（错误详情用 [lastError] 取）
     */
    @JvmStatic
    external fun startCore(
        homeDir: String,
        configPath: String,
        fd: Int,
        stack: String,
        gateway: String,
        dns: String,
    ): Int

    @JvmStatic
    external fun stopCore(): Int

    /**
     * 切换 规则/全局/直连。
     * 直接调内核的 tunnel.SetMode —— 内核以 cmfa 标签构建时，HTTP `PATCH /configs` 不可用。
     */
    @JvmStatic
    external fun setMode(mode: String): Int

    /**
     * 控制端实际监听的端口。
     *
     * 配置里的端口（默认 9090）被别的程序占用时，内核会自动换一个空闲端口。
     * 所以请求控制接口前必须读这里，不能假设端口还是配置里写的那样。
     * 内核未运行时返回 0。
     */
    @JvmStatic
    external fun apiPort(): Int

    @JvmStatic
    external fun lastError(): String

    @JvmStatic
    external fun coreVersion(): String
}
