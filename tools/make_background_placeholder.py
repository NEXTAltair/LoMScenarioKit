# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""試作の背景 (公式と同じ 1920x1080)。使い方: make_background_placeholder.py <出力PNG> [ラベル]"""
import sys
from placeholder_fonts import font_path as find_font
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1]
label = sys.argv[2] if len(sys.argv) > 2 else "試作背景"
W, H = 1920, 1080
img = Image.new("RGB", (W, H))
d = ImageDraw.Draw(img)
for y in range(H):                                      # 夜空のグラデーション
    t = y / H
    d.line((0, y, W, y), fill=(int(20 + 30 * t), int(24 + 30 * t), int(48 + 40 * t)))
for x, y in ((200, 120), (520, 80), (900, 160), (1300, 90), (1650, 140), (1100, 60), (380, 240)):
    d.ellipse((x - 3, y - 3, x + 3, y + 3), fill=(240, 240, 220))     # 星
d.ellipse((1500, 90, 1620, 210), fill=(235, 230, 200))                  # 月
d.polygon([(0, 700), (300, 480), (620, 700), (900, 520), (1250, 700), (1600, 470), (1920, 680), (1920, 760), (0, 760)], fill=(40, 52, 60))  # 山並み
d.rectangle((0, 740, W, H), fill=(52, 60, 44))                          # 地面
d.polygon([(1200, 900), (1420, 640), (1640, 900)], fill=(150, 120, 80))  # 天幕
d.polygon([(1380, 900), (1420, 760), (1460, 900)], fill=(60, 45, 30))
d.ellipse((760, 860, 900, 930), fill=(70, 50, 30))                      # 焚き火
d.polygon([(790, 890), (830, 760), (870, 890)], fill=(240, 150, 40))
d.polygon([(810, 890), (830, 810), (850, 890)], fill=(255, 220, 120))
font = ImageFont.truetype(find_font(), 72)
d.text((W // 2, 360), label, font=font, fill=(230, 230, 230), anchor="mm")
img.save(out)
print("ok", out)
