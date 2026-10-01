// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LoMScenarioAddon.Shared
{
    internal sealed class CharacterDef
    {
        /// <summary>キャラ ID。Lua・セーブ・テキストキーで使う文字列 (例: lsa_test)。</summary>
        public string Id;
        public int DefaultAffinity;
        public bool SocialPanel = true;
        /// <summary>周回の引き継ぎ「繼承人際」(天命点で好感度を上げる画面) に載せるか。</summary>
        public bool Inherit = true;
    }

    internal sealed class FlagDef
    {
        /// <summary>フラグ名。Lua の statmodifymanager.SetFlag / GetFlag と条件で使う。</summary>
        public string Id;
        public int Default;
    }

    internal sealed class RateBoostDef
    {
        public Dictionary<string, object> When;
        public int Rate;
    }

    /// <summary>
    /// 場所イベント。本編の PositionEventData と同じ意味を持つ。
    /// </summary>
    internal sealed class EventDef
    {
        public string Id;
        /// <summary>候補を足す場所の抽選表の名前 (例: Back_Free_001)。</summary>
        public string Position;
        /// <summary>当たったときに流すシナリオ (scenario/story/&lt;名前&gt;.lua)。</summary>
        public string Story;
        /// <summary>基本の重み。本編の候補は 40〜100 程度。</summary>
        public int Rate;
        /// <summary>true なら一度起きたら二度と候補にならない (フラグ OnceFlag で管理)。</summary>
        public bool Once = true;
        /// <summary>登場条件 { "logic": "AND"|"OR", "items": [ { "left": 値, "op": "&gt;=", "right": 値 } ] }。null なら無条件。</summary>
        public Dictionary<string, object> When;
        public readonly List<RateBoostDef> Boosts = new List<RateBoostDef>();

        public string OnceFlag => "lsa_done_" + Id;
        /// <summary>抽選の候補名。本編は「場所の接頭辞_候補名」をシナリオ名にする。</summary>
        public string ResultType => "lsa_" + Id;
    }

    /// <summary>
    /// 訪問。自由行動で場所をクリックしたとき、条件を満たしていればその場所の固定シナリオの代わりに Story を流す。
    /// 通常の行動へ進む場合だけ差し替え、本編のイベントへ進む場合は差し替えない。
    /// </summary>
    internal sealed class VisitDef
    {
        public string Id;
        /// <summary>場所 (PositionType の文字列: Mall Center Alchemy Forge BackMountain Room1 Door Study Kitchen Room2 Secret)。</summary>
        public string Position;
        public string Story;
        public Dictionary<string, object> When;
        /// <summary>既定 false。true なら一度きり (フラグ OnceFlag で管理)。</summary>
        public bool Once;

        public string OnceFlag => "lsa_done_" + Id;
    }

    /// <summary>
    /// エンディング。本編のエンディング分岐の直前で When を判定し、成立すれば Story へ進む。
    /// Story の中で endgamepanel.Open(Id) を呼ぶと、本編と同じ画面にこのエンディングのカードが出る。
    /// </summary>
    internal sealed class EndingDef
    {
        /// <summary>エンディングのキー。汗青書・列伝の保存にもこの名前で残る。</summary>
        public string Id;
        public string Story;
        public Dictionary<string, object> When;

        /// <summary>本編の分岐に差し込む判定で使う条件名 (checkpointmanager.Condition)。</summary>
        public string ConditionName => "lsa_ending_" + Id;
    }

    /// <summary>
    /// 天賦 (技能)。本編の天賦 Base を複製し、ID・名前・説明を差し替える。戦闘の効果は Base のもの、または Effect (本編の効果キー)。
    /// Lua: statmodifymanager.AddTalent("ID", 1)。名前・説明は PlayerTalent/Name|Desc/&lt;ID&gt;。
    /// </summary>
    internal sealed class TalentDef
    {
        public string Id;
        public string Base;
        public string Effect;
        public int? MaxLevel;
        public bool? DisplayLevel;
        public int? Priority;
    }

    internal sealed class EffectUnitDef
    {
        /// <summary>複製元の本編の部品のアセット名 (例: B_Stat_003_自身_防禦_加20)。一覧は combat_units.tsv。</summary>
        public string Base;
        /// <summary>上書きする値 (部品のフィールド名 → 値)。</summary>
        public Dictionary<string, object> Set = new Dictionary<string, object>();
    }

    /// <summary>
    /// 決闘の効果 (状態)。本編の効果 Base を複製し、キーと部品を差し替える。天賦の effect や、決闘の敵の天賦から使う。
    /// 名前・説明は CombatSkill/Name|Desc/&lt;キー&gt;。
    /// </summary>
    internal sealed class EffectDef
    {
        public string Key;
        public string Base;
        /// <summary>null なら複製元の部品のまま。</summary>
        public List<EffectUnitDef> Units;
        /// <summary>効果そのものの値の上書き (例: _rounds)。</summary>
        public Dictionary<string, object> Set = new Dictionary<string, object>();
    }

    internal sealed class TalentLevel
    {
        public string Id;
        public int Level = 1;
    }

    /// <summary>秘笈の1レベル分。Points 回修練するとレベルが上がり、変化が適用される。</summary>
    internal sealed class BookLevelDef
    {
        public int Points = 1;
        /// <summary>能力値の増減 (GameStatType の文字列: m-sword, dexterity, mental など)。</summary>
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>();
        /// <summary>天賦のレベル増減 (本編の天賦 ID、または talents で足した ID)。</summary>
        public readonly Dictionary<string, int> Talents = new Dictionary<string, int>();
        /// <summary>好感度の増減 (キャラ ID)。</summary>
        public readonly Dictionary<string, int> Affinity = new Dictionary<string, int>();
    }

    /// <summary>
    /// 秘笈。本編の秘笈 Base を複製し、ID・アイコン・名前・レベルごとのデータを差し替える。
    /// Lua: statmodifymanager.AddBook("ID", 1)。名前などは Book/Title|Name|Desc/&lt;ID&gt;、アイコンは books/&lt;ID&gt;.png (312x300)。
    /// </summary>
    internal sealed class BookDef
    {
        public string Id;
        public string Base;
        /// <summary>階級 (1〜5)。武功系の能力値は「能力値/25+1 &gt;= 階級」のときだけ上がる。省略すると Base のまま。</summary>
        public int? Tier;
        public readonly List<BookLevelDef> Levels = new List<BookLevelDef>();
        /// <summary>
        /// 能力値加成 (秘笈の今のレベルに応じて常にかかる上乗せ)。能力値 → レベル1〜最大レベルの値の表。
        /// 例: "m-sword": [2, 5, 10]。本編は曲線で持つので、各レベルの位置にちょうどその値が来る曲線に直して渡す。
        /// </summary>
        public readonly Dictionary<string, List<int>> Bonus = new Dictionary<string, List<int>>();
    }

    /// <summary>
    /// 決闘 (1対1)。本編の決闘設定を複製し、敵を Character の見た目・名前・台詞に差し替える。
    /// Lua: luamanager.SetCurrentStoryScript("戻り先") → luamanager.ChangeScene("Combat", Id, "Story")。
    /// 勝てばフラグ WonFlag = 1、負ければ 0。
    /// </summary>
    internal sealed class DuelDef
    {
        public string Id;
        /// <summary>敵として出す追加キャラの ID。</summary>
        public string Character;
        /// <summary>複製元の本編の決闘キー (CL_ を除いた名前、例: 3001_01)。省略すると三師兄との手合わせ。能力値・技・背景・曲はここから借りる。</summary>
        public string Template;
        /// <summary>能力値・行動の重み・絶招・才能を借りる本編の敵 (例: Enemy_南宮淺_001)。省略すると Template の敵。</summary>
        public string Enemy;
        /// <summary>決闘の曲 (本編の曲名 例: 戰鬥_001、または自前の music/&lt;名前&gt;.wav)。省略すると Template の曲。</summary>
        public string Music;
        /// <summary>体力 (省略すると借りた敵のまま)。</summary>
        public int? Health;
        /// <summary>行動の重み (attack / talk / block / weapon / ultimate)。本編の既定は口撃 0.9、攻撃・暗器・絶招 0.25、防御 0.5。</summary>
        public readonly Dictionary<string, double> Rates = new Dictionary<string, double>();
        /// <summary>能力値の上書き (CombatStat の数値フィールド名: Strength, Dexterity, Disposition など)。</summary>
        public readonly Dictionary<string, double> Stats = new Dictionary<string, double>();
        /// <summary>台詞 (normal / good / bad / attacked)。本文を直接書く。</summary>
        public readonly Dictionary<string, List<string>> Talk = new Dictionary<string, List<string>>();
        /// <summary>敵の技構成 (天賦 ID とレベル)。書けば借りた敵の技構成を置き換える。本編の天賦も追加の天賦も使える。</summary>
        public readonly List<TalentLevel> Talents = new List<TalentLevel>();

        public string WonFlag => Id + "_won";
        public string LevelName => "CL_" + Id;
    }

    /// <summary>
    /// scenario/scenario.json。パッチャーとプラグインが同じファイルを読む。
    /// </summary>
    internal sealed class ScenarioManifest
    {
        public const string FileName = "scenario.json";

        // 公式 ID (brother4, special999 など) と同じ文字種。英小文字で始める。
        internal static readonly Regex IdPattern = new Regex("^[a-z][a-z0-9_]{0,47}$");

        public string Id;
        /// <summary>訪問シナリオから本来の行動 (差し替えた場所の固定シナリオ) へ戻るときの名前。</summary>
        public const string PositionDefaultAlias = "lsa_position_default";

        public string Title;
        /// <summary>配布用の版 (zip の名前に使う)。</summary>
        public string Version;
        /// <summary>テキストの既定の言語 (text.&lt;言語&gt;.csv)。今の言語のファイルやキーが無いときに使う。</summary>
        public string DefaultLanguage = "ja";
        public readonly List<CharacterDef> Characters = new List<CharacterDef>();
        public readonly List<FlagDef> Flags = new List<FlagDef>();
        public readonly List<EventDef> Events = new List<EventDef>();
        public readonly List<VisitDef> Visits = new List<VisitDef>();
        public readonly List<EndingDef> Endings = new List<EndingDef>();
        /// <summary>死亡エンディング (奪命譜) のキー。Lua: luamanager.ChangeScene("GameOver", "&lt;キー&gt;", "")。</summary>
        public readonly List<string> Deaths = new List<string>();
        /// <summary>CG 画面 (結局CG の一覧) のキー。Lua: runwait(cgpanel.Open("&lt;キー&gt;"))。絵は cgs/&lt;キー&gt;.png (1100x1080)。</summary>
        public readonly List<string> Cgs = new List<string>();
        public readonly List<DuelDef> Duels = new List<DuelDef>();
        public readonly List<TalentDef> Talents = new List<TalentDef>();
        public readonly List<BookDef> Books = new List<BookDef>();
        public readonly List<EffectDef> Effects = new List<EffectDef>();
        /// <summary>Lua の checkpointmanager.Condition(名前) で判定できる条件。</summary>
        public readonly Dictionary<string, Dictionary<string, object>> Conditions = new Dictionary<string, Dictionary<string, object>>();

        /// <summary>
        /// 読み込み。致命的でない問題は warnings に積み、その項目だけ飛ばす。
        /// </summary>
        public static ScenarioManifest Load(string path, List<string> warnings)
        {
            var root = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null)
                throw new FormatException("scenario.json: top level must be an object");

            var manifest = new ScenarioManifest
            {
                Id = GetString(root, "id"),
                Title = GetString(root, "title"),
                Version = GetString(root, "version"),
                DefaultLanguage = GetString(root, "default_language") ?? "ja",
            };

            var seen = new HashSet<string>();
            foreach (var item in GetList(root, "characters"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id))
                {
                    warnings.Add($"character id '{id}' is invalid (use lowercase letters, digits, '_'; start with a letter), skipped");
                    continue;
                }
                if (!seen.Add(id))
                {
                    warnings.Add($"character id '{id}' is duplicated, skipped");
                    continue;
                }
                var def = new CharacterDef { Id = id };
                if (obj.TryGetValue("affinity", out var aff) && aff is Dictionary<string, object> affObj)
                    def.DefaultAffinity = Clamp(GetInt(affObj, "default", 0), 0, 100);
                if (obj.TryGetValue("social_panel", out var sp) && sp is bool spb)
                    def.SocialPanel = spb;
                if (obj.TryGetValue("inherit", out var inh) && inh is bool inhb)
                    def.Inherit = inhb;
                manifest.Characters.Add(def);
            }
            var flagIds = new HashSet<string>();
            foreach (var item in GetList(root, "flags"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !flagIds.Add(id))
                {
                    warnings.Add($"flag id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                manifest.Flags.Add(new FlagDef { Id = id, Default = GetInt(obj, "default", 0) });
            }

            // events と visits の ID は1回きりのフラグ名 (lsa_done_<id>) を共有するので、まとめて重複を確かめる。
            var eventIds = new HashSet<string>();
            foreach (var item in GetList(root, "events"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !eventIds.Add(id))
                {
                    warnings.Add($"event id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                var ev = new EventDef
                {
                    Id = id,
                    Position = GetString(obj, "position"),
                    Story = GetString(obj, "story"),
                    Rate = GetInt(obj, "rate", 50),
                    When = obj.TryGetValue("when", out var when) ? when as Dictionary<string, object> : null,
                };
                if (obj.TryGetValue("once", out var once) && once is bool onceb)
                    ev.Once = onceb;
                if (string.IsNullOrEmpty(ev.Position) || string.IsNullOrEmpty(ev.Story))
                {
                    warnings.Add($"event '{id}' needs both 'position' and 'story', skipped");
                    continue;
                }
                foreach (var b in GetList(obj, "boosts"))
                {
                    if (b is Dictionary<string, object> bobj && bobj.TryGetValue("when", out var bw) && bw is Dictionary<string, object> bwhen)
                        ev.Boosts.Add(new RateBoostDef { When = bwhen, Rate = GetInt(bobj, "rate", 0) });
                    else
                        warnings.Add($"event '{id}': boost needs 'when' and 'rate', skipped");
                }
                manifest.Events.Add(ev);
            }

            foreach (var item in GetList(root, "visits"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !eventIds.Add(id))
                {
                    warnings.Add($"visit id '{id}' is invalid or duplicated (ids are shared with events), skipped");
                    continue;
                }
                var visit = new VisitDef
                {
                    Id = id,
                    Position = GetString(obj, "position"),
                    Story = GetString(obj, "story"),
                    When = obj.TryGetValue("when", out var when) ? when as Dictionary<string, object> : null,
                };
                if (obj.TryGetValue("once", out var once) && once is bool onceb)
                    visit.Once = onceb;
                if (string.IsNullOrEmpty(visit.Position) || string.IsNullOrEmpty(visit.Story))
                {
                    warnings.Add($"visit '{id}' needs both 'position' and 'story', skipped");
                    continue;
                }
                manifest.Visits.Add(visit);
            }

            foreach (var (key, target) in new[] { ("deaths", manifest.Deaths), ("cgs", manifest.Cgs) })
            {
                foreach (var item in GetList(root, key))
                {
                    string id = item as string ?? (item is Dictionary<string, object> o ? GetString(o, "id") : null);
                    if (id == null || !IdPattern.IsMatch(id) || target.Contains(id))
                        warnings.Add($"{key} id '{id}' is invalid or duplicated, skipped");
                    else
                        target.Add(id);
                }
            }

            var endingIds = new HashSet<string>();
            foreach (var item in GetList(root, "endings"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !endingIds.Add(id))
                {
                    warnings.Add($"ending id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                var ending = new EndingDef
                {
                    Id = id,
                    Story = GetString(obj, "story"),
                    When = obj.TryGetValue("when", out var when) ? when as Dictionary<string, object> : null,
                };
                if (string.IsNullOrEmpty(ending.Story) || ending.When == null)
                {
                    warnings.Add($"ending '{id}' needs both 'story' and 'when', skipped");
                    continue;
                }
                manifest.Endings.Add(ending);
            }

            var duelIds = new HashSet<string>();
            foreach (var item in GetList(root, "duels"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !duelIds.Add(id))
                {
                    warnings.Add($"duel id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                var duel = new DuelDef
                {
                    Id = id,
                    Character = GetString(obj, "character"),
                    Template = GetString(obj, "template"),
                    Music = GetString(obj, "music"),
                    Enemy = GetString(obj, "enemy"),
                };
                foreach (var (key, target) in new[] { ("rates", duel.Rates), ("stats", duel.Stats) })
                {
                    if (obj.TryGetValue(key, out var v) && v is Dictionary<string, object> numbers)
                    {
                        foreach (var pair in numbers)
                        {
                            if (pair.Value is double d)
                                target[pair.Key] = d;
                            else
                                warnings.Add($"duel '{id}': {key}.{pair.Key} must be a number, skipped");
                        }
                    }
                }
                if (obj.TryGetValue("health", out var hp) && hp is double hpd)
                    duel.Health = (int)Math.Round(hpd);
                if (string.IsNullOrEmpty(duel.Character) || !manifest.Characters.Any(c => c.Id == duel.Character))
                {
                    warnings.Add($"duel '{id}': 'character' must be one of this scenario's characters, skipped");
                    continue;
                }
                foreach (var t in GetList(obj, "talents"))
                {
                    if (t is Dictionary<string, object> tobj && GetString(tobj, "id") is string tid)
                        duel.Talents.Add(new TalentLevel { Id = tid, Level = GetInt(tobj, "level", 1) });
                    else
                        warnings.Add($"duel '{id}': talents entries need 'id', skipped");
                }
                if (obj.TryGetValue("talk", out var talk) && talk is Dictionary<string, object> talkObj)
                {
                    foreach (var pair in talkObj)
                    {
                        if (pair.Value is List<object> lines)
                            duel.Talk[pair.Key] = lines.OfType<string>().ToList();
                    }
                }
                manifest.Duels.Add(duel);
            }

            var effectKeys = new HashSet<string>();
            foreach (var item in GetList(root, "effects"))
            {
                var obj = item as Dictionary<string, object>;
                string key = obj != null ? GetString(obj, "key") : null;
                if (key == null || !IdPattern.IsMatch(key) || !effectKeys.Add(key))
                {
                    warnings.Add($"effect key '{key}' is invalid or duplicated, skipped");
                    continue;
                }
                var effect = new EffectDef { Key = key, Base = GetString(obj, "base") };
                if (string.IsNullOrEmpty(effect.Base))
                {
                    warnings.Add($"effect '{key}' needs 'base' (an official effect key such as B0019), skipped");
                    continue;
                }
                if (obj.TryGetValue("set", out var es) && es is Dictionary<string, object> esd)
                    effect.Set = esd;
                if (obj.TryGetValue("units", out var ul) && ul is List<object> units)
                {
                    effect.Units = new List<EffectUnitDef>();
                    foreach (var u in units)
                    {
                        if (u is Dictionary<string, object> uo && GetString(uo, "base") is string ub)
                            effect.Units.Add(new EffectUnitDef { Base = ub, Set = uo.TryGetValue("set", out var us) && us is Dictionary<string, object> usd ? usd : new Dictionary<string, object>() });
                        else
                            warnings.Add($"effect '{key}': units entries need 'base', skipped");
                    }
                }
                manifest.Effects.Add(effect);
            }

            var talentIds = new HashSet<string>();
            foreach (var item in GetList(root, "talents"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !talentIds.Add(id))
                {
                    warnings.Add($"talent id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                var talent = new TalentDef { Id = id, Base = GetString(obj, "base"), Effect = GetString(obj, "effect") };
                if (string.IsNullOrEmpty(talent.Base))
                {
                    warnings.Add($"talent '{id}' needs 'base' (an official talent id such as 0019), skipped");
                    continue;
                }
                if (obj.TryGetValue("max_level", out var ml) && ml is double mld) talent.MaxLevel = Math.Max(1, (int)mld);
                if (obj.TryGetValue("display_level", out var dl) && dl is bool dlb) talent.DisplayLevel = dlb;
                if (obj.TryGetValue("priority", out var pr) && pr is double prd) talent.Priority = (int)prd;
                manifest.Talents.Add(talent);
            }

            var bookIds = new HashSet<string>();
            foreach (var item in GetList(root, "books"))
            {
                var obj = item as Dictionary<string, object>;
                string id = obj != null ? GetString(obj, "id") : null;
                if (id == null || !IdPattern.IsMatch(id) || !bookIds.Add(id))
                {
                    warnings.Add($"book id '{id}' is invalid or duplicated, skipped");
                    continue;
                }
                var book = new BookDef { Id = id, Base = GetString(obj, "base") };
                if (obj.TryGetValue("tier", out var tier) && tier is double tierd) book.Tier = Clamp((int)tierd, 1, 5);
                foreach (var lv in GetList(obj, "levels"))
                {
                    if (!(lv is Dictionary<string, object> lvObj))
                        continue;
                    var level = new BookLevelDef { Points = Math.Max(1, GetInt(lvObj, "points", 1)) };
                    foreach (var (key, target) in new[] { ("stats", level.Stats), ("talents", level.Talents), ("affinity", level.Affinity) })
                    {
                        if (lvObj.TryGetValue(key, out var v) && v is Dictionary<string, object> numbers)
                            foreach (var pair in numbers)
                                if (pair.Value is double d) target[pair.Key] = (int)Math.Round(d);
                    }
                    book.Levels.Add(level);
                }
                if (obj.TryGetValue("bonus", out var bonus) && bonus is Dictionary<string, object> bonusObj)
                {
                    foreach (var pair in bonusObj)
                    {
                        var values = pair.Value is List<object> list ? list.OfType<double>().Select(v => (int)Math.Round(v)).ToList() : null;
                        if (values == null || values.Count != book.Levels.Count)
                            warnings.Add($"book '{id}': bonus.{pair.Key} must have one value per level ({book.Levels.Count}), skipped");
                        else
                            book.Bonus[pair.Key] = values;
                    }
                }
                if (string.IsNullOrEmpty(book.Base) || book.Levels.Count == 0)
                {
                    warnings.Add($"book '{id}' needs 'base' (an official book id such as 2001) and at least one level, skipped");
                    continue;
                }
                manifest.Books.Add(book);
            }

            if (root.TryGetValue("conditions", out var conds) && conds is Dictionary<string, object> condObj)
            {
                foreach (var pair in condObj)
                {
                    if (!IdPattern.IsMatch(pair.Key) || !(pair.Value is Dictionary<string, object> when))
                    {
                        warnings.Add($"condition '{pair.Key}' is invalid (name: lowercase id, value: {{\"logic\", \"items\"}}), skipped");
                        continue;
                    }
                    manifest.Conditions[pair.Key] = when;
                }
            }
            return manifest;
        }

        /// <summary>enum に足すメンバー名。ID から機械的に作る (内部用で、画面には出ない)。</summary>
        public static string EnumMemberName(string id)
        {
            return "LSA_" + id;
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var v) ? v as string : null;
        }

        private static int GetInt(Dictionary<string, object> obj, string key, int fallback)
        {
            return obj.TryGetValue(key, out var v) && v is double d ? (int)Math.Round(d) : fallback;
        }

        private static IEnumerable<object> GetList(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var v) && v is List<object> list ? list : Enumerable.Empty<object>();
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : v > max ? max : v;
        }
    }
}
