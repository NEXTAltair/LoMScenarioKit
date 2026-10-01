// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Combat;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 決闘の効果 (状態)。本編の効果と部品を複製して値を上書きし、効果の一覧 (CombatStateEffectDatabase) に足す。
    /// 部品は数値パラメータ付きの汎用品 (能力値補正・追加攻撃・状態付与など) なので、組み合わせで新しい効果になる。
    /// 部品や演出の参照先は決闘の場面の資産なので、決闘の場面を読み込むたびに作り直す。
    /// </summary>
    internal static class Effects
    {
        private static readonly HashSet<string> _ourKeys = new HashSet<string>();

        internal static void Inject()
        {
            if (Plugin.Manifest == null || Plugin.Manifest.Effects.Count == 0)
                return;
            var db = Resources.FindObjectsOfTypeAll<CombatStateEffectDatabase>().FirstOrDefault();
            if (db == null)
                return;
            var itemsField = AccessTools.Field(typeof(CombatStateEffectDatabase), "_items");
            var official = ((CombatStateEffectScriptable[])itemsField.GetValue(db)).Where(x => x != null && !_ourKeys.Contains(x.Key)).ToList();
            var units = Resources.FindObjectsOfTypeAll<CombatStateEffectUnitScriptable>()
                .GroupBy(u => u.name).ToDictionary(g => g.Key, g => g.First());

            var added = new List<CombatStateEffectScriptable>();
            foreach (var def in Plugin.Manifest.Effects)
            {
                var errors = new List<string>();
                var effect = Build(def, official, units, errors);
                if (errors.Count > 0 || effect == null)
                {
                    foreach (var e in errors)
                        Plugin.Log.LogError(e);
                    Plugin.Log.LogError($"effect '{def.Key}' is disabled");
                    continue;
                }
                added.Add(effect);
                _ourKeys.Add(def.Key);
            }
            itemsField.SetValue(db, official.Concat(added).ToArray());
            Plugin.Log.LogInfo($"effects: added [{string.Join(", ", added.Select(e => $"{e.Key}({string.Join("+", e.Units.Select(u => u.name))})"))}]");
        }

        private static CombatStateEffectScriptable Build(Shared.EffectDef def, List<CombatStateEffectScriptable> official,
            Dictionary<string, CombatStateEffectUnitScriptable> units, List<string> errors)
        {
            var baseEffect = official.FirstOrDefault(x => x.Key == def.Base);
            if (baseEffect == null)
            {
                errors.Add($"effect '{def.Key}': base effect '{def.Base}' not found (see combat_effects.tsv)");
                return null;
            }
            var effect = Object.Instantiate(baseEffect);
            effect.name = "LSA_" + def.Key;
            CharacterRegistry.SetField(effect, "_key", def.Key);
            FieldSetter.Apply(effect, def.Set, $"effect '{def.Key}'", errors);

            if (def.Units != null)
            {
                var built = new List<CombatStateEffectUnitScriptable>();
                for (int i = 0; i < def.Units.Count; i++)
                {
                    var u = def.Units[i];
                    if (!units.TryGetValue(u.Base, out var baseUnit))
                    {
                        errors.Add($"effect '{def.Key}'.units[{i}]: unit '{u.Base}' not found (see combat_units.tsv)");
                        continue;
                    }
                    // 部品は全決闘で共有されるので、本編の部品を書き換えず必ず複製する
                    var unit = Object.Instantiate(baseUnit);
                    unit.name = $"LSA_{def.Key}_{i}_{baseUnit.name}";
                    FieldSetter.Apply(unit, u.Set, $"effect '{def.Key}'.units[{i}]", errors);
                    built.Add(unit);
                }
                CharacterRegistry.SetField(effect, "_units", built.ToArray());
            }
            return effect;
        }
    }
}
