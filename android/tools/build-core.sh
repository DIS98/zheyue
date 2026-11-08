#!/usr/bin/env bash
# 交叉编译 mihomo 内核为 Android c-shared 库。
#
# 产物：app/src/main/jniLibs/<abi>/libclash.so
# 依赖：Go 1.23+ / NDK r27+
#
# 用法：
#   ./tools/build-core.sh            # 只编 arm64-v8a（手机用，默认）
#   ./tools/build-core.sh x86_64     # 只编 x86_64（模拟器验证用）
#   ./tools/build-core.sh all        # 两个都编
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NDK="${NDK_HOME:-./"
GO_BIN="${GO_BIN:-/e/app/go/go/bin}"

TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
if [ ! -x "$TOOLCHAIN/clang.exe" ]; then
  echo "找不到 NDK clang：$TOOLCHAIN/clang.exe" >&2
  exit 1
fi

build_one() {
  local abi="$1" goarch="$2" triple="$3" api="$4"
  echo "==> 编译 libclash.so（$abi，api $api）"
  (
    cd "$ROOT/core-go"
    export GOOS=android GOARCH="$goarch" CGO_ENABLED=1
    # Windows 上 Go 无法直接执行 NDK 的 .cmd 包装脚本，改用 clang.exe + 显式 target
    export CC="$TOOLCHAIN/clang.exe"
    export CGO_CFLAGS="--target=$triple$api"
    export CGO_LDFLAGS="--target=$triple$api"
    export GOPROXY="${GOPROXY:-https://goproxy.cn,direct}"
    mkdir -p "$ROOT/app/src/main/jniLibs/$abi"
    # foss       : 去掉非开源特性
    # with_gvisor: 纯 Go 的 tun 协议栈，安卓上不需要额外 C 依赖
    # cmfa       : 必加。不加的话 listener/sing_tun/server_android.go
    #              （//go:build android && !cmfa）会去读 /data/system/packages.xml 建
    #              android rules，普通应用权限下必然 permission denied，内核直接起不来。
    #              代价：route 进入 embed 模式，PATCH /configs 不可用 → 模式切换改走
    #              Native.setMode（内部 tunnel.SetMode）。
    go build -tags "foss with_gvisor cmfa" \
      -buildmode=c-shared \
      -trimpath \
      -ldflags "-s -w -X github.com/metacubex/mihomo/constant.Version=v1.19.33" \
      -o "$ROOT/app/src/main/jniLibs/$abi/libclash.so" .
    # Go 会顺手生成同名头文件，不参与打包，删掉
    rm -f "$ROOT/app/src/main/jniLibs/$abi/libclash.h"
  )
  echo "    $(ls -lh "$ROOT/app/src/main/jniLibs/$abi/libclash.so" | awk '{print $5}')"
}

export PATH="$GO_BIN:$PATH"

case "${1:-arm64}" in
  arm64)   build_one "arm64-v8a" "arm64" "aarch64-linux-android" 26 ;;
  x86_64)  build_one "x86_64"    "amd64" "x86_64-linux-android"  26 ;;
  all)     build_one "arm64-v8a" "arm64" "aarch64-linux-android" 26
           build_one "x86_64"    "amd64" "x86_64-linux-android"  26 ;;
  *) echo "未知 ABI: $1（可选 arm64 / x86_64 / all）" >&2; exit 1 ;;
esac

echo "==> 完成"
