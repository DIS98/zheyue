#!/usr/bin/env bash
# 把当前打好的 release 包同步到 dist/ 与 share/，并生成应用内升级用的 version.json。
#
# 版本号会出现在**四处**，手工改必然漏一次：
#   1. APK 里（界面「关于」）
#   2. dist/ 文件名
#   3. 下载页文案（版本徽章 / 大小 / 日期 / SHA-256 / 更新说明）
#   4. share/version.json（App 内升级的判据）
# 所以统一由这个脚本从**同一个 APK** 推出来。
#
# 用法：
#   ./tools/publish-share.sh            # 用已构建的 APK 同步
#   ./tools/publish-share.sh --build    # 先 assembleRelease 再同步
#
# 同步完成后仍需一步发布（覆盖线上链接）：
#   workbuddy_sites_deploy(directory="share/", userAskedToPublish=true)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JAVA_HOME="${JAVA_HOME:-./toolchain/"
GRADLE="${GRADLE:-/e/app/gradle/gradle-8.9/bin/gradle}"
AAPT2="${AAPT2:-./"
export JAVA_HOME

APK="$ROOT/app/build/outputs/apk/release/app-release.apk"

if [ "${1:-}" = "--build" ]; then
  echo "==> 构建 release（仅 arm64）"
  "$GRADLE" -p "$ROOT" assembleRelease --console=plain | grep -E "BUILD|^e: "
fi

[ -f "$APK" ] || { echo "没有找到 $APK，先执行 assembleRelease 或加 --build" >&2; exit 1; }

# 1. 以 build.gradle.kts 为准
# 版本真源是仓库根的 version.txt（build.gradle 从这里派生 versionName 与 versionCode）
GRADLE_VER=$(tr -d ' \r\n' < "$ROOT/version.txt")
GRADLE_CODE=$(python - "$GRADLE_VER" <<'PYV'
import sys
maj, mi, pa = sys.argv[1].split(".")
print(int(maj) * 1_000_000 + int(mi) * 1_000 + int(pa))
PYV
)

# 2. 读 APK 里真实的版本，必须一致——防止"版本号改了但忘了重新打包"
APK_INFO=$("$AAPT2" dump badging "$APK" 2>/dev/null | head -1)
APK_VER=$(echo "$APK_INFO" | grep -oE "versionName='[^']+'" | sed "s/.*'\(.*\)'/\1/")
APK_CODE=$(echo "$APK_INFO" | grep -oE "versionCode='[0-9]+'" | grep -oE "[0-9]+")

if [ "$GRADLE_VER" != "$APK_VER" ] || [ "$GRADLE_CODE" != "$APK_CODE" ]; then
  echo "版本不一致：gradle=$GRADLE_VER($GRADLE_CODE)  apk=$APK_VER($APK_CODE)" >&2
  echo "APK 是旧的，先重新打包（加 --build）" >&2
  exit 1
fi

# 3. 只接受纯 arm64 的包
if unzip -l "$APK" | grep -q "lib/x86_64"; then
  echo "这个 APK 含 x86_64（模拟器验证包），不能用于分发。请不带 -PwithX86 重新打包。" >&2
  exit 1
fi

