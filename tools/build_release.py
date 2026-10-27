# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""ゲームの DLL を参照してビルドし、検査済みの配布 ZIP を作る。標準ライブラリのみ。"""
import argparse
from datetime import datetime
import io
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
LEGAL_FILES = ["LICENSE", "LICENSE-GAME-EXCEPTION.txt", "THIRD-PARTY-NOTICES.md"]
MOONSHARP_COMMIT = "3154416e535ab96c0d52bf12e3e472985a1532f4"


def copy_moonsharp_source(destination):
    """同梱 DLL 2.0.0 のタグに対応するソースを配布物にも保存する。"""
    url = f"https://codeload.github.com/moonsharp-devs/moonsharp/zip/{MOONSHARP_COMMIT}"
    with urllib.request.urlopen(url, timeout=60) as response:
        data = response.read()
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        prefix = f"moonsharp-{MOONSHARP_COMMIT}/"
        if prefix + "LICENSE" not in archive.namelist() or archive.testzip() is not None:
            raise RuntimeError("MoonSharp のソース ZIP を検証できません")
        # Interpreter のソース・ビルド定義だけを保存する。上流の Unity サンプルや
        # packages/ 内の DLL、開発用バイナリは配布対象ではない。
        with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as out:
            for name in archive.namelist():
                rel = name[len(prefix):] if name.startswith(prefix) else ""
                if rel == "LICENSE" or (rel.startswith("src/MoonSharp.Interpreter/")
                        and Path(rel).suffix in {".cs", ".csproj", ".json", ".xproj"}):
                    out.writestr(rel, archive.read(name))
            out.writestr("SOURCE.txt", f"MoonSharp 2.0.0 Interpreter source and project files\n"
                          f"Upstream commit: {MOONSHARP_COMMIT}\n{url}\n"
                          "Extracted without code changes. Other upstream projects and binaries omitted.\n")


def package_source(destination):
    """現在のビルドのソースを同梱。履歴・生成物・外部資料は取り込まない。"""
    paths = [ROOT / p for p in LEGAL_FILES + ["README.md", ".gitignore", "editor",
             "Directory.Build.props", "Directory.Build.targets"]]
    extensions = {".cs", ".csproj", ".py", ".sh", ".md", ".txt", ".lua",
                  ".json", ".csv", ".png", ".wav", ".rs", ".toml", ".lock", ".ico", ".ps1", ".cmd", ".html", ".svg", ".icns", ".ttc"}
    for folder in ["src", "tools", "tests", "docs", "licenses", "sample"]:
        for path in (ROOT / folder).rglob("*"):
            if any(p in {"bin", "obj", "target", "__pycache__"} or p.startswith(".")
                   for p in path.relative_to(ROOT).parts):
                continue
            embedded = {
                "tools/modkit/third-party/ww2ogg/COPYING",
                "tools/modkit/third-party/ww2ogg/packed_codebooks_aoTuV_603.bin",
                "tools/modkit/editor/assets/doorstop/win-x64-doorstop.dll",
                "tools/modkit/editor/assets/doorstop/win-x86-doorstop.dll",
                "tools/modkit/editor/assets/doorstop/UnityDoorstop-4.3.0-source.zip",
            }
            decoder_source = (path.relative_to(ROOT).as_posix().startswith("tools/modkit/third-party/ww2ogg/src/")
                              and path.suffix in {".c", ".cpp", ".h"})
            if path.is_file() and (path.suffix in extensions or decoder_source or path.name.startswith("LICENSE")
                                   or path.relative_to(ROOT).as_posix() in embedded):
                paths.append(path)
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(paths):
            archive.write(path, path.relative_to(ROOT).as_posix())


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def ensure_game_stopped():
    tasklist = "tasklist" if os.name == "nt" else "/mnt/c/Windows/System32/tasklist.exe"
    result = subprocess.run([tasklist, "/FI", "IMAGENAME eq Mortal.exe", "/NH"],
                            check=True, stdout=subprocess.PIPE)
    if b"mortal.exe" in result.stdout.lower():
        raise RuntimeError("ゲームを終了してから --install を実行してください")


def install(package, game):
    # プロセス一覧を取得できない場合も導入を止める。
    ensure_game_stopped()
    backup = game / "LoMScenarioAddon-backups" / datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    paths = ["BepInEx/patchers/LoMScenarioAddon", "BepInEx/plugins/LoMScenarioAddon",
             "LoMScenarioTool", "LoMScenarioAddon-docs"]
    for rel in paths:
        target = game / rel
        if target.exists():
            saved = backup / rel
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(target), str(saved))
        shutil.copytree(package / rel, target)
    print(f"導入先: {game}\n既存ファイルの退避先: {backup}")


