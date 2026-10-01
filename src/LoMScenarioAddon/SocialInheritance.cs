// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Linq;
using HarmonyLib;
using Mortal.Core;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 周回の引き継ぎ「繼承人際」(天命点で好感度を上げる画面) に追加キャラの行を足す。
    /// 画面は SocialBonusStatData の配列 (公式25人) を1件ずつ読んで行を作るので、キャラを追加しただけでは載らない。
    /// 行の見た目 (Prefab) は公式の行を流用し、中身 (小アイコン・名前・好感度) は追加キャラの好感度データから出る。
    /// </summary>
    internal static class SocialInheritance
    {
        /// <summary>FateBonusPanel.InitSocial の直前。配列に追加キャラが無ければ足す (同じキャラは重ねない)。</summary>
        internal static void Extend(FateBonusPanel panel)
        {
            var data = AccessTools.Field(typeof(FateBonusPanel), "_socialData").GetValue(panel) as SocialBonusStatData;
            var list = data != null ? data.List : null;
            if (list == null || list.Length == 0 || Plugin.Manifest == null)
                return;

            var prefab = list[0].Prefab;
            var additions = CharacterRegistry.Stats
                .Where(pair => CharacterRegistry.GetDef(pair.Key)?.Inherit == true && list.All(m => m.Stat != pair.Value))
                .Select(pair =>
                {
                    var model = new FateBonusSocialModel();
                    CharacterRegistry.SetField(model, "_stat", pair.Value);
                    CharacterRegistry.SetField(model, "_prefab", prefab);
                    return model;
                })
                .ToArray();
            if (additions.Length == 0)
                return;

            CharacterRegistry.SetField(data, "_list", list.Concat(additions).ToArray());
            Plugin.Log.LogInfo($"social inheritance: added {additions.Length} character(s) (rows={list.Length + additions.Length})");
        }
    }
}
