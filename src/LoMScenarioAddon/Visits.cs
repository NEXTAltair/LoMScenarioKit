// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using Mortal.Free;
using OBB.Framework.Extensions;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 訪問: 自由行動で場所をクリックしたとき、条件を満たす訪問があれば、その場所の固定シナリオの代わりに訪問シナリオを流す。
    /// 本編の OnPositionClick で通常の行動に進んだ場合だけ差し替える。
    /// 訪問シナリオから lsa_position_default へつなぐと、差し替えた固定シナリオ (本来の行動) に戻れる。
    /// </summary>
    internal static class Visits
    {
        /// <summary>訪問シナリオから本来の行動へ戻るときの仮のシナリオ名。</summary>
        internal const string DefaultActivityAlias = ScenarioManifest.PositionDefaultAlias;

        private static readonly FieldInfo PositionField =
            typeof(PositionController).GetField("_position", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ScriptNameField =
            typeof(PositionController).GetField("_scriptName", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Dictionary<string, ConditionResultItem> _conditions = new Dictionary<string, ConditionResultItem>();
        private static readonly HashSet<string> _failed = new HashSet<string>();

        /// <summary>最後に差し替えた本来のシナリオ名。</summary>
        internal static string LastDefaultScript { get; private set; }

        /// <summary>
        /// OnPositionClick の直前。_scriptName に値が入るのは「通常の行動」の分岐だけなので、空にしておいて後で見分ける。
        /// (FreePositionData.GetExecuteScript は短いメソッドで JIT にインライン化され、割り込みが効かない)
        /// </summary>
        internal static void BeforeClick(PositionController controller)
        {
            ScriptNameField.SetValue(controller, "");
        }

        /// <summary>
        /// OnPositionClick の直後。通常の行動に進んでいたら、訪問に差し替える。
        /// 行動の処理 (ExecuteActivityAction) は最初の待ちより前に SetStoryScript を済ませ、シーン移動はその後なので、
        /// ここでシナリオ名を書き換えれば移動先が変わる。
        /// </summary>
        internal static void AfterClick(PositionController controller)
        {
            string defaultScript = ScriptNameField.GetValue(controller) as string;
            if (string.IsNullOrEmpty(defaultScript))
                return;
            string chosen = Choose(controller, defaultScript);
            if (chosen == defaultScript)
                return;
            ScriptNameField.SetValue(controller, chosen);
            PlayerStatManagerData.Instance.SetStoryScript(chosen);
        }

        private static string Choose(PositionController controller, string defaultScript)
        {
            if (Plugin.Manifest == null || Plugin.Manifest.Visits.Count == 0)
                return defaultScript;
            string position = ((PositionType)PositionField.GetValue(controller)).GetStringValue();

            foreach (var visit in Plugin.Manifest.Visits.Where(v => v.Position == position))
            {
                if (visit.Once && PositionEvents.GetFlagState(visit.OnceFlag) != 0)
                    continue;
                if (!IsMet(visit))
                    continue;
                if (!Plugin.Content.HasStory(visit.Story))
                {
                    Plugin.Log.LogError($"visit '{visit.Id}': scenario/story/{visit.Story}.lua not found");
                    continue;
                }
                if (visit.Once)
                    PositionEvents.SetFlagState(visit.OnceFlag, 1);
                LastDefaultScript = defaultScript;
                Plugin.Log.LogInfo($"visit '{visit.Id}' at {position}: {visit.Story} (default was {defaultScript})");
                return visit.Story;
            }
            if (Plugin.Manifest.Visits.Any(v => v.Position == position))
                Plugin.Log.LogInfo($"visit check at {position}: no visit matched, default {defaultScript}");
            return defaultScript;
        }

        private static bool IsMet(VisitDef visit)
        {
            if (visit.When == null)
                return true;
            if (_failed.Contains(visit.Id))
                return false;
            if (!_conditions.TryGetValue(visit.Id, out var condition))
            {
                var errors = new List<string>();
                condition = PositionEvents.BuildCondition(visit.When, $"visit '{visit.Id}'.when", errors);
                if (errors.Count > 0)
                {
                    foreach (var e in errors)
                        Plugin.Log.LogError(e);
                    Plugin.Log.LogError($"visit '{visit.Id}' is disabled");
                    _failed.Add(visit.Id);
                    return false;
                }
                _conditions[visit.Id] = condition;
            }
            return condition.Check();
        }
    }
}
