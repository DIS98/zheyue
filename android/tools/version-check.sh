#!/usr/bin/env bash
# 版本同步校验（对应 version-numbering.md §6.1 的四步）
#
# 为什么要有这个脚本：版本号会同时出现在真源、文档、脚本、产物文件名、version.json、
# 关于页、tag 七处。漏一处都不会报错，只会让"我装的到底是哪一版"出现多个答案。
# 所以"每次出包都同步"不能靠记性，必须是一条跑完会红/绿的命令。
#
# 用法：./tools/version-check.sh
# 退出码：0 = 全部同步；1 = 有脱节（输出里标明哪一条）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

fail=0
info() { printf '  %s\n' "$1"; }
bad()  { printf '  [FAIL] %s\n' "$1"; fail=1; }
ok()   { printf '  [PASS] %s\n' "$1"; }

echo "· 版本真源"
[ -f version.txt ] || { bad "缺少 version.txt（唯一真源）"; exit 1; }
VER=$(tr -d ' \r\n' < version.txt)
CODE=$(python - "$VER" <<'PY'
import sys
maj, mi, pa = sys.argv[1].split(".")
print(int(maj) * 1_000_000 + int(mi) * 1_000 + int(pa))
PY
)
echo "$VER" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' || bad "version.txt 不是三段式：$VER"
ok "显示版 $VER（派生 versionCode $CODE）"

echo "· ① 源码里的版本必须来自真源，不许手写"
if grep -qE 'versionName\s*=\s*"[0-9]' app/build.gradle.kts; then
  bad "build.gradle.kts 里还有手写的 versionName"
else
  ok "build.gradle.kts 从 version.txt 派生"
fi
if grep -qE 'versionCode\s*=\s*[0-9]' app/build.gradle.kts; then
  bad "build.gradle.kts 里还有手写的 versionCode"
else
  ok "versionCode 由公式派生"
fi

echo "· ② 文档与脚本里出现的安装包版本串必须等于 $VER"
# 只查"当前对外文档与脚本"里的产物名。
# 历史报告（docs/全量测试报告-*.md）与 archive/ 记录的是**当时**的产物名，
# 它们本来就该是旧版本；把它们算成脱节会让这条判据永远红，于是被无视——那才是真危险。
# 范围只含"当前对外文档 + 脚本"。docs/ 下的报告是**历史记录**（记录当时那一版叫什么），
# archive/ 是被取代的旧文档 —— 它们天然带旧版本号，算进来会让这条判据永远红，
# 而永远红的判据等于没有判据（会被无视）。要查"现行文档是否写死旧版本"，
# 看 README 与下载页就够，这两处才是用户真会读到的。
OUT=$(grep -rnoE 'Zheyue-[0-9]+\.[0-9]+\.[0-9]+(-[0-9a-f]{8}|-arm64)?\.apk' README.md share/index.html tools/ 2>/dev/null \
  | grep -v "$VER")
if [ -n "$OUT" ]; then
  bad "文档里写的是别的版本："
  echo "$OUT" | sed 's/^/      /'
else
  ok "文档里的产物名与真源一致（或未写死版本）"
fi

echo "· ②b README 里若写了"当前版本"，必须等于 $VER"
OUT=$(grep -oE '当前版本 [0-9]+\.[0-9]+\.[0-9]+' README.md 2>/dev/null | grep -v "$VER")
if [ -n "$OUT" ]; then
  bad "README 写的当前版本与真源不一致：$OUT"
else
  ok "README 的当前版本与真源一致（或未写）"
fi

echo "· ③ 实际产物文件名必须等于 $VER"
OUT=$(find dist share -maxdepth 1 -name "*.apk" 2>/dev/null | grep -v "$VER")
if [ -n "$OUT" ]; then
  bad "产物目录里有别的版本："
  echo "$OUT" | sed 's/^/      /'
else
  ok "dist/ share/ 里只有 $VER 的包"
fi

echo "· ④ version.json 与本地产物必须对得上"
VJ="share/version.json"
if [ -f "$VJ" ]; then
  VJV=$(python - "$VJ" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print(f'{d.get("versionName")} {d.get("versionCode")} {d.get("sha256")} {d.get("file") or ""}')
PY
)
  set -- $VJV
  [ "$1" = "$VER" ] && ok "version.json 版本名 $1" || bad "version.json 版本名是 $1，应为 $VER"
  [ "$2" = "$CODE" ] && ok "version.json 版本号 $2" || bad "version.json 版本号是 $2，应为 $CODE"
  # 产物名从清单取。名字带内容哈希，每版都变，写死在这里下次改名就成了 SKIP——
  # 而 SKIP 不计入通过，等于这条判据自己消失了。
  APK="dist/${4:-Zheyue-$VER-arm64.apk}"
  if [ -f "$APK" ]; then
    SUM=$(sha256sum "$APK" | awk '{print toupper($1)}')
    [ "$SUM" = "$3" ] && ok "version.json sha256 = 本地产物" || bad "sha256 不一致：产物 ${SUM:0:16}… / 清单 ${3:0:16}…"
  else
    bad "清单声明的产物不在 dist/：$APK"
  fi
else
  bad "缺少 $VJ"
fi

echo "· ⑤ tag 与显示版一致"
if git rev-parse --git-dir >/dev/null 2>&1; then
  if git rev-parse -q --verify "refs/tags/v$VER" >/dev/null; then
    ok "tag v$VER 存在"
  else
    bad "缺少 tag v$VER（发布前请 git tag v$VER）"
  fi
else
  bad "当前目录不是 git 仓库，tag 无法落地"
fi

echo
if [ "$fail" -eq 0 ]; then
  echo "== 版本同步：全部通过（$VER / $CODE）"
else
  echo "== 版本同步：有脱节，先修再发布"
fi
exit "$fail"
