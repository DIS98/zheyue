#!/usr/bin/env bash
# 发布门禁：对 dist/ 下的正式包做「能不能发给用户」的判定。
#
# 设计原则：每条判据都必须是**能抓错**的观测，不接受"看起来对"。
# 例如"含 x86_64"这一条，判据是解压后统计 lib/x86_64 的条目数，而不是看构建日志。
#
# 用法：
#   ./tools/gate.sh            # 检查 dist/ 下最新包 + share/version.json
#   退出码 0 = 全通过，非 0 = 有失败项（失败项会逐条打印）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AAPT2="${AAPT2:-./"
APKSIGNER="${APKSIGNER:-./"
NM="${NM:-./"
export JAVA_HOME="${JAVA_HOME:-./toolchain/"

PASS=0; FAIL=0
ok()   { echo "  [PASS] $1"; PASS=$((PASS+1)); }
bad()  { echo "  [FAIL] $1"; FAIL=$((FAIL+1)); }
check(){ if [ "$2" = "$3" ]; then ok "$1（$2）"; else bad "$1：期望 $3，实际 $2"; fi; }

# APK_OVERRIDE 供变异测试用：拿一个被故意弄坏的包喂给门禁，确认它会报红
APK="${APK_OVERRIDE:-$(ls -1 "$ROOT"/dist/*-arm64.apk 2>/dev/null | tail -1)}"
[ -n "${APK:-}" ] || { echo "dist/ 里没有正式包"; exit 1; }
NAME=$(basename "$APK")
echo "== 门禁对象：$NAME"
echo

TMP=$(mktemp -d)
trap 'python -c "import shutil,sys;shutil.rmtree(sys.argv[1],ignore_errors=True)" "$TMP"' EXIT

# ---------- 1. 版本一致性 ----------
echo "· 构建门禁"
# 版本真源是仓库根的 version.txt（build.gradle 从这里派生 versionName 与 versionCode）
GRADLE_VER=$(tr -d ' \r\n' < "$ROOT/version.txt")
GRADLE_CODE=$(python - "$GRADLE_VER" <<'PYV'
import sys
maj, mi, pa = sys.argv[1].split(".")
print(int(maj) * 1_000_000 + int(mi) * 1_000 + int(pa))
PYV
)
INFO=$("$AAPT2" dump badging "$APK" 2>/dev/null | head -1)
APK_VER=$(echo "$INFO" | grep -oE "versionName='[^']+'" | sed "s/.*'\(.*\)'/\1/")
APK_CODE=$(echo "$INFO" | grep -oE "versionCode='[0-9]+'" | grep -oE "[0-9]+")
check "gradle 与 APK 版本名一致" "$APK_VER" "$GRADLE_VER"
check "gradle 与 APK 版本号一致" "$APK_CODE" "$GRADLE_CODE"

# 文件名里的版本号也要对（分发时用户看的就是这个）
case "$NAME" in
  *"-$APK_VER-arm64.apk") ok "安装包文件名版本与 APK 一致" ;;
  *) bad "安装包文件名版本与 APK 不一致：$NAME vs v$APK_VER" ;;
esac

# ---------- 2. ABI：绝不能带 x86_64 ----------
echo "· 产物门禁"
unzip -o -q "$APK" "lib/*/*" -d "$TMP" 2>/dev/null
X86=$(find "$TMP/lib" -maxdepth 1 -type d -name "x86*" 2>/dev/null | wc -l | tr -d ' ')
check "不含 x86 库（模拟器包不能分发）" "$X86" "0"
ARM_SO=$(find "$TMP/lib/arm64-v8a" -name "libclash.so" 2>/dev/null | wc -l | tr -d ' ')
check "含 arm64 内核库" "$ARM_SO" "1"

# 内核 JNI 符号必须齐全（少了 setMode 会导致模式切换静默失效）
if [ -f "$TMP/lib/arm64-v8a/libclash.so" ] && [ -x "$NM" ]; then
  SYM=$("$NM" -D --defined-only "$TMP/lib/arm64-v8a/libclash.so" 2>/dev/null | grep -c "Java_com_clashdeck")
  if [ "$SYM" -ge 5 ]; then ok "内核 JNI 符号数 $SYM（≥5）"; else bad "内核 JNI 符号只有 $SYM 个"; fi
