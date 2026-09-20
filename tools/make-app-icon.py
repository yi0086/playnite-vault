# -*- coding: utf-8 -*-
"""生成 VaultUnpacker 的图标。

需要 Pillow（用哪个 Python 跑就用哪个装，例如）：

    <你的 python> -m pip install pillow
    <你的 python> tools/make-app-icon.py

产出三样东西：

    tools/assets/vault-unpacker.ico    多尺寸 ICO，给 PyInstaller --icon 用（exe 文件图标）
                                       逐尺寸选格式：≤64px 存 DIB，128/256 存 PNG
    tools/assets/vault-unpacker.png    256px PNG，纯粹给人看/预览
    tools/assets/_preview.png          拼版预览：浅底/深底两行 + 小尺寸放大行
    tools/vault_unpacker/icon.py       内嵌 base64 PNG（48/72/128 三档），给页眉、窗口、任务栏用

为什么 ICO 要手写容器而不是 Pillow 一句 save：
Pillow 的 ICO 存法是把每一档都存成 PNG。Vista 以后系统理论上认 PNG 小图，
但外壳里有一部分代码路径只认 DIB，表现出来就是「exe 的文件图标没生效」。
自己写容器就能逐尺寸选：小图 DIB、大图 PNG，这是兼容性最好的组合。

为什么运行时图标要内嵌 base64 而不是随包带一个 png 文件：
单文件 exe 运行时资源解压在临时目录，路径要兼容「源码直跑」和「冻结后」两种模式，
还依赖 PyInstaller 的 --add-data 配置。直接把 PNG 塞进源码里就完全没有路径问题。

风格：新粗野主义（硬边方块、粗黑描边、粉黄蓝配色），和小方块 + 木箱 + 向下箭头，
表示「把归档取回到本地」。
"""

import base64
import io
import os
import struct
import sys

try:
    from PIL import Image, ImageDraw
except ImportError:
    sys.stderr.write("需要 Pillow：请用带 PIL 的 Python 运行本脚本。\n")
    sys.exit(1)

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "assets")
PKG = os.path.join(HERE, "vault_unpacker")

SIZES = [16, 24, 32, 48, 64, 128, 256]
# 运行时内嵌的几档尺寸：48 普通 DPI，72 高 DPI（页眉那块），128 窗口/任务栏
RUNTIME_SIZES = [48, 72, 128]

# ICO 里小于等于这个尺寸的图必须用 DIB（BMP）存。
# 只有 256×256 用 PNG 才是标准做法（也是微软推荐的）。
# 早先整包 7 档全用 PNG 存，虽然 Vista 以后理论上支持，但外壳的一部分代码路径
# 读 PNG 小图会失败，表现出来就是「exe 的文件图标没生效」——
# 所以这里自己写 ICO 容器，逐尺寸选格式，不再用 Pillow 的默认行为。
DIB_MAX = 64

# ---- 配色（与 GUI 的调色板保持一致）----
INK = (18, 19, 26, 255)          # 描边/字
YELLOW = (255, 212, 71, 255)     # 底板
BLUE = (91, 155, 255, 255)       # 木箱左面
BLUE_D = (59, 123, 224, 255)     # 木箱右面
BLUE_L = (168, 205, 255, 255)    # 木箱顶面
PINK = (255, 123, 169, 255)      # 箭头

SS = 8  # 超采样倍数，缩小时得到干净的抗锯齿


def _poly(d, pts, fill, stroke=0, ink=INK):
    d.polygon(pts, fill=fill)
    if stroke:
        d.line(list(pts) + [pts[0]], fill=ink, width=stroke, joint="curve")


# 木箱三个面（等轴测）—— 大尺寸用它，有立体感
BOX_TOP = ((0.500, 0.290), (0.845, 0.455), (0.500, 0.620), (0.155, 0.455))
BOX_LEFT = ((0.155, 0.455), (0.500, 0.620), (0.500, 0.915), (0.155, 0.750))
BOX_RIGHT = ((0.845, 0.455), (0.500, 0.620), (0.500, 0.915), (0.845, 0.750))
# 小尺寸只留整体轮廓，去掉内部的三面分割线，否则 16px 下糊成一团
BOX_SILHOUETTE = ((0.155, 0.455), (0.500, 0.290), (0.845, 0.455),
                  (0.845, 0.750), (0.500, 0.915), (0.155, 0.750))
# 向下箭头：单条闭合多边形，避免拼接处出现多余描边
ARROW = ((0.425, 0.115), (0.575, 0.115), (0.575, 0.530),
         (0.720, 0.530), (0.500, 0.815), (0.280, 0.530), (0.425, 0.530))


