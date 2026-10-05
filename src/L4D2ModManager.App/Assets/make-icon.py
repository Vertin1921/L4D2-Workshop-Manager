"""生成程序图标 Assets/app.ico（无第三方依赖，直接写出 ICO 结构）。

设计：黑色圆角方块 + 暗红描边 + 中央红十字（L4D2 医疗包意象）+ 左上高光。
"""
import os
import struct

SIZES = [256, 128, 64, 48, 32, 24, 16]

BG_TOP = (26, 26, 30)
BG_BOTTOM = (14, 14, 17)
BORDER = (150, 26, 32)
CROSS = (208, 46, 52)
CROSS_HI = (236, 96, 96)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def render(size):
    """返回 BGRA 像素（自下而上，BMP 约定）。"""
    px = [[(0, 0, 0, 0) for _ in range(size)] for _ in range(size)]
    radius = max(2.0, size * 0.22)
    border = max(1.0, size * 0.055)
    # 十字尺寸
    arm_len = size * 0.56
    arm_thick = size * 0.18
    center = (size - 1) / 2.0

    for y in range(size):
        for x in range(size):
            # 圆角矩形遮罩（用距离判定）
            dx = max(radius - x, x - (size - 1 - radius), 0)
            dy = max(radius - y, y - (size - 1 - radius), 0)
            dist = (dx * dx + dy * dy) ** 0.5
            if dist > radius:
                continue
            edge = min(1.0, max(0.0, radius - dist))
            alpha = int(255 * min(1.0, edge + 0.25))

            # 背景渐变
            color = lerp(BG_TOP, BG_BOTTOM, y / max(1, size - 1))

            # 边框
            inset = dist > radius - border if radius > border else False
            if inset:
                color = BORDER

            # 红十字
            in_cross = (abs(x - center) <= arm_thick / 2 and abs(y - center) <= arm_len / 2) or \
                       (abs(y - center) <= arm_thick / 2 and abs(x - center) <= arm_len / 2)
            if in_cross:
                t = (x + y) / max(1.0, 2 * (size - 1))
                color = lerp(CROSS_HI, CROSS, t)

            px[y][x] = (color[2], color[1], color[0], alpha)  # BGRA

    # BMP 要求自下而上
    rows = []
    for y in range(size - 1, -1, -1):
        rows.extend(px[y])
    return rows


def bmp_entry(size):
    pixels = render(size)
    # BITMAPINFOHEADER：高度为 2 倍（包含 AND 掩码）
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0,
                         size * size * 4, 0, 0, 0, 0)
    data = bytearray(header)
    for b, g, r, a in pixels:
        data += bytes((b, g, r, a))
    # AND 掩码（32 位图仍需要，按行 4 字节对齐，全 0 即可）
    mask_row = ((size + 31) // 32) * 4
    data += bytes(mask_row * size)
    return bytes(data)


def main():
    entries = [(size, bmp_entry(size)) for size in SIZES]
    out = bytearray()
    out += struct.pack('<HHH', 0, 1, len(entries))

    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    for _, data in entries:
        out += data

    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'app.ico')
    with open(target, 'wb') as handle:
        handle.write(out)
    print('written %s (%d bytes, %d sizes)' % (target, len(out), len(entries)))


if __name__ == '__main__':
    main()
