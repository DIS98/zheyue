package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
	"unsafe"

	"github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/hub"
	"github.com/metacubex/mihomo/hub/executor"
	"github.com/metacubex/mihomo/hub/route"
	"github.com/metacubex/mihomo/listener"
	LC "github.com/metacubex/mihomo/listener/config"
	"github.com/metacubex/mihomo/listener/sing_tun"
	"github.com/metacubex/mihomo/log"
	"github.com/metacubex/mihomo/tunnel"
	"github.com/sirupsen/logrus"
)

var (
	// 控制端地址与密钥：断开时要按这个地址确认端口真的关了。
	controllerAddr   string
	controllerSecret string
	// 控制端实际监听的端口。配置里的端口被占用时会换一个，
	// 界面必须用这个值来请求，不能假设还是配置里写的那个。
	apiPort int32

	mu      sync.Mutex
	running bool
	tun     io.Closer
	lastErr string
	errBuf  *C.char
)

// pickControllerAddr 返回一个真的能 bind 的控制端地址。
// 配置里的端口被占用时（常见：别的代理客户端默认也用 9090），从 19090 起找一个空闲的。
func pickControllerAddr(preferred string) string {
	if canBind(preferred) {
		return preferred
	}
	host, _, err := net.SplitHostPort(preferred)
	if err != nil || host == "" {
		host = "127.0.0.1"
	}
	for p := 19090; p <= 19120; p++ {
		cand := net.JoinHostPort(host, strconv.Itoa(p))
		if canBind(cand) {
			log.Warnln("[clashdeck] 控制端 %s 已被占用，改用 %s（不影响代理本身）", preferred, cand)
			return cand
		}
	}
	log.Warnln("[clashdeck] 19090-19120 也被占满，仍用 %s，可能失败", preferred)
	return preferred
}

func canBind(addr string) bool {
	l, err := net.Listen("tcp", addr)
	if err != nil {
		return false
	}
	_ = l.Close()
	return true
}

// waitControllerHTTP 轮询探测控制端能否**真正应答 HTTP**，直到成功或超时。
//
// 不能只判断 TCP 可连：三次握手由系统协议栈完成，只要端口上有 listener，
// 哪怕 server.Serve 还没跑起来、应用层根本没人 accept，net.Dial 也会成功。
// 实测就是这样"探活通过 → 界面照样超时"。这里的判据是 GET /version 返回 200。
func waitControllerHTTP(addr, secret string, limit time.Duration) bool {
	client := &http.Client{Timeout: 900 * time.Millisecond}
	url := "http://" + addr + "/version"
	deadline := time.Now().Add(limit)
	for time.Now().Before(deadline) {
		req, err := http.NewRequest(http.MethodGet, url, nil)
		if err != nil {
			return false
		}
		if secret != "" {
			req.Header.Set("Authorization", "Bearer "+secret)
		}
		if resp, err := client.Do(req); err == nil {
			_, _ = io.Copy(io.Discard, resp.Body)
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				log.Infoln("[clashdeck] controller ready (HTTP 200): %s", addr)
				return true
			}
		}
		time.Sleep(80 * time.Millisecond)
	}
	log.Warnln("[clashdeck] controller NOT answering HTTP after %v: %s", limit, addr)
	return false
}

// waitPortClosed 等到端口真的不再接受连接，用于"断开"之后。
//
// route.ReCreateServer 内部是 `go start(cfg)`，关闭同样是异步的。
// 如果紧接着重连，新的 start 会和旧的 start 并发操作同一个包级 httpServer，
// 旧的会把刚建好的 server 一起 Close —— 表现就是"显示已连接，但接口永远无响应"。
// 所以断开必须等端口真的关了才算完。
func waitPortClosed(addr string, limit time.Duration) {
	deadline := time.Now().Add(limit)
	for time.Now().Before(deadline) {
		conn, err := net.DialTimeout("tcp", addr, 80*time.Millisecond)
		if err != nil {
			log.Infoln("[clashdeck] controller closed: %s", addr)
			return
		}
		_ = conn.Close()
		time.Sleep(30 * time.Millisecond)
	}
	log.Warnln("[clashdeck] controller still listening after %v: %s", limit, addr)
}

func setErr(err error) {
	if err == nil {
		lastErr = ""
		return
	}
	lastErr = err.Error()
}

const maxLogSize = 4 << 20 // 4 MB

