# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the repository game-linking exception.
"""Read a named BGM from the game's Wwise bank into an external OGG cache."""
import hashlib
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def decoder_files():
    folder = Path(sys.executable).parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parents[1] / "third-party/ww2ogg"
    return folder / ("ww2ogg.exe" if os.name == "nt" else "ww2ogg"), folder / "packed_codebooks_aoTuV_603.bin"


def music_event(data, name):
    import UnityPy
    from _probe_assets import _read_str

    environment = UnityPy.load(str(data / "sharedassets2.assets"))
    for obj in environment.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        raw = obj.get_raw_data()
        try:
            title, offset = _read_str(raw, 0x1C)
        except (ValueError, struct.error):
            continue
        if title != "_MusicDataCollection":
            continue
        count, = struct.unpack_from("<I", raw, offset)
        if not 0 < count <= 4096 or offset + 4 + count * 12 > len(raw):
            raise ValueError("Invalid music collection")
        assets = {0: obj.assets_file}
        for index in range(count):
            file_id, path_id = struct.unpack_from("<iq", raw, offset + 4 + index * 12)
            if file_id not in assets:
                if not 1 <= file_id <= len(obj.assets_file.externals):
                    raise ValueError("Invalid music reference")
                filename = obj.assets_file.externals[file_id - 1].path.replace("\\", "/").split("/")[-1]
                path = (data / filename).resolve(strict=True)
                if data.resolve() not in path.parents:
                    raise ValueError("Music reference is outside the game data")
                external = UnityPy.load(str(path))
                assets[file_id] = next(iter(external.objects)).assets_file
            item = assets[file_id].objects[path_id].get_raw_data()
            title, item_offset = _read_str(item, 0x1C)
            if title == name:
                suffix, _ = _read_str(item, item_offset)
                return "Music_" + suffix
        break
    raise ValueError(f"Music not found in game: {name}")


def media_id(xml, event):
    root = ET.parse(xml).getroot()
    for bank in root.findall("./SoundBanks/SoundBank"):
        if bank.findtext("ShortName") != "Music":
            continue
        for item in bank.findall("./IncludedEvents/Event"):
            if item.get("Name") == event:
                files = item.findall("./IncludedMemoryFiles/File")
                if len(files) == 1:
                    return int(files[0].get("Id"))
    raise ValueError(f"Music bank event not found: {event}")


def read_wem(bank, wanted):
    """Validate chunk/index bounds before reading only the selected track."""
    length = bank.stat().st_size
    index = None
    data = None
    with bank.open("rb") as stream:
        while stream.tell() < length:
            header = stream.read(8)
            if len(header) != 8:
                raise ValueError("Truncated music bank header")
            kind, size = struct.unpack("<4sI", header)
            offset = stream.tell()
            if offset + size > length:
                raise ValueError("Truncated music bank chunk")
            if kind == b"DIDX":
                if size % 12 or size > 12 * 100_000:
                    raise ValueError("Invalid music bank index")
                for entry in struct.iter_unpack("<III", stream.read(size)):
                    if entry[0] == wanted:
                        if index is not None:
                            raise ValueError("Duplicate music media ID")
                        index = entry[1:]
            elif kind == b"DATA":
                data = offset, size
            stream.seek(offset + size)
        if index is None or data is None or index[0] + index[1] > data[1]:
            raise ValueError("Music media is missing or outside bank data")
        stream.seek(data[0] + index[0])
        return stream.read(index[1])


def prepare(game, cache, name):
    game = game.resolve(strict=True)
    cache = cache.resolve()
    if cache == game or game in cache.parents:
        raise ValueError("Music cache must be outside the game installation")
    decoder, codebook = decoder_files()
    if not decoder.is_file() or not codebook.is_file():
        raise ValueError("Music decoder is missing; rebuild or reinstall the preview helper")
    data = game / "Mortal_Data"
    folder = data / "StreamingAssets/Audio/GeneratedSoundBanks/Windows"
    bank, xml = folder / "Music.bnk", folder / "SoundbanksInfo.xml"
    # Include all referenced config files and the converter, so game/helper
    # updates cannot reuse a stale track with the same display name.
    inputs = [bank, xml, decoder, codebook, *sorted(data.glob("sharedassets*.assets"))]
    stamp = [(str(path.resolve()), path.stat().st_size, path.stat().st_mtime_ns) for path in inputs]
    key = hashlib.sha256(repr((name, stamp)).encode()).hexdigest()
    cache.mkdir(parents=True, exist_ok=True)
    result = cache / (key + ".ogg")
    if result.is_file() and result.stat().st_size > 4:
        with result.open("rb") as stream:
            if stream.read(4) == b"OggS":
                return result
    event = music_event(data, name)
    media = media_id(xml, event)
    with tempfile.TemporaryDirectory(prefix="music-", dir=cache) as scratch:
        wem = Path(scratch) / "track.wem"
        ogg = Path(scratch) / "track.ogg"
        wem.write_bytes(read_wem(bank, media))
        converted = subprocess.run([str(decoder), str(wem), "--pcb", str(codebook), "-o", str(ogg)],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        if converted.returncode or not ogg.is_file() or ogg.stat().st_size < 4:
            raise ValueError("Music conversion failed: " + converted.stdout.decode(errors="replace")[-2000:])
        ogg.replace(result)
    return result
