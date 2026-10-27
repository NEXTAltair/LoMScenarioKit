#!/usr/bin/env python3
# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""Prepare the preview helper, or build a portable native editor for this OS."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import venv

ROOT = Path(__file__).resolve().parents[1]
MODKIT = ROOT / "tools/modkit"
HELPER_INPUTS = [
    "tools/preview_asset_worker.py", "tools/_probe_assets.py",
    "tools/requirements-preview-extractor.txt", "scripts/build_asset_extractor.py",
    "scripts/pyinstaller-hooks/hook-archspec.py",
]
LEGAL_FILES = ["LICENSE", "LICENSE-GAME-EXCEPTION.txt", "THIRD-PARTY-NOTICES.md"]


def helper_name():
    return "lom-asset-extractor.exe" if os.name == "nt" else "lom-asset-extractor"


def target_directory():
    # Ask Cargo so CARGO_TARGET_DIR and repository/user Cargo configuration agree.
    result = subprocess.run([
        "cargo", "metadata", "--no-deps", "--format-version", "1", "--locked",
        "--manifest-path", str(MODKIT / "Cargo.toml"),
    ], check=True, stdout=subprocess.PIPE, text=True)
    return Path(json.loads(result.stdout)["target_directory"])


def native_target():
    result = subprocess.run([os.environ.get("RUSTC", "rustc"), "-vV"],
                            check=True, stdout=subprocess.PIPE, text=True)
    for line in result.stdout.splitlines():
        if line.startswith("host: "):
            return line.removeprefix("host: ").strip()
    raise RuntimeError("Rustコンパイラーの実行環境を確認できませんでした。")


