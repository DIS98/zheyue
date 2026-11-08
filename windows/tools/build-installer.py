"""组装载荷 + 编译安装包 + 签名 + 发布目录清单（自带运行时 + 内置内核）。

产物： dist\\ClashDeck-Setup-<VER>-<hash8>.exe               完整版（本机自用，不交付）
       release-win\\ClashDeck-Setup-<VER>-<hash8>-clean.exe 干净版（打包产物，供交接核对）
       release-win\\desktop-version.json                     清单，与包同批产出
       share\\  同名 exe + desktop-version.json + 同步后的 index.html（操作说明从这里发）

下载源走分享页同目录：https://updates.example.com<file>
腾讯云静态托管已弃用，清单与页面里都不许再出现 updates.example.com

命名里的 hash8 = sha256 前 8 位，**签名之后**才算，否则名字与内容对不上。
清单 sha256/size 由脚本写入，手改即视为第二真源。

用法：
    python tools/build-installer.py            # 完整版
    python tools/build-installer.py --clean    # 干净版（末尾四条自检，任一不过即失败）

依赖目标机器：无。安装器/卸载器/系统代理工具都是 net48（系统自带），
主程序是 self-contained 单文件（自带 .NET 运行时），内核随包附带。
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(ROOT, "build")
STAGE = os.path.join(BUILD, "stage")
DOTNET = r"./toolchain/"
VERSION = "2.6.15"

# ---- 发布约定（唯一真源 ./
# 命名 <产品>-<版本>-<sha256 前 8 位>-<形态>.exe：同名不同内容只能靠哈希前缀分辨。
# 顺序是"先签名再算哈希"，反过来签完名字里的哈希立刻对不上内容。
SIGNTOOL = r"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
CERT_SHA1 = "07E8ABD6685F2E20CE9E483DBF10AD31491CA678"
# 下载源回迁分享页（2026-11-07）：腾讯云 updates.example.com 全线弃用。
# 清单 url 与页面 href 都落在本站同目录，不再有 /clashdeck/ 中间层。
SHARE_HOST = "https://updates.example.com"
SHARE_CODE = "<REDACTED>"
RELEASE = os.path.join(ROOT, "release-win")
# 部署目录（操作说明从这里发）。打包完自动同步过去，不靠手拷——
# 手拷会让两处 sha256 漂移，而页面与清单要逐字一致。
SHARE_DIR = os.path.join(ROOT, "share")
# 契约第 4 条：包与清单一次产出，**打包脚本自带判据**。只打印自检命令等于没有判据，
# 所以这里真调用，非零退出即整个打包失败——不给人"先出了包再说"的机会。
# 可用 PKG_CHECK 覆盖，用于换机器或验证"判据真的会拦"。
CHECK = os.environ.get("PKG_CHECK", r"./")
NOTES = [
    "更新通道整体切到分享页：原腾讯云线路已退役，旧版点「检查更新」会连不上服务器",
    "手动装这一次就恢复自动更新，之后有新版本会正常提示，不用再手动下",
    "安装包与下载页同源同目录，下载链接不变",
    "功能与 2.6.14 相同（TUN 分流规则误报的修复沿用），本版只换通道",
]

# 内核来源：项目内 core/（自包含，不再依赖本机其它软件是否安装）
# 早期版本从这里取 ./ Verge —— 一旦那个软件被卸载，打包立刻失败。
CORE_ROOT = os.path.join(ROOT, "core")
CORE_EXE = "verge-mihomo.exe"
CORE_RES = ["geoip.dat", "geosite.dat", "Country.mmdb", "ASN.mmdb"]

# 安装后进安装目录的文件（相对 ROOT 或编译产物）
PAYLOAD_FROM_ROOT = [
    "server.js",
    "public/index.html",
    "public/style.css",
    "public/app.js",
    "runtime/node.exe",
    "tools/selftest.js",
    "start-admin.cmd",
    "README.md",
]

# 首次安装附带的初始配置（升级安装不覆盖 data）
DATA_INIT = [
    "data/config.json",
    "data/subscriptions.json",
    "data/profiles/CrossWall__克洛斯_.yaml",
    "data/profiles/Kitty_Network.yaml",
    "data/profiles/REYl7gyOdqS4.yaml",
]

# 对外分享用的干净包：只带默认设置，不含任何订阅与节点配置
DATA_CLEAN = ["data/config.json"]


def log(msg):
    print("  " + msg, flush=True)


def clean_env():
    return {k: v for k, v in os.environ.items()
            if k.lower() not in ("http_proxy", "https_proxy", "all_proxy")}


def reset_dir(p):
    if os.path.isdir(p):
        shutil.rmtree(p)
    os.makedirs(p, exist_ok=True)


def dotnet(args, cwd, want):
    """跑 dotnet 并校验期望产物存在。"""
    r = subprocess.run([DOTNET] + args, cwd=cwd, env=clean_env(),
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (r.stdout or "") + (r.stderr or "")
    for line in out.strip().splitlines():
        if "error" in line.lower() or "错误" in line:
            print("  " + line.strip())
    if r.returncode != 0 or not os.path.isfile(want):
        print(out[-2500:])
        sys.exit(1)
    return want


# ---------------------------------------------------------------- 编译各组件

def build_sysproxy():
    """net48 控制台小工具：几 KB，零依赖。"""
    proj = os.path.join(ROOT, "src", "SysProxy")
    out = os.path.join(BUILD, "sysproxy")
    reset_dir(out)
    exe = dotnet(["build", "-c", "Release", "-o", out, "-v", "q", "--nologo"],
                 proj, os.path.join(out, "sysproxy.exe"))
    log(f"系统代理工具 {os.path.getsize(exe) / 1024:.0f} KB（net48，零依赖）")
    return exe


def sync_app_version():
    """
    把 VERSION 写进**三个** csproj：主程序、安装器、卸载器。

    以前只同步 Desktop.csproj，结果主程序 2.6.1 而安装器还停在 2.0.1 ——
    用户在安装包属性里看到的是 2.0.1，和软件里显示的版本对不上。
    "每处版本都要对得上"这条判据，靠的就是这里一次写全，而不是靠人记。
    """
    targets = [
        os.path.join(ROOT, "src", "Desktop", "Desktop.csproj"),
        os.path.join(ROOT, "src", "Setup", "Setup.csproj"),
        os.path.join(ROOT, "src", "Uninstaller", "Uninstaller.csproj"),
    ]
    for p in targets:
        if not os.path.exists(p):
            continue
        s = open(p, encoding="utf-8").read()
        before = s
        s = re.sub(r"<Version>[^<]*</Version>", "<Version>%s</Version>" % VERSION, s)
        s = re.sub(r"<FileVersion>[^<]*</FileVersion>", "<FileVersion>%s.0</FileVersion>" % VERSION, s)
        if s != before:
            open(p, "w", encoding="utf-8").write(s)
            log("版本同步 → %s (%s)" % (VERSION, os.path.basename(p)))

    # Shared.cs 的 AppVersion 是注册表 DisplayVersion 的唯一来源，
    # 也就是「设置 → 应用」里显示的那一行。它不在任何 csproj 里，
    # 漏同步的表现是：程序集 FileVersion 正确，但设置里永远显示旧版本号（实测停在 2.0.1）。
    shared = os.path.join(ROOT, "src", "Shared", "Shared.cs")
    if os.path.exists(shared):
        s = open(shared, encoding="utf-8").read()
        patched = re.sub(r'(public const string AppVersion = ")[^"]*(")',
                         lambda m: m.group(1) + VERSION + m.group(2), s)
        if patched != s:
            open(shared, "w", encoding="utf-8").write(patched)
            log("版本同步 → %s (Shared.cs AppVersion)" % VERSION)


def publish_app():
    """
    net48 WPF 原生外壳：产物是 ~200 KB 的单文件 exe。
    运行时用系统自带的 .NET Framework 4.8（Win10 1903+ / Win11 都有），
    不打包运行时、不依赖 WebView2 —— 这就是这一版换成原生界面的意义。
    """
    sync_app_version()
    proj = os.path.join(ROOT, "src", "Desktop")
    out = os.path.join(BUILD, "app")
    reset_dir(out)
    exe = dotnet(["build", "-c", "Release", "-o", out, "-v", "q", "--nologo"],
                 proj, os.path.join(out, "ClashDeck.exe"))
    log(f"主程序 {os.path.getsize(exe) / 1024:.0f} KB（net48 WPF，无运行时打包、无 WebView2）")
    return exe


def publish_uninstaller():
    proj = os.path.join(ROOT, "src", "Uninstaller")
    out = os.path.join(BUILD, "uninstaller")
    reset_dir(out)
    exe = dotnet(["publish", "-c", "Release", "-o", out, "-v", "q", "--nologo"],
                 proj, os.path.join(out, "uninstall.exe"))
    log(f"卸载器 {os.path.getsize(exe) / 1024:.0f} KB（net48）")
    return exe


def publish_setup():
    proj = os.path.join(ROOT, "src", "Setup")
    out = os.path.join(BUILD, "publish")
    reset_dir(out)
    exe = dotnet(["publish", "-c", "Release", "-o", out, "-v", "q", "--nologo"],
                 proj, os.path.join(out, "ClashDeckSetup.exe"))
    # 载荷 zip 已经被嵌进安装器 exe 了，源码目录里这份是纯中间产物（70 MB），
    # 留着只会让 src/ 看起来莫名其妙地大，而且每次打包都重新生成。
    staged = os.path.join(proj, "payload.zip")
    try:
        if os.path.isfile(staged):
            os.remove(staged)
    except OSError:
        pass
    return exe


# ---------------------------------------------------------------- 载荷

def add_file(src, rel, total):
    dst = os.path.join(STAGE, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(dst) or STAGE, exist_ok=True)
    shutil.copy2(src, dst)
    return os.path.getsize(dst)


def stage_payload(app_exe, sysproxy_exe, un_exe, clean=False):
    print("[1/6] 组装载荷" + ("（干净包：不含订阅配置）" if clean else ""))
    reset_dir(STAGE)
    total = 0
    count = 0

    # 编译产物
    for src, rel in [(app_exe, "ClashDeck.exe"),
                     (sysproxy_exe, "tools/sysproxy.exe"),
                     (un_exe, "uninstall.exe")]:
        total += add_file(src, rel, total)
        count += 1

    # 仓库内文件
    for rel in PAYLOAD_FROM_ROOT:
        src = os.path.join(ROOT, rel.replace("/", os.sep))
        if not os.path.isfile(src):
            print("  !! 缺少文件: " + rel)
            sys.exit(1)
        total += add_file(src, rel, total)
        count += 1

    # 初始配置
    extra = DATA_CLEAN if clean else DATA_INIT
    for rel in extra:
        src = os.path.join(ROOT, rel.replace("/", os.sep))
        if not os.path.isfile(src):
            print("  !! 缺少文件: " + rel)
            sys.exit(1)
        total += add_file(src, rel, total)
        count += 1

    # 内核 + geo 数据
    core = os.path.join(CORE_ROOT, CORE_EXE)
    if not os.path.isfile(core):
        print(f"  !! 找不到内核: {core}")
        print("     内核随安装包分发，构建前需要先有它（默认取 Clash Verge 安装目录）")
        sys.exit(1)
    total += add_file(core, "core/" + CORE_EXE, total)
    count += 1
    log(f"内核 {CORE_EXE} {os.path.getsize(core) / 1024 / 1024:.1f} MB")
    for f in CORE_RES:
        src = os.path.join(CORE_ROOT, "resources", f)
        if not os.path.isfile(src):
            print(f"  !! 缺少 geo 数据: {src}")
            sys.exit(1)
        total += add_file(src, "core/resources/" + f, total)
        count += 1
    log(f"geo 数据 {len(CORE_RES)} 个文件（离线可用，不依赖首次联网下载）")

    # 增强分流规则集（本地 mrs）：不联网、不依赖 GitHub 可达性
    rules_dir = os.path.join(ROOT, "rules")
    if os.path.isdir(rules_dir):
        n = 0
        for f in sorted(os.listdir(rules_dir)):
            if not f.endswith(".mrs"):
                continue
            total += add_file(os.path.join(rules_dir, f), "rules/" + f, total)
            n += 1
        log(f"分流规则集 {n} 个文件（本地 mrs，离线加载）")
    else:
        print("  !! 缺少 rules/ 目录 —— 增强分流将不可用")

    # 版本号文件：应用内更新靠它判断"我是哪一版"，与安装包文件名同源
    ver_file = os.path.join(STAGE, "version.txt")
    with open(ver_file, "w", encoding="utf-8") as f:
        f.write(VERSION + "\n")

    # 初始 config：路径交给程序按安装目录解析
    cfg_path = os.path.join(STAGE, "data", "config.json")
    with open(cfg_path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg.pop("sysProxyPath", None)
    cfg["corePath"] = ""        # 空 -> 程序回退到 core\verge-mihomo.exe
    cfg["geoSourceDir"] = ""    # 空 -> 程序回退到 core\resources
    if clean:
        cfg["activeProfile"] = ""
    with open(cfg_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)

    log(f"{count} 个文件，{total / 1024 / 1024:.1f} MB")
    return total


def make_zip():
    print("[2/6] 压缩载荷")
    zpath = os.path.join(BUILD, "payload.zip")
    if os.path.exists(zpath):
        os.remove(zpath)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for base, _, files in os.walk(STAGE):
            for fn in files:
                full = os.path.join(base, fn)
                rel = os.path.relpath(full, STAGE).replace(os.sep, "/")
                z.write(full, rel)
    size = os.path.getsize(zpath)
    log(f"payload.zip {size / 1024 / 1024:.1f} MB")
    shutil.copy2(zpath, os.path.join(ROOT, "src", "Setup", "payload.zip"))
    return size


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def sign(path):
    """自签证书签名。时间戳服务器不可达就退成不带时间戳，签名本身仍然有效。"""
    if not os.path.isfile(SIGNTOOL):
        log("!! 找不到 signtool，本次未签名：%s" % SIGNTOOL)
        return False
    base = [SIGNTOOL, "sign", "/sha1", CERT_SHA1, "/fd", "sha256"]
    for extra in (["/tr", "http://timestamp.digicert.com", "/td", "sha256"], []):
        r = subprocess.run(base + extra + [path], env=clean_env(),
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode == 0:
            log("已签名（%s）" % ("含时间戳" if extra else "无时间戳，时间服务器不可达"))
            return True
    log("!! 签名失败，仍出包：%s" % ((r.stdout or "") + (r.stderr or "")).strip()[:300])
    return False


def ship(exe, clean=False):
    print("[6/6] 签名 → 命名 → 发布目录 + 清单")
    signed = sign(exe)
    digest = sha256_of(exe)
    h8 = digest[:8]
    name = "ClashDeck-Setup-%s-%s%s.exe" % (VERSION, h8, "-clean" if clean else "")
    size = os.path.getsize(exe)

    # 干净包进发布目录（带清单）；完整版只留在 dist\，属于本机自用，不对外交付
    outdir = RELEASE if clean else os.path.join(ROOT, "dist")
    reset_dir(outdir)
    dst = os.path.join(outdir, name)
    shutil.copy2(exe, dst)
    if sha256_of(dst) != digest:
        print("  !! 拷贝后哈希不一致：%s" % dst)
        sys.exit(1)

    # notes 只放用户看得懂的更新要点：页面"更新要点"区逐字取这几条（契约 §四-3）。
    # 签名状态正常时不写进来——它不是更新内容；只有未签名才提示，因为用户装的时候会看到弹窗。
    notes = list(NOTES)
    if not signed:
        notes.append("本次未签名：安装时 Windows 会提示未知发布者")
    manifest = {
        "latest": VERSION,
        "file": name,
        "size": size,
        "sha256": digest,
        "url": "%s/%s?ticket=%s" % (SHARE_HOST, name, SHARE_CODE),
        "published": datetime.date.today().isoformat(),
        "notes": notes,
    }
    mpath = os.path.join(outdir, "desktop-version.json")
    if clean:
        with open(mpath, "w", encoding="utf-8", newline="\n") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=2)
            f.write("\n")
        sync_share(dst, mpath, name)

    cfg = exe + ".config"
    if os.path.isfile(cfg):
        log("（.exe.config 未随包分发，实测非必需）")
    log(dst)
    log("%s  %d B（%.1f MB）  sha256=%s…" % (name, size, size / 1048576.0, digest[:12]))
    log("清单 %s（%d 条 notes）" % (mpath, len(notes)))
    return dst, size, manifest


def sync_page(m):
    """把页面里必须与清单逐字一致的项就地改掉。

    为什么让脚本改源码：这七处每版都会变，靠人记必然漂移；实测已漂过一次
    （清单 4 条要点 / 页面 2 条）。改完由 sync_page 自己核一遍，不匹配就退出。
    只碰固定锚点，不碰版式与文案结构；改不到就报错，不静默跳过。
    """
    p = os.path.join(SHARE_DIR, "index.html")
    if not os.path.isfile(p):
        log("部署目录无 index.html，跳过页面同步")
        return
    s = open(p, encoding="utf-8").read()
    name, sha, ver = m["file"], m["sha256"], m["latest"]
    mb = "%.2f MB" % (m["size"] / 1048576.0)

    # #dl 整个标签：data-file 与静态 href 必须同源，JS 只负责补 ticket
    tag = re.search(r'<a[^>]*id="dl"[^>]*>', s)
    if not tag:
        print("  !! 页面里找不到 id=dl，停止")
        sys.exit(1)
    old = tag.group(0)
    new = re.sub(r'data-file="[^"]*"', 'data-file="%s"' % name, old)
    if 'href=' in new:
        new = re.sub(r'href="[^"]*"', 'href="%s?ticket=%s"' % (name, SHARE_CODE), new)
    else:
        new = new.replace(" download", ' href="%s?ticket=%s" download' % (name, SHARE_CODE))
    s = s.replace(old, new)

    s = re.sub(r'(id="fname">)[^<]*(<)', lambda mo: mo.group(1) + name + mo.group(2), s)
    # noscript 里的包名也是 §2.3 要求同步的一处，漏了会在自核里被"残留别的包名"抓住
    s = re.sub(r'(id="nofile">)[^<]*(<)', lambda mo: mo.group(1) + name + mo.group(2), s)
    s = re.sub(r'(id="sha">)[^<]*(<)', lambda mo: mo.group(1) + sha + mo.group(2), s)
    s = re.sub(r'(id="fmeta">)[^<]*(<)', lambda mo: mo.group(1) + mb + " · 单文件 · 离线安装 · 免管理员" + mo.group(2), s)
    s = re.sub(r'(id="ver"[^>]*>v)[\d.]+(<)', lambda mo: mo.group(1) + ver + mo.group(2), s)
    s = re.sub(r'(<title>[^<]*?v)[\d.]+( · 下载</title>)', lambda mo: mo.group(1) + ver + mo.group(2), s)
    s = re.sub(r"(STATIC_VERSION = ')[^']*(')", lambda mo: mo.group(1) + ver + mo.group(2), s)

    # 更新要点：整块重写为清单 notes，保证静态兜底与云端 highlights 同一份文案
    rows = "".join('      <div class="step"><div><b>%s</b></div></div>\n' % n for n in m["notes"])
    s2, cnt = re.subn(r'(<div class="steps" id="changes">)\n.*?\n    </div>',
                      lambda mo: mo.group(1) + "\n" + rows.rstrip("\n") + "\n    </div>", s, flags=re.S)
    if cnt != 1:
        print("  !! 页面 changes 块匹配 %d 处（应为 1），停止" % cnt)
        sys.exit(1)
    s = s2

    open(p, "w", encoding="utf-8", newline="\n").write(s)

    # 自核：七项必须都在，且页面里只剩本次包名
    for label, ok in [
        ("文件名", s.count(name) >= 2),
        ("SHA256", sha in s),
        ("STATIC_VERSION", "STATIC_VERSION = '%s'" % ver in s),
        ("标题", ("v%s · 下载" % ver) in s),
        ("要点", all(n in s for n in m["notes"])),
    ]:
        if not ok:
            print("  !! 页面同步后核对失败：%s" % label)
            sys.exit(1)
    others = set(re.findall(r'ClashDeck-Setup-[\d.]+-[0-9a-f]{8}-clean\.exe', s)) - {name}
    if others:
        print("  !! 页面残留别的包名：%s" % "、".join(sorted(others)))
        sys.exit(1)
    log("页面已按清单同步（文件名 %d 处 / 要点 %d 条）" % (s.count(name), len(m["notes"])))


def sync_share(dst, mpath, name):
    """把本次包与清单同步进部署目录，并清掉上一版 exe。

    为什么不用 reset_dir：share\\ 里有部署平台写的 .wbapp_*.genie 与 .workbuddy\\，
    清空目录会把它们删掉，导致应用卡与上次发布记录一起丢。
    只按后缀删旧安装包，页面与证书不动。
    """
    print("[6.5/7] 同步部署目录")
    if not os.path.isdir(SHARE_DIR):
        os.makedirs(SHARE_DIR, exist_ok=True)
    removed = []
    for fn in os.listdir(SHARE_DIR):
        if fn.lower().endswith(".exe"):
            os.remove(os.path.join(SHARE_DIR, fn))
            removed.append(fn)
    if removed:
        log("清掉上一版安装包 %d 个：%s" % (len(removed), "、".join(removed)))
    shutil.copy2(dst, os.path.join(SHARE_DIR, name))
    shutil.copy2(mpath, os.path.join(SHARE_DIR, "desktop-version.json"))
    if sha256_of(os.path.join(SHARE_DIR, name)) != sha256_of(dst):
        print("  !! 部署目录内哈希与产物不一致，停止")
        sys.exit(1)
    log("%s\\%s + desktop-version.json" % (SHARE_DIR, name))
    sync_page(json.load(open(mpath, encoding="utf-8")))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clean", action="store_true", help="出干净包（不含订阅配置），用于对外分享")
    a = ap.parse_args()
    t0 = time.time()

    print("[0/6] 编译组件")
    app_exe = publish_app()
    sysproxy_exe = build_sysproxy()
    un_exe = publish_uninstaller()

    stage_payload(app_exe, sysproxy_exe, un_exe, clean=a.clean)
    make_zip()

    print("[3/6] 编译安装程序（net48）")
    setup_exe = publish_setup()
    log(f"安装器 {os.path.getsize(setup_exe) / 1024:.0f} KB")

    dst, size, manifest = ship(setup_exe, clean=a.clean)
    print(f"完成，用时 {time.time() - t0:.1f}s")
    if a.clean:
        # 操作说明 §2.6 四条：产物目录、部署目录、清单可解析、server.js 语法。
        # 四条全 EXIT=0 才算交付物，缺一条就整体失败。
        print("[7/7] 四条自检（任一非零即打包失败）")
        if not os.path.isfile(CHECK):
            print("  !! 找不到自检脚本：%s" % CHECK)
            sys.exit(1)
        steps = [
            ("产物目录", [sys.executable, CHECK, RELEASE]),
            ("部署目录", [sys.executable, CHECK, SHARE_DIR]),
            ("清单可解析", [sys.executable, "-m", "json.tool",
                        os.path.join(SHARE_DIR, "desktop-version.json")]),
            ("server.js 语法", [os.path.join(ROOT, "runtime", "node.exe"),
                              "--check", os.path.join(ROOT, "server.js")]),
        ]
        for label, cmd in steps:
            r = subprocess.run(cmd, env=clean_env(), capture_output=True, text=True,
                               encoding="utf-8", errors="replace")
            if label == "产物目录" or label == "部署目录":
                print((r.stdout or "").rstrip())
            if r.returncode != 0:
                print("  !! %s 自检未通过（EXIT=%d）" % (label, r.returncode))
                print(((r.stdout or "") + (r.stderr or "")).strip()[-800:])
                sys.exit(1)
            print("  PASS %s" % label)
        print("下一步（操作说明，非本脚本）：")
        print("  用 sites 部署 %s，再把 releases 表按 version=%s 更新三字段"
              % (SHARE_DIR, manifest["latest"]))


if __name__ == "__main__":
    main()