def render(size):
    """画一张 size×size 的 RGBA 图。

    坐标全是 0..1 的比例，天然可缩放；但**线宽必须按最终像素给**（再乘超采样倍数），
    不能也跟着比例缩 —— 否则 16px 时描边不到 1px，整体糊掉。
    """
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def P(pts):
        return [(x * s, y * s) for x, y in pts]

    if size <= 24:
        # 极小尺寸：满出血、1px 线，只保留轮廓
        margin, border, stroke, faces = 0.0, 1 * SS, 1 * SS, (BOX_SILHOUETTE,)
        fills = (BLUE,)
    elif size <= 32:
        margin, border, stroke = 0.012, 2 * SS, 2 * SS
        faces = (BOX_SILHOUETTE,)
        fills = (BLUE,)
    else:
        margin = 0.022
        border = max(2, round(size * 0.045)) * SS
        stroke = max(2, round(size * 0.036)) * SS
        faces = (BOX_LEFT, BOX_RIGHT, BOX_TOP)
        fills = (BLUE, BLUE_D, BLUE_L)

    # 底板：硬边方块 + 粗黑描边（品牌底色，任务栏里也显眼）
    m = margin * s
    d.rectangle([m, m, s - m, s - m], fill=YELLOW, outline=INK, width=border)

    for pts, fill in zip(faces, fills):
        _poly(d, P(pts), fill, stroke)
    _poly(d, P(ARROW), PINK, stroke)

    return img.resize((size, size), Image.LANCZOS)


