// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using LoMScenarioAddon.Audio;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// BGM・効果音・環境音の追加。
    /// scenario/music|sounds|env/*.wav から Wwise の SoundBank をその場で組み立てて読み込み、
    /// 本編の名前→キー表 (MusicData / SoundData / EnvSoundData) に足す。
    /// 鳴らすのは本編と同じ Wwise なので、音量スライダー・フェード・曲の切替・StopMusic はすべて本編どおり効く。
    /// Lua: luamanager.PlayMusic("&lt;ファイル名&gt;") / PlaySound / PlayEnvSound。
    /// </summary>
    internal static class ScenarioAudio
    {
        private const string BankName = "LoMScenarioAddon";
        private const string KeyPrefix = "LSA_";

        private sealed class Kind
        {
            public string Folder;
            public string EventPrefix; // 本編の SoundManager が付ける接頭辞
            public string Bus;
            public bool Loop;
            public bool StopBusFirst;
            public readonly List<string> Names = new List<string>();
        }

        // 本編の Music_* / Env_* イベントは「同じバスを止めてから再生」、Sound_* は再生だけ
        private static readonly Kind Music = new Kind { Folder = "music", EventPrefix = "Music_", Bus = "Music", Loop = true, StopBusFirst = true };
        private static readonly Kind Sounds = new Kind { Folder = "sounds", EventPrefix = "Sound_", Bus = "SFX" };
        private static readonly Kind Env = new Kind { Folder = "env", EventPrefix = "Env_", Bus = "Env", Loop = true, StopBusFirst = true };

        private static string _dir;
        private static bool _loaded;
        private static bool _failed;
        private static readonly Dictionary<string, ScriptableObject> _data = new Dictionary<string, ScriptableObject>();

        internal static void Configure(string scenarioDir)
        {
            _dir = scenarioDir;
            foreach (var kind in new[] { Music, Sounds, Env })
            {
                string dir = Path.Combine(scenarioDir, kind.Folder);
                if (!Directory.Exists(dir))
                    continue;
                foreach (var path in Directory.GetFiles(dir, "*.wav").OrderBy(p => p, StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!ScenarioManifest.IdPattern.IsMatch(name))
                    {
                        Plugin.Log.LogWarning($"audio: '{kind.Folder}/{name}.wav' skipped (file names must be lower-case letters, digits and _)");
                        continue;
                    }
                    kind.Names.Add(name);
                }
            }
        }

        internal static bool HasAny => Music.Names.Count + Sounds.Names.Count + Env.Names.Count > 0;

        /// <summary>場面の読み込みごと。初回に SoundBank を読み込み、名前→キー表に自前の音を足す (足してあれば何もしない)。</summary>
        internal static void OnSceneLoaded()
        {
            if (!HasAny)
                return;
            if (!_loaded && !_failed)
                TryLoadBank();
            if (!_loaded)
                return;
            Register<MusicDataCollection, MusicData>(Music);
            Register<SoundDataCollection, SoundData>(Sounds);
            Register<EnvSoundDataCollection, EnvSoundData>(Env);
        }

        private static void TryLoadBank()
        {
            if (!AkSoundEngine.IsInitialized())
                return;
            byte[] bank;
            try
            {
                var builder = new BankBuilder(BankName);
                foreach (var kind in new[] { Music, Sounds, Env })
                {
                    foreach (var name in kind.Names.ToList())
                    {
                        try
                        {
                            var wav = WavFile.Read(Path.Combine(Path.Combine(_dir, kind.Folder), name + ".wav"));
                            builder.Add(new BankBuilder.Entry
                            {
                                EventName = kind.EventPrefix + KeyPrefix + name, Bus = kind.Bus,
                                Loop = kind.Loop, StopBusFirst = kind.StopBusFirst, Wav = wav,
                            });
                            Plugin.Log.LogInfo($"audio: {kind.Folder}/{name}.wav ({wav.Channels}ch {wav.SampleRate}Hz {wav.Seconds:F1}s{(wav.SampleChunk != null ? ", loop points" : "")})");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogError($"audio: {kind.Folder}/{name}.wav: {ex.Message}");
                            kind.Names.Remove(name);
                        }
                    }
                }
                bank = builder.Build();
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError($"audio: failed to build the sound bank: {ex}");
                return;
            }

            var handle = GCHandle.Alloc(bank, GCHandleType.Pinned);
            try
            {
                // Copy なので、呼んだ後は手元の配列を離してよい
                var result = AkSoundEngine.LoadBankMemoryCopy(handle.AddrOfPinnedObject(), (uint)bank.Length, out uint bankId);
                if (result == AKRESULT.AK_Success || result == AKRESULT.AK_BankAlreadyLoaded)
                {
                    _loaded = true;
                    Plugin.Log.LogInfo($"audio: sound bank loaded ({bank.Length / 1024} KB, id={bankId}): " +
                                       $"music [{string.Join(", ", Music.Names)}], sounds [{string.Join(", ", Sounds.Names)}], env [{string.Join(", ", Env.Names)}]");
                }
                else
                {
                    _failed = true;
                    Plugin.Log.LogError($"audio: Wwise refused the sound bank: {result}");
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static void Register<TCollection, TData>(Kind kind)
            where TCollection : CollectionData<TData>
            where TData : ScriptableObject
        {
            if (kind.Names.Count == 0)
                return;
            foreach (var collection in Resources.FindObjectsOfTypeAll<TCollection>())
            {
                if (collection.List == null)
                    continue;
                foreach (var name in kind.Names)
                {
                    if (collection.List.Any(x => x != null && x.name == name))
                        continue;
                    collection.List.Add(GetData<TData>(kind, name));
                }
            }
        }

        private static TData GetData<TData>(Kind kind, string name) where TData : ScriptableObject
        {
            string id = kind.Folder + "/" + name;
            if (_data.TryGetValue(id, out var existing))
                return (TData)existing;
            var data = ScriptableObject.CreateInstance<TData>();
            data.name = name;
            data.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // 本編の SoundManager が "Music_" + key のイベントを投げる
            AccessTools.Field(typeof(TData), "_key").SetValue(data, KeyPrefix + name);
            _data[id] = data;
            return data;
        }

        /// <summary>決闘の曲などに使う MusicData を名前で探す (自前の曲 → 本編の曲の順)。</summary>
        internal static MusicData FindMusic(string name)
        {
            if (Music.Names.Contains(name) && _loaded)
                return GetData<MusicData>(Music, name);
            return Resources.FindObjectsOfTypeAll<MusicDataCollection>()
                .SelectMany(c => c.List ?? new List<MusicData>())
                .FirstOrDefault(m => m != null && m.name == name);
        }
    }
}