var logFile *os.File

// redirectLogToFile 把内核日志接到文件；超过 4MB 先把旧文件删掉重建，避免无限增长。
// 注意：mihomo 的 log 包底层是 logrus（log/log.go: init 里 SetOutput(os.Stdout)），
// 不是标准库 log —— 所以这里必须调 logrus.SetOutput，stdlog 是错的。
func redirectLogToFile(path string) {
	if fi, err := os.Stat(path); err == nil && fi.Size() > maxLogSize {
		_ = os.Remove(path)
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return
	}
	logFile = f
	logrus.SetOutput(f)
}

// cd_start_core 解析配置并启动内核，然后用 Java 侧传入的 TUN fd 建立 tun 栈。
// fd 由 VpnService.Builder.establish().detachFd() 得来，所有权转移给内核。
func cdStartCore(home, cfgPath string, fd int, stack, gateway, dns string) (err error) {
	mu.Lock()
	defer mu.Unlock()

	if running {
		return fmt.Errorf("core already running")
	}

	defer func() {
		if r := recover(); r != nil {
			err = fmt.Errorf("panic: %v", r)
		}
		setErr(err)
	}()

	constant.SetHomeDir(home)
	constant.SetConfig(cfgPath)
	log.SetLevel(log.INFO)

	// 安卓上进程的 stdout 无处可去（不是终端），内核日志默认等于丢失。
	// 这里把标准库 log 的输出接到文件，出问题时能拿到现场，而不是只有界面上一行字。
	redirectLogToFile(filepath.Join(home, "core.log"))

	log.Infoln("[clashdeck] starting core, home=%s cfg=%s fd=%d stack=%s", home, cfgPath, fd, stack)

	cfg, err := executor.ParseWithPath(cfgPath)
	if err != nil {
		return fmt.Errorf("parse config: %w", err)
	}

	// 日志级别跟随运行期配置：界面上的「详细日志」开关会把它切成 debug。
	// 之前这里硬编码 INFO，用户想深挖时没有任何办法拿到更细的记录。
	if cfg.General != nil {
		log.SetLevel(cfg.General.LogLevel)
		log.Infoln("[clashdeck] log level = %s", cfg.General.LogLevel)
	}
	// 控制端端口被别的程序占着时（不少代理客户端默认也用 9090），
	// mihomo 会在 listen 阶段直接失败，于是"能上网但界面永远读不到数据"。
	// 这里先探测，被占就换一个空闲端口，并把实际端口记下来交给界面。
	if cfg.Controller != nil && cfg.Controller.ExternalController != "" {
		cfg.Controller.ExternalController = pickControllerAddr(cfg.Controller.ExternalController)
	}

	// 必须用 hub.ApplyConfig 而不是 executor.ApplyConfig：
	// hub.ApplyConfig = applyRoute（启动 external-controller HTTP 服务）+ executor.ApplyConfig。
	// 只调 executor 的话隧道和代理都正常、能上网，但界面读不到任何数据（控制端根本没监听）。
	hub.ApplyConfig(cfg)

	// route.ReCreateServer 内部是 `go start(cfg)` —— 控制端在 goroutine 里异步监听，
	// hub.ApplyConfig 返回时 9090 往往还没真正 bind。
	// 若界面此时立刻发第一次请求，就撞上"端口未就绪"的窗口，
	// 表现为 SocketTimeoutException / ConnectException，几秒后又自己好了。
	// 这里等端口真正可连再返回，把"内核已启动"这个承诺做实。
	if cfg.Controller != nil && cfg.Controller.ExternalController != "" {
		controllerAddr = cfg.Controller.ExternalController
		controllerSecret = cfg.Controller.Secret
		if _, portStr, splitErr := net.SplitHostPort(controllerAddr); splitErr == nil {
			if pv, convErr := strconv.Atoi(portStr); convErr == nil {
				apiPort = int32(pv)
			}
		}
		if !waitControllerHTTP(controllerAddr, controllerSecret, 6*time.Second) {
			log.Warnln("[clashdeck] 控制接口未就绪：界面会读不到数据。请看本文件更早的 listen/serve 错误行")
		}
	}

	// 网关必须与 VpnService.addAddress 的地址/掩码一致，否则 tun 收不到包
	var prefix4, prefix6 []netip.Prefix
	for _, s := range strings.Split(gateway, ",") {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		p, err := netip.ParsePrefix(s)
		if err != nil {
			return fmt.Errorf("bad gateway %q: %w", s, err)
		}
		if p.Addr().Is4() {
			prefix4 = append(prefix4, p)
		} else {
			prefix6 = append(prefix6, p)
		}
	}

	var dnsHijack []string
	for _, s := range strings.Split(dns, ",") {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		dnsHijack = append(dnsHijack, s+":53")
	}

	tunStack, ok := constant.StackTypeMapping[strings.ToLower(stack)]
	if !ok {
		tunStack = constant.TunGvisor
	}

	opts := LC.Tun{
		Enable:              true,
		Device:              "tun",
		Stack:               tunStack,
		DNSHijack:           dnsHijack,
		AutoRoute:           false, // 路由由 VpnService 负责
		AutoDetectInterface: false, // 用 addDisallowedApplication 让本进程绕过 tun
		Inet4Address:        prefix4,
		Inet6Address:        prefix6,
		MTU:                 9000,
		FileDescriptor:      fd,
	}

	closer, err := sing_tun.New(opts, tunnel.Tunnel)
	if err != nil {
		return fmt.Errorf("start tun: %w", err)
	}

	tun = closer
	running = true
	log.Infoln("[clashdeck] core started")
	return nil
}

