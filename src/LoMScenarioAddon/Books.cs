// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.Linq;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using OBB.Framework.Utils;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 秘笈。本編の秘笈を複製して書籍の一覧 (ItemDatabase.Books) に足す。
    /// 入手 (Lua の AddBook)・所持品と修練の画面・レベルアップ・セーブ (ID) は本編の処理。
    /// 秘笈はレベルと修練の進み具合を自分の中に持つので、本編の秘笈と共有せず必ず複製する。
    /// レベルごとのデータは scenario.json から組み立てる (複製元のデータは複製元の天賦まで付いてくるので使わない)。
    /// </summary>
    internal static class Books
    {
        /// <summary>セーブの読み込みより前 (起動時の登録) に呼ぶ。同じ ID があれば何もしない。</summary>
        internal static void Register(PlayerStatManagerData player)
        {
            var db = Resources.FindObjectsOfTypeAll<ItemDatabase>().FirstOrDefault();
            if (db == null || db.Books == null || player == null || Plugin.Manifest == null)
                return;
            foreach (var def in Plugin.Manifest.Books)
            {
                try
                {
                    RegisterOne(db, def, player);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"book '{def.Id}' failed: {ex}");
                }
            }
        }

        private static void RegisterOne(ItemDatabase db, BookDef def, PlayerStatManagerData player)
        {
            {
                if (db.Books.Get(def.Id) != null)
                    return;
                var baseBook = db.Books.Get(def.Base) as Book;
                if (baseBook == null)
                {
                    Plugin.Log.LogError($"book '{def.Id}': base book '{def.Base}' not found");
                    return;
                }
                var errors = new List<string>();
                var levels = def.Levels.Select((lv, i) => BuildLevel(def, i, lv, player, errors)).ToArray();
                if (errors.Count > 0)
                {
                    foreach (var e in errors)
                        Plugin.Log.LogError(e);
                    Plugin.Log.LogError($"book '{def.Id}' is disabled");
                    return;
                }

                var book = Object.Instantiate(baseBook);
                book.name = "Book_" + def.Id;
                book.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CharacterRegistry.SetField(book, "_devNote", "LoMScenarioAddon");
                CharacterRegistry.SetField(book, "_id", def.Id);
                var image = Plugin.Content.GetBookImage(def.Id);
                if (image != null)
                    CharacterRegistry.SetField(book, "_image", image);
                CharacterRegistry.SetField(book, "_currentCount", 0);
                CharacterRegistry.SetField(book, "_currentMartialLevel", 0);
                CharacterRegistry.SetField(book, "_currentLevelPoint", 0);
                CharacterRegistry.SetField(book, "_martialLevelData", levels);
                // 複製元に結び付いたもの (能力値加成、入手時の任務処理、分冊) は持ち込まない
                CharacterRegistry.SetField(book, "_relationStatConvert", new BookLevelStatConvertData[0]);
                CharacterRegistry.SetField(book, "_applyMissionStat", new PlayerMissionStat[0]);
                CharacterRegistry.SetField(book, "_partId", new string[0]);
                CharacterRegistry.SetField(book, "_completeId", string.Empty);
                if (def.Tier.HasValue)
                    CharacterRegistry.SetField(book, "_martialLevel", def.Tier.Value);
                db.Books.List.Add(book);
                AddBonus(def, book, levels, player);
                Plugin.Log.LogInfo($"book '{def.Id}' registered from {def.Base} ({book.MartialType}, levels={levels.Length}, image={image != null})");
            }
        }

        // 秘笈の画面の「能力値加成」欄の行数 (本編の画面の枠)
        private const int MaxStatRows = 8;

        /// <summary>
        /// 能力値加成。本編と同じく、能力値の上乗せ一覧 (その能力値が参照する「〜加成_秘笈」) に変換データを足し、
        /// 秘笈の画面の欄にも同じデータを載せる。値の表は、レベル i/最大 の位置にちょうどその値が来る折れ線の曲線にする。
        /// </summary>
        private static void AddBonus(BookDef def, Book book, MartialLevelData[] levels, PlayerStatManagerData player)
        {
            if (def.Bonus.Count == 0)
                return;
            // 欄にはレベルアップで上がる能力値の種類も並ぶので、その分を差し引いた数までしか載せない
            int levelupRows = levels.SelectMany(l => l.LevelUpStat)
                .Where(m => m.Stat.StatType == MartialLevelupStatType.角色數值 && m.ModifyValue != 0)
                .Select(m => m.Stat.GetNameKey()).Distinct().Count();
            var converters = new List<BookLevelStatConvertData>();
            foreach (var pair in def.Bonus)
            {
                if (levelupRows + converters.Count >= MaxStatRows)
                {
                    Plugin.Log.LogWarning($"book '{def.Id}': bonus.{pair.Key} dropped (the book panel shows at most {MaxStatRows} stat rows)");
                    continue;
                }
                GameStat stat = EnumUtils.TryParseByStringValue<GameStatType>(pair.Key, out var type) ? player.Stats.Get(type) : null;
                var collections = stat != null
                    ? HarmonyLib.AccessTools.Field(typeof(GameStat), "_statAdditionData").GetValue(stat) as StatConvertCollectionData[]
                    : null;
                var collection = collections?.FirstOrDefault(c => c != null && c.name.EndsWith("_秘笈"));
                if (collection == null)
                {
                    Plugin.Log.LogError($"book '{def.Id}': stat '{pair.Key}' has no martial bonus list (e.g. m-sword, m-fist, m-projectile, dexterity, internal, stamina, life)");
                    continue;
                }

                var reference = new MartialStatReference();
                CharacterRegistry.SetField(reference, "_type", MartialLevelupStatType.角色數值);
                CharacterRegistry.SetField(reference, "_stat", stat);
                var converter = ScriptableObject.CreateInstance<BookLevelStatConvertData>();
                converter.name = $"秘笈_{def.Id}_{pair.Key}";
                converter.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CharacterRegistry.SetField(converter, "_book", book);
                CharacterRegistry.SetField(converter, "_relationStat", reference);
                CharacterRegistry.SetField(converter, "_curve", TableCurve(pair.Value));
                collection.List.Add(converter);
                converters.Add(converter);
            }
            CharacterRegistry.SetField(book, "_relationStatConvert", converters.ToArray());
            Plugin.Log.LogInfo($"book '{def.Id}': bonus [{string.Join(", ", def.Bonus.Select(p => p.Key + "=" + string.Join("/", p.Value)))}]");
        }

        /// <summary>(0,0) と (i/N, values[i-1]) を結ぶ折れ線。各点の傾きを前後の区間に合わせ、区間内も直線になるようにする。</summary>
        private static AnimationCurve TableCurve(List<int> values)
        {
            int n = values.Count;
            var points = new List<Vector2> { Vector2.zero };
            for (int i = 0; i < n; i++)
                points.Add(new Vector2((float)(i + 1) / n, values[i]));
            var keys = new Keyframe[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                float inSlope = i > 0 ? Slope(points[i - 1], points[i]) : Slope(points[i], points[i + 1]);
                float outSlope = i < points.Count - 1 ? Slope(points[i], points[i + 1]) : inSlope;
                keys[i] = new Keyframe(points[i].x, points[i].y, inSlope, outSlope);
            }
            return new AnimationCurve(keys);
        }

        private static float Slope(Vector2 a, Vector2 b)
        {
            return (b.y - a.y) / (b.x - a.x);
        }

        private static MartialLevelData BuildLevel(BookDef def, int index, BookLevelDef lv, PlayerStatManagerData player, List<string> errors)
        {
            string at = $"book '{def.Id}'.levels[{index}]";
            var stats = new List<MartialModifyStat>();

            foreach (var pair in lv.Stats)
            {
                GameStat stat = EnumUtils.TryParseByStringValue<GameStatType>(pair.Key, out var type) ? player.Stats.Get(type) : null;
                if (stat == null)
                    errors.Add($"{at}: unknown stat '{pair.Key}' (e.g. m-sword, m-fist, dexterity, mental)");
                else
                    stats.Add(Modify(MartialLevelupStatType.角色數值, "_stat", stat, pair.Value));
            }
            foreach (var pair in lv.Talents)
            {
                var talent = player.Talents.Get(pair.Key);
                if (talent == null)
                    errors.Add($"{at}: unknown talent '{pair.Key}'");
                else
                    stats.Add(Modify(MartialLevelupStatType.天賦, "_talent", talent, pair.Value));
            }
            foreach (var pair in lv.Affinity)
            {
                var rel = EnumUtils.TryParseByStringValue<RelationshipStatType>(pair.Key, out var rtype) ? player.Relationships.Get(rtype) : null;
                if (rel == null)
                    errors.Add($"{at}: unknown character '{pair.Key}'");
                else
                    stats.Add(Modify(MartialLevelupStatType.好感度, "_relationship", rel, pair.Value));
            }

            var level = ScriptableObject.CreateInstance<MartialLevelData>();
            level.name = $"{def.Id}_{index + 1}";
            level.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(level, "_points", lv.Points);
            // 条件が空なら常に修練できる
            var condition = ScriptableObject.CreateInstance<MartialLearnCondition>();
            condition.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(level, "_condition", condition);
            CharacterRegistry.SetField(level, "_levelupStats", stats);
            CharacterRegistry.SetField(level, "_consumable", new List<MartialLearnConsumable>());
            return level;
        }

        private static MartialModifyStat Modify(MartialLevelupStatType type, string field, object target, int value)
        {
            var reference = new MartialStatReference();
            CharacterRegistry.SetField(reference, "_type", type);
            CharacterRegistry.SetField(reference, field, target);
            var modify = new MartialModifyStat();
            CharacterRegistry.SetField(modify, "_stat", reference);
            CharacterRegistry.SetField(modify, "_value", value);
            return modify;
        }
    }
}
