// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// scenario/ フォルダのシナリオを本体に差し込む。キャラ定義は scenario.json から読む。
    /// </summary>
    [BepInPlugin("lom.scenarioaddon", "LoMScenarioAddon", "2.5.01")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ScenarioContent Content;
        internal static ScenarioManifest Manifest;

        private ConfigEntry<KeyCode> _testStoryKey;
        private ConfigEntry<string> _testStoryName;
        private ConfigEntry<KeyCode> _toggleActiveKey;
        private ConfigEntry<string> _language;
        private float _nextPoll;
        private readonly Dictionary<string, (int Value, bool Active)> _last = new Dictionary<string, (int, bool)>();

        private void Awake()
        {
            Log = Logger;
            // 試作・確認用のキー。F キーは日本語化 MOD (ルビ F5〜F7、フォント F7〜F12) と Binarizer (F1〜F6)、
            // Ctrl は会話スキップが使うので避ける。配布時に誤爆しないよう既定は無効 (None)。
            _testStoryKey = Config.Bind("Debug", "TestStoryKey", KeyCode.None, "自由行動の画面で押すと TestStoryName のシナリオを始める (例: Home)");
            _testStoryName = Config.Bind("Debug", "TestStoryName", "lsa_test_meet", "TestStoryKey で始めるシナリオ (scenario/story/<名前>.lua)");
            _language = Config.Bind("Text", "Language", "auto",
                "テキストの言語 (text.<言語>.csv)。auto なら日本語化 MOD があれば ja、無ければゲームの言語 (zh-TW / zh-CN / ko)");
            _toggleActiveKey = Config.Bind("Debug", "ToggleActiveKey", KeyCode.None, "追加キャラ全員の「出会い済み」(Active) を切り替える (例: End)");

            string scenarioDir = Path.Combine(Path.GetDirectoryName(Info.Location), "scenario");
            Manifest = LoadManifest(scenarioDir);
            Endings.RegisterConditions(Manifest);
            CharacterRegistry.Configure(Manifest);
            Content = ScenarioContent.Load(scenarioDir, Manifest.DefaultLanguage, _language.Value);
            ScenarioAudio.Configure(scenarioDir);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                ScenarioAudio.OnSceneLoaded();
                if (scene.name == "Combat")
                {
                    Effects.Inject();
                    Duels.Inject();
                }
            };
            var harmony = Harmony.CreateAndPatchAll(typeof(Patches), "lom.scenarioaddon");
            Patches.PatchBinarizerStoryRedirect(harmony);
            Log.LogInfo($"loaded scenario '{Manifest.Title ?? Manifest.Id ?? "(untitled)"}' with {Manifest.Characters.Count} character(s). " +
                        $"{_testStoryKey.Value} = test story (free mode), {_toggleActiveKey.Value} = toggle active");
        }

        private static ScenarioManifest LoadManifest(string scenarioDir)
        {
            string path = Path.Combine(scenarioDir, ScenarioManifest.FileName);
            if (!File.Exists(path))
            {
                Log.LogWarning($"{path} not found");
                return new ScenarioManifest();
            }
            try
            {
                var warnings = new List<string>();
                var manifest = ScenarioManifest.Load(path, warnings);
                foreach (var w in warnings)
                    Log.LogWarning(w);
                return manifest;
            }
            catch (Exception ex)
            {
                Log.LogError($"failed to read scenario.json: {ex.Message}");
                return new ScenarioManifest();
            }
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 1f;
                if (!CharacterRegistry.Registered)
                    CharacterRegistry.TryRegister();
                else
                    ReportChanges();
            }

            if (!CharacterRegistry.Registered)
                return;
            if (_testStoryKey.Value != KeyCode.None && Input.GetKeyDown(_testStoryKey.Value))
                StartTestStory();
            if (_toggleActiveKey.Value != KeyCode.None && Input.GetKeyDown(_toggleActiveKey.Value))
            {
                foreach (var pair in CharacterRegistry.Stats)
                {
                    pair.Value.SetActive(!pair.Value.Active);
                    Log.LogInfo($"direct SetActive {pair.Key} -> {pair.Value.Active} (reopen the status panel to refresh)");
                }
            }
        }

        private void ReportChanges()
        {
            foreach (var pair in CharacterRegistry.Stats)
            {
                var now = (pair.Value.Value, pair.Value.Active);
                if (_last.TryGetValue(pair.Key, out var prev) && prev == now)
                    continue;
                string before = _last.ContainsKey(pair.Key) ? prev.Value.ToString() : "-";
                Log.LogInfo($"{pair.Key}: value {before} -> {now.Value}, active={now.Active}");
                _last[pair.Key] = now;
            }
        }

        /// <summary>
        /// 本編の場所イベントと同じく、次のシナリオ名を設定してストーリー画面へ移る。
        /// </summary>
        private void StartTestStory()
        {
            string story = _testStoryName.Value;
            if (LuaManager.Instance != null)
            {
                Log.LogWarning("test story: already in a story scene (use it from free mode)");
                return;
            }
            var controller = SceneController.Instance;
            if (controller == null || controller.IsPrepare || controller.IsLoading)
            {
                Log.LogWarning("test story: scene controller busy or missing");
                return;
            }
            // 本編のシナリオ名 (エンディング分岐の確認用の ch8_6_3_2 など) も指定できる
            if (!Content.HasStory(story))
                Log.LogInfo($"test story: {story} is not in scenario/story, running it as a game script");
            PlayerStatManagerData.Instance.SetStoryScript(story);
            controller.LoadStory();
            Log.LogInfo($"test story: {story} requested");
        }
    }
}
