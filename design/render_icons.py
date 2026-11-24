"""Fako の改名候補向けアイコン案を描く（プレビュー用）。

各案は 8 倍スーパーサンプリングで描き、LANCZOS で縮小してアンチエイリアスを得る。
出力: design/out/concept_<name>_<size>.png と design/out/sheet.png
"""
from __future__ import annotations

import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
SS = 8  # supersampling

# ---- パレット -------------------------------------------------------------
INK_TOP = (43, 52, 71)
INK_BOT = (18, 23, 34)
BLUE = (140, 196, 255)
BLUE_DEEP = (76, 155, 255)
PAPER_TOP = (251, 245, 233)
PAPER_BOT = (231, 220, 201)
WOOD = (94, 70, 50)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def vgrad(size, top, bot, box, radius):
    """角丸矩形の中だけを縦グラデーションで塗る。"""
    w = int(round(box[2] - box[0]))
    h = int(round(box[3] - box[1]))
    grad = Image.new("RGB", (1, max(h, 1)))
    for y in range(max(h, 1)):
        grad.putpixel((0, y), lerp(top, bot, y / max(h - 1, 1)))
    grad = grad.resize((max(w, 1), max(h, 1)), Image.NEAREST)
    mask = Image.new("L", (max(w, 1), max(h, 1)), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, max(w - 1, 1), max(h - 1, 1)), radius=radius, fill=255)
    layer = Image.new("RGBA", (max(w, 1), max(h, 1)), (0, 0, 0, 0))
    layer.paste(grad, (0, 0), mask)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(layer, (int(box[0]), int(box[1])), layer)
    return canvas


def tile(size, ss, top, bot, radius_frac=0.235, inset=0.055):
    box = (inset * size, inset * size, (1 - inset) * size, (1 - inset) * size)
    return vgrad(size, top, bot, box, radius_frac * size * ss)


def blend(img, layer):
    return Image.alpha_composite(img, layer)


def draw_bg_panel(img, defs, color):
    pass


# ---- A: 囲い（frame holding icons） --------------------------------------
def concept_frame(size):
    n = size * SS
    img = tile(n, SS, INK_TOP, INK_BOT)
    d = ImageDraw.Draw(img, "RGBA")
    # 内側の半透明パネル（フェンス）
    panel = (0.155 * n, 0.215 * n, 0.845 * n, 0.815 * n)
    d.rounded_rectangle(panel, radius=0.075 * n, fill=(255, 255, 255, 24),
                        outline=(126, 176, 255, 150), width=int(0.022 * n))
    # 中のアイコン 2x2
    side = 0.155 * n
    gap = 0.055 * n
    cx, cy = n / 2, n * 0.515
    x0 = cx - side - gap / 2
    y0 = cy - side - gap / 2
    alphas = (235, 150, 150, 235)
    r = 0.042 * n
    for i, (col, row) in enumerate(((0, 0), (1, 0), (0, 1), (1, 1))):
        x = x0 + col * (side + gap)
        y = y0 + row * (side + gap)
        d.rounded_rectangle((x, y, x + side, y + side), radius=r,
                            fill=(BLUE[0], BLUE[1], BLUE[2], alphas[i]))
    return img


# ---- B: 障子（translucent paper + lattice） -------------------------------
def concept_shoji(size):
    n = size * SS
    img = tile(n, SS, PAPER_TOP, PAPER_BOT)
    d = ImageDraw.Draw(img, "RGBA")
    # 紙の内側にわずかな陰
    inset = 0.115 * n
    d.rounded_rectangle((inset, inset, n - inset, n - inset), radius=0.14 * n,
                        fill=(255, 255, 255, 60))
    # 木枠
    f = 0.062 * n
    d.rounded_rectangle((inset, inset, n - inset, n - inset),
                        radius=0.145 * n, outline=WOOD + (255,), width=int(f))
    # 格子（縦 1 本＋横 1 本）
    d.rectangle((n / 2 - 0.026 * n, inset + f * 0.6, n / 2 + 0.026 * n, n - inset - f * 0.6),
                fill=WOOD + (255,))
    d.rectangle((inset + f * 0.6, n * 0.40 - 0.024 * n, n - inset - f * 0.6, n * 0.40 + 0.024 * n),
                fill=WOOD + (255,))
    return img


# ---- C: 囲い込み（corner brackets） ---------------------------------------
def concept_bracket(size):
    n = size * SS
    img = tile(n, SS, (86, 160, 255), (26, 74, 160))
    d = ImageDraw.Draw(img)
    lw = int(0.058 * n)
    arm = 0.17 * n
    m = 0.205 * n
    col = (255, 255, 255, 245)
    # 4 つの L 字
    for sx, sy in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
        x = m if sx > 0 else n - m
        y = m if sy > 0 else n - m
        d.line((x, y, x + sx * arm, y), fill=col, width=lw)
        d.line((x, y, x, y + sy * arm), fill=col, width=lw)
    # 中身
    side = 0.135 * n
    gap = 0.045 * n
    cx, cy = n / 2, n / 2
    x0 = cx - side - gap / 2
    y0 = cy - side - gap / 2
    for col_i, row_i in ((0, 0), (1, 0), (0, 1), (1, 1)):
        x = x0 + col_i * (side + gap)
        y = y0 + row_i * (side + gap)
        d.rounded_rectangle((x, y, x + side, y + side), radius=0.036 * n,
                            fill=(255, 255, 255, 230))
    return img


