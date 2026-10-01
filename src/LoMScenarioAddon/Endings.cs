// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// エンディング。
    /// 選択: 本編のエンディング分岐シナリオ (ch8_6_3_2) を読み込むとき、ヒロインごとの分岐 (Switch) の直前に
    ///       「自前の条件が成立すれば自前のエンディングへ進む」判定を差し込む。成立しなければ本編のまま。
    ///       公式のルートフラグ (G11000〜) を立てないキャラなら、本編の判定は「どれでもない」に流れるだけなので、先に分岐すれば衝突しない。
    /// 表示: 自前のエンディングカード (LibraryItemData) を汗青書 (LibrarySystem.EndGame) に登録し、
    ///       Lua の endgamepanel.Open(ID) で本編と同じ画面・保存 (汗青書の件数、列伝スロット) を使う。
    /// </summary>
    internal static class Endings
    {
        // 本編のエンディング分岐。公式の更新で文面が変わって見つからなければ、差し込まずに警告だけ出す。
        private const string HookScript = "ch8_6_3_2";
        private const string HookAnchor = "checkpointmanager.Switch(\"Ch_8_6_3_2_001\")";

        private static string _patchedScript;
        private static bool _patchTried;

        /// <summary>scenario.json の endings の条件を、名前付きの条件として登録する (Lua から判定するため)。</summary>
        internal static void RegisterConditions(ScenarioManifest manifest)
        {
            foreach (var ending in manifest.Endings)
            {
                if (manifest.Conditions.ContainsKey(ending.ConditionName))
                {
                    Plugin.Log.LogWarning($"condition '{ending.ConditionName}' is reserved for ending '{ending.Id}', the one in conditions is replaced");
                }
                manifest.Conditions[ending.ConditionName] = ending.When;
            }
        }

        /// <summary>
        /// 自前のカードを講經堂の一覧に登録する: エンディング (汗青書 EndGame)、死亡エンディング (奪命譜 Dead)、CG 画面 (結局CG EndCg)。
        /// 全セーブ共通のデータ (件数) が読み込まれる前に呼ぶ (LibrarySystem.Load の直前、または起動時)。同名があれば何もしない。
        /// </summary>
        internal static void RegisterCards(LibrarySystem library)
        {
            if (library == null || Plugin.Manifest == null || Plugin.Content == null)
                return;
            int added = 0;
            foreach (var ending in Plugin.Manifest.Endings)
                added += AddCard(library.EndGame, ending.Id, Plugin.Content.GetEndingPicture(ending.Id));
            foreach (var id in Plugin.Manifest.Deaths)
                added += AddCard(library.Dead, id, null);
            foreach (var id in Plugin.Manifest.Cgs)
                added += AddCard(library.EndCg, id, Plugin.Content.GetCgPicture(id));
            if (added > 0)
                Plugin.Log.LogInfo($"library cards registered: {added} (endgame={library.EndGame.List.Count}, dead={library.Dead.List.Count}, cg={library.EndCg.List.Count})");
        }

        private static int AddCard(LibraryItemCollection collection, string id, Sprite picture)
        {
            if (collection == null || collection.List.Any(x => x != null && x.name == id))
                return 0;
            var card = ScriptableObject.CreateInstance<LibraryItemData>();
            card.name = id;
            card.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(card, "_devNote", "LoMScenarioAddon");
            CharacterRegistry.SetField(card, "_picture", picture);
            // null だと周回特典の計算などで落ちるので空配列にする
            CharacterRegistry.SetField(card, "_awardStatItems", new LibraryAwardStatItem[0]);
            collection.List.Add(card);
            return 1;
        }

        /// <summary>本編のエンディング分岐シナリオなら、判定を差し込んだ Lua を返す。</summary>
        internal static bool TryGetPatchedScript(string name, out string code)
        {
            code = null;
            if (name != HookScript || Plugin.Manifest == null || Plugin.Manifest.Endings.Count == 0)
                return false;
            if (!_patchTried)
            {
                _patchTried = true;
                _patchedScript = BuildPatchedScript();
            }
            code = _patchedScript;
            return code != null;
        }

        private static string BuildPatchedScript()
        {
            string original = LoadOfficialScript(HookScript);
            if (original == null)
            {
                Plugin.Log.LogError($"ending hook: {HookScript} could not be read, endings are disabled");
                return null;
            }
            var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
            int at = lines.FindIndex(l => l.Contains(HookAnchor));
            if (at < 0)
            {
                Plugin.Log.LogWarning($"ending hook: '{HookAnchor}' not found in {HookScript} (game updated?), endings are disabled");
                return null;
            }

            var block = new List<string> { "-- [LoMScenarioAddon] 自前のエンディング (上から順に判定、成立すればそちらへ進む)" };
            foreach (var ending in Plugin.Manifest.Endings)
            {
                block.Add($"if checkpointmanager.Condition(\"{ending.ConditionName}\") then");
                block.Add($"\tluamanager.SetNextScript(\"{ending.Story}\")");
                block.Add("\tluamanager.Init()");
                block.Add("\treturn");
                block.Add("end");
            }
            lines.InsertRange(at, block);
            Plugin.Log.LogInfo($"ending hook: {Plugin.Manifest.Endings.Count} ending(s) inserted into {HookScript} at line {at + 1}");
            return string.Join("\n", lines);
        }

        /// <summary>本編のシナリオ本文。Binarizer 系で外部ファイルに差し替えられていれば、そちらを使う。</summary>
        private static string LoadOfficialScript(string name)
        {
            try
            {
                var type = AccessTools.TypeByName("Mortal.HookCustomStory");
                var map = type != null ? AccessTools.Field(type, "mapStory")?.GetValue(null) as Dictionary<string, string> : null;
                if (map != null && map.TryGetValue(name, out var path) && File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ending hook: Binarizer story map could not be read: {ex.Message}");
            }
            var asset = Resources.Load<TextAsset>("Story/ChineseTraditional/" + name);
            return asset != null ? asset.text : null;
        }
    }
}
