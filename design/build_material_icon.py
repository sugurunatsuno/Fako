"""Material デザインのマスター画像からアプリ用 ICO を再生成する。

実行: python design/build_material_icon.py
依存: Pillow
"""
from pathlib import Path
import io
import struct

from PIL import Image

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "fako-material.png"
TARGET = HERE.parent / "src" / "Fako" / "Assets" / "fako.ico"
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def main():
    with Image.open(SOURCE) as source:
        master = source.convert("RGBA")

    entries = []
    payload = []
    offset = 6 + 16 * len(SIZES)

    for size in SIZES:
        frame = master.resize((size, size), Image.Resampling.LANCZOS)
        buf = io.BytesIO()
        frame.save(buf, format="PNG", optimize=True)
        data = buf.getvalue()
        dim = size % 256
        entries.append(struct.pack(
            "<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset
        ))
        payload.append(data)
        offset += len(data)

    TARGET.write_bytes(
        struct.pack("<HHH", 0, 1, len(SIZES))
        + b"".join(entries)
        + b"".join(payload)
    )


if __name__ == "__main__":
    main()
