// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using Mortal.Story;
using OBB.Framework.Data;
using OBB.Framework.Utils;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// scenario.json の flags / events を本編のデータに足す。
    /// イベントは本編と同じクラス (PositionEventData と条件の一式) で組み立て、場所の抽選の候補一覧に追加する。
    /// 抽選・条件判定・重みの計算は本編の処理がそのまま行う。
    /// </summary>
    internal static class PositionEvents
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo PositionConfigField = typeof(CheckPointManager).GetField("_position", Private);
        private static readonly FieldInfo GameTimeDataField = typeof(PlayerStatManagerData).GetField("_gameTimeData", Private);

        private static readonly Dictionary<string, PositionEventData> _built = new Dictionary<string, PositionEventData>();
        private static readonly HashSet<string> _failed = new HashSet<string>();
        private static readonly HashSet<string> _warnedPositions = new HashSet<string>();

        private static IEnumerable<EventDef> Events => Plugin.Manifest != null ? Plugin.Manifest.Events : Enumerable.Empty<EventDef>();

        // ---------------------------------------------------------------- フラグ

        /// <summary>
        /// scenario.json のフラグと、1回きりのイベント用のフラグを本編のフラグ一覧に足す。
        /// 一覧に載ったフラグは本編と同じく名前でセーブされ、ロードで戻る。
        /// </summary>
        internal static bool RegisterFlags()
        {
            var mission = Resources.FindObjectsOfTypeAll<MissionManagerData>().FirstOrDefault();
            if (mission == null || mission.FlagsCollection == null || mission.FlagsCollection.Length == 0)
                return false;

            int added = 0;
            foreach (var flag in Plugin.Manifest.Flags)
            {
                if (FindFlag(mission, flag.Id) != null)
                {
                    Plugin.Log.LogWarning($"flag '{flag.Id}' already exists in the game, the existing one is used");
                    continue;
                }
                AddFlag(mission, flag.Id, flag.Default);
                added++;
            }
            var onceFlags = Events.Where(e => e.Once).Select(e => e.OnceFlag)
                .Concat(Plugin.Manifest.Visits.Where(v => v.Once).Select(v => v.OnceFlag))
                .Concat(Plugin.Manifest.Duels.Select(d => d.WonFlag));
            foreach (var name in onceFlags)
            {
                if (FindFlag(mission, name) == null)
                {
                    AddFlag(mission, name, 0);
                    added++;
                }
            }
            Plugin.Log.LogInfo($"flags registered: {added}");
            return true;
        }

        private static FlagData FindFlag(MissionManagerData mission, string name)
        {
            // MissionManagerData.GetFlag は見つからないと警告ログを出すので自前で探す。
            foreach (var collection in mission.FlagsCollection)
            {
                var flag = collection.List.FirstOrDefault(f => f != null && f.name == name);
                if (flag != null)
                    return flag;
            }
            return null;
        }

        private static void AddFlag(MissionManagerData mission, string name, int defaultValue)
        {
            var flag = ScriptableObject.CreateInstance<FlagData>();
            flag.name = name;
            flag.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(flag, "_devNote", "LoMScenarioAddon");
            CharacterRegistry.SetField(flag, "_default", defaultValue);
            flag.State = defaultValue;
            mission.FlagsCollection[0].List.Add(flag);
        }

        // ---------------------------------------------------------------- 場所イベント

        internal static IEnumerable<PositionEventData> RegisteredEvents(string position) =>
            Events.Where(e => e.Position == position && _built.ContainsKey(e.Id)).Select(e => _built[e.Id]);

        /// <summary>CheckPointManager.Position(name) の直前。その場所の候補一覧に追加イベントを足す (重複しない)。</summary>
        internal static void EnsureInjected(CheckPointManager manager, string position)
        {
            var events = Events.Where(e => e.Position == position).ToList();
            if (events.Count == 0)
                return;
            var config = PositionConfigField.GetValue(manager) as PositionResultConfig;
            var data = config != null ? config.List.FirstOrDefault(x => x != null && x.name == position) : null;
            if (data == null)
            {
                if (_warnedPositions.Add(position))
                    Plugin.Log.LogWarning($"position '{position}' not found in the game, its events are ignored");
                return;
            }

            int added = 0;
            foreach (var ev in events)
            {
                var eventData = GetOrBuild(ev);
                if (eventData != null && !data.List.Contains(eventData))
                {
                    data.List.Add(eventData);
                    added++;
                }
            }
            if (added > 0)
                Plugin.Log.LogInfo($"position {position}: added {added} event(s) (candidates={data.List.Count})");
        }

        /// <summary>本編が「場所の接頭辞_候補名」として作ったシナリオ名から、追加イベントを探す。</summary>
        internal static EventDef MatchScript(string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName))
                return null;
            return Events.FirstOrDefault(e => scriptName.EndsWith("_" + e.ResultType, StringComparison.Ordinal));
        }

        /// <summary>イベントのシナリオが始まったとき。1回きりならフラグを立てる。</summary>
        internal static void MarkStarted(EventDef ev)
        {
            if (ev.Once)
                SetFlagState(ev.OnceFlag, 1);
        }

        internal static void SetFlagState(string name, int state)
        {
            var mission = Resources.FindObjectsOfTypeAll<MissionManagerData>().FirstOrDefault();
            var flag = mission != null ? FindFlag(mission, name) : null;
            if (flag != null)
                flag.State = state;
        }

        internal static FlagData FindFlagPublic(string name)
        {
            var mission = Resources.FindObjectsOfTypeAll<MissionManagerData>().FirstOrDefault();
            return mission != null ? FindFlag(mission, name) : null;
        }

        internal static int GetFlagState(string name)
        {
            var mission = Resources.FindObjectsOfTypeAll<MissionManagerData>().FirstOrDefault();
            var flag = mission != null ? FindFlag(mission, name) : null;
            return flag != null ? flag.State : 0;
        }

        /// <summary>scenario.json の条件 ({ "logic", "items" }) を本編の ConditionResultItem にする。書き間違いは errors に積む。</summary>
        internal static ConditionResultItem BuildCondition(Dictionary<string, object> when, string where, List<string> errors)
        {
            return Condition(ConditionParts(when, where, errors));
        }

        private static PositionEventData GetOrBuild(EventDef ev)
        {
            if (_built.TryGetValue(ev.Id, out var cached))
                return cached;
            if (_failed.Contains(ev.Id))
                return null;

            var errors = new List<string>();
            var parts = new List<StatCompareItem>();
            if (ev.Once)
                parts.Add(Compare(FlagValue(ev.OnceFlag, errors), "=", ConstValue(0), errors));
            if (ev.When != null)
                parts.AddRange(ConditionParts(ev.When, $"event '{ev.Id}'.when", errors));

            var boosts = ev.Boosts.Select((b, i) =>
            {
                var item = new PositionEventRateItem();
                CharacterRegistry.SetField(item, "_condition", Condition(ConditionParts(b.When, $"event '{ev.Id}'.boosts[{i}]", errors)));
                CharacterRegistry.SetField(item, "_rate", b.Rate);
                return item;
            }).ToArray();

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    Plugin.Log.LogError(e);
                Plugin.Log.LogError($"event '{ev.Id}' is disabled");
                _failed.Add(ev.Id);
                return null;
            }

            var mapping = ScriptableObject.CreateInstance<StoryMappingItem>();
            mapping.name = mapping.Key = mapping.Value = ev.ResultType;
            mapping.hideFlags = HideFlags.DontUnloadUnusedAsset;

            var data = ScriptableObject.CreateInstance<PositionEventData>();
            data.name = ev.ResultType;
            data.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(data, "_devNote", "LoMScenarioAddon: " + ev.Id);
            CharacterRegistry.SetField(data, "_config", mapping);
            // 条件が一つも無いと本編の判定は偽になるので、そのときは判定自体を切る。
            CharacterRegistry.SetField(data, "_toggleCondition", parts.Count > 0);
            CharacterRegistry.SetField(data, "_activeCondition", Condition(parts));
            CharacterRegistry.SetField(data, "_defaultRate", ev.Rate);
            CharacterRegistry.SetField(data, "_eventRateItem", boosts);
            _built[ev.Id] = data;
            return data;
        }

        // ---------------------------------------------------------------- 条件の組み立て

        /// <summary>
        /// { "logic": "AND"|"OR", "items": [ { "left": 値, "op": "&gt;=", "right": 値 } ] } を比較の並びにする。
        /// OR は本編の「條件設定」(ConditionResultData) で包み、呼び出し側の AND に 1 つの比較として混ぜる。
        /// </summary>
        private static List<StatCompareItem> ConditionParts(Dictionary<string, object> when, string where, List<string> errors)
        {
            var items = new List<StatCompareItem>();
            var list = when.TryGetValue("items", out var v) ? v as List<object> : null;
            if (list == null || list.Count == 0)
            {
                errors.Add($"{where}: 'items' must be a non-empty array");
                return items;
            }
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i] as Dictionary<string, object>;
                string at = $"{where}.items[{i}]";
                if (item == null || !item.ContainsKey("left") || !item.ContainsKey("op") || !item.ContainsKey("right"))
                {
                    errors.Add($"{at}: needs 'left', 'op' and 'right'");
                    continue;
                }
                items.Add(Compare(Value(item["left"], at + ".left", errors), item["op"] as string, Value(item["right"], at + ".right", errors), errors, at));
            }

            string logic = when.TryGetValue("logic", out var l) ? l as string : "AND";
            if (string.Equals(logic, "OR", StringComparison.OrdinalIgnoreCase))
            {
                var nested = ScriptableObject.CreateInstance<ConditionResultData>();
                nested.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var orItem = Condition(items, LogicOperatorType.OR);
                CharacterRegistry.SetField(nested, "_items", new List<ConditionResultItem> { orItem });
                var reference = NewReference(StatCheckType.條件設定);
                CharacterRegistry.SetField(reference, "_condition", nested);
                return new List<StatCompareItem> { Compare(reference, "=", ConstValue(1), errors) };
            }
            if (!string.Equals(logic, "AND", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{where}: logic must be \"AND\" or \"OR\"");
            return items;
        }

        private static ConditionResultItem Condition(List<StatCompareItem> items, LogicOperatorType logic = LogicOperatorType.AND)
        {
            var condition = new ConditionResultItem();
            CharacterRegistry.SetField(condition, "_devNote", "LoMScenarioAddon");
            CharacterRegistry.SetField(condition, "_logicOp", logic);
            CharacterRegistry.SetField(condition, "_items", items);
            return condition;
        }

        private static StatCompareItem Compare(StatValueReference left, string op, StatValueReference right, List<string> errors, string where = null)
        {
            if (!EnumUtils.TryParseByStringValue<StatCompareType>(op, out var compare))
                errors.Add($"{where ?? "condition"}: op '{op}' must be one of = != < <= > >=");
            var item = new StatCompareItem();
            CharacterRegistry.SetField(item, "_compareOp", compare);
            CharacterRegistry.SetField(item, "_value1", Group(left));
            CharacterRegistry.SetField(item, "_value2", Group(right));
            return item;
        }

        private static StatGroupVariable Group(StatValueReference reference)
        {
            var group = ScriptableObject.CreateInstance<StatGroupVariable>();
            group.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(group, "_groupType", StatGroupType.Sum);
            CharacterRegistry.SetField(group, "_value", new List<StatValueReference> { reference ?? ConstValue(0) });
            return group;
        }

        /// <summary>
        /// 値の書き方:
        ///   数値 / {"flag": "名前"} / {"affinity": "キャラID"} / {"stat": "mental"} /
        ///   {"time": "now"} / {"time": [年, 月, 旬(1〜3)]} / {"random": [最小, 最大]}
        /// </summary>
        private static StatValueReference Value(object value, string where, List<string> errors)
        {
            if (value is double d)
                return ConstValue((int)Math.Round(d));
            var obj = value as Dictionary<string, object>;
            if (obj == null || obj.Count != 1)
            {
                errors.Add($"{where}: must be a number or an object with one of flag / affinity / stat / time / random");
                return null;
            }
            var pair = obj.First();
            var player = Resources.FindObjectsOfTypeAll<PlayerStatManagerData>().FirstOrDefault();
            switch (pair.Key)
            {
                case "flag":
                    return FlagValue(pair.Value as string, errors, where);

                case "affinity":
                {
                    if (!(pair.Value is string id) || !EnumUtils.TryParseByStringValue<RelationshipStatType>(id, out var type)
                        || player == null || player.Relationships.Get(type) == null)
                    {
                        errors.Add($"{where}: unknown character '{pair.Value}'");
                        return null;
                    }
                    var r = NewReference(StatCheckType.好感度);
                    CharacterRegistry.SetField(r, "_relationship", player.Relationships.Get(type));
                    return r;
                }

                case "stat":
                {
                    if (!(pair.Value is string key) || !EnumUtils.TryParseByStringValue<GameStatType>(key, out var type)
                        || player == null || player.Stats.Get(type) == null)
                    {
                        errors.Add($"{where}: unknown stat '{pair.Value}' (e.g. mental, money, fame, charisma)");
                        return null;
                    }
                    var r = NewReference(StatCheckType.角色數值);
                    CharacterRegistry.SetField(r, "_stat", player.Stats.Get(type));
                    return r;
                }

                case "time":
                {
                    GameTimeData time = null;
                    if (pair.Value is string s && s == "now")
                        time = player != null ? GameTimeDataField.GetValue(player) as GameTimeData : null;
                    else if (pair.Value is List<object> ymd && ymd.Count == 3 && ymd.All(x => x is double))
                    {
                        int y = (int)(double)ymd[0], m = (int)(double)ymd[1], st = (int)(double)ymd[2];
                        if (y >= 1 && m >= 1 && m <= 12 && st >= 1 && st <= 3)
                        {
                            time = ScriptableObject.CreateInstance<GameTimeData>();
                            time.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            time.GameTime = new GameTime(y, m, (MonthStageType)st);
                        }
                    }
                    if (time == null)
                    {
                        errors.Add($"{where}: time must be \"now\" or [year, month(1-12), stage(1-3)]");
                        return null;
                    }
                    var r = NewReference(StatCheckType.遊戲時間);
                    CharacterRegistry.SetField(r, "_gameTime", time);
                    return r;
                }

                case "random":
                {
                    if (!(pair.Value is List<object> mm) || mm.Count != 2 || !mm.All(x => x is double))
                    {
                        errors.Add($"{where}: random must be [min, max]");
                        return null;
                    }
                    var r = NewReference(StatCheckType.隨機值);
                    CharacterRegistry.SetField(r, "_randomMin", (int)(double)mm[0]);
                    CharacterRegistry.SetField(r, "_randomMax", (int)(double)mm[1]);
                    return r;
                }
            }
            errors.Add($"{where}: unknown value type '{pair.Key}'");
            return null;
        }

        private static StatValueReference FlagValue(string name, List<string> errors, string where = null)
        {
            var mission = Resources.FindObjectsOfTypeAll<MissionManagerData>().FirstOrDefault();
            var flag = mission != null && name != null ? FindFlag(mission, name) : null;
            if (flag == null)
            {
                errors.Add($"{where ?? "condition"}: unknown flag '{name}' (add it to \"flags\" in scenario.json if it is yours)");
                return null;
            }
            var r = NewReference(StatCheckType.旗標);
            CharacterRegistry.SetField(r, "_flag", flag);
            return r;
        }

        private static StatValueReference ConstValue(int value)
        {
            var r = NewReference(StatCheckType.常數);
            CharacterRegistry.SetField(r, "_constant", value);
            return r;
        }

        private static StatValueReference NewReference(StatCheckType type)
        {
            var r = new StatValueReference();
            CharacterRegistry.SetField(r, "_checkType", type);
            return r;
        }
    }
}
