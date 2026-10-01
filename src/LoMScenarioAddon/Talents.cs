// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Linq;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 天賦 (技能)。本編の天賦を複製して天賦の一覧 (PlayerStatManagerData.Talents) に足す。
    /// 取得 (Lua の AddTalent)・人物画面・セーブ (ID)・決闘での効果は本編の処理がそのまま扱う。
    /// 天賦はレベルを自分の中に持つので、本編の天賦と共有せず必ず複製する。
    /// </summary>
    internal static class Talents
    {
        /// <summary>セーブの読み込みより前 (起動時の登録) に呼ぶ。同じ ID があれば何もしない。</summary>
        internal static void Register(PlayerStatManagerData data)
        {
            if (data == null || data.Talents == null || Plugin.Manifest == null)
                return;
            foreach (var def in Plugin.Manifest.Talents)
            {
                if (data.Talents.Get(def.Id) != null)
                    continue;
                var baseTalent = data.Talents.Get(def.Base);
                if (baseTalent == null)
                {
                    Plugin.Log.LogError($"talent '{def.Id}': base talent '{def.Base}' not found");
                    continue;
                }
                var talent = Object.Instantiate(baseTalent);
                talent.name = "天賦_" + def.Id;
                talent.hideFlags = HideFlags.DontUnloadUnusedAsset;
                string effect = def.Effect ?? baseTalent.CombatSkillKey;
                CharacterRegistry.SetField(talent, "_id", def.Id);
                CharacterRegistry.SetField(talent, "_combatSkillKey", effect ?? "");
                CharacterRegistry.SetField(talent, "_combatSkill", !string.IsNullOrEmpty(effect));
                // 集団戦用の技と、上位天賦を得たときに消す下位天賦は持ち込まない
                CharacterRegistry.SetField(talent, "_battleSkill", false);
                CharacterRegistry.SetField(talent, "_lowLevelTalent", new PlayerTalentData[0]);
                CharacterRegistry.SetField(talent, "_level", 0);
                if (def.MaxLevel.HasValue)
                    CharacterRegistry.SetField(talent, "_maxLevel", def.MaxLevel.Value);
                if (def.DisplayLevel.HasValue)
                    CharacterRegistry.SetField(talent, "_displayLevel", def.DisplayLevel.Value);
                if (def.Priority.HasValue)
                    CharacterRegistry.SetField(talent, "_priority", def.Priority.Value);
                data.Talents.List.Add(talent);
                Plugin.Log.LogInfo($"talent '{def.Id}' registered from {def.Base} (effect={effect}, max={AccessMax(talent)})");
            }
        }

        private static int AccessMax(PlayerTalentData talent)
        {
            return (int)HarmonyLib.AccessTools.Field(typeof(PlayerTalentData), "_maxLevel").GetValue(talent);
        }
    }
}
