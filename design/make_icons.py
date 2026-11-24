"""改名案のアイコン／ワードマークのプレビューを描く。

タブ付きパネルをモチーフに、クリーンでミニマルな 4 案。
8 倍スーパーサンプリング → LANCZOS で縮小。
出力: design/out/preview/icons_sheet.png, names_sheet.png, icons/<name>_<size>.png
"""
from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out", "preview")
ICONS = os.path.join(OUT, "icons")
SS = 8

# ---- palette -------------------------------------------------------------
TILE_TOP = (88, 150, 250)
TILE_BOT = (23, 79, 205)
ACCENT = (37, 99, 235)
ACCENT_SOFT = (147, 197, 253)
WHITE = (255, 255, 255, 255)
INK = (23, 30, 46)

SIZES = [256, 128, 64, 48, 32, 24, 16]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def new_canvas(n):
    return Image.new("RGBA", (n, n), (0, 0, 0, 0))


def grad_round(n, box, radius, top, bot):
    """角丸矩形の中だけ縦グラデーション。box は n 基準の実座標。"""
    x0, y0, x1, y1 = (int(round(v)) for v in box)
    w, h = max(x1 - x0, 1), max(y1 - y0, 1)
    strip = Image.new("RGB", (1, h))
    for y in range(h):
        strip.putpixel((0, y), lerp(top, bot, y / max(h - 1, 1)))
    grad = strip.resize((w, h), Image.NEAREST)
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, w - 1, h - 1), radius=max(int(round(radius)), 0), fill=255)
    layer = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    layer.paste(grad, (x0, y0), mask)
    return layer


def tile(n):
    inset = 0.035 * n
    return grad_round(
        n,
        (inset, inset, n - inset, n - inset),
        radius=0.235 * (n - 2 * inset),
        top=TILE_TOP,
        bot=TILE_BOT,
    )


def rr(d, box, radius, fill):
    d.rounded_rectangle(tuple(round(v, 2) for v in box), radius=radius, fill=fill)


def dots(d, n, count, y, side, gap, cols):
    total = side * count + gap * (count - 1)
    x = (n - total) / 2
    for i in range(count):
        rr(d, (x, y, x + side, y + side), 0.028 * n, cols[i])
        x += side + gap


# ---- A: タブ付きパネル（1 タブ） -----------------------------------------
def icon_panel(n, size):
    img = tile(n)
    d = ImageDraw.Draw(img)
    if size <= 16:
        rr(d, (0.165 * n, 0.235 * n, 0.53 * n, 0.375 * n), 0.045 * n, WHITE)
        rr(d, (0.16 * n, 0.345 * n, 0.84 * n, 0.815 * n), 0.055 * n, WHITE)
    elif size <= 24:
        rr(d, (0.165 * n, 0.235 * n, 0.53 * n, 0.375 * n), 0.045 * n, WHITE)
        rr(d, (0.16 * n, 0.345 * n, 0.84 * n, 0.815 * n), 0.055 * n, WHITE)
        dots(d, n, 2, 0.525 * n, 0.135 * n, 0.06 * n,
             [ACCENT + (255,), ACCENT + (200,)])
    else:
        rr(d, (0.19 * n, 0.235 * n, 0.505 * n, 0.365 * n), 0.042 * n, WHITE)
        rr(d, (0.185 * n, 0.33 * n, 0.815 * n, 0.80 * n), 0.055 * n, WHITE)
        dots(d, n, 3, 0.505 * n, 0.105 * n, 0.045 * n,
              [ACCENT + (255,), ACCENT + (235,), ACCENT + (185,)])
    return img


# ---- B: パネルの重なり（複数のフェンス） ---------------------------------
def icon_stack(n, size):
    img = tile(n)
    d = ImageDraw.Draw(img)
    if size <= 32:
        rr(d, (0.20 * n, 0.235 * n, 0.54 * n, 0.375 * n), 0.045 * n,
           (255, 255, 255, 235))
        rr(d, (0.195 * n, 0.345 * n, 0.83 * n, 0.815 * n), 0.055 * n,
           (255, 255, 255, 250))
        return img

    edge = (95, 155, 250, 255)
    cards = [
        (0.295, 0.44, 0.865, 0.90, 0.05),
        (0.225, 0.355, 0.795, 0.815, 0.05),
        (0.155, 0.27, 0.725, 0.73, 0.055),
    ]
    for i, (x0, y0, x1, y1, r) in enumerate(cards):
        d.rounded_rectangle((x0 * n, y0 * n, x1 * n, y1 * n), radius=r * n,
                            fill=WHITE, outline=edge, width=max(int(0.018 * n), 1))
    rr(d, (0.16 * n, 0.185 * n, 0.435 * n, 0.31 * n), 0.04 * n, WHITE)
    dots(d, n * 0.88, 2, 0.425 * n, 0.09 * n, 0.045 * n,
         [ACCENT + (255,), ACCENT + (205,)])
    return img


