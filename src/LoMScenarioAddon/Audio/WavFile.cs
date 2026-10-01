// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.IO;
using System.Text;

namespace LoMScenarioAddon.Audio
{
    /// <summary>
    /// wav を 16bit PCM に読み込む (8 / 16 / 24 / 32bit 整数・32bit 浮動小数に対応、1〜2ch)。
    /// ループ位置の smpl チャンクがあればそのまま持ち越す。
    /// </summary>
    internal sealed class WavFile
    {
        public int Channels;
        public int SampleRate;
        /// <summary>16bit リトルエンディアン、チャンネル交互。</summary>
        public byte[] Pcm16;
        /// <summary>smpl チャンクの中身 (ヘッダーを除く)。無ければ null。</summary>
        public byte[] SampleChunk;

        public double Seconds => Pcm16.Length / 2.0 / Channels / SampleRate;

        public static WavFile Read(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
                throw new InvalidDataException("not a RIFF/WAVE file");
            int format = 0, channels = 0, rate = 0, bits = 0;
            byte[] data = null, smpl = null;
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > b.Length)
                    size = b.Length - body; // 壊れた長さ (書き出し途中など) は末尾まで
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (format == 0xFFFE && size >= 40)
                        format = BitConverter.ToUInt16(b, body + 24); // WAVE_FORMAT_EXTENSIBLE の SubFormat
                }
                else if (id == "data")
                {
                    data = new byte[size];
                    Buffer.BlockCopy(b, body, data, 0, size);
                }
                else if (id == "smpl")
                {
                    smpl = new byte[size];
                    Buffer.BlockCopy(b, body, smpl, 0, size);
                }
                pos = body + size + (size & 1);
            }
            if (data == null || channels == 0)
                throw new InvalidDataException("missing fmt or data chunk");
            if (channels > 2)
                throw new InvalidDataException($"{channels} channels (only mono / stereo)");
            if (rate < 8000 || rate > 96000)
                throw new InvalidDataException($"sample rate {rate}");
            return new WavFile { Channels = channels, SampleRate = rate, Pcm16 = To16(data, format, bits), SampleChunk = smpl };
        }

        private static byte[] To16(byte[] src, int format, int bits)
        {
            if (format == 1 && bits == 16)
                return src;
            int bytes = bits / 8;
            if (!(format == 1 && (bits == 8 || bits == 24 || bits == 32)) && !(format == 3 && bits == 32))
                throw new InvalidDataException($"unsupported sample format {format} / {bits}bit (use PCM or float)");
            int n = src.Length / bytes;
            var dst = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                int o = i * bytes;
                int v;
                if (format == 3)
                    v = (int)Math.Round(Math.Max(-1f, Math.Min(1f, BitConverter.ToSingle(src, o))) * 32767f);
                else if (bits == 8)
                    v = (src[o] - 128) << 8;
                else if (bits == 24)
                    v = (short)(src[o + 1] | (src[o + 2] << 8));
                else
                    v = (short)(BitConverter.ToInt32(src, o) >> 16);
                dst[i * 2] = (byte)v;
                dst[i * 2 + 1] = (byte)(v >> 8);
            }
            return dst;
        }
    }
}
