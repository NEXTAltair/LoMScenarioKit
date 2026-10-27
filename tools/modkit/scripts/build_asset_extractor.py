#!/usr/bin/env python3
# LoMScenarioKit modifications: Copyright (C) 2026 NEXTAltair, 2026-10-27.
# GPL-3.0-only with the repository game-linking exception; upstream portions retain MIT.
"""Build a self-contained preview helper for the current OS (no game input)."""
import argparse
import importlib.metadata
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import sysconfig


def build_music_decoder(root, output, build):
    source = root / "third-party/ww2ogg"
    name = "ww2ogg.exe" if sys.platform == "win32" else "ww2ogg"
    compiler = shlex.split(os.environ.get("CXX", "cl" if sys.platform == "win32" else "c++"))
    files = [source / "src" / name for name in ["ww2ogg.cpp", "wwriff.cpp", "codebook.cpp", "crc.c"]]
    if Path(compiler[0]).stem.lower() == "cl":
        command = compiler + ["/nologo", "/EHsc", "/O2", "/TP", *map(str, files), "/Fe:" + str(output / name)]
    else:
        command = compiler + ["-O2", *map(str, files), "-o", str(output / name)]
    subprocess.run(command, check=True, cwd=build)
    shutil.copy2(source / "packed_codebooks_aoTuV_603.bin", output)
    notices = output / "third-party-licenses"
    notices.mkdir(exist_ok=True)
    shutil.copy2(source / "COPYING", notices / "ww2ogg-LICENSE.txt")
    shutil.copy2(source / "provenance.json", notices / "ww2ogg-provenance.json")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        import UnityPy
        import PyInstaller
    except ImportError as error:
        raise SystemExit("Missing build dependency. Run: python -m pip install -r tools/requirements-preview-extractor.txt") from error
    if UnityPy.__version__ != "1.24.2" or PyInstaller.__version__ != "6.22.3":
        raise SystemExit("Extractor build versions differ; install tools/requirements-preview-extractor.txt")
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    build = args.work_dir.resolve()
    build.mkdir(parents=True, exist_ok=True)
    subprocess.run([
        sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onedir",
        "--name", "lom-asset-extractor", "--distpath", str(output),
        "--workpath", str(build / "work"), "--specpath", str(build),
        "--paths", str(root / "tools"), "--collect-all", "UnityPy",
        # UnityPy.export imports AudioClipConverter eagerly, even for Sprite.image.
        # fmod_toolkit loads this platform's native library via ctypes at import.
        "--collect-all", "fmod_toolkit",
        # Keep archspec's runtime CPU tables, excluding its test fixtures.
        "--additional-hooks-dir", str(root / "scripts/pyinstaller-hooks"),
        "--hidden-import", "archspec",
        "--hidden-import", "PIL.PngImagePlugin", str(root / "tools/preview_asset_worker.py"),
    ], check=True, cwd=root)
    name = "lom-asset-extractor.exe" if sys.platform == "win32" else "lom-asset-extractor"
    build_music_decoder(root, output / "lom-asset-extractor", build)
    subprocess.run([str(output / "lom-asset-extractor" / name), "--help"], check=True)
    subprocess.run([str(output / "lom-asset-extractor" / name), "--self-check"], check=True)
    # Ship the helper's dependency notices, including PyInstaller's bootloader
    # exception. No game data or decompiled source is collected here.
    notices = output / "lom-asset-extractor/third-party-licenses"
    notices.mkdir(exist_ok=True)
    python_license = next((path for path in [
        Path(sys.base_prefix) / "LICENSE.txt",
        Path(sysconfig.get_path("stdlib")) / "LICENSE.txt",
    ] if path.is_file()), None)
    if python_license is None:
        raise SystemExit("Python's LICENSE.txt is missing from the build interpreter")
    (notices / "Python-LICENSE.txt").write_bytes(python_license.read_bytes())
    from packaging.requirements import Requirement
    pending = ["UnityPy", "PyInstaller"]
    seen = set()
    while pending:
        package = pending.pop()
        distribution = importlib.metadata.distribution(package)
        canonical = distribution.metadata["Name"]
        if canonical in seen:
            continue
        seen.add(canonical)
        for requirement in distribution.requires or []:
            parsed = Requirement(requirement)
            if parsed.marker is None or parsed.marker.evaluate():
                pending.append(parsed.name)
        for file in distribution.files or []:
            if any(marker in file.name.lower() for marker in ["license", "copying", "copyright"]):
                source = distribution.locate_file(file)
                if source.is_file():
                    target = notices / (canonical + "-" + "-".join(file.parts))
                    target.write_bytes(source.read_bytes())
    (notices / "dependencies.txt").write_text("\n".join(sorted(seen)) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