# ---- C: 区画（2x2） ------------------------------------------------------
def icon_grid(n, size):
    img = tile(n)
    d = ImageDraw.Draw(img)
    if size <= 16:
        rr(d, (0.165 * n, 0.235 * n, 0.53 * n, 0.375 * n), 0.045 * n, WHITE)
        rr(d, (0.16 * n, 0.345 * n, 0.84 * n, 0.815 * n), 0.055 * n, WHITE)
    elif size <= 24:
        rr(d, (0.165 * n, 0.235 * n, 0.53 * n, 0.375 * n), 0.045 * n, WHITE)
        rr(d, (0.16 * n, 0.345 * n, 0.84 * n, 0.815 * n), 0.055 * n, WHITE)
        side, gap = 0.155 * n, 0.055 * n
        x0 = (n - (side * 2 + gap)) / 2
        y0 = 0.455 * n
        k = 0
        for row in range(2):
            for col in range(2):
                x = x0 + col * (side + gap)
                y = y0 + row * (side + gap)
                rr(d, (x, y, x + side, y + side), 0.03 * n,
                   ACCENT + ((255, 225, 225, 180)[k],))
                k += 1
    else:
        rr(d, (0.19 * n, 0.235 * n, 0.505 * n, 0.365 * n), 0.042 * n, WHITE)
        rr(d, (0.185 * n, 0.33 * n, 0.815 * n, 0.80 * n), 0.055 * n, WHITE)
        side, gap = 0.105 * n, 0.05 * n
        x0 = (n - (side * 2 + gap)) / 2
        y0 = 0.415 * n
        alphas = (255, 225, 225, 180)
        k = 0
        for row in range(2):
            for col in range(2):
                x = x0 + col * (side + gap)
                y = y0 + row * (side + gap)
                rr(d, (x, y, x + side, y + side), 0.03 * n,
                   ACCENT + (alphas[k],))
                k += 1
    return img


# ---- D: 3 つのタブ -------------------------------------------------------
def icon_tabs(n, size):
    img = tile(n)
    d = ImageDraw.Draw(img)
    if size <= 24:
        rr(d, (0.165 * n, 0.235 * n, 0.53 * n, 0.375 * n), 0.045 * n, WHITE)
        rr(d, (0.16 * n, 0.345 * n, 0.84 * n, 0.815 * n), 0.055 * n, WHITE)
        return img

    body = (0.135 * n, 0.36 * n, 0.865 * n, 0.83 * n)
    tw = 0.20 * n
    gap = 0.032 * n
    x = 0.15 * n
    for i in range(3):
        if i == 0:
            rr(d, (x, 0.245 * n, x + tw, 0.40 * n), 0.04 * n, WHITE)
        else:
            rr(d, (x, 0.295 * n, x + tw, 0.40 * n), 0.035 * n,
               (255, 255, 255, 165))
        x += tw + gap
    rr(d, body, 0.055 * n, WHITE)
    dots(d, n, 2, 0.56 * n, 0.115 * n, 0.055 * n,
         [ACCENT + (255,), ACCENT + (200,)])
    return img


CONCEPTS = {
    "panel": (icon_panel, "A  タブ付きパネル", "1 枚のパネル＋タブ。一番ミニマル"),
    "stack": (icon_stack, "B  パネルの重なり", "複数のフェンスが重なる様子"),
    "grid": (icon_grid, "C  区画（2×2）", "仕切りに分けて整理するイメージ"),
    "tabs": (icon_tabs, "D  3 つのタブ", "タブ機能そのものを前面に"),
}


# ---- render --------------------------------------------------------------
def render(name, size):
    fn = CONCEPTS[name][0]
    return fn(size * SS, size).resize((size, size), Image.LANCZOS)


