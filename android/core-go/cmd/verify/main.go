// 机制验证程序（仅开发期使用，不参与 APK 构建）。
//
// 目的：证明 external-controller 的启动只发生在 hub.ApplyConfig 里。
//   A: 只调 executor.ApplyConfig（本项目 1.0.1 及之前的做法）→ /version 应连不上
//   B: 再调 hub.ApplyConfig（1.0.2 起的做法）→ /version 应返回 200
//
// 这一条对应用户现象：能上网（隧道/代理由 ApplyConfig 建好），但界面读不到任何数据。
//
// 用法：go run ./cmd/verify <homeDir> <configPath>
package main

import (
	"fmt"
	"io"
	"net/http"
	"os"
	"time"

	"github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/hub"
	"github.com/metacubex/mihomo/hub/executor"
)

func probe() string {
	client := &http.Client{Timeout: 2 * time.Second}
	resp, err := client.Get("http://127.0.0.1:9090/version")
	if err != nil {
		return "连不上（" + err.Error() + "）"
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	return fmt.Sprintf("HTTP %d %s", resp.StatusCode, string(body))
}

func main() {
	if len(os.Args) < 3 {
		fmt.Println("用法: go run ./cmd/verify <homeDir> <configPath>")
		os.Exit(2)
	}
	home, cfgPath := os.Args[1], os.Args[2]

	constant.SetHomeDir(home)
	constant.SetConfig(cfgPath)

	cfg, err := executor.ParseWithPath(cfgPath)
	if err != nil {
		panic(err)
	}

	executor.ApplyConfig(cfg, true)
	time.Sleep(500 * time.Millisecond)
	fmt.Println("[A] executor.ApplyConfig 之后 :", probe())

	hub.ApplyConfig(cfg)
	time.Sleep(1500 * time.Millisecond)
	fmt.Println("[B] hub.ApplyConfig 之后      :", probe())
}
