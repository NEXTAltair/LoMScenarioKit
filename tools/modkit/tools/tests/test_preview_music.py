"""Synthetic sound banks: reads stay bounded and caches survive failures."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import preview_music as music


def chunk(kind, data):
    return kind + struct.pack("<I", len(data)) + data


class MusicTests(unittest.TestCase):
    def test_reads_selected_media_and_rejects_invalid_or_truncated_banks(self):
        with TemporaryDirectory() as folder:
            bank = Path(folder) / "Music.bnk"
            index = struct.pack("<IIIIII", 11, 0, 3, 22, 3, 4)
            valid = chunk(b"DIDX", index) + chunk(b"DATA", b"oneTWO!")
            bank.write_bytes(valid)
            self.assertEqual(music.read_wem(bank, 22), b"TWO!")
            self.assertEqual(bank.read_bytes(), valid)
            for invalid in [valid[:-1], valid + b"bad", chunk(b"DIDX", b"bad"),
                            chunk(b"DIDX", struct.pack("<III", 22, 4, 4)) + chunk(b"DATA", b"short"),
                            chunk(b"DIDX", index + index) + chunk(b"DATA", b"oneTWO!")]:
                bank.write_bytes(invalid)
                with self.assertRaises(ValueError):
                    music.read_wem(bank, 22)

    def test_music_collection_follows_external_asset_references(self):
        import UnityPy
        def string(value):
            data = value.encode()
            result = struct.pack("<I", len(data)) + data
            return result + b"\0" * (-len(result) % 4)
        with TemporaryDirectory() as folder:
            data = Path(folder)
            (data / "sharedassets1.assets").touch()
            raw = bytes(0x1c) + string("_MusicDataCollection") + struct.pack("<Iiq", 1, 1, 655)
            item = bytes(0x1c) + string("傳奇_001") + string("Legend1")
            asset = SimpleNamespace(externals=[SimpleNamespace(path="sharedassets1.assets")])
            obj = SimpleNamespace(type=SimpleNamespace(name="MonoBehaviour"), get_raw_data=lambda: raw, assets_file=asset)
            external = SimpleNamespace(objects={655: SimpleNamespace(get_raw_data=lambda: item)})
            with patch.object(UnityPy, "load", side_effect=[SimpleNamespace(objects=[obj]), SimpleNamespace(objects=[SimpleNamespace(assets_file=external)])]):
                self.assertEqual(music.music_event(data, "傳奇_001"), "Music_Legend1")

    def test_cache_reuses_unchanged_track_and_failed_conversion_keeps_previous_track(self):
        with TemporaryDirectory() as folder:
            root = Path(folder)
            game, cache = root / "ゲーム", root / "外部キャッシュ"
            bank_dir = game / "Mortal_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows"
            bank_dir.mkdir(parents=True)
            bank = bank_dir / "Music.bnk"
            bank.write_bytes(chunk(b"DIDX", struct.pack("<III", 22, 0, 4)) + chunk(b"DATA", b"WEM!"))
            (bank_dir / "SoundbanksInfo.xml").write_text('<SoundBanksInfo><SoundBanks><SoundBank><ShortName>Music</ShortName><IncludedEvents><Event Name="Music_Theme1"><IncludedMemoryFiles><File Id="22"/></IncludedMemoryFiles></Event></IncludedEvents></SoundBank></SoundBanks></SoundBanksInfo>')
            decoder, book = root / "decoder", root / "codebook"
            decoder.write_bytes(b"decoder")
            book.write_bytes(b"book")
            before = {p: p.read_bytes() for p in game.rglob("*") if p.is_file()}
            def convert(args, **kwargs):
                Path(args[-1]).write_bytes(b"OggSfixture")
                return subprocess.CompletedProcess(args, 0, stdout=b"")
            with patch.object(music, "decoder_files", return_value=(decoder, book)), patch.object(music, "music_event", return_value="Music_Theme1") as lookup, patch.object(music.subprocess, "run", side_effect=convert) as run:
                first = music.prepare(game, cache, "主題_001")
                self.assertEqual(music.prepare(game, cache, "主題_001"), first)
                self.assertEqual(lookup.call_count, 1)
                self.assertEqual(run.call_count, 1)
                bank.write_bytes(bank.read_bytes() + chunk(b"JUNK", b"updated"))
                run.side_effect = lambda *a, **k: subprocess.CompletedProcess([], 1, stdout=b"bad WEM")
                with self.assertRaisesRegex(ValueError, "conversion failed"):
                    music.prepare(game, cache, "主題_001")
                self.assertEqual(first.read_bytes(), b"OggSfixture")
                self.assertEqual(list(cache.iterdir()), [first])
                with self.assertRaisesRegex(ValueError, "outside"):
                    music.prepare(game, game / "cache", "主題_001")
            for path, value in before.items():
                if path != bank:
                    self.assertEqual(path.read_bytes(), value)

    def test_vendored_converter_matches_pinned_sources(self):
        root = Path(__file__).resolve().parents[2] / "third-party/ww2ogg"
        manifest = json.loads((root / "provenance.json").read_text())
        for name, digest in manifest["sha256"].items():
            self.assertEqual(hashlib.sha256((root / name).read_bytes()).hexdigest(), digest, name)


if __name__ == "__main__":
    unittest.main()
