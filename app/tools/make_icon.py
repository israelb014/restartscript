"""Generates app/src/ScheduledRestart/Assets/app.ico (a circular restart arrow) with no dependencies.

Run: python3 app/tools/make_icon.py
"""
import math
import os
import struct
import zlib

BG = (18, 22, 30)          # dark rounded square
ACCENT = (61, 139, 253)    # same accent as the app theme (#3D8BFD)
SIZES = [16, 24, 32, 48, 64, 256]
SUPERSAMPLE = 4

R = 0.27                   # ring radius (unit square)
T = 0.095                  # ring thickness
ARC_START = math.radians(55)    # gap between ARC_END and ARC_START (top-right)
ARC_END = math.radians(355)


def in_round_rect(x, y, lo=0.04, hi=0.96, rad=0.22):
    if x < lo or x > hi or y < lo or y > hi:
        return False
    cx = min(max(x, lo + rad), hi - rad)
    cy = min(max(y, lo + rad), hi - rad)
    return (x - cx) ** 2 + (y - cy) ** 2 <= rad * rad


def angle_of(x, y):
    a = math.atan2(0.5 - y, x - 0.5)  # math angle, y up
    return a % (2 * math.pi)


def in_ring(x, y):
    d = math.hypot(x - 0.5, y - 0.5)
    if abs(d - R) > T / 2:
        return False
    a = angle_of(x, y)
    return ARC_START <= a <= ARC_END


def arrow_triangle():
    # Arrowhead at ARC_START, pointing along the direction of decreasing angle (clockwise).
    a = ARC_START
    px, py = 0.5 + R * math.cos(a), 0.5 - R * math.sin(a)
    # tangent for clockwise motion (screen coordinates)
    tx, ty = math.sin(a), math.cos(a)
    nx, ny = math.cos(a), -math.sin(a)  # outward normal
    w = T * 1.25
    tip = (px + tx * T * 1.6, py + ty * T * 1.6)
    b1 = (px + nx * w, py + ny * w)
    b2 = (px - nx * w, py - ny * w)
    return tip, b1, b2


TRI = arrow_triangle()


def in_triangle(x, y, tri=TRI):
    (x1, y1), (x2, y2), (x3, y3) = tri

    def sign(ax, ay, bx, by, cx, cy):
        return (ax - cx) * (by - cy) - (bx - cx) * (ay - cy)

    d1 = sign(x, y, x1, y1, x2, y2)
    d2 = sign(x, y, x2, y2, x3, y3)
    d3 = sign(x, y, x3, y3, x1, y1)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def render(size):
    """Returns rows (top-down) of RGBA tuples."""
    rows = []
    n = SUPERSAMPLE
    for py in range(size):
        row = []
        for px in range(size):
            bg = fg = 0
            for sy in range(n):
                for sx in range(n):
                    x = (px + (sx + 0.5) / n) / size
                    y = (py + (sy + 0.5) / n) / size
                    if in_round_rect(x, y):
                        bg += 1
                        if in_ring(x, y) or in_triangle(x, y):
                            fg += 1
            total = n * n
            alpha = bg / total
            if bg == 0:
                row.append((0, 0, 0, 0))
                continue
            f = fg / bg
            r = round(BG[0] * (1 - f) + ACCENT[0] * f)
            g = round(BG[1] * (1 - f) + ACCENT[1] * f)
            b = round(BG[2] * (1 - f) + ACCENT[2] * f)
            row.append((r, g, b, round(alpha * 255)))
        rows.append(row)
    return rows


def dib(size, rows):
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b''.join(bytes((b, g, r, a)) for row in reversed(rows) for (r, g, b, a) in row)
    mask_row = ((size + 31) // 32) * 4
    mask = b'\x00' * mask_row * size
    return header + pixels + mask


def png(size, rows):
    raw = b''.join(b'\x00' + b''.join(bytes(p) for p in row) for row in rows)

    def chunk(kind, data):
        c = kind + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')


def main():
    images = []
    for s in SIZES:
        rows = render(s)
        images.append((s, png(s, rows) if s >= 256 else dib(s, rows)))
    out = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    data = b''
    for s, blob in images:
        dim = 0 if s >= 256 else s
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    target = os.path.join(os.path.dirname(__file__), '..', 'src', 'ScheduledRestart', 'Assets', 'app.ico')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'wb') as f:
        f.write(out + entries + data)
    print('wrote', os.path.normpath(target), len(out + entries + data), 'bytes')


if __name__ == '__main__':
    main()
