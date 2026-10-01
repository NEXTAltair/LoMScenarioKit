// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mortal.Core;
using Mortal.Story;

namespace LoMScenarioAddon
{
    /// <summary>
    /// scenario.json の conditions。Lua の checkpointmanager.Condition(名前) で判定できるようにする。
    /// Lua には好感度を読む関数が無いので、好感度による分岐はこれで書く。本編と同名の条件があれば本編を優先する。
    /// </summary>
    internal static class ScenarioConditions
    {
        private static readonly FieldInfo ConditionConfigField =
            typeof(CheckPointManager).GetField("_condition", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Dictionary<string, ConditionResultItem> _built = new Dictionary<string, ConditionResultItem>();
        private static readonly HashSet<string> _skipped = new HashSet<string>();

        /// <summary>自前の条件なら判定して true を返す (result に結果)。そうでなければ false (本編の処理に任せる)。</summary>
        internal static bool TryEvaluate(CheckPointManager manager, string name, out bool result)
        {
            result = false;
            if (name == null || Plugin.Manifest == null || _skipped.Contains(name)
                || !Plugin.Manifest.Conditions.TryGetValue(name, out var when))
                return false;

            if (!_built.TryGetValue(name, out var condition))
            {
                var config = ConditionConfigField.GetValue(manager) as ConditionResultConfig;
                if (config != null && config.List.Any(x => x != null && x.name == name))
                {
                    Plugin.Log.LogWarning($"condition '{name}' exists in the game, the game's one is used");
                    _skipped.Add(name);
                    return false;
                }
                var errors = new List<string>();
                condition = PositionEvents.BuildCondition(when, $"condition '{name}'", errors);
                if (errors.Count > 0)
                {
                    foreach (var e in errors)
                        Plugin.Log.LogError(e);
                    Plugin.Log.LogError($"condition '{name}' always returns false");
                    condition = null;
                }
                _built[name] = condition;
            }
            result = condition != null && condition.Check();
            return true;
        }
    }
}