# ---- D: タブ付きパネル ----------------------------------------------------
def concept_tab(size):
    n = size * SS
    img = tile(n, SS, INK_TOP, INK_BOT)
    d = ImageDraw.Draw(img, "RGBA")
    left, right = 0.13 * n, 0.87 * n
    top, bottom = 0.235 * n, 0.825 * n
    d.rounded_rectangle((left, top, right, bottom), radius=0.06 * n,
                        fill=(255, 255, 255, 22), outline=(255, 255, 255, 60),
                        width=int(0.012 * n))
    # タブ
    tw = 0.40 * n
    d.rounded_rectangle((left + 0.03 * n, top - 0.075 * n, left + 0.03 * n + tw, top + 0.075 * n),
                        radius=0.045 * n, fill=BLUE_DEEP + (255,))
    # 3 つのアイコン
    side = 0.125 * n
    gap = 0.045 * n
    total = side * 3 + gap * 2
    x = n / 2 - total / 2
    y = n * 0.58
    for i in range(3):
        d.rounded_rectangle((x, y, x + side, y + side), radius=0.034 * n,
                            fill=(BLUE[0], BLUE[1], BLUE[2], (235, 170, 170)[i]))
        x += side + gap
    return img


CONCEPTS = {
    "frame": concept_frame,
    "shoji": concept_shoji,
    "bracket": concept_bracket,
    "tab": concept_tab,
}


def render(name, size):
    fn = CONCEPTS[name]
    big = fn(size)
    return big.resize((size, size), Image.LANCZOS)


def font(size):
    for path in (r"C:\Windows\Fonts\YuGothM.ttc", r"C:\Windows\Fonts\segoeui.ttf"):
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main():
    os.makedirs(OUT, exist_ok=True)
    sizes = [256, 128, 64, 48, 32, 24, 16]
    for name in CONCEPTS:
        for s in sizes:
            render(name, s).save(os.path.join(OUT, f"concept_{name}_{s}.png"))

    # ---- 比較シート ----
    pad = 28
    label_w = 230
    zoom = 8                      # 16px は 8 倍で拡大して確認
    cells = []
    for s in sizes:
        cells.append((s, zoom if s <= 16 else max(1, 256 // s // 2)))
    row_h = 300
    width = label_w + sum(max(s, s * 1) for s, _ in cells) + pad * len(cells) + pad
    height = pad * 2 + row_h * len(CONCEPTS) + 60
    sheet = Image.new("RGBA", (width, height), (250, 250, 252, 255))
    d = ImageDraw.Draw(sheet)

    d.text((pad, 18), "アイコン案 / size 256 → 16（16px は 8 倍拡大、上段=明るい背景・下段=暗い背景）",
           font=font(20), fill=(30, 30, 40))

    titles = {"frame": "A  囲い（フレーム＋アイコン）", "shoji": "B  障子（半透明＋格子）",
              "bracket": "C  囲い込み（ブラケット）", "tab": "D  タブ付きパネル"}
    accent = {"frame": INK_TOP, "shoji": PAPER_BOT, "bracket": BLUE_DEEP, "tab": INK_TOP}

    for r, name in enumerate(CONCEPTS):
        y = 56 + r * row_h
        d.text((pad, y + 4), titles[name], font=font(18), fill=(30, 30, 40))
        shade_w = width - pad * 2
        d.rounded_rectangle((pad, y + 34, pad + shade_w, y + row_h - 14), radius=10,
                            fill=(255, 255, 255, 255), outline=(225, 225, 232, 255))
        x = pad + 18
        for s, z in cells:
            # 明るい背景
            im = render(name, s)
            if z > 1:
                im = im.resize((s * z, s * z), Image.NEAREST)
            bx = x
            by = y + 50
            d.text((bx, by - 18), f"{s}px", font=font(13), fill=(120, 120, 130))
            sheet.alpha_composite(im, (bx, by))
            x += im.width + 14
        # 2 段目（暗い背景）— 大きい 2 サイズだけ
        x2 = pad + 18
        for s, z in cells[:3]:
            im = render(name, s)
            bx, by = x2, y + row_h - 100
            d.rounded_rectangle((bx - 6, by - 6, bx + max(im.width, 60) + 6, by + 60), radius=8,
                                fill=(28, 32, 42, 255))
            sheet.alpha_composite(im, (bx, by))
            x2 += max(im.width, 60) + 26

    sheet.save(os.path.join(OUT, "sheet.png"))
    print("wrote", OUT)


if __name__ == "__main__":
    main()
