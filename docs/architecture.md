# 架构与开发逻辑 · 折跃 Zheyue


## 数据流

```mermaid
flowchart LR
  N1["界面层"] --> N2["本地 HTTP API"] --> N3["内核控制接口"] --> N4["mihomo 内核"] --> N5["系统网络栈"]
```

## 模块划分

| 位置 | 职责 |
| --- | --- |

| `windows/server.js` | 零第三方依赖的本地后端：HTTP API、静态资源、内核进程管理与配置生成 |
| `windows/src/Desktop` | WPF 原生界面、主题、自绘双线流量图 |
| `windows/src/Setup · SysProxy · Uninstaller` | 安装器、系统代理工具与卸载器 |
| `android/app/src/main` | Kotlin + Compose 六页界面、VpnService、内核控制接口封装 |
| `android/core-go` | Go c-shared 桥接与手写 JNI 胶水层 |
| `android/tools` | 内核交叉编译、版本门禁、出包与校验脚本 |

## 关键设计决策

### 界面不算数据

延迟、命中规则、连接链全部来自内核控制接口。界面自己推算的值不作为判据，避免两套状态互相矛盾。

### 配置写入是单事务

运行期配置先写临时文件再原子替换；多订阅合并生成后必须通过内核校验才落盘；启停与配置生成走串行闸门，防止并发抢端口。

### 内核同进程

移动系统上进程创建会关闭非白名单继承 fd，因此把内核编成 c-shared 库同进程加载，而不是起子进程再传 fd。

### fd 所有权清晰

VPN 服务建立后把 fd 交给内核；仅当启动失败或未启动时由服务自己关闭。应用自身流量绕过隧道，防止出站回环。

### 升级可验证

同一份产物生成版本清单，包内校验值与清单同源；下载后做 SHA-256 与字节数双校验，不符即丢弃重试。

### 权限最小化

系统代理只写当前用户配置；TUN 默认关闭且仅在需要时提权；移动端不申请与功能无关的权限。

## 目录结构

```text
zheyue/
├── windows/              桌面端：本地后端 + 原生界面 + 构建脚本
│   ├── server.js         HTTP API 与内核进程管理
│   ├── src/              WPF 界面、安装器、系统代理工具
│   ├── public/           旧版静态资源
│   ├── rules/            分流规则配置
│   └── tools/            自检与打包脚本
├── android/              移动端：Compose 界面 + 同进程内核
│   ├── app/src/main/     Kotlin 源码、资源与清单
│   ├── core-go/          Go c-shared 桥接与 JNI 胶水
│   └── tools/            内核编译、门禁、出包脚本
├── docs/                 使用指南、架构说明、第三方组件声明
└── README.md
```