def render_tray(n, size, color):
    """タスクトレイ用の単色アイコン（塗り＋抜き）。"""
    img = new_canvas(n)
    d = ImageDraw.Draw(img)
    if size <= 20:
        rr(d, (0.14 * n, 0.28 * n, 0.50 * n, 0.40 * n), 0.04 * n, color)
        rr(d, (0.13 * n, 0.38 * n, 0.87 * n, 0.83 * n), 0.06 * n, color)
        rr(d, (0.25 * n, 0.51 * n, 0.44 * n, 0.70 * n), 0.03 * n, (0, 0, 0, 0))
        rr(d, (0.56 * n, 0.51 * n, 0.75 * n, 0.70 * n), 0.03 * n, (0, 0, 0, 0))
    else:
        rr(d, (0.15 * n, 0.25 * n, 0.52 * n, 0.38 * n), 0.04 * n, color)
        rr(d, (0.14 * n, 0.36 * n, 0.86 * n, 0.84 * n), 0.05 * n, color)
        rr(d, (0.26 * n, 0.50 * n, 0.44 * n, 0.68 * n), 0.02 * n, (0, 0, 0, 0))
        rr(d, (0.56 * n, 0.50 * n, 0.74 * n, 0.68 * n), 0.02 * n, (0, 0, 0, 0))
    return img.resize((size, size), Image.LANCZOS)


