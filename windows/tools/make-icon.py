"""生成 ClashDeck 图标（多尺寸 ICO，全部内嵌 PNG）。
图形：圆角方块渐变底 + 白色"分流"符号（一进两出），呼应代理分流。
纯标准库实现，无需 Pillow。
"""
import io
import math
import os
import struct
import zlib

OUT = r"./"

# ---------- 基础工具 ----------

def lerp(a, b, t):
    return a + (b - a) * t


def mix(c1, c2, t):
    return tuple(lerp(c1[i], c2[i], t) for i in range(3))


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    L2 = vx * vx + vy * vy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / L2))
    cx, cy = ax + t * vx, ay + t * vy
    return math.hypot(px - cx, py - cy)


def in_round_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    # 四角圆判断
    for cx, cy in ((x0 + r, y0 + r), (x1 - r, y0 + r), (x0 + r, y1 - r), (x1 - r, y1 - r)):
        pass
    if x0 + r <= x <= x1 - r or y0 + r <= y <= y1 - r:
        return True
    for cx, cy in ((x0 + r, y0 + r), (x1 - r, y0 + r), (x0 + r, y1 - r), (x1 - r, y1 - r)):
        if math.hypot(x - cx, y - cy) <= r:
            return True
    return False


# 24x24 设计稿坐标（放大到任意尺寸）
S = 24.0
BG0, BG1 = (0.30, 0.42, 1.00), (0.55, 0.36, 0.96)   # 蓝 → 紫
NODE = (1.00, 1.00, 1.00)

# 分流符号：主干 (6.5,12) → (11,12)，再分叉到 (17.5,7.2) 与 (17.5,16.8)
STEM = ((6.2, 12.0), (11.0, 12.0))
BR_A = ((11.0, 12.0), (17.6, 7.2))
BR_B = ((11.0, 12.0), (17.6, 16.8))
LINE_W = 1.55
DOTS = [((17.6, 7.2), 1.5), ((17.6, 16.8), 1.5)]
DOT_ROOT = ((6.2, 12.0), 1.85)


def sample(u, v):
    """u,v ∈ [0,24)，返回 (r,g,b,a) 浮点。"""
    x0, y0, x1, y1 = 1.2, 1.2, 22.8, 22.8
    if not in_round_rect(u, v, x0, y0, x1, y1, 5.2):
        return (0, 0, 0, 0)

    t = ((u - x0) / (x1 - x0) * 0.55) + ((v - y0) / (y1 - y0) * 0.45)
    r, g, b = mix(BG0, BG1, max(0.0, min(1.0, t)))

    # 顶部高光，让底不那么平
    hl = max(0.0, 1.0 - (v - y0) / 9.0) * 0.16
    r, g, b = min(1, r + hl), min(1, g + hl), min(1, b + hl)

    # 白色符号
    d = min(seg_dist(u, v, *STEM[0], *STEM[1]),
            seg_dist(u, v, *BR_A[0], *BR_A[1]),
            seg_dist(u, v, *BR_B[0], *BR_B[1]))
    for (cx, cy), rad in DOTS + [DOT_ROOT]:
        d = min(d, math.hypot(u - cx, v - cy) - rad + LINE_W / 2)
    if d <= LINE_W / 2:
        r, g, b = NODE
    return (r, g, b, 1.0)


def render(size, ss=4):
    px = []
    scale = S / size
    for y in range(size):
        row = bytearray()
        for x in range(size):
            ar = ag = ab = aa = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    u = (x + (sx + 0.5) / ss) * scale
                    v = (y + (sy + 0.5) / ss) * scale
                    cr, cg, cb, ca = sample(u, v)
                    ar += cr * ca
                    ag += cg * ca
                    ab += cb * ca
                    aa += ca
            n = ss * ss
            a = aa / n
            if a <= 0.0001:
                row += b"\x00\x00\x00\x00"
            else:
                row += bytes((int(round(ar / n / a * 255)), int(round(ag / n / a * 255)),
                              int(round(ab / n / a * 255)), int(round(a * 255))))
        px.append(bytes(row))
    return px


def png_bytes(size, ss):
    rows = render(size, ss)
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sizes = [(16, 4), (24, 4), (32, 4), (48, 4), (64, 3), (128, 3), (256, 2)]
    blobs = [(s, png_bytes(s, ss)) for s, ss in sizes]

    hdr = struct.pack("<HHH", 0, 1, len(blobs))
    entries, offset = b"", 6 + 16 * len(blobs)
    for s, blob in blobs:
        entries += struct.pack("<BBBBHHII", s if s < 256 else 0, s if s < 256 else 0,
                               0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(OUT, "wb") as f:
        f.write(hdr + entries + b"".join(b for _, b in blobs))
    print("wrote %s (%.1f KB, %d sizes)" % (OUT, os.path.getsize(OUT) / 1024, len(blobs)))


if __name__ == "__main__":
    main()
