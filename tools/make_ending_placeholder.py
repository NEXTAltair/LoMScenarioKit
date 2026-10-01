# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""試作エンディングの仮の挿絵 (公式と同じ 480x700)。使い方: make_ending_placeholder.py <出力PNG> [題名]"""
import sys
from placeholder_fonts import font_path as find_font
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1]
title = sys.argv[2] if len(sys.argv) > 2 else "試作の結末"
img = Image.new("RGBA", (480, 700), (214, 200, 170, 255))
d = ImageDraw.Draw(img)
d.rectangle((0, 420, 480, 700), fill=(120, 140, 110, 255))                 # 野
d.polygon([(0, 430), (140, 300), (260, 420), (360, 330), (480, 420), (480, 440), (0, 440)], fill=(150, 160, 150, 255))  # 山並み
for x, robe in ((170, (92, 122, 150, 255)), (300, (154, 91, 110, 255))):    # 二人の後ろ姿
    d.polygon([(x - 40, 620), (x + 40, 620), (x + 25, 480), (x - 25, 480)], fill=robe)
    d.ellipse((x - 22, 440, x + 22, 486), fill=(40, 52, 70, 255))
font = ImageFont.truetype(find_font(), 44)
d.text((240, 120), title, font=font, fill=(60, 50, 40, 255), anchor="mm")
img.save(out)
print("ok", out)