# ---- fonts ---------------------------------------------------------------
def font(size, bold=False, jp=True):
    names = []
    if jp:
        names += [r"C:\Windows\Fonts\YuGothB.ttc" if bold else r"C:\Windows\Fonts\YuGothM.ttc"]
    names += [r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\segoeui.ttf"]
    for p in names:
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def place(sheet, im, x, y, zoom=1):
    if zoom > 1:
        im = im.resize((im.width * zoom, im.height * zoom), Image.NEAREST)
    sheet.alpha_composite(im, (int(x), int(y)))
    return im


# ---- sheets --------------------------------------------------------------
def icons_sheet():
    W = 1280
    m = 46
    H = 1660
    sheet = Image.new("RGBA", (W, H), (247, 248, 251, 255))
    d = ImageDraw.Draw(sheet)

    d.text((m, 34), "アイコン案 — タブ付きパネル", font=font(30, bold=True), fill=INK)
    d.text((m, 78), "クリーン／ミニマル。実寸表示（16px は 4 倍、24px は 2 倍に拡大）",
           font=font(15), fill=(110, 118, 135))

    # hero row
    for i, name in enumerate(CONCEPTS):
        x = m + i * 302
        y = 122
        d.rounded_rectangle((x, y, x + 280, y + 244), radius=16,
                            fill=(255, 255, 255, 255), outline=(226, 230, 238, 255))
        place(sheet, render(name, 128), x + 76, y + 20)
        d.text((x + 18, y + 164), CONCEPTS[name][1], font=font(17, bold=True), fill=INK)
        d.text((x + 18, y + 194), CONCEPTS[name][2], font=font(13), fill=(120, 128, 143))

    # per-concept size rows
    cells = [(64, 1), (48, 1), (32, 1), (24, 2), (16, 4)]

    def band(top, dark, name):
        d.rounded_rectangle((m, top, W - m, top + 96), radius=12,
                            fill=(28, 33, 44, 255) if dark else (255, 255, 255, 255),
                            outline=None if dark else (228, 232, 240, 255))
        x = m + 22
        for size, zoom in cells:
            im = render(name, size)
            v = im.height * zoom
            place(sheet, im, x, top + (70 - v) / 2 + 2, zoom)
            d.text((x, top + 76), f"{size}px", font=font(11),
                   fill=(120, 128, 145) if dark else (150, 156, 170))
            x += max(im.width * zoom, 64) + 38
        d.text((W - m - 34, top + 40), "暗" if dark else "明", font=font(13),
               fill=(130, 138, 155))

    for r, name in enumerate(CONCEPTS):
        top = 400 + r * 238
        d.text((m, top), CONCEPTS[name][1], font=font(17, bold=True), fill=INK)
        band(top + 28, False, name)
        band(top + 130, True, name)

    # tray strip
    top = 400 + len(CONCEPTS) * 238 + 6
    d.text((m, top), "トレイ用（単色・抜き）", font=font(17, bold=True), fill=INK)
    for j, (color, dark) in enumerate((((23, 30, 46, 255), False),
                                       ((255, 255, 255, 255), True))):
        y = top + 28 + j * 102
        d.rounded_rectangle((m, y, W - m, y + 96), radius=12,
                            fill=(28, 33, 44, 255) if dark else (255, 255, 255, 255),
                            outline=None if dark else (228, 232, 240, 255))
        x = m + 22
        for size, zoom in [(64, 1), (32, 1), (24, 2), (16, 4), (20, 3)]:
            im = render_tray(size * SS, size, color)
            v = im.height * zoom
            place(sheet, im, x, y + (70 - v) / 2 + 2, zoom)
            d.text((x, y + 76), f"{size}px", font=font(11),
                   fill=(120, 128, 145) if dark else (150, 156, 170))
            x += max(im.width * zoom, 64) + 38

    sheet.convert("RGB").save(os.path.join(OUT, "icons_sheet.png"))
    return sheet


NAMES = [
    ("Fakaro", "ファカーロ", "区画の集まり（fako + -aro）", "本命"),
    ("Langeto", "ランゲート", "タブ（langeto）そのもの", "対抗"),
    ("Fasko", "ファスコ", "束・ひとまとめ（fasko）", ""),
    ("Kestaro", "ケスターロ", "箱の棚（kesto + -aro）", ""),
    ("Fako", "ファーコ", "仕切り・区画（fako）— 最短", ""),
    ("Ordigo", "オルディーゴ", "整える・整列させる（ordigi）", ""),
]


def names_sheet():
    W = 1280
    m = 46
    sheet = Image.new("RGBA", (W, 690), (247, 248, 251, 255))
    d = ImageDraw.Draw(sheet)
    d.text((m, 34), "名前の候補 — エスペラント語風", font=font(30, bold=True), fill=INK)
    d.text((m, 78), "「本命」「対抗」は意味と形の相性で選んだもの。発音はカタカナ目安。",
           font=font(15), fill=(110, 118, 135))

    for i, (name, kana, mean, badge) in enumerate(NAMES):
        col, row = i % 2, i // 2
        x = m + col * 600
        y = 128 + row * 158
        d.rounded_rectangle((x, y, x + 566, y + 138), radius=14,
                            fill=(255, 255, 255, 255), outline=(228, 232, 240, 255))
        place(sheet, render("panel", 64), x + 22, y + 37)
        d.text((x + 108, y + 24), name, font=font(34, bold=True), fill=INK)
        d.text((x + 110, y + 74), kana, font=font(15), fill=(90, 100, 120))
        d.text((x + 110, y + 100), mean, font=font(14), fill=(125, 132, 148))
        if badge:
            fg = (255, 255, 255, 255) if badge == "本命" else (74, 95, 130, 255)
            bg = ACCENT + (255,) if badge == "本命" else (226, 233, 243, 255)
            d.rounded_rectangle((x + 466, y + 24, x + 542, y + 52), radius=14, fill=bg)
            d.text((x + 481, y + 28), badge, font=font(14, bold=True), fill=fg)

    d.text((m, 610), "※ Ordigo は同名の業務アプリ（ordigo.app / ordigo.io）あり。Fako は短い反面、英語圏で語感の悪い連想（fuck-o）があるため要注意。",
           font=font(13), fill=(130, 137, 152))
    d.text((m, 634), "※ Tavolo はイタリア語（エスペラントでは tablo）。Kesto・Nesto・Kolekto・Kunigo・Bretaro は一般名詞か既存サービスと重なるため外しました。",
           font=font(13), fill=(130, 137, 152))

    sheet.convert("RGB").save(os.path.join(OUT, "names_sheet.png"))
    return sheet


def main():
    os.makedirs(ICONS, exist_ok=True)
    for name in CONCEPTS:
        for s in SIZES:
            render(name, s).save(os.path.join(ICONS, f"{name}_{s}.png"))
        for s in (32, 24, 20, 16):
            render_tray(s * SS, s, (255, 255, 255, 255)).save(
                os.path.join(ICONS, f"tray_white_{s}.png"))
            render_tray(s * SS, s, INK + (255,)).save(
                os.path.join(ICONS, f"tray_ink_{s}.png"))
    icons_sheet()
    names_sheet()
    print("wrote", OUT)


if __name__ == "__main__":
    main()
