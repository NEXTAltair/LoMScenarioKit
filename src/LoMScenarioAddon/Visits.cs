// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using Mortal.Free;
using OBB.Framework.Extensions;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 訪問: 自由行動で場所をクリックしたとき、条件を満たす訪問があれば、その場所の固定シナリオの代わりに訪問シナリオを流す。
    /// 本編の ExecuteActivityAction が実行された場合だけ差し替える。
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
        /// 通常行動のコルーチンだけを包む。任務・行動不能時のクリックや、他MODのホバー表示には触れない。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PositionController), "ExecuteActivityAction")]
        private static void ActivityPostfix(PositionController __instance, ref IEnumerator __result)
        {
            if (__result != null && Plugin.Manifest != null && Plugin.Manifest.Visits.Count > 0)
                __result = RunActivity(__instance, __result);
        }

        private static IEnumerator RunActivity(PositionController controller, IEnumerator original)
        {
            IEnumerator firstStep = null;
            try
            {
                // 本編が行き先と行動回数を更新した後、シーン遷移前に訪問へ差し替える。
                // コルーチンを取得しただけでは条件判定や一度きりのフラグを消費しない。
                if (!original.MoveNext())
                    yield break;
                if (original.Current is IEnumerator actionUpdate)
                {
                    // 現行ゲームの最初の待機は UpdateActionValue。外側が yield しただけでは
                    // まだ行動回数は減っていないので、内側の最初の MoveNext の後に判定する。
                    firstStep = RunFirstStep(controller, actionUpdate);
                    yield return firstStep;
                }
                else
                {
                    AfterActivityStarted(controller);
                    yield return original.Current;
                }
                while (original.MoveNext()) yield return original.Current;
            }
            finally
            {
                (firstStep as IDisposable)?.Dispose();
                (original as IDisposable)?.Dispose();
            }
        }

        private static IEnumerator RunFirstStep(PositionController controller, IEnumerator original)
        {
            try
            {
                bool running = original.MoveNext();
                AfterActivityStarted(controller);
                if (running)
                    do { yield return original.Current; } while (original.MoveNext());
            }
            finally { (original as IDisposable)?.Dispose(); }
        }

        private static void AfterActivityStarted(PositionController controller)
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
            if (Plugin.Log.IsDebugEnabled && Plugin.Manifest.Visits.Any(v => v.Position == position))
                Plugin.Log.LogDebug($"visit check at {position}: no visit matched, default {defaultScript}");
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