else
  echo "  [SKIP] 找不到 llvm-nm，跳过符号检查"
fi

# 失败路径的痕迹必须已从二进制里消失（P-A06 的回归判据）
if [ -f "$TMP/lib/arm64-v8a/libclash.so" ]; then
  PKGREF=$(grep -ac "packages.xml" "$TMP/lib/arm64-v8a/libclash.so" 2>/dev/null | head -1)
  check "内核里不含 packages.xml 失败路径" "$PKGREF" "0"
fi

# ---------- 3. 清单：升级链路必须齐 ----------
echo "· 清单门禁"
MANIFEST=$("$AAPT2" dump xmltree --file AndroidManifest.xml "$APK" 2>/dev/null)
echo "$MANIFEST" | grep -q "REQUEST_INSTALL_PACKAGES" \
  && ok "声明 REQUEST_INSTALL_PACKAGES" || bad "缺少 REQUEST_INSTALL_PACKAGES（应用内升级无法安装）"
echo "$MANIFEST" | grep -q "androidx.core.content.FileProvider" \
  && ok "声明 FileProvider" || bad "缺少 FileProvider（无法把安装包交给系统）"
# 系统备份必须关闭：files/ 里是订阅原文与节点凭据，进云备份/换机迁移就等于外泄
if echo "$MANIFEST" | grep -q "allowBackup(0x01010280)=false"; then
  ok "关闭系统备份（订阅与节点凭据不随备份离开设备）"
else
  bad "allowBackup 不是 false —— 订阅 token 与节点密码会被系统备份出去"
fi

# FileProvider 绝不能导出
if echo "$MANIFEST" | grep -A 30 "FileProvider" | grep -q "exported(0x01010010)=false"; then
  ok "FileProvider exported=false"
else
  bad "FileProvider 的 exported 不是 false（会把私有目录读权限开给任意应用）"
fi
echo "$MANIFEST" | grep -q "extractNativeLibs(0x010104ea)=true" \
  && ok "extractNativeLibs=true（Go 的 so 必须解压才能 dlopen）" \
  || bad "extractNativeLibs 不是 true"

# ---------- 4. 升级源常量：必须是线上地址，且不能漏 debug 覆盖 ----------
# 判据从「dex 里有某个固定域名」改成「dex 里的地址 == build.gradle.kts 里的 UPDATE_BASE」。
# 原因：2.0.4 的包编的是旧站点地址，而 gradle 早已改成云通道 —— 固定域名判据看不出来，
# 只会在用户机上表现为「检查更新连不上旧站」。同源比对才能抓住「改了常量没重打包」。
echo "· 升级配置门禁"
unzip -o -q "$APK" "classes*.dex" -d "$TMP" 2>/dev/null
EXPECT_BASE=$(python - "$ROOT/app/build.gradle.kts" <<'PYG'
import io, re, sys
s = io.open(sys.argv[1], encoding="utf-8").read()
m = re.search(r'buildConfigField\("String",\s*"UPDATE_BASE",\s*"\\"([^"\\]+)\\""\)', s)
print(m.group(1) if m else "")
PYG
)
EXPECT_CODE=$(python - "$ROOT/app/build.gradle.kts" <<'PYG'
import io, re, sys
s = io.open(sys.argv[1], encoding="utf-8").read()
m = re.search(r'buildConfigField\("String",\s*"UPDATE_ticket",\s*"\\"([^"\\]+)\\""\)', s)
print(m.group(1) if m else "")
PYG
)
if [ -z "${EXPECT_BASE:-}" ]; then
  bad "build.gradle.kts 里读不到 UPDATE_BASE"
elif grep -aq "$EXPECT_BASE" "$TMP"/classes*.dex 2>/dev/null; then
  ok "升级源与 build.gradle.kts 同源（$EXPECT_BASE）"
else
  bad "dex 里的升级源与 build.gradle.kts 不一致（期望 $EXPECT_BASE）—— 常量改了但包没重打"
