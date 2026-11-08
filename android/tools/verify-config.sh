#!/usr/bin/env bash
# 运行期配置的两级验收：
#   ① 单测：用真实订阅生成 runtime.yaml（不走 Android 运行时，纯 JVM）
#   ② 内核校验：用真实 mihomo 二进制 `-t` 判断这份配置能不能起内核
#   ③ 可选：实跑内核并核对 UI 依赖的接口返回结构
#
# 用法：
#   ./tools/verify-config.sh /path/to/subscription.yaml
#   ./tools/verify-config.sh /path/to/subscription.yaml --run
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE="${1:?请给出订阅 yaml 路径}"
RUN_IT="${2:-}"
# 单测的工作目录在 app/ 模块下，相对路径会解析不到 → 一律传绝对路径
PROFILE="$(cd "$(dirname "$PROFILE")" && pwd)/$(basename "$PROFILE")"
JAVA_HOME="${JAVA_HOME:-./toolchain/"
GRADLE="${GRADLE:-/e/app/gradle/gradle-8.9/bin/gradle}"
# 内核二进制的默认位置是工具区（见下方 MIHOMO），本脚本不依赖另一端的存在。
# 校验用的 mihomo 二进制来自**工具区**，不是隔壁 Windows 端的目录。
# 原因：安卓端不该依赖另一端的存在——那边一改名/一删，这边的门禁就会静默失效。
# 需要换版本时：MIHOMO=/path/to/mihomo ./tools/verify-config.sh
MIHOMO="${MIHOMO:-./toolchain/"

export JAVA_HOME

echo "==> ① 单测 + 生成运行期配置（真实订阅：$PROFILE）"
# 先删旧产物：否则上一次的 yaml 会被当成这次的证据（内核校验照样通过 = 假绿）
rm -f "$ROOT"/app/build/test-runtime*.yaml
# --rerun-tasks 是必须的：单测会顺带把 runtime.yaml 写到 app/build/ 作为证据，
# 而 gradle 的 test 任务只要输入没变就 UP-TO-DATE —— 此时 yaml 不会重新生成，
# 脚本就会报"缺少 test-runtime.yaml"。这不是配置有问题，是装置在骗人（已实测踩到）。
"$GRADLE" -p "$ROOT" :app:testReleaseUnitTest --rerun-tasks --console=plain -PtestProfile="$PROFILE" \
  | grep -E "\[dump\]|\[skip\]|FAILED|BUILD"

CFG="$ROOT/app/build/test-runtime.yaml"
[ -f "$CFG" ] || { echo "没有生成 $CFG" >&2; exit 1; }

echo "==> ② 铺一份离线 homeDir（ruleset + geo），交给真实内核校验"
HOME_DIR="$ROOT/.verify-home"
rm -rf "$HOME_DIR" && mkdir -p "$HOME_DIR"
cp -r "$ROOT/app/src/main/assets/ruleset" "$HOME_DIR/"
cp "$ROOT/app/src/main/assets/geo/geoip.dat" \
   "$ROOT/app/src/main/assets/geo/geosite.dat" \
   "$ROOT/app/src/main/assets/geo/Country.mmdb" "$HOME_DIR/"

# 两种分流模式各自过一遍内核：配置写错的表现是"内核起不来"，模式写错的表现是"能起来但全走代理"，
# 后者只有单测能判（见 RuntimeConfigBuilderTest），前者在这里判。
for CFG_FILE in test-runtime.yaml test-runtime-strict.yaml; do
  ONE="$ROOT/app/build/$CFG_FILE"
  [ -f "$ONE" ] || { echo "缺少 $ONE（单测未产出）" >&2; exit 1; }
  echo "--- 内核校验 $CFG_FILE ---"
  "$MIHOMO" -t -d "$HOME_DIR" -f "$ONE" 2>&1 | tail -2
done

if [ "$RUN_IT" = "--run" ]; then
  echo "==> ③ 实跑内核（Ctrl+C 结束），随后核对接口"
  mkdir -p "$ROOT/.tmp"
  "$MIHOMO" -d "$HOME_DIR" -f "$CFG" > "$ROOT/.tmp/mihomo-verify.log" 2>&1 &
  MH_PID=$!
  trap 'kill $MH_PID 2>/dev/null || true' EXIT
  sleep 6
  echo "--- /version ---"; curl -s --max-time 4 http://127.0.0.1:9090/version; echo
  echo "--- /configs ---"; curl -s --max-time 4 http://127.0.0.1:9090/configs \
    | python -c "import sys,json;d=json.load(sys.stdin);print({k:d.get(k) for k in ['mode','mixed-port','allow-lan','log-level']})"
  echo "--- /proxies ---"; curl -s --max-time 6 http://127.0.0.1:9090/proxies \
    | python -c "
import sys,json
d=json.load(sys.stdin)['proxies']
g=[k for k,v in d.items() if v.get('all')]
print('条目',len(d),'策略组',len(g))
for k in g[:5]: print(' ',k,d[k]['type'],d[k].get('now'),len(d[k]['all']))
"
  kill $MH_PID 2>/dev/null || true
  trap - EXIT
fi

# 离线 homeDir 是 29MB 的临时副本（ruleset + geo），校验完必须删——
# 不删就会在项目里留下一个"每次跑都长一次"的目录，且它看起来像配置（容易被误认）。
rm -rf "$HOME_DIR"

# 离线 homeDir 是 29MB 的临时副本（ruleset + geo），校验完必须删——
# 不删就会在项目里留下一个"每次跑都长一次"的目录，且它看起来像配置（容易被误认）。
rm -rf "$HOME_DIR"

echo "==> 完成"
