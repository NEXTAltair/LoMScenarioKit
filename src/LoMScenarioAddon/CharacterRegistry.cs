// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using OBB.Framework.Utils;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// scenario.json の各キャラについて好感度データ (RelationshipStat) を作り、全キャラ一覧 (RelationshipData) に登録する。
    /// </summary>
    internal static class CharacterRegistry
    {
        private static readonly Dictionary<string, RelationshipStat> _stats = new Dictionary<string, RelationshipStat>();
        private static readonly Dictionary<string, CharacterDef> _defs = new Dictionary<string, CharacterDef>();

        internal static bool Registered { get; private set; }

        /// <summary>登録済みの追加キャラ (scenario.json の順)。</summary>
        internal static IEnumerable<KeyValuePair<string, RelationshipStat>> Stats =>
            _defs.Keys.Where(_stats.ContainsKey).Select(id => new KeyValuePair<string, RelationshipStat>(id, _stats[id]));

        internal static void Configure(ScenarioManifest manifest)
        {
            _defs.Clear();
            foreach (var def in manifest.Characters)
                _defs[def.Id] = def;
        }

        internal static bool IsAdded(string id)
        {
            return id != null && _defs.ContainsKey(id);
        }

        internal static CharacterDef GetDef(string id)
        {
            return id != null && _defs.TryGetValue(id, out var def) ? def : null;
        }

        internal static RelationshipStat GetStat(string id)
        {
            return id != null && _stats.TryGetValue(id, out var stat) ? stat : null;
        }

        internal static bool TryRegister()
        {
            if (Registered)
                return true;

            // PlayerStatManagerData.Instance は未ロード時にエラーログを出すので直接探す。
            var data = Resources.FindObjectsOfTypeAll<PlayerStatManagerData>().FirstOrDefault();
            var relationships = data != null ? data.Relationships : null;
            if (relationships == null || relationships.List == null)
                return false;
            // フラグの一覧 (MissionManagerData) も読み込まれてから一度に登録する。
            if (!Resources.FindObjectsOfTypeAll<MissionManagerData>().Any())
                return false;

            foreach (var def in _defs.Values)
            {
                // パッチャーが足していない (ID 不正・公式と衝突など) キャラは登録しない。
                if (!EnumUtils.TryParseByStringValue<RelationshipStatType>(def.Id, out var type))
                {
                    Plugin.Log.LogWarning($"character '{def.Id}' is not in RelationshipStatType (see patcher log), skipped");
                    continue;
                }

                var stat = relationships.Get(type);
                if (stat == null)
                {
                    stat = ScriptableObject.CreateInstance<RelationshipStat>();
                    stat.name = "Relationship_" + def.Id;
                    stat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    SetField(stat, "_type", type);
                    SetField(stat, "_note", "LoMScenarioAddon");
                    SetField(stat, "_default", def.DefaultAffinity);
                    SetField(stat, "_value", def.DefaultAffinity);
                    SetField(stat, "_max", 100);
                    SetField(stat, "_active", false);
                    relationships.List.Add(stat);
                }

                var images = Plugin.Content.GetCharacterImages(def.Id);
                SetField(stat, "_icon", images.Icon);
                SetField(stat, "_headIcon", images.Head);
                SetField(stat, "_avatar", images.Avatar);
                _stats[def.Id] = stat;
                Plugin.Log.LogInfo($"registered {def.Id} ({(int)type}): default={def.DefaultAffinity} socialPanel={def.SocialPanel} " +
                                   $"images icon={images.Icon != null} head={images.Head != null} avatar={images.Avatar != null}");
            }

            // 1つの段階が失敗しても他の段階と登録完了の印は止めない (失敗を毎秒やり直さない)
            Step("flags", PositionEvents.RegisterFlags);
            Step("talents", () => Talents.Register(data));
            Step("books", () => Books.Register(data));
            Step("ending cards", () => Endings.RegisterCards(Resources.FindObjectsOfTypeAll<LibrarySystem>().FirstOrDefault()));
            Registered = true;
            Plugin.Log.LogInfo($"characters registered: {_stats.Count}/{_defs.Count} (relationship list count={relationships.List.Count})");
            return true;
        }

        private static void Step(string name, System.Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"registration '{name}' failed: {ex}");
            }
        }

        private static void Step(string name, System.Func<bool> action)
        {
            Step(name, () => { action(); });
        }

        internal static void SetField(object target, string name, object value)
        {
            // 親クラスの非公開フィールド (例: Book の _id は ItemData にある) も探す
            var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
            if (field == null)
                throw new MissingFieldException(target.GetType().Name, name);
            field.SetValue(target, value);
        }
    }
}
