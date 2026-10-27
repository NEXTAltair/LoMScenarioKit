# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""Helper preparation is reusable and cannot destroy an existing working build."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("build_editor", Path(__file__).resolve().parents[1] / "tools/build_editor.py")
editor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(editor)


class EditorBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "source with spaces"
        self.modkit = self.root / "tools/modkit"
        self.target = Path(self.temp.name) / "target with spaces"
        for name in editor.HELPER_INPUTS:
            self.write(self.modkit / name, "fixture")
        self.addCleanup(patch.stopall)
        patch.object(editor, "ROOT", self.root).start()
        patch.object(editor, "MODKIT", self.modkit).start()
        patch.object(editor.venv.EnvBuilder, "create", side_effect=self.fake_venv).start()
        patch("sys.stderr", new=io.StringIO()).start()

    @staticmethod
    def write(path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def fake_venv(self, folder):
        self.write(folder / ("Scripts/python.exe" if editor.os.name == "nt" else "bin/python"), "python")

    def fake_command(self, args, log):
        if "--output-dir" in args:
            folder = Path(args[args.index("--output-dir") + 1]) / "lom-asset-extractor"
            self.write(folder / editor.helper_name(), "new executable")
            self.write(folder / "_internal/runtime", "runtime")
            self.write(folder / "third-party-licenses/dependencies.txt", "license fixtures")

    def test_initial_prepare_reuses_exact_inputs_and_builds_in_private_environment(self):
        with patch.object(editor, "run_logged", side_effect=self.fake_command) as run:
            first = editor.prepare_helper(self.target, "debug")
            calls = run.call_args_list.copy()
            second = editor.prepare_helper(self.target, "debug")
            self.assertEqual(first, second)
            self.assertEqual(run.call_args_list, calls, "unchanged startup must not install or rebuild")
        self.assertEqual(first.parent, self.target / "debug")
        install = calls[0].args[0]
        self.assertIn(str(self.target), str(install[0]))
        self.assertEqual(install[1:4], ["-m", "pip", "install"])
        self.assertTrue(editor.current_helper(first, editor.fingerprint()))
        self.assertEqual(list(first.parent.glob("extractor-staging-*")), [])

    def test_changed_worker_rebuilds_and_failed_update_preserves_old_helper(self):
        with patch.object(editor, "run_logged", side_effect=self.fake_command):
            folder = editor.prepare_helper(self.target, "debug")
        old_state = (folder / "build-state.json").read_bytes()
        self.write(folder / "keep.txt", "previous working build")
        self.write(self.modkit / "tools/preview_asset_worker.py", "updated worker")
        with patch.object(editor, "run_logged", side_effect=subprocess.CalledProcessError(1, "fixture")):
            with self.assertRaisesRegex(RuntimeError, "ログ"):
                editor.prepare_helper(self.target, "debug")
        self.assertEqual((folder / "keep.txt").read_text(), "previous working build")
        self.assertEqual((folder / "build-state.json").read_bytes(), old_state)
        self.assertEqual(list(folder.parent.glob("extractor-staging-*")), [])
        with patch.object(editor, "run_logged", side_effect=self.fake_command):
            editor.prepare_helper(self.target, "debug")
        self.assertFalse((folder / "keep.txt").exists())
        self.assertNotEqual((folder / "build-state.json").read_bytes(), old_state)

    def test_self_check_failure_never_publishes_staged_helper(self):
        with patch.object(editor, "run_logged", side_effect=self.fake_command):
            folder = editor.prepare_helper(self.target, "debug")
        state = (folder / "build-state.json").read_bytes()
        self.write(self.modkit / "tools/preview_asset_worker.py", "updated worker")

        def fail_check(args, log):
            self.fake_command(args, log)
            if "--self-check" in args:
                raise subprocess.CalledProcessError(1, "self-check")

        with patch.object(editor, "run_logged", side_effect=fail_check):
            with self.assertRaises(RuntimeError):
                editor.prepare_helper(self.target, "debug")
        self.assertEqual((folder / "build-state.json").read_bytes(), state)
        self.assertEqual(list(folder.parent.glob("extractor-staging-*")), [])

    def test_directory_publish_failure_restores_previous_build(self):
        destination = self.target / "asset-extractor"
        staged = self.target / "staging"
        self.write(destination / "old.txt", "keep")
        self.write(staged / "new.txt", "new")
        rename = Path.rename

        def fail_staging(path, target):
            if path == staged:
                raise OSError("rename failed")
            return rename(path, target)

        with patch.object(Path, "rename", new=fail_staging):
            with self.assertRaises(OSError):
                editor.replace_directory(staged, destination)
        self.assertEqual((destination / "old.txt").read_text(), "keep")
        self.assertEqual((staged / "new.txt").read_text(), "new")

    def test_target_directory_uses_cargo_metadata_without_splitting_paths(self):
        result = subprocess.CompletedProcess([], 0, stdout=json.dumps({"target_directory": str(self.target)}))
        with patch.object(editor.subprocess, "run", return_value=result) as run:
            self.assertEqual(editor.target_directory(), self.target)
        self.assertEqual(run.call_args.args[0][-1], str(self.modkit / "Cargo.toml"))

    def test_package_refuses_an_existing_output_folder(self):
        output = self.root / "existing output"
        self.write(output / "user.txt", "keep")
        with self.assertRaisesRegex(RuntimeError, "新しいフォルダー"):
            editor.package_editor(self.target, self.target / "helper", output)
        self.assertEqual((output / "user.txt").read_text(), "keep")


if __name__ == "__main__":
    unittest.main()
