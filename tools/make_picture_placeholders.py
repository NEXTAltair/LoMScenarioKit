# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""イベント CG・CG 画面の仮画像。使い方: make_picture_placeholders.py <scenario フォルダ>"""
import sys
from pathlib import Path
from placeholder_fonts import font_path as find_font
from PIL import Image, ImageDraw, ImageFont

root = Path(sys.argv[1])
font = lambda size: ImageFont.truetype(find_font(), size)

def scene(w, h, label, sky=(24, 28, 56), ground=(52, 60, 44)):
    img = Image.new("RGB", (w, h), sky)
    d = ImageDraw.Draw(img)
    d.rectangle((0, int(h * 0.68), w, h), fill=ground)
    cx, gy = w // 2, int(h * 0.8)
    d.ellipse((cx - 50, gy - 10, cx + 50, gy + 25), fill=(70, 50, 30))                   # 焚き火
    d.polygon([(cx - 30, gy + 5), (cx, gy - 110), (cx + 30, gy + 5)], fill=(240, 150, 40))
    for x, robe in ((cx - int(w * 0.22), (92, 122, 150)), (cx + int(w * 0.22), (154, 91, 110))):  # 二人の人影
        d.polygon([(x - 45, gy + 20), (x + 45, gy + 20), (x + 28, gy - 120), (x - 28, gy - 120)], fill=robe)
        d.ellipse((x - 26, gy - 175, x + 26, gy - 118), fill=(40, 52, 70))
    d.text((cx, int(h * 0.2)), label, font=font(max(28, w // 16)), fill=(235, 235, 225), anchor="mm")
    return img

scene(480, 700, "イベントCG").save(root / "pictures" / "lsa_pic_fire.png")
scene(1920, 1080, "イベントCG（全画面）", sky=(120, 150, 190), ground=(90, 110, 70)).save(root / "pictures" / "lsa_pic_road.png")
# CG 画面: 中国語の言語設定では本文が縦書きで絵の左に並ぶので、本編の CG と同じく左側を明るい余白にする
paper = (236, 228, 210)
cg = Image.new("RGB", (1100, 1080), paper)
cg.paste(scene(700, 1080, "CG画面", sky=(60, 40, 70)), (400, 0))           # 絵は右側 700px
mask = Image.linear_gradient("L").rotate(90, expand=True).resize((140, 1080))  # 左 255 → 右 0
mask = mask.transpose(Image.FLIP_LEFT_RIGHT) if mask.getpixel((0, 0)) < mask.getpixel((139, 0)) else mask
cg.paste(Image.new("RGB", (140, 1080), paper), (400, 0), mask)               # 余白から絵へぼかす
cg.save(root / "cgs" / "lsa_cg_test.png")
print("ok")
