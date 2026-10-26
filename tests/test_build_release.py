# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""配布の失敗で古い成果物を更新しないことと、導入時の退避を検証する。"""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("build_release", Path(__file__).resolve().parents[1] / "tools/build_release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / "repo"
        self.game = Path(self.tmp.name) / "game with spaces"
        for rel in ["Mortal.exe", "Mortal_Data/Managed/Mortal.Core.dll", "BepInEx/core/BepInEx.Core.dll"]:
            self.write(self.game / rel, "fixture")
        for rel in release.LEGAL_FILES + ["licenses/MoonSharp.txt", "licenses/GPL-3.0.txt",
                    "licenses/CC-BY-NC-SA-4.0.txt", "licenses/Plugin-DD2.txt", "licenses/lom_modkit.txt",
                    "README.md", ".gitignore", "Directory.Build.props", "Directory.Build.targets",
                    "docs/scenario-guide.md", "sample/scenario/scenario.json", "sample/scenario/LICENSE"]:
            self.write(self.root / rel, "fixture")
        self.write(self.root / "src/LoMScenarioAddon/Plugin.cs", 'BepInPlugin("lom.scenarioaddon", "LoMScenarioAddon", "2.5.00")')
        self.old_zip = self.root / "dist/LoMScenarioAddon-2.5.00.zip"
        self.write(self.old_zip, "previous zip")
        self.write(self.root / "dist/package/previous.txt", "previous package")
        self.addCleanup(patch.stopall)
        patch.object(release, "ROOT", self.root).start()
        patch.object(release, "copy_moonsharp_source", side_effect=self.fake_moonsharp_source).start()

    @staticmethod
    def fake_moonsharp_source(destination):
        with zipfile.ZipFile(destination, "w") as archive:
            archive.writestr("LICENSE", "fixture")

    @staticmethod
    def write(path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def fake_run(self, *args):
        if args[1] != "build":
            return
        out = Path(args[args.index("-o") + 1])
        project = Path(args[2]).name
        self.write(out / f"{project}.dll", "new build")
        if project == "LoMScenarioTool":
            for name in ["LoMScenarioTool.exe", "LoMScenarioTool.exe.config", "MoonSharp.Interpreter.dll"]:
                self.write(out / name, "new build")
        # 参照 DLL がビルド出力にあっても配布 ZIP へ混入させない。
        self.write(out / "Mortal.Core.dll", "must not ship")

    def assert_old_release(self):
        self.assertEqual(self.old_zip.read_text(), "previous zip")
        self.assertTrue((self.root / "dist/package/previous.txt").exists())
        self.assertEqual(list((self.root / "dist").glob("release-*")), [])

    def test_missing_license_stops_before_build(self):
        (self.root / "LICENSE").unlink()
        with patch.object(release, "run") as run:
            with self.assertRaises(RuntimeError):
                release.build(self.game)
            run.assert_not_called()
        self.assert_old_release()

    def test_unavailable_process_list_stops_install(self):
        with patch.object(release.subprocess, "run", side_effect=FileNotFoundError):
            with self.assertRaises(FileNotFoundError):
                release.install(self.root / "dist/package", self.game)
        self.assertFalse((self.game / "LoMScenarioAddon-backups").exists())

    def test_failed_build_preserves_previous_release(self):
        with patch.object(release, "run", side_effect=subprocess.CalledProcessError(1, "dotnet")):
            with self.assertRaises(subprocess.CalledProcessError):
                release.build(self.game)
        self.assert_old_release()

    def test_failed_validation_preserves_previous_release(self):
        def fail_check(*args):
            if args[1] != "build":
                raise subprocess.CalledProcessError(1, "validator")
            self.fake_run(*args)
        with patch.object(release, "run", side_effect=fail_check):
            with self.assertRaises(subprocess.CalledProcessError):
                release.build(self.game)
        self.assert_old_release()

    def test_success_packages_only_selected_binaries_and_docs(self):
        self.write(self.root / "tools/authoring/web/shell.html", "editor")
        self.write(self.root / "tools/authoring/web/app.js", "script")
        self.write(self.root / "tools/authoring/web/editor.css", "style")
        self.write(self.root / "tools/authoring/.authoring/scripts.sqlite3", "private manuscript")
        self.write(self.root / "src/LoMScenarioAddon/obj/secret.json", "must not ship")
        self.write(self.root / "src/LoMScenarioAddon/bin/dependency.dll", "must not ship")
        with patch.object(release, "run", side_effect=self.fake_run):
            release.build(self.game)
        self.assertFalse((self.root / "dist/LoMScenarioAddon-2.5.0.zip").exists())
        with zipfile.ZipFile(self.old_zip) as z:
            names = z.namelist()
            self.assertIn("LoMScenarioAddon-docs/README.md", names)
            self.assertIn("LoMScenarioAddon-docs/licenses/MoonSharp.txt", names)
            self.assertIn("BepInEx/plugins/LoMScenarioAddon/LICENSE", names)
            self.assertIn("BepInEx/plugins/LoMScenarioAddon/LICENSE-GAME-EXCEPTION.txt", names)
            self.assertIn("BepInEx/plugins/LoMScenarioAddon/licenses/GPL-3.0.txt", names)
            self.assertIn("BepInEx/plugins/LoMScenarioAddon/Source/MoonSharp-2.0.0-source.zip", names)
            self.assertFalse(any("Mortal.Core.dll" in n for n in names))
        source = self.root / "dist/package/BepInEx/plugins/LoMScenarioAddon/Source/LoMScenarioKit-source.zip"
        with zipfile.ZipFile(source) as z:
            self.assertIn("src/LoMScenarioAddon/Plugin.cs", z.namelist())
            self.assertIn("LICENSE-GAME-EXCEPTION.txt", z.namelist())
            self.assertIn("sample/scenario/LICENSE", z.namelist())
            self.assertIn("tools/authoring/web/shell.html", z.namelist())
            self.assertIn("tools/authoring/web/app.js", z.namelist())
            self.assertIn("tools/authoring/web/editor.css", z.namelist())
            self.assertFalse(any(".sqlite3" in n for n in z.namelist()))
            self.assertFalse(any("/obj/" in n or "/bin/" in n for n in z.namelist()))
        self.assertFalse((self.root / "dist/package/previous.txt").exists())

    def test_missing_additional_permission_stops_before_build(self):
        (self.root / "LICENSE-GAME-EXCEPTION.txt").unlink()
        with patch.object(release, "run") as run:
            with self.assertRaises(RuntimeError):
                release.build(self.game)
            run.assert_not_called()
        self.assert_old_release()

    def test_install_backs_up_custom_scenario_and_removes_stale_files(self):
        with patch.object(release, "run", side_effect=self.fake_run):
            release.build(self.game)
        custom = "BepInEx/plugins/LoMScenarioAddon/scenario/custom.lua"
        self.write(self.game / custom, "user scenario")
        with patch.object(release, "ensure_game_stopped"):
            release.install(self.root / "dist/package", self.game)
        self.assertFalse((self.game / custom).exists())
        backups = list((self.game / "LoMScenarioAddon-backups").glob("*/" + custom))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_text(), "user scenario")

    def test_install_refuses_running_game(self):
        result = subprocess.CompletedProcess([], 0, stdout=b"Mortal.exe 1234")
        with patch.object(release.subprocess, "run", return_value=result):
            with self.assertRaises(RuntimeError):
                release.install(self.root / "dist/package", self.game)
        self.assertFalse((self.game / "LoMScenarioAddon-backups").exists())


if __name__ == "__main__":
    unittest.main()