# 4. 包里编进去的 ticket 必须与站点当前 ticket 一致。
#    不一致的后果极隐蔽：检查更新返回 403，界面上只显示"检查更新失败：HTTP 403"，
#    而下载页一切正常——所以放在这里硬拦。
ticket=$(python - "$ROOT/app/build.gradle.kts" <<'PY'
import io, re, sys
s = io.open(sys.argv[1], encoding="utf-8").read()
m = re.search(r'UPDATE_ticket",\s*"\\"([^"\\]+)\\"', s)
print(m.group(1) if m else "")
PY
)
if [ -n "${ticket:-}" ]; then
  TMPDEX=$(mktemp -d)
  unzip -o -q "$APK" "classes*.dex" -d "$TMPDEX" || true
  if ! grep -aq "$ticket" "$TMPDEX"/*.dex 2>/dev/null; then
    echo "APK 里找不到 ticket=$ticket（升级请求会 403）" >&2
    python -c "import shutil,sys; shutil.rmtree(sys.argv[1], ignore_errors=True)" "$TMPDEX"; exit 1
  fi
  python -c "import shutil,sys; shutil.rmtree(sys.argv[1], ignore_errors=True)" "$TMPDEX"
fi

# 产物名跟品牌走：用户在文件管理器里看到的就是这个名字
BRAND="Zheyue"
SIZE=$(stat -c %s "$APK")
SHA=$(sha256sum "$APK" | awk '{print toupper($1)}')
SIZE_MB=$(python -c "print(round($SIZE/1024/1024, 1))")
# 文件名必须带内容哈希前 8（发布约定）：同名不同内容的两个包，
# 只看名字无法分辨，线上也就无法证明页面指向的那一个就是我测过的那个。
SHA8=$(printf '%s' "$SHA" | tr 'A-Z' 'a-z' | cut -c1-8)
NAME="${BRAND}-${GRADLE_VER}-${SHA8}.apk"
TODAY=$(date +%Y-%m-%d)

echo "==> 归档 $NAME（${SIZE_MB} MB）"
mkdir -p "$ROOT/dist" "$ROOT/share" "$ROOT/./tmp/"
# 回滚保留：删除旧包之前，把上一版留一份到 ./tmp/
# 为什么需要：dist/share 只放当前版本（否则发布页会指向多个包），
# 但"能不能装回上一版"是发布门禁的一条硬判据 —— 旧包全删掉就等于没有回滚能力。
# 只留一个：回滚只需要退一步，留多了就是堆积。
python - "$ROOT" "$NAME" <<'ROLLBACK'
import glob, os, shutil, sys
root, newname = sys.argv[1], sys.argv[2]
rb = os.path.join(root, "_backup", "rollback")
for f in glob.glob(os.path.join(root, "dist", "*.apk")):
    if os.path.basename(f) == newname:
        continue
    os.makedirs(rb, exist_ok=True)
    for old in glob.glob(os.path.join(rb, "*.apk")):
        os.remove(old)
    shutil.copy2(f, os.path.join(rb, os.path.basename(f)))
    print("   回滚包已保留:", os.path.basename(f))
ROLLBACK
# 清旧包用 python 而不是 rm 通配：Windows 上的 shell 层对带盘符前缀的通配删除有额外限制
python - "$ROOT" <<'PY'
import glob, os, sys
root = sys.argv[1]
for d in ("dist", "share"):
    for f in glob.glob(os.path.join(root, d, "*.apk")):
        os.remove(f)
PY
cp "$APK" "$ROOT/dist/$NAME"
cp "$APK" "$ROOT/share/$NAME"

echo "==> 生成 version.json + 更新下载页"
python - "$ROOT" "$NAME" "$GRADLE_VER" "$GRADLE_CODE" "$SHA" "$SIZE" "$SIZE_MB" "$TODAY" "${ticket:-<REDACTED>}" <<'PY'
import html, io, json, os, re, sys

root, name, ver, code, sha, size, size_mb, today, ticket = sys.argv[1:10]
HOST = "updates.example.com"
share = os.path.join(root, "share")

# ---------- 更新说明：单一来源 tools/release-notes.json ----------
notes_file = os.path.join(root, "tools", "release-notes.json")
notes = []
if os.path.exists(notes_file):
    all_notes = json.load(io.open(notes_file, encoding="utf-8"))
    notes = all_notes.get(ver, [])
if not notes:
    print("  ! tools/release-notes.json 里没有 %s 的条目，version.json 的 notes 为空" % ver)

# ---------- version.json（App 内升级的判据） ----------
manifest = {
    "latest": ver,
    "versionCode": int(code),
    "versionName": ver,
    "file": name,
    # url 写成分享页绝对地址（操作说明 §1.5）。客户端不读它，按 UPDATE_BASE + "/" + file 拼，
    # 写在这里是给人看与给自检核；basename 必须等于 file，否则同一个包出现两个坐标。
    "url": "https://%s/%s?ticket=%s" % (HOST, name, ticket),
    "size": int(size),
    "sha256": sha,
    "minSupported": 0,
    "published": today,
    # 客户端 UpdateRepo 读的是 publishedAt，契约 §3 要的是 published，两个都留，同值。
    "publishedAt": today,
    "notes": notes,
}
out = io.open(os.path.join(share, "version.json"), "w", encoding="utf-8", newline="\n")
json.dump(manifest, out, ensure_ascii=False, indent=2)
out.write("\n")
out.close()

# ---------- 下载页 ----------
p = os.path.join(share, "index.html")
s = io.open(p, encoding="utf-8").read()
# 下载链接：相对路径 + ticket（操作说明 §1.6/§1.7），data-file 与清单 file 逐字一致。
if "data-file=" not in s:
    s = s.replace('id="download"', 'id="download" data-file="%s"' % name, 1)
s = re.sub(r'(id="download"[^>]*href=")[^"]*(")', r'\g<1>%s?ticket=%s\g<2>' % (name, ticket), s)
s = re.sub(r'(id="download"[^>]*data-file=")[^"]*(")', r'\g<1>%s\g<2>' % name, s)
s = re.sub(r'<span class="badge">v[0-9.]+</span>', '<span class="badge">v%s</span>' % ver, s)
s = re.sub(r'<span>[0-9.]+ MB</span>', '<span>%s MB</span>' % size_mb, s, count=1)
s = re.sub(r'<span>20[0-9]{2}-[0-9]{2}-[0-9]{2}</span>', '<span>%s</span>' % today, s, count=1)
s = re.sub(r'SHA-256：[0-9A-Fa-f]+', 'SHA-256：%s' % sha, s)

# 更新说明列表从同一个 notes 生成，避免"页面写的和 App 里补的不一样"
if notes:
    items = "\n".join("      <li>%s</li>" % html.escape(n) for n in notes)
    s = re.sub(r'(<ul id="notes">)(.*?)(</ul>)',
               lambda m: m.group(1) + "\n" + items + "\n    " + m.group(3),
               s, count=1, flags=re.S)

io.open(p, "w", encoding="utf-8").write(s)
print("  版本 v%s (build %s) / %s MB / %s / notes %d 条" % (ver, code, size_mb, today, len(notes)))
PY

echo
echo "==> 已同步："
echo "    dist/$NAME"
echo "    share/$NAME"
echo "    share/version.json   (App 内升级读这个)"
echo "    APK 内「关于」页显示：${GRADLE_VER}（构建号 ${GRADLE_CODE} 只在诊断信息里）"
echo "    下一步：发布 share/ 目录以覆盖线上链接（workbuddy_sites_deploy）"