def preview(imgs, path):
    """拼预览图：浅底一行（真实尺寸）+ 深底一行 + 小尺寸放大行。

    最后那行是放大后的真实像素，用来判断 16/24px 下还认不认得出。
    """
    pad = 14
    row_h = max(i.height for i in imgs) + pad * 2
    w = sum(i.width + pad for i in imgs) + pad

    small = [i for i in imgs if i.width <= 32]
    zoom = 6
    zoom_h = 32 * zoom + pad * 2
    sheet = Image.new("RGB", (w, row_h * 2 + zoom_h), (255, 255, 255))
    d = ImageDraw.Draw(sheet)

    d.rectangle([0, row_h, w, row_h * 2], fill=(24, 26, 33))
    d.rectangle([0, row_h * 2, w, row_h * 2 + zoom_h], fill=(126, 130, 140))

    for y0 in (0, row_h):
        x = pad
        for im in imgs:
            sheet.paste(im, (x, y0 + pad + (row_h - pad * 2 - im.height) // 2), im)
            x += im.width + pad

    x = pad
    y0 = row_h * 2
    for im in small:
        z = im.resize((im.width * zoom, im.height * zoom), Image.NEAREST)
        sheet.paste(z, (x, y0 + pad + (zoom_h - pad * 2 - z.height) // 2), z)
        x += z.width + pad

    sheet.save(path)
    return path


def _b64_of(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def _wrap_b64(png_bytes):
    b64 = base64.b64encode(png_bytes).decode("ascii")
    return "\n".join('    "%s"' % b64[i:i + 76]
                     for i in range(0, len(b64), 76))


# --------------------------------------------------------------------------
# ICO 容器（手写，为了逐尺寸选 DIB / PNG）
# --------------------------------------------------------------------------

def _png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def _bgra_top_down(img):
    px = img.convert("RGBA")
    try:
        return px.tobytes("raw", "BGRA")
    except (ValueError, KeyError):
        # 极老的 Pillow 不认 "BGRA"，自己换通道
        raw = px.tobytes()                      # RGBA
        out = bytearray(raw)
        out[0::4] = raw[2::4]
        out[2::4] = raw[0::4]
        return bytes(out)


def _dib_bytes(img):
    """ICO 里的 DIB 段：BITMAPINFOHEADER + BGRA 位图 + AND 掩码。

    注意三处坑：
      · biHeight 要写 **两倍** 高度（图标 = XOR 位图 + AND 掩码上下叠放）
      · 位图是自底向上存的
      · 32bpp 有 alpha 通道，AND 掩码全 0 就行，但那一块必须存在、且每行按 4 字节对齐
    """
    w, h = img.size
    raw = _bgra_top_down(img)
    stride = w * 4
    rows = [raw[i * stride:(i + 1) * stride] for i in range(h)]
    xor = b"".join(reversed(rows))

    mask_row = ((w + 31) // 32) * 4             # 1bpp，行按 4 字节对齐
    and_mask = b"\x00" * (mask_row * h)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,                                     # biSize
        w,                                      # biWidth
        h * 2,                                  # biHeight（两倍！）
        1,                                      # biPlanes
        32,                                     # biBitCount
        0,                                      # biCompression = BI_RGB
        len(xor) + len(and_mask),               # biSizeImage
        0, 0, 0, 0)                             # 分辨率与调色板
    return header + xor + and_mask


def write_ico(imgs, sizes, path):
    """写一个多尺寸 ICO：<=64px 用 DIB，>64px 用 PNG。"""
    entries = []
    for n in sizes:
        blob = _png_bytes(imgs[n]) if n > DIB_MAX else _dib_bytes(imgs[n])
        entries.append((n, blob))

    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))   # reserved / type=1 / count
    offset = 6 + 16 * len(entries)
    for n, blob in entries:
        dim = 0 if n >= 256 else n                       # 256 在 ICO 里记作 0
        out.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32,
                              len(blob), offset))
        offset += len(blob)
    for _n, blob in entries:
        out.write(blob)

    with open(path, "wb") as f:
        f.write(out.getvalue())
    return path


def describe_ico(path):
    """把 ICO 里每一档的存储格式读出来，构建日志里打出来便于核对。"""
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 6:
        return "（文件太短）"
    _r, _t, count = struct.unpack("<HHH", data[:6])
    out = []
    for i in range(count):
        base = 6 + i * 16
        w, _h, _c, _r2, _p, bits, size, off = struct.unpack(
            "<BBBBHHII", data[base:base + 16])
        dim = 256 if w == 0 else w
        head = data[off:off + 8]
        kind = "PNG" if head[:4] == b"\x89PNG" else "DIB"
        out.append("%d:%s/%dbpp" % (dim, kind, bits))
    return "  ".join(out)


_ICON_PY_HEAD = '''# -*- coding: utf-8 -*-
"""运行时要用的窗口 / 任务栏 / 页眉图标（内嵌 base64 PNG）。

本文件由 tools/make-app-icon.py 自动生成，请勿手改。

为什么内嵌 base64 而不是随包带 png 文件：单文件 exe 运行时资源解压在临时目录，
反正要兼容「源码直跑」和「冻结后」两种路径模式；塞进源码里就完全没有路径问题。
"""

# 尺寸 -> base64 PNG（换行只是为了好看，拼接后仍是合法 base64）
_B64 = {
'''

_ICON_PY_TAIL = '''}

# 窗口 / 任务栏用哪一档
WINDOW_SIZE = 128


def best_size(want):
    """挑一个最接近 want 的现成尺寸（至少给一档，不会返回 None）。"""
    if not want or want <= 0:
        return max(_B64)
    return min(_B64, key=lambda n: (abs(n - want), n))


def load(want=WINDOW_SIZE):
    """返回 tk.PhotoImage。必须在 Tk 根窗口创建之后再调用。"""
    import tkinter as tk
    return tk.PhotoImage(data=_B64[best_size(want)])


def load_bytes(want=WINDOW_SIZE):
    """返回原始 PNG 字节（给需要落盘/预览的场合用）。"""
    import base64
    return base64.b64decode(_B64[best_size(want)])


def available():
    """现成的尺寸列表，升序。"""
    return sorted(_B64)
'''


def write_icon_py(imgs):
    """把各档 PNG 以 base64 写进包内，运行时不需要任何外部文件。

    存好几档尺寸（而不是只存一张大的）是为了避免 Tk 的整数 subsample 缩放：
    在 150% DPI 的屏幕上按 1.5 倍缩放半像素级别的东西，图会糊。
    """
    parts = [_ICON_PY_HEAD]
    for n in RUNTIME_SIZES:
        parts.append("    %d: (\n%s\n    ),\n" % (n, _wrap_b64(_b64_of(imgs[n]))))
    parts.append(_ICON_PY_TAIL)
    src = "".join(parts)
    out = os.path.join(PKG, "icon.py")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(src)
    return out


def main():
    os.makedirs(ASSETS, exist_ok=True)

    imgs = {n: render(n) for n in SIZES}
    # 页眉要用 72px，ICO 里不需要这一档，单独补渲染
    for n in RUNTIME_SIZES:
        imgs.setdefault(n, render(n))

    # 1) 多尺寸 ICO（给 PyInstaller --icon，也就是 exe 的文件图标）
    ico = os.path.join(ASSETS, "vault-unpacker.ico")
    write_ico(imgs, SIZES, ico)
    print("[ico]  %s  %.1f KB" % (ico, os.path.getsize(ico) / 1024.0))
    print("[ico]  逐档格式 %s" % describe_ico(ico))

    # 2) 预览 PNG
    png = os.path.join(ASSETS, "vault-unpacker.png")
    imgs[256].save(png, format="PNG", optimize=True)
    print("[png]  %s  %.1f KB" % (png, os.path.getsize(png) / 1024.0))

    # 3) 运行时图标（内嵌 base64，48 / 72 / 128 三档）
    icon_py = write_icon_py(imgs)
    print("[py]   %s  %.1f KB" % (icon_py, os.path.getsize(icon_py) / 1024.0))

    # 4) 预览拼图，给人看
    sheet = preview([imgs[n] for n in SIZES], os.path.join(ASSETS, "_preview.png"))
    print("[预览] %s" % sheet)


if __name__ == "__main__":
    main()
