package com.clashdeck.android.core

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class CoreStatus { STOPPED, STARTING, RUNNING, STOPPING, ERROR }

data class CoreState(
    val status: CoreStatus = CoreStatus.STOPPED,
    val message: String? = null,
) {
    val running: Boolean get() = status == CoreStatus.RUNNING
}

/**
 * 内核状态在进程内的唯一出口。UI 只读这里，TunService 负责写。
 * 放在内存里而不是磁盘：进程被杀 → 内核也随之消失，状态自动归零，不会出现"界面说在跑其实没跑"。
 */
object CoreManager {

    private val _state = MutableStateFlow(CoreState())
    val state: StateFlow<CoreState> = _state.asStateFlow()

    fun markStarting() {
        _state.value = CoreState(CoreStatus.STARTING)
    }

    fun markRunning() {
        _state.value = CoreState(CoreStatus.RUNNING)
    }

    fun markStopping() {
        _state.value = CoreState(CoreStatus.STOPPING)
    }

    fun markStopped() {
        _state.value = CoreState(CoreStatus.STOPPED)
    }

    fun markError(message: String) {
        _state.value = CoreState(CoreStatus.ERROR, message)
    }
}