func cdStopCore() error {
	mu.Lock()
	defer mu.Unlock()

	if !running {
		return nil
	}
	if tun != nil {
		_ = tun.Close()
		tun = nil
	}
	executor.Shutdown()

	// executor.Shutdown() 内部只调 listener.Cleanup() → 只关 tun，**不关 inbound 监听**；
	// 而独立进程里这些端口是靠"进程退出"释放的。我们嵌在 App 进程里，进程不退出，
	// 不显式关掉就会出现"断开后 7890 还在 LISTEN"——用户看到的就是"代理没关干净"。
	listener.ReCreateHTTP(0, tunnel.Tunnel)
	listener.ReCreateSocks(0, tunnel.Tunnel)
	listener.ReCreateMixed(0, tunnel.Tunnel)

	// 控制端没有 Shutdown 接口，但 start() 会先 Close 旧的 http server；
	// Addr 为空则不再重新监听，等价于关掉。
	route.ReCreateServer(&route.Config{})
	if controllerAddr != "" {
		waitPortClosed(controllerAddr, 900*time.Millisecond)
	}

	running = false
	setErr(nil)
	log.Infoln("[clashdeck] core stopped（tun/inbound/controller 已全部释放）")
	return nil
}

func cString(goStr string) *C.char {
	if goStr == "" {
		return C.CString("")
	}
	return C.CString(goStr)
}

//export cd_start_core
func cd_start_core(home, cfg *C.char, fd C.int, stack, gateway, dns *C.char) C.int {
	err := cdStartCore(
		C.GoString(home),
		C.GoString(cfg),
		int(fd),
		C.GoString(stack),
		C.GoString(gateway),
		C.GoString(dns),
	)
	if err != nil {
		return -1
	}
	return 0
}

//export cd_stop_core
func cd_stop_core() C.int {
	if err := cdStopCore(); err != nil {
		setErr(err)
		return -1
	}
	return 0
}

// cd_set_mode 切换 规则/全局/直连。
//
// 为什么不走 HTTP `PATCH /configs`：内核以 cmfa 标签构建时 route 层处于 embed 模式，
// 该接口**不会被注册**（hub/route/configs.go:28）。这里直接调 tunnel.SetMode，
// 与官方 patchConfigs 的处理（configs.go:387 `tunnel.SetMode(*general.Mode)`）等价。
//
//export cd_set_mode
func cd_set_mode(mode *C.char) C.int {
	var m tunnel.TunnelMode
	if err := m.UnmarshalText([]byte(strings.ToLower(C.GoString(mode)))); err != nil {
		setErr(err)
		return -1
	}
	tunnel.SetMode(m)
	setErr(nil)
	return 0
}

//export cd_last_error
func cd_last_error() *C.char {
	if errBuf != nil {
		C.free(unsafe.Pointer(errBuf))
		errBuf = nil
	}
	errBuf = cString(lastErr)
	return errBuf
}

//export cd_api_port
func cd_api_port() C.int {
	return C.int(apiPort)
}

//export cd_version
func cd_version() *C.char {
	return cString(constant.Version)
}

func main() {}
