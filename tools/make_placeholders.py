# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""試作キャラ用の仮画像を作る (公式と同じサイズ)。

使い方: make_placeholders.py <出力フォルダ> [ラベル] [衣の色 #RRGGBB] [--right]

絵は公式と同じく左向きが基準。腰の刀 (画像の右側) で向きが分かる。
--right を付けると、左右非対称キャラ用の右向きの絵 (portraits/right/) も作る。
"""
import sys
from pathlib import Path
from placeholder_fonts import font_path as find_font
from PIL import Image, ImageDraw, ImageFont

out = Path(sys.argv[1])
label = sys.argv[2] if len(sys.argv) > 2 else "試作"
font_path = find_font()
INK = (40, 52, 70, 255)
ROBE = tuple(int(sys.argv[3][i:i + 2], 16) for i in (1, 3, 5)) + (255,) if len(sys.argv) > 3 else (92, 122, 150, 255)
SKIN = (236, 214, 190, 255)

def figure(w, h, scale, dx=0, dy=0):
    """単純な人物シルエット (頭・髪・衣)。座標は 900x1700 基準。"""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = lambda *p: [v * scale + (dx if i % 2 == 0 else dy) for i, v in enumerate(p)]
    d.polygon(s(250, 700, 650, 700, 820, 1700, 80, 1700), fill=ROBE)          # 衣
    d.polygon(s(330, 690, 570, 690, 450, 900), fill=(230, 230, 220, 255))    # 襟
    d.ellipse(s(300, 300, 600, 660), fill=SKIN)                               # 顔
    d.pieslice(s(280, 260, 620, 620), 180, 360, fill=INK)                     # 髪
    d.ellipse(s(400, 150, 500, 280), fill=INK)                                # 髷
    d.polygon(s(600, 1000, 640, 990, 700, 1500, 660, 1510), fill=(150, 110, 60, 255))  # 腰の刀 (非対称の目印)
    return img, d

# 立ち絵 900x1700
img, d = figure(900, 1700, 1.0)
font = ImageFont.truetype(font_path, 90)
d.text((450, 1180), label, font=font, fill=(255, 255, 255, 230), anchor="mm")
img.save(out / "avatar.png")

# 社交パネルの縦長の顔 66x172 (顔の上半分を切り出す)
big, _ = figure(900, 1700, 1.0)
head = big.crop((265, 150, 635, 1114)).resize((66, 172), Image.LANCZOS)
head.save(out / "head.png")

# 小アイコン 101x100 (円形)
icon = Image.new("RGBA", (101, 100), (0, 0, 0, 0))
face = big.crop((250, 170, 650, 570)).resize((96, 96), Image.LANCZOS)
mask = Image.new("L", (96, 96), 0)
ImageDraw.Draw(mask).ellipse((0, 0, 95, 95), fill=255)
ImageDraw.Draw(icon).ellipse((1, 1, 99, 98), fill=(200, 190, 170, 255))
icon.paste(face, (3, 2), mask)
icon.save(out / "icon.png")
print("ok", [p.name for p in sorted(out.glob("*.png"))])

# 会話シーン用の表情差分 (portraits/<表情>.png)。normal は avatar.png で代用される。
pdir = out / "portraits"
pdir.mkdir(exist_ok=True)
smile, d = figure(900, 1700, 1.0)
d.arc((380, 470, 520, 580), 20, 160, fill=INK, width=8)       # 口元の笑み
d.text((450, 1180), "笑", font=font, fill=(255, 255, 255, 230), anchor="mm")
smile.save(pdir / "smile.png")
print("ok", [p.name for p in sorted(pdir.glob("*.png"))])

# 右向きの絵: 体は反転、文字は反転しない
if "--right" in sys.argv:
    rdir = pdir / "right"
    rdir.mkdir(exist_ok=True)
    for mood, mark in (("normal", label), ("smile", "笑")):
        body, _ = figure(900, 1700, 1.0)
        if mood == "smile":
            ImageDraw.Draw(body).arc((380, 470, 520, 580), 20, 160, fill=INK, width=8)
        body = body.transpose(Image.FLIP_LEFT_RIGHT)
        ImageDraw.Draw(body).text((450, 1180), mark + "→", font=font, fill=(255, 255, 255, 230), anchor="mm")
        body.save(rdir / f"{mood}.png")
    print("ok", [p.name for p in sorted(rdir.glob("*.png"))])

# 決闘の敵の絵 (combat/<normal|attack|hurt|defence>.png)。左向きが基準
if "--combat" in sys.argv:
    cdir = out / "combat"
    cdir.mkdir(exist_ok=True)
    blade = (190, 190, 200, 255)
    for kind, mark in (("normal", label), ("attack", "攻"), ("hurt", "傷"), ("defence", "防")):
        body, d = figure(900, 1700, 1.0)
        if kind == "attack":      # 刀を左 (相手側) へ突き出す
            d.polygon([(40, 820), (560, 900), (560, 930), (40, 850)], fill=blade)
        elif kind == "defence":   # 刀を体の前で横に構える
            d.polygon([(150, 760), (750, 760), (750, 800), (150, 800)], fill=blade)
        elif kind == "hurt":      # のけぞる
            body = body.rotate(-8, resample=Image.BICUBIC, center=(450, 1700))
            d = ImageDraw.Draw(body)
        d.text((450, 1180), mark, font=font, fill=(255, 255, 255, 230), anchor="mm")
        body.save(cdir / f"{kind}.png")
    print("ok", [p.name for p in sorted(cdir.glob("*.png"))])
