"""Fako の最終アイコン一式を書き出す。

- design/out/fako/fako_<size>.png   （16〜256）
- design/out/fako/fako.ico          （アプリ用・マルチサイズ）
- design/out/fako/fako_tray.ico     （トレイ用・16〜32。小サイズは簡略化）
- design/out/fako/brand.png         （アイコン＋ワードマーク＋実寸比較）
"""
from __future__ import annotations

import io
import os
import struct

from PIL import Image, ImageDraw, ImageFont

from make_icons import (ACCENT, INK, SS, font, place, render, render_tray,
                        tile)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out", "fako")

APP_SIZES = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]
TRAY_SIZES = [32, 24, 20, 16]


def write_ico(path, images):
    """サイズごとに描いた RGBA を、そのまま PNG 圧縮で ICO に詰める。"""
    sizes = sorted(images)
    payload = []
    for s in sizes:
        buf = io.BytesIO()
        images[s].save(buf, format="PNG", optimize=True)
        payload.append(buf.getvalue())

    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, payload):
        d = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", d, d, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    with open(path, "wb") as f:
        f.write(header)
        f.write(entries)
        for data in payload:
            f.write(data)


def latin(size, semibold=True):
    names = [r"C:\Windows\Fonts\seguisb.ttf", r"C:\Windows\Fonts\seguisbi.ttf"] \
        if semibold else [r"C:\Windows\Fonts\segoeui.ttf"]
    names.append(r"C:\Windows\Fonts\YuGothB.ttc")
    for p in names:
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def brand_sheet():
    W, H = 1280, 900
    sheet = Image.new("RGBA", (W, H), (247, 248, 251, 255))
    d = ImageDraw.Draw(sheet)
    m = 46

    # ---- lockup ----
    d.rounded_rectangle((m, 34, W - m, 296), radius=18,
                        fill=(255, 255, 255, 255), outline=(226, 230, 238, 255))
    place(sheet, render("panel", 160), m + 34, 70)
    d.text((m + 232, 76), "Fako", font=latin(78), fill=INK)
    d.text((m + 236, 176), "デスクトップのアイコンを、区画で整える。",
           font=font(20), fill=(80, 90, 108))
    d.text((m + 238, 216), "エスペラント語 fako ＝ 仕切り・区画",
           font=font(14), fill=(140, 147, 160))

    # ---- app icon sizes ----
    d.text((m, 326), "アプリアイコン（実寸。24px は 2 倍、16px は 4 倍に拡大）",
           font=font(16, bold=True), fill=INK)
    cells = [(64, 1), (48, 1), (32, 1), (24, 2), (16, 4)]
    for dark, top in ((False, 358), (True, 452)):
        d.rounded_rectangle((m, top, W - m, top + 86), radius=12,
                            fill=(28, 33, 44, 255) if dark else (255, 255, 255, 255),
                            outline=None if dark else (228, 232, 240, 255))
        x = m + 24
        for size, zoom in cells:
            im = render("panel", size)
            v = im.height * zoom
            place(sheet, im, x, top + (62 - v) / 2 + 4, zoom)
            d.text((x, top + 68), f"{size}px", font=font(10),
                   fill=(120, 128, 145) if dark else (152, 158, 172))
            x += max(im.width * zoom, 56) + 40

    # ---- tray ----
    d.text((m, 560), "タスクトレイ（左：カラー / 右：単色）",
           font=font(16, bold=True), fill=INK)
    for dark, top in ((False, 592), (True, 682)):
        bg = (28, 33, 44, 255) if dark else (255, 255, 255, 255)
        border = None if dark else (228, 232, 240, 255)
        d.rounded_rectangle((m, top, 690, top + 80), radius=12, fill=bg,
                            outline=border)
        x = m + 22
        for size, zoom in [(32, 2), (24, 2), (16, 3)]:
            im = render("panel", size)
            place(sheet, im, x, top + (58 - size * zoom) / 2 + 4, zoom)
            x += size * zoom + 34

        # mono（同じ帯の中に、区切って並べる）
        d.line((700, top + 10, 700, top + 70),
               fill=(210, 216, 226, 255) if not dark else (70, 78, 96, 255))
        d.rounded_rectangle((712, top, 966, top + 80), radius=12, fill=bg,
                            outline=border)
        x = 730
        color = (255, 255, 255, 255) if dark else INK + (255,)
        for size, zoom in [(32, 2), (24, 2), (16, 3)]:
            im = render_tray(size * SS, size, color)
            place(sheet, im, x, top + (58 - size * zoom) / 2 + 4, zoom)
            x += size * zoom + 30

    # ---- notes ----
    d.text((m, 792), "16px はタブとパネルのシルエットだけに簡略化（細部は落として輪郭を優先）。",
           font=font(13), fill=(130, 137, 152))
    d.text((m, 816), "色: #5896FA → #174FCD / パネル: #FFFFFF / アクセント: #2563EB",
           font=font(13), fill=(130, 137, 152))

    sheet.convert("RGB").save(os.path.join(OUT, "brand.png"))
    return sheet


def main():
    os.makedirs(OUT, exist_ok=True)

    for s in APP_SIZES:
        render("panel", s).save(os.path.join(OUT, f"fako_{s}.png"))
    write_ico(os.path.join(OUT, "fako.ico"),
              {s: render("panel", s) for s in APP_SIZES})

    tray = {s: render("panel", s) for s in TRAY_SIZES}
    write_ico(os.path.join(OUT, "fako_tray.ico"), tray)
    for s in TRAY_SIZES:
        render_tray(s * SS, s, (255, 255, 255, 255)).save(
            os.path.join(OUT, f"fako_tray_white_{s}.png"))
        render_tray(s * SS, s, INK + (255,)).save(
            os.path.join(OUT, f"fako_tray_ink_{s}.png"))

    brand_sheet()
    print("wrote", OUT)


if __name__ == "__main__":
    main()