def fingerprint():
    digest = hashlib.sha256()
    digest.update(f"{sys.version_info[:2]}:{sys.platform}:{platform.machine()}".encode())
    for path in [Path(__file__), *(MODKIT / p for p in HELPER_INPUTS)]:
        digest.update(path.name.encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def current_helper(folder, expected):
    try:
        state = json.loads((folder / "build-state.json").read_text())
        return (state.get("fingerprint") == expected
                and (folder / helper_name()).is_file()
                and (folder / "_internal").is_dir()
                and (folder / "third-party-licenses/dependencies.txt").is_file())
    except (OSError, ValueError):
        return False


@contextmanager
def build_lock(path):
    """Serialize preparation across two launchers without leaving stale locks."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as lock:
        if os.name == "nt":
            import msvcrt
            lock.write(b"\0")
            lock.flush()
            deadline = time.monotonic() + 600
            while True:
                lock.seek(0)
                try:
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    if time.monotonic() >= deadline:
                        raise RuntimeError("別の抽出ツール準備が終了するまで待ってから再実行してください。")
                    time.sleep(0.2)
        else:
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            if os.name == "nt":
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock, fcntl.LOCK_UN)


def run_logged(args, log):
    subprocess.run([str(a) for a in args], check=True, stdout=log, stderr=subprocess.STDOUT)


def replace_directory(staged, destination):
    """Publish a checked directory, restoring the previous one on a rename error."""
    backup = None
    if destination.exists():
        backup = Path(tempfile.mkdtemp(prefix=destination.name + "-previous-", dir=destination.parent))
        backup.rmdir()
        destination.rename(backup)
    try:
        staged.rename(destination)
    except BaseException:
        if backup is not None:
            backup.rename(destination)
        raise
    if backup is not None:
        shutil.rmtree(backup)


def prepare_helper(target, profile, build_target):
    target = Path(target)
    destination = target / build_target / profile / "asset-extractor"
    expected = fingerprint()
    with build_lock(target / "asset-extractor-build/prepare.lock"):
        if current_helper(destination, expected):
            return destination
        print("プレビュー素材の抽出ツールを準備しています（初回・更新時のみ）…", file=sys.stderr)
        build = target / "asset-extractor-build"
        build.mkdir(parents=True, exist_ok=True)
        log_path = build / "build.log"
        environment = build / f"venv-{sys.version_info.major}.{sys.version_info.minor}"
        python = environment / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            with log_path.open("w", encoding="utf-8") as log:
                # Also repairs an environment whose first setup was interrupted.
                venv.EnvBuilder(with_pip=True).create(environment)
                run_logged([python, "-m", "pip", "install", "--disable-pip-version-check",
                            "-r", MODKIT / "tools/requirements-preview-extractor.txt"], log)
                # Build next to the destination. Only complete, self-checked bundles
                # replace a previous working helper; errors leave it untouched.
                with tempfile.TemporaryDirectory(prefix="extractor-staging-", dir=destination.parent) as temporary:
                    staging = Path(temporary)
                    run_logged([python, MODKIT / "scripts/build_asset_extractor.py",
                                "--output-dir", staging / "dist", "--work-dir", staging / "work"], log)
                    bundle = staging / "dist/lom-asset-extractor"
                    run_logged([bundle / helper_name(), "--self-check"], log)
                    (bundle / "build-state.json").write_text(
                        json.dumps({"fingerprint": expected}, indent=2) + "\n", encoding="utf-8")
                    replace_directory(bundle, destination)
        except (OSError, subprocess.CalledProcessError) as error:
            raise RuntimeError(f"抽出ツールを準備できませんでした。ログ: {log_path}\n{error}") from error
        print("抽出ツールの準備ができました。", file=sys.stderr)
    return destination


def package_editor(target, helper, output, build_target):
    output = Path(output).resolve()
    if output.exists():
        raise RuntimeError(f"配布先は新しいフォルダーを指定してください: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    suffix = ".exe" if os.name == "nt" else ""
    with tempfile.TemporaryDirectory(prefix="editor-package-", dir=output.parent) as temporary:
        package = Path(temporary) / "LoMScenarioKit"
        package.mkdir()
        for name in ["lom-editor", "lomc"]:
            shutil.copy2(Path(target) / build_target / "release" / (name + suffix), package / (name + suffix))
        # Follow PyInstaller's internal symlinks: plain folder copies and ZIPs
        # must work on machines where symlink creation is unavailable.
        shutil.copytree(helper, package / "asset-extractor")
        for name in LEGAL_FILES:
            shutil.copy2(ROOT / name, package / name)
        shutil.copytree(ROOT / "licenses", package / "licenses")
        shutil.copytree(ROOT / "docs", package / "docs")
        shutil.copy2(MODKIT / "LICENSE", package / "licenses/lom_modkit-source.txt")
        fonts = MODKIT / "rust/lom-editor/assets/fonts"
        (package / "licenses/noto-sans-cjk").mkdir()
        for name in ["OFL.txt", "README.md"]:
            shutil.copy2(fonts / name, package / "licenses/noto-sans-cjk" / name)
        (package / "README.txt").write_text(
            "LoMScenarioKit\n\nlom-editor を起動してください（Windows: lom-editor.exe）。\n"
            "asset-extractor フォルダーはエディターと一緒に保持してください。\n"
            "Pythonのインストールは不要です。\n"
            "ファイル → ゲームからプレビュー素材を抽出… でゲームの場所を指定します。\n"
            "抽出画像はローカルキャッシュへ保存され、この配布フォルダーには入りません。\n"
            "詳しくは docs/native-editor.md を参照してください。\n", encoding="utf-8")
        # A source archive also carries the unmodified helper inputs, provenance,
        # and rebuild instructions. Generated caches and game assets are excluded.
        from build_release import package_source
        package_source(package / "LoMScenarioKit-source.zip")
        subprocess.run([str(package / "asset-extractor" / helper_name()), "--self-check"],
                       check=True, stdout=subprocess.DEVNULL)
        package.rename(output)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-extractor", action="store_true", help="Prepare only the helper for cargo run")
    parser.add_argument("--profile", choices=["debug", "release"], default="debug")
    parser.add_argument("--output-dir", type=Path, help="New directory for a portable release build")
    parser.add_argument("--print-target", action="store_true", help="Print the native target for the source launcher")
    args = parser.parse_args()
    if sys.version_info < (3, 12):
        parser.error("抽出ツールのビルドにはPython 3.12以降を使ってください。")
    if args.print_target and not args.prepare_extractor:
        parser.error("--print-target は --prepare-extractor と一緒に指定してください。")
    target = target_directory()
    build_target = native_target()
    profile = args.profile if args.prepare_extractor else "release"
    # Both executables run here; override build.target and CARGO_BUILD_TARGET.
    helper = prepare_helper(target, profile, build_target)
    if args.prepare_extractor:
        if args.print_target:
            print(build_target)
        return
    subprocess.run(["cargo", "build", "--quiet", "--locked", "--release", "--manifest-path",
                    str(MODKIT / "Cargo.toml"), "--target", build_target,
                    "-p", "lom-editor", "-p", "lomc"], check=True)
    output = args.output_dir or ROOT / "dist" / f"LoMScenarioKit-editor-{sys.platform}-{platform.machine()}"
    print(f"エディターの配布フォルダー: {package_editor(target, helper, output, build_target)}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
