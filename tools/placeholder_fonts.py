# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""仮画像の日本語フォント。外部フォントは配布物に含めない。"""
import os
from pathlib import Path


def font_path():
    explicit = os.environ.get("LOM_PLACEHOLDER_FONT")
    candidates = [Path(explicit)] if explicit else [
        Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts/yumindb.ttf",
        Path("/mnt/c/Windows/Fonts/yumindb.ttf"),
        Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
    ]
    for path in candidates:
        if path.is_file():
            return str(path)
    raise FileNotFoundError("LOM_PLACEHOLDER_FONT に日本語対応フォントのファイルを指定してください")
