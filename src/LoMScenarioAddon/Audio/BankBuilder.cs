// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LoMScenarioAddon.Audio
{
    /// <summary>
    /// Wwise 2021.1 (バンク版 140) の SoundBank を組み立てる。Wwise のオーサリングツールは使わない。
    /// 中身は最小限: 音 (PCM を埋め込んだ Sound)・停止/再生の Action・Event だけ。
    /// バスや音量パラメータは本編の Init.bnk にあるものを名前 (= ID) で指す。
    /// 各オブジェクトの並びは本編の SFX.bnk / Music.bnk を wwiser で読んだものに合わせてある。
    /// </summary>
    internal sealed class BankBuilder
    {
        public const uint BankVersion = 140;
        // 本編のバンク頭と同じ値 (非ローカライズの言語 ID = "SFX"、プロジェクト ID)
        private const uint LanguageSfx = 393239870;
        private const uint ProjectId = 10403;
        private const uint PluginPcm = 0x00010001;
        private const int MediaAlignment = 16;

        internal sealed class Entry
        {
            /// <summary>本編に投げるイベント名 (例: Music_LSA_theme)。ID はこの名前のハッシュ。</summary>
            public string EventName;
            /// <summary>出力先のバス名 (Music / SFX / Env)。</summary>
            public string Bus;
            /// <summary>再生前に同じバスの音を止める (本編の曲・環境音のイベントと同じ)。</summary>
            public bool StopBusFirst;
            /// <summary>無限ループ。</summary>
            public bool Loop;
            public WavFile Wav;
        }

        private readonly string _bankName;
        private readonly List<Entry> _entries = new List<Entry>();

        public BankBuilder(string bankName)
        {
            _bankName = bankName;
        }

        public uint BankId => Hash(_bankName);

        public void Add(Entry entry) => _entries.Add(entry);

        /// <summary>Wwise の短い ID: 小文字にした名前の FNV-1 32bit。本編のバス・イベント ID もこれで一致する。</summary>
        public static uint Hash(string name)
        {
            uint h = 2166136261;
            foreach (byte c in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            {
                h *= 16777619;
                h ^= c;
            }
            return h;
        }

        // 自前の中身の ID (本編と重ならないよう名前空間を付けてハッシュ)
        private static uint LocalId(string kind, string eventName) => Hash("LoMScenarioAddon/" + kind + "/" + eventName);

        public byte[] Build()
        {
            var media = _entries.Select(e => (Id: LocalId("media", e.EventName), Wem: BuildWem(e.Wav))).OrderBy(m => m.Id).ToList();
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);

            // BKHD。DATA の中身が 16 バイト境界に来るように末尾を詰める (本編のバンクも同じ)
            int didxSize = media.Count * 12;
            int pad = (MediaAlignment - (8 + 20 + 8 + didxSize + 8) % MediaAlignment) % MediaAlignment;
            Chunk(w, "BKHD", 20 + pad, () =>
            {
                w.Write(BankVersion);
                w.Write(BankId);
                w.Write(LanguageSfx);
                w.Write((uint)MediaAlignment); // uAltValues: alignment=16, deviceAllocated=0
                w.Write(ProjectId);
                w.Write(new byte[pad]);
            });

            var offsets = new List<int>();
            int offset = 0;
            foreach (var m in media)
            {
                offsets.Add(offset);
                offset += m.Wem.Length;
                if (m != media[media.Count - 1])
                    offset += (MediaAlignment - offset % MediaAlignment) % MediaAlignment;
            }
            Chunk(w, "DIDX", didxSize, () =>
            {
                for (int i = 0; i < media.Count; i++)
                {
                    w.Write(media[i].Id);
                    w.Write((uint)offsets[i]);
                    w.Write((uint)media[i].Wem.Length);
                }
            });
            Chunk(w, "DATA", offset, () =>
            {
                long start = ms.Position;
                for (int i = 0; i < media.Count; i++)
                {
                    w.Write(new byte[start + offsets[i] - ms.Position]);
                    w.Write(media[i].Wem);
                }
            });

            var objects = new List<byte[]>();
            foreach (var e in _entries)
            {
                uint mediaId = LocalId("media", e.EventName);
                int wemSize = media.First(m => m.Id == mediaId).Wem.Length;
                uint soundId = LocalId("sound", e.EventName);
                objects.Add(HircObject(0x02, o => WriteSound(o, soundId, mediaId, wemSize, Hash(e.Bus), e.Loop)));
                var actions = new List<uint>();
                if (e.StopBusFirst)
                {
                    uint stopId = LocalId("stop", e.EventName);
                    objects.Add(HircObject(0x03, o => WriteStopBus(o, stopId, Hash(e.Bus))));
                    actions.Add(stopId);
                }
                uint playId = LocalId("play", e.EventName);
                objects.Add(HircObject(0x03, o => WritePlay(o, playId, soundId, BankId)));
                actions.Add(playId);
                objects.Add(HircObject(0x04, o =>
                {
                    o.Write(Hash(e.EventName));
                    o.Write((byte)actions.Count);
                    foreach (var a in actions)
                        o.Write(a);
                }));
            }
            Chunk(w, "HIRC", 4 + objects.Sum(o => o.Length), () =>
            {
                w.Write((uint)objects.Count);
                foreach (var o in objects)
                    w.Write(o);
            });
            w.Flush();
            return ms.ToArray();
        }

        private static void Chunk(BinaryWriter w, string tag, int size, Action body)
        {
            w.Write(Encoding.ASCII.GetBytes(tag));
            w.Write((uint)size);
            long start = w.BaseStream.Position;
            body();
            if (w.BaseStream.Position - start != size)
                throw new InvalidOperationException($"{tag}: wrote {w.BaseStream.Position - start} bytes, expected {size}");
        }

        private static byte[] HircObject(byte type, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var o = new BinaryWriter(ms);
            body(o);
            o.Flush();
            byte[] content = ms.ToArray();
            var result = new byte[5 + content.Length];
            result[0] = type;
            BitConverter.GetBytes((uint)content.Length).CopyTo(result, 1);
            content.CopyTo(result, 5);
            return result;
        }

        // CAkSound (v140)。親なしで出力バスを直接指定する
        private static void WriteSound(BinaryWriter o, uint id, uint mediaId, int mediaSize, uint busId, bool loop)
        {
            o.Write(id);
            o.Write(PluginPcm);
            o.Write((byte)0);            // StreamType: バンク埋め込み
            o.Write(mediaId);            // sourceID
            o.Write((uint)mediaSize);    // uInMemoryMediaSize
            o.Write((byte)0);            // uSourceBits
            o.Write((byte)0);            // bIsOverrideParentFX
            o.Write((byte)0);            // uNumFx
            o.Write((byte)0);            // bIsOverrideParentMetadata
            o.Write((byte)0);            // uNumFx
            o.Write((byte)0);            // bOverrideAttachmentParams
            o.Write(busId);              // OverrideBusId
            o.Write(0u);                 // DirectParentID
            o.Write((byte)0);            // byBitVector (priority / midi)
            if (loop)
            {
                o.Write((byte)1);        // cProps
                o.Write((byte)0x3A);     // Loop
                o.Write(0);              // 0 = 無限
            }
            else
            {
                o.Write((byte)0);
            }
            o.Write((byte)0);            // ranged modifiers
            o.Write((byte)0);            // positioning (2D)
            o.Write((byte)0);            // aux
            o.Write(0u);                 // reflectionsAuxBus
            o.Write((byte)0);            // adv settings
            o.Write((byte)1);            // eVirtualQueueBehavior = FromElapsedTime
            o.Write((ushort)0);          // u16MaxNumInstance
            o.Write((byte)0);            // eBelowThresholdBehavior = ContinueToPlay
            o.Write((byte)0);            // hdr / loudness
            o.Write((byte)0);            // ulNumStateProps
            o.Write((byte)0);            // ulNumStateGroups
            o.Write((ushort)0);          // ulNumRTPC
        }

        // CAkActionStop: Stop_E_O (投げたゲームオブジェクト上の、そのバスの音を止める)
        private static void WriteStopBus(BinaryWriter o, uint id, uint busId)
        {
            o.Write(id);
            o.Write((ushort)0x0103);
            o.Write(busId);
            o.Write((byte)0x01);         // bIsBus
            o.Write((byte)0);            // props
            o.Write((byte)0);            // ranged
            o.Write((byte)0x04);         // fade curve = Linear
            o.Write((byte)0x06);         // applyToStateTransitions | applyToDynamicSequence
            o.Write((byte)0);            // exceptions
        }

        // CAkActionPlay
        private static void WritePlay(BinaryWriter o, uint id, uint target, uint bankId)
        {
            o.Write(id);
            o.Write((ushort)0x0403);
            o.Write(target);
            o.Write((byte)0);
            o.Write((byte)0);
            o.Write((byte)0);
            o.Write((byte)0x04);
            o.Write(bankId);
        }

        /// <summary>
        /// PCM の wem。本編の SFX.bnk の中身と同じ形 (fmt は WAVE_FORMAT_EXTENSIBLE 風の 24 バイト + Wwise のチャンネル設定、JUNK で data を 64 バイト目に)。
        /// </summary>
        public static byte[] BuildWem(WavFile wav)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int blockAlign = wav.Channels * 2;
            // AkChannelConfig: 下位8bit=チャンネル数、8〜11bit=種類 (1=標準)、12bit〜=スピーカー配置 (モノ=FC, ステレオ=FL|FR)
            uint channelConfig = (uint)wav.Channels | (1u << 8) | ((wav.Channels == 1 ? 0x4u : 0x3u) << 12);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(0u); // 後で埋める
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(24u);
            w.Write((ushort)0xFFFE);
            w.Write((ushort)wav.Channels);
            w.Write((uint)wav.SampleRate);
            w.Write((uint)(wav.SampleRate * blockAlign));
            w.Write((ushort)blockAlign);
            w.Write((ushort)16);
            w.Write((ushort)6);
            w.Write((ushort)0);
            w.Write(channelConfig);
            if (wav.SampleChunk != null)
            {
                w.Write(Encoding.ASCII.GetBytes("smpl"));
                w.Write((uint)wav.SampleChunk.Length);
                w.Write(wav.SampleChunk);
                if ((wav.SampleChunk.Length & 1) != 0)
                    w.Write((byte)0);
            }
            // data の中身が 16 バイト境界に来るよう JUNK で詰める (本編は 4 バイトの JUNK で 64 バイト目)
            int junk = (int)((16 - (ms.Position + 8 + 8) % 16) % 16);
            w.Write(Encoding.ASCII.GetBytes("JUNK"));
            w.Write((uint)junk);
            w.Write(new byte[junk]);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((uint)wav.Pcm16.Length);
            w.Write(wav.Pcm16);
            w.Flush();
            byte[] result = ms.ToArray();
            BitConverter.GetBytes((uint)(result.Length - 8)).CopyTo(result, 4);
            return result;
        }
    }
}
