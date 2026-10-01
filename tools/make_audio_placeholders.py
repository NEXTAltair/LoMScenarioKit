# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
"""サンプル用の仮の音 (曲: 8秒ループの五音音階、効果音: 鈴の音) を wav で作る。依存なし。"""
import math, struct, wave, os, sys

RATE = 44100
out = sys.argv[1] if len(sys.argv) > 1 else "sample/scenario"

def note(freq, t, length):
    if t < 0 or t > length:
        return 0.0
    env = min(1.0, t / 0.01) * math.exp(-3.0 * t / length)
    return env * (math.sin(2 * math.pi * freq * t) + 0.3 * math.sin(4 * math.pi * freq * t) + 0.1 * math.sin(6 * math.pi * freq * t))

def write(path, frames, channels):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(channels); w.setsampwidth(2); w.setframerate(RATE)
        w.writeframes(b"".join(frames))
    print(path)

# 曲: D の五音音階、0.5 秒ごとの旋律 + 低音。8 秒でちょうど一周するのでつなぎ目なくループする
scale = [293.66, 329.63, 369.99, 440.00, 493.88, 587.33]
melody = [0, 2, 3, 5, 4, 3, 2, 1, 0, 1, 2, 4, 3, 2, 1, 0]
bass = [146.83, 146.83, 110.00, 123.47]
length = 8.0
frames = []
for i in range(int(RATE * length)):
    t = i / RATE
    m = sum(note(scale[n], t - k * 0.5, 0.9) for k, n in enumerate(melody) if -0.1 < t - k * 0.5 < 1.0)
    b = sum(note(f, t - k * 2.0, 2.2) for k, f in enumerate(bass) if -0.1 < t - k * 2.0 < 2.3)
    l = 0.22 * m + 0.25 * b
    r = 0.18 * m + 0.28 * b
    frames.append(struct.pack("<hh", int(max(-1, min(1, l)) * 32000), int(max(-1, min(1, r)) * 32000)))
write(os.path.join(out, "music", "lsa_theme.wav"), frames, 2)

# 効果音: 鈴 (モノラル)
frames = []
for i in range(int(RATE * 0.8)):
    t = i / RATE
    v = 0.5 * (note(1318.5, t, 0.8) + 0.6 * note(1975.5, t - 0.08, 0.7))
    frames.append(struct.pack("<h", int(max(-1, min(1, v)) * 32000)))
write(os.path.join(out, "sounds", "lsa_chime.wav"), frames, 1)