fi
if echo "${EXPECT_BASE:-}" | grep -qE '^http://(127\.0\.0\.1|10\.0\.2\.2)'; then
  bad "build.gradle.kts 的 UPDATE_BASE 指向本机调试源"
fi
if grep -aq "10.0.2.2" "$TMP"/classes*.dex 2>/dev/null; then
  bad "dex 里含 10.0.2.2（调试包的本地升级源漏进正式包）"
else
  ok "不含调试用本地升级源"
fi
if [ -z "${EXPECT_CODE:-}" ]; then
  bad "build.gradle.kts 里读不到 UPDATE_ticket"
else
  # 用 grep -c 计次而不是 grep -q：-q 命中即退出，上游 cat 收到 SIGPIPE 得 141，
  # 在 pipefail 下整条判据会假红（这里踩过一次）。
  CODE_IN_DEX=$(cat "$TMP"/classes*.dex 2>/dev/null | grep -ac "$EXPECT_CODE")
  if [ "${CODE_IN_DEX:-0}" -ge 1 ]; then
    ok "dex 内 ticket 与 build.gradle.kts 同源"
  else
    bad "dex 内找不到 ticket=$EXPECT_CODE（检查更新会 403）"
  fi
fi

# ---------- 5. 签名 ----------
echo "· 签名门禁"
if "$APKSIGNER" verify "$APK" >/dev/null 2>&1; then ok "签名校验通过"; else bad "签名校验失败"; fi

# ---------- 6. version.json 与 APK 必须同源 ----------
echo "· 升级描述门禁"
VJ="$ROOT/share/version.json"
if [ -f "$VJ" ]; then
  VJ_OUT=$(python - "$VJ" "$APK" "$APK_VER" "$APK_CODE" <<'PY'
import hashlib, json, os, sys
vj, apk, ver, code = sys.argv[1:5]
m = json.load(open(vj, encoding="utf-8"))
h = hashlib.sha256()
with open(apk, "rb") as f:
    for c in iter(lambda: f.read(1 << 20), b""):
        h.update(c)
sha, size = h.hexdigest().upper(), os.path.getsize(apk)
bad = 0
def chk(name, got, want):
    global bad
    if str(got) == str(want): print("  [PASS] %s（%s）" % (name, got))
    else:
        print("  [FAIL] %s：期望 %s，实际 %s" % (name, want, got)); bad += 1
chk("version.json 版本号", m.get("versionCode"), code)
chk("version.json 版本名", m.get("versionName"), ver)
chk("version.json 文件大小", m.get("size"), size)
chk("version.json SHA-256", str(m.get("sha256", "")).upper(), sha)
chk("version.json 文件名", m.get("file"), os.path.basename(apk))
sys.exit(1 if bad else 0)
PY
)
  echo "$VJ_OUT"
  PASS=$((PASS + $(printf '%s\n' "$VJ_OUT" | grep -c "\[PASS\]" || true)))
  FAIL=$((FAIL + $(printf '%s\n' "$VJ_OUT" | grep -c "\[FAIL\]" || true)))
else
  bad "share/version.json 不存在（App 内升级会失败）"
fi

# ---------- 7. 下载页 ----------
echo "· 下载页门禁"
if grep -q "$APK_VER-arm64.apk" "$ROOT/share/index.html"; then
  ok "下载页链接指向当前版本"
else
  bad "下载页链接不是当前版本"
fi
grep -q ">v$APK_VER<" "$ROOT/share/index.html" && ok "下载页版本徽章正确" || bad "下载页版本徽章不是 v$APK_VER"

echo "· 版本同步门禁（文档 / 产物 / 清单 / tag 四处不许脱节）"
mkdir -p "$ROOT/.tmp"
if bash "$ROOT/tools/version-check.sh" > "$ROOT/.tmp/version-check.log" 2>&1; then
  ok "版本同步校验通过（$GRADLE_VER / $GRADLE_CODE）"
else
  bad "版本同步校验失败："
  grep "FAIL" "$ROOT/.tmp/version-check.log" | sed 's/^/      /' | head -5
fi

echo
echo "== 结果：通过 $PASS 项，失败 $FAIL 项"
[ "$FAIL" -eq 0 ] || exit 1
echo "== 门禁通过，可以发布"