def build(game, do_install=False):
    game = game.expanduser().resolve()
    for rel in ["Mortal.exe", "Mortal_Data/Managed/Mortal.Core.dll", "BepInEx/core/BepInEx.Core.dll"]:
        if not (game / rel).is_file():
            raise RuntimeError(f"必要なファイルがありません: {game / rel}")
    # ライセンスを未決定のまま配布物を生成しない。
    for rel in LEGAL_FILES + ["licenses/MoonSharp.txt", "licenses/GPL-3.0.txt",
                             "licenses/CC-BY-NC-SA-4.0.txt", "licenses/Plugin-DD2.txt",
                             "licenses/lom_modkit.txt", "sample/scenario/LICENSE"]:
        if not (ROOT / rel).is_file():
            raise RuntimeError(f"配布に必要な文書がありません: {rel}")
    if do_install:
        ensure_game_stopped()
    version = re.search(r'BepInPlugin\("lom\.scenarioaddon", "LoMScenarioAddon", "([0-9.]+)"',
                        (ROOT / "src/LoMScenarioAddon/Plugin.cs").read_text(encoding="utf-8")).group(1)
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="release-", dir=dist) as tmp:
        stage = Path(tmp)
        for project in ["LoMScenarioAddon.Patcher", "LoMScenarioAddon", "LoMScenarioTool"]:
            run("dotnet", "build", ROOT / "src" / project, "-c", "Release", "-f", "net472",
                "-t:Rebuild", "-o", stage / project, f"-p:GameDir={game}", "--nologo")
        run("dotnet", "build", ROOT / "src/LoMScenarioTool", "-c", "Release", "-f", "net8.0",
            "-o", stage / "validator", "--nologo")
        run("dotnet", stage / "validator/LoMScenarioTool.dll", "check", ROOT / "sample/scenario", "--no-pause")
        package = stage / "package"
        for project, folder in [("LoMScenarioAddon.Patcher", "patchers"), ("LoMScenarioAddon", "plugins")]:
            target = package / "BepInEx" / folder / "LoMScenarioAddon"
            target.mkdir(parents=True)
            shutil.copy2(stage / project / f"{project}.dll", target)
        plugin = package / "BepInEx/plugins/LoMScenarioAddon"
        shutil.copytree(ROOT / "sample/scenario", plugin / "scenario")
        for name in LEGAL_FILES:
            shutil.copy2(ROOT / name, plugin / name)
        shutil.copytree(ROOT / "licenses", plugin / "licenses")
        source = plugin / "Source"
        source.mkdir()
        package_source(source / "LoMScenarioKit-source.zip")
        copy_moonsharp_source(source / "MoonSharp-2.0.0-source.zip")
        tool = package / "LoMScenarioTool"
        tool.mkdir()
        for name in ["LoMScenarioTool.exe", "LoMScenarioTool.exe.config", "MoonSharp.Interpreter.dll"]:
            shutil.copy2(stage / "LoMScenarioTool" / name, tool)
        docs = package / "LoMScenarioAddon-docs"
        shutil.copytree(ROOT / "docs", docs / "docs")
        shutil.copytree(ROOT / "licenses", docs / "licenses")
        for name in ["README.md"] + LEGAL_FILES:
            shutil.copy2(ROOT / name, docs / name)
        # 配布物ではサンプルへのリンクを実際の導入先へ向ける。
        for path in [docs / "README.md", docs / "docs/scenario-guide.md"]:
            text = path.read_text(encoding="utf-8")
            text = text.replace("](sample/scenario/)", "](../BepInEx/plugins/LoMScenarioAddon/scenario/)")
            text = text.replace("](../sample/scenario/)", "](../../BepInEx/plugins/LoMScenarioAddon/scenario/)")
            path.write_text(text, encoding="utf-8")
        archive = stage / f"LoMScenarioAddon-{version}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            for path in sorted(package.rglob("*")):
                if path.is_file():
                    z.write(path, path.relative_to(package).as_posix())
        # ビルド・検査・梱包がすべて成功した場合だけ既存の配布物を更新する。
        destination = dist / "package"
        if destination.exists():
            shutil.rmtree(destination)
        shutil.move(str(package), str(destination))
        os.replace(archive, dist / archive.name)
        print(f"作成: {dist / archive.name}")
        if do_install:
            install(destination, game)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", default=os.environ.get("LOM_GAME_DIR"))
    parser.add_argument("--install", action="store_true", help="ゲーム終了を確認し、既存ファイルを退避して導入")
    args = parser.parse_args()
    if not args.game_dir:
        parser.error("--game-dir または環境変数 LOM_GAME_DIR でゲームの場所を指定してください")
    try:
        build(Path(args.game_dir), args.install)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as ex:
        parser.exit(1, f"配布処理を中止しました: {ex}\n")


if __name__ == "__main__":
    main()
