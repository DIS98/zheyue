#!/usr/bin/env python3
"""本地升级源（仅用于测试，不参与分发）。

刻意复刻线上站点的两个真实约束，否则测不出真问题：
1. **每个请求都必须带 ?ticket=**，不带返回 403 —— 线上站点就是这样（实测）
2. **支持 Range 请求** —— 断点续传要靠它；线上是否支持见开发记录

用法：
    python tools/test-server.py <目录> [port] [ticket]
"""
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 18080
CODE = sys.argv[3] if len(sys.argv) > 3 else "TESTCODE"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("[test-server] " + (fmt % args) + "\n")

    def do_GET(self):
        path, _, query = self.path.partition("?")
        if not re.search(r"(^|&)ticket=" + re.escape(CODE) + r"(&|$)", query):
            self.send_error(403, "missing ticket")
            return

        name = "version.json" if path in ("/", "/version.json") else path.lstrip("/")
        target = os.path.join(ROOT, name)
        if not os.path.isfile(target):
            self.send_error(404, "not found")
            return

        size = os.path.getsize(target)
        start, end = 0, size - 1
        rng = self.headers.get("Range")
        if rng:
            self.log_message("Range 请求: %s -> %s", rng, name)
        partial = False
        if rng and rng.startswith("bytes="):
            m = re.match(r"bytes=(\d+)-(\d*)$", rng)
            if m:
                start = int(m.group(1))
                if m.group(2):
                    end = min(int(m.group(2)), size - 1)
                if start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", "bytes */%d" % size)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                partial = True

        length = end - start + 1
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", "application/json" if name.endswith(".json") else "application/vnd.android.package-archive")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if partial:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()

        with open(target, "rb") as f:
            f.seek(start)
            left = length
            while left > 0:
                chunk = f.read(min(256 * 1024, left))
                if not chunk:
                    break
                self.wfile.write(chunk)
                left -= len(chunk)


if __name__ == "__main__":
    print("serving %s on :%d (ticket=%s)" % (ROOT, PORT, CODE))
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
