#!/usr/bin/env python3
"""建一个隔离沙盒：独立目录 + 独立端口 + 独立 data，用来改代码和做验证。

为什么需要它：
  用户一直挂着运行 ./
  而 ./ 里的 data/ 一旦起后端就会写日志、写 config、
  还会抢占 8787/7897/9097 这些端口，和用户实例互相覆盖。
  所以：改代码在源码目录，**跑起来验证必须在沙盒**。

用法：
  python tools/mk-sandbox.py --name tg-fix          # 建沙盒
  python tools/mk-sandbox.py --name tg-fix --keep   # 已存在则复用
  python tools/mk-sandbox.py --clean                # 删掉所有沙盒

产出：
  ./
  ├── server.js          （复制，改它不影响源码）
  ├── core/ runtime/     （硬链接，省磁盘）
  ├── rules/*.mrs        （硬链接）
  └── data/config.json   （独立端口，自动挑空闲）
      data/profiles/sb.yaml    最小订阅：一个假节点 + MATCH
      data/custom-rules.json   空

输出末尾会打印可直接执行的启动命令与端口号。
"""
import argparse
import json
import os
import shutil
import socket
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SANDBOX_ROOT = os.path.join(ROOT, ".sandbox")


def port_free(port):
    """真实探测：能不能 bind。不要用'看起来没被占用'这种推断当判据。"""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def pick_ports(count=5, start=48700):
    """从 start 往上找连续可用的端口，避免和用户实例（8787/7897/9097）撞车。"""
    port = start
    found = []
    while port < 65000 and len(found) < count:
        if port_free(port):
            found.append(port)
        else:
            found = []
        port += 1
    if len(found) < count:
        sys.exit("找不到 %d 个连续空闲端口" % count)
    return found


def link_or_copy(src, dst):
    """同分区用硬链接（core/ 98MB，复制会让每次建沙盒都等半分钟）。"""
    if os.path.exists(dst):
        return
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def link_tree(src, dst):
    if not os.path.isdir(src):
        return 0
    n = 0
    for dirpath, _dirnames, filenames in os.walk(src):
        rel = os.path.relpath(dirpath, src)
        target_dir = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(target_dir, exist_ok=True)
        for f in filenames:
            link_or_copy(os.path.join(dirpath, f), os.path.join(target_dir, f))
            n += 1
    return n


def build(name, keep=False):
    os.makedirs(SANDBOX_ROOT, exist_ok=True)
    stamp = time.strftime("%m%d-%H%M")
    path = os.path.join(SANDBOX_ROOT, "%s-%s" % (name, stamp))

    if os.path.isdir(path) and not keep:
        sys.exit("沙盒已存在：%s（加 --keep 复用，或换个 --name）" % path)
    os.makedirs(path, exist_ok=True)

    # 1) 代码与运行时
    src_server = os.path.join(ROOT, "server.js")
    if not os.path.exists(src_server):
        sys.exit("找不到 server.js：%s" % src_server)
    shutil.copy2(src_server, os.path.join(path, "server.js"))

    linked = 0
    for d in ("core", "runtime"):
        linked += link_tree(os.path.join(ROOT, d), os.path.join(path, d))
    rules_src = os.path.join(ROOT, "rules")
    os.makedirs(os.path.join(path, "rules"), exist_ok=True)
    if os.path.isdir(rules_src):
        for f in os.listdir(rules_src):
            if f.endswith(".mrs"):
                link_or_copy(os.path.join(rules_src, f), os.path.join(path, "rules", f))
                linked += 1

    # 2) 独立 data
    for d in ("data/runtime", "data/profiles", "data/logs"):
        os.makedirs(os.path.join(path, d), exist_ok=True)

    web, mixed, socks, http, ctrl = pick_ports(5)
    # 假节点的端口要单独挑，不能落在上面这五个里 —— 否则会撞上 socks 或 http 端口
    fake = pick_ports(1, start=49700)[0]
    cfg = {
        "webPort": web, "mixedPort": mixed, "socksPort": socks, "httpPort": http,
        "controllerPort": ctrl, "secret": "sandbox",
        "enhancedRules": True, "proxyMode": "smart",
        "logConn": False, "logToFile": True,
        "autoStartCore": False, "autoStartProxy": False,
        "activeProfile": "sb.yaml",
    }
    with open(os.path.join(path, "data", "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)

    # 3) 最小订阅：节点指向一个假 HTTP 代理（走代理必失败，直连才通）
    #    这样"是否走代理"不用推断，看连通性就知道。
    with open(os.path.join(path, "data", "profiles", "sb.yaml"), "w", encoding="utf-8") as f:
        f.write(
            "proxies:\n"
            '  - {name: "沙盒节点", type: http, server: 127.0.0.1, port: %d}\n'
            "proxy-groups:\n"
            '  - {name: "节点选择", type: select, proxies: ["沙盒节点", "DIRECT"]}\n'
            "rules:\n"
            "  - MATCH,节点选择\n" % fake
        )
    with open(os.path.join(path, "data", "custom-rules.json"), "w", encoding="utf-8") as f:
        json.dump([], f)

    print("沙盒已建：%s" % path)
    print("  硬链接/复制文件 %d 个（core 与 rules 不占额外磁盘）" % linked)
    print("  端口 web=%d mixed=%d socks=%d http=%d controller=%d" % (web, mixed, socks, http, ctrl))
    print("  启动：cd \"%s\" && runtime\\node.exe server.js --no-browser" % path)
    print("  假节点：127.0.0.1:%d（无服务，走代理必失败、直连才通，用它判定分流结果）" % fake)
    return path


def clean():
    if not os.path.isdir(SANDBOX_ROOT):
        print("没有沙盒目录，无需清理")
        return
    n = 0
    for d in sorted(os.listdir(SANDBOX_ROOT)):
        p = os.path.join(SANDBOX_ROOT, d)
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
            n += 1
    print("已删除 %d 个沙盒" % n)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="sb", help="任务名，用于目录命名")
    ap.add_argument("--keep", action="store_true", help="同名沙盒已存在时复用")
    ap.add_argument("--clean", action="store_true", help="删除所有沙盒")
    a = ap.parse_args()
    if a.clean:
        clean()
    else:
        build(a.name, a.keep)
