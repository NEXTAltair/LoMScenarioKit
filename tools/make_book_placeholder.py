# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""試作秘笈の仮のアイコン (公式と同じ 312x300)。使い方: make_book_placeholder.py <出力PNG> [題字]"""
import sys
from placeholder_fonts import font_path as find_font
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1]
title = sys.argv[2] if len(sys.argv) > 2 else "試作"
img = Image.new("RGBA", (312, 300), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((56, 20, 256, 280), radius=8, fill=(120, 60, 40, 255))      # 表紙
d.rectangle((56, 20, 80, 280), fill=(90, 40, 28, 255))                           # 綴じ
for y in (60, 120, 180, 240):
    d.line((56, y, 80, y), fill=(220, 200, 160, 255), width=3)                   # 綴じ糸
d.rectangle((140, 40, 220, 250), fill=(235, 225, 200, 255))                      # 題簽
font = ImageFont.truetype(find_font(), 40)
for i, ch in enumerate(title[:4]):
    d.text((180, 75 + i * 48), ch, font=font, fill=(40, 30, 20, 255), anchor="mm")
img.save(out)
print("ok", out)
