// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LoMScenarioAddon.Shared;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 決闘 (1対1)。
    /// 決闘の場面を読み込んだ直後に、本編の決闘設定 (CombatLevel) と敵 (CombatStat) を複製して一覧に足す。
    /// 敵の部品 (Prefab)・技・音・背景・曲は複製元のまま。見た目は4枚の絵 (通常・攻撃・被弾・防御) と
    /// 状態パネルの立ち絵・台詞の顔だけなので、それを追加キャラの画像に差し替える。
    /// </summary>
    internal static class Duels
    {
        private static readonly string[] Renderers = { "_idleSprite", "_chargeSprite", "_attackSprite", "_hurtSprite", "_defenceSprite" };
        private static readonly string[] RendererKinds = { "normal", "normal", "attack", "hurt", "defence" };

        // 台詞の種類 → CombatTalkingDialog のフィールド
        private static readonly Dictionary<string, string> TalkFields = new Dictionary<string, string>
        {
            { "normal", nameof(CombatTalkingDialog.NormalTalk) },
            { "good", nameof(CombatTalkingDialog.GoodTalk) },
            { "bad", nameof(CombatTalkingDialog.BadTalk) },
            { "attacked", nameof(CombatTalkingDialog.TalkAttacked) },
        };

        private static IEnumerable<DuelDef> Defs => Plugin.Manifest != null ? Plugin.Manifest.Duels : Enumerable.Empty<DuelDef>();

        internal static bool IsOurs(CombatStat stat)
        {
            return stat != null && Defs.Any(d => d.Character == stat.Name);
        }

        /// <summary>決闘の場面を読み込んだ直後。一覧に自前の決闘設定を足す (前回の分は作り直す)。</summary>
        internal static void Inject()
        {
            var config = Resources.FindObjectsOfTypeAll<CombatLevelConfig>().FirstOrDefault();
            if (config == null || !Defs.Any())
                return;

            foreach (var duel in Defs)
            {
                config.List.RemoveAll(x => x != null && x.name == duel.LevelName);
                var template = duel.Template != null ? config.Get("CL_" + duel.Template) : DefaultTemplate(config);
                if (template == null || template.EnemyStat == null)
                {
                    Plugin.Log.LogError($"duel '{duel.Id}': template '{duel.Template ?? "(first)"}' not found");
                    continue;
                }
                config.List.Add(Build(duel, template));
                Plugin.Log.LogInfo($"duel '{duel.Id}': {duel.LevelName} built from {template.name} ({template.EnemyStat.name})");
            }
        }

        // 既定の複製元: 三師兄との手合わせ (体力600の通常の決闘)。CL_0000 系は開発用のテスト関卡なので使わない。
        private const string DefaultTemplateName = "CL_3001_01";

        private static CombatLevel DefaultTemplate(CombatLevelConfig config)
        {
            return config.Get(DefaultTemplateName)
                ?? config.List.FirstOrDefault(x => x != null && !x.name.StartsWith("CL_0000") && x.EnemyStat != null && x.EnemyStat.CombatAvatar != null);
        }

        // 行動の重み (scenario.json の rates) → CombatStat のフィールド
        private static readonly Dictionary<string, string> RateFields = new Dictionary<string, string>
        {
            { "attack", nameof(CombatStat.AttackRate) },
            { "talk", nameof(CombatStat.TalkRate) },
            { "block", nameof(CombatStat.BlockRate) },
            { "weapon", nameof(CombatStat.WeaponkRate) },
            { "ultimate", nameof(CombatStat.UltimateRate) },
        };

        // stats で書き換えてはいけないもの (決闘中の状態)
        private static readonly HashSet<string> RuntimeFields = new HashSet<string>
        {
            nameof(CombatStat.CurrentHealth), nameof(CombatStat.CurrentStamina),
            nameof(CombatStat.CurrentPoisonValue), nameof(CombatStat.CurrentParalyzedValue), nameof(CombatStat.FailureRate),
        };

        private static CombatStat BaseEnemy(DuelDef duel, CombatLevel template)
        {
            if (duel.Enemy == null)
                return template.EnemyStat;
            var enemy = Resources.FindObjectsOfTypeAll<CombatStat>().FirstOrDefault(x => x.name == duel.Enemy);
            if (enemy == null)
                Plugin.Log.LogError($"duel '{duel.Id}': enemy '{duel.Enemy}' not found, using the template's enemy ({template.EnemyStat.name})");
            return enemy ?? template.EnemyStat;
        }

        private static void ApplyOverrides(DuelDef duel, CombatStat stat)
        {
            foreach (var pair in duel.Rates)
            {
                if (RateFields.TryGetValue(pair.Key, out var field))
                    AccessTools.Field(typeof(CombatStat), field).SetValue(stat, (float)pair.Value);
                else
                    Plugin.Log.LogWarning($"duel '{duel.Id}': unknown rate '{pair.Key}' (attack / talk / block / weapon / ultimate)");
            }
            foreach (var pair in duel.Stats)
            {
                var field = typeof(CombatStat).GetFields().FirstOrDefault(f =>
                    string.Equals(f.Name, pair.Key, System.StringComparison.OrdinalIgnoreCase)
                    && (f.FieldType == typeof(int) || f.FieldType == typeof(float)));
                if (field == null || RuntimeFields.Contains(field.Name))
                {
                    Plugin.Log.LogWarning($"duel '{duel.Id}': unknown stat '{pair.Key}'");
                    continue;
                }
                field.SetValue(stat, field.FieldType == typeof(int) ? (object)(int)System.Math.Round(pair.Value) : (float)pair.Value);
            }
        }

        private static void ApplyTalents(DuelDef duel, CombatStat stat)
        {
            if (duel.Talents.Count == 0)
                return;
            var talents = Resources.FindObjectsOfTypeAll<PlayerStatManagerData>().FirstOrDefault()?.Talents;
            var items = new List<CombatTalentItem>();
            foreach (var t in duel.Talents)
            {
                var data = talents?.Get(t.Id);
                if (data == null)
                {
                    Plugin.Log.LogWarning($"duel '{duel.Id}': talent '{t.Id}' not found, skipped");
                    continue;
                }
                items.Add(new CombatTalentItem { Data = data, Level = t.Level });
            }
            stat.TalentItems = items;
            Plugin.Log.LogInfo($"duel '{duel.Id}': talents [{string.Join(", ", items.Select(i => i.Data.Id + " Lv" + i.Level))}]");
        }

        private static CombatLevel Build(DuelDef duel, CombatLevel template)
        {
            var baseEnemy = BaseEnemy(duel, template);
            var stat = Object.Instantiate(baseEnemy);
            stat.name = "Stat_" + duel.Id;
            stat.Name = duel.Character;
            var images = Plugin.Content.GetCharacterImages(duel.Character);
            if (images.Icon != null)
                stat.HeadImage = images.Icon;
            if (duel.Health.HasValue)
            {
                stat.MaxHealth = duel.Health.Value;
                stat.DefaultHealth = duel.Health.Value;
            }
            ApplyOverrides(duel, stat);
            ApplyTalents(duel, stat);
            Plugin.Log.LogInfo($"duel '{duel.Id}': enemy based on {baseEnemy.name}, hp={stat.DefaultHealth} " +
                               $"rates attack={stat.AttackRate} talk={stat.TalkRate} block={stat.BlockRate} weapon={stat.WeaponkRate} ultimate={stat.UltimateRate}");

            var level = Object.Instantiate(template);
            level.name = duel.LevelName;
            CharacterRegistry.SetField(level, "_desc", "LoMScenarioAddon: " + duel.Id);
            CharacterRegistry.SetField(level, "_enemyStat", stat);
            CharacterRegistry.SetField(level, "_enemyTalking", BuildTalking(duel));
            // 複製元のキャラ固有の演出・結果は持ち込まない
            CharacterRegistry.SetField(level, "_talkingEvent", null);
            CharacterRegistry.SetField(level, "_deadEnd", false);
            CharacterRegistry.SetField(level, "_deadLibrary", null);
            level.Events = new List<CombatEventData>();
            if (duel.Music != null)
            {
                var music = ScenarioAudio.FindMusic(duel.Music);
                if (music != null)
                    CharacterRegistry.SetField(level, "_musicData", music);
                else
                    Plugin.Log.LogWarning($"duel '{duel.Id}': music '{duel.Music}' not found, using the template's music");
            }
            CharacterRegistry.SetField(level, "_winResult", ResultFlags(duel.WonFlag, 1));
            CharacterRegistry.SetField(level, "_loseResult", ResultFlags(duel.WonFlag, 0));
            return level;
        }

        private static CombatTalkingDialog BuildTalking(DuelDef duel)
        {
            var dialog = ScriptableObject.CreateInstance<CombatTalkingDialog>();
            dialog.name = "Talking_" + duel.Id;
            // 本編は空の配列なら台詞なしで進む (null は不可)
            dialog.NormalTalk = dialog.GoodTalk = dialog.BadTalk = dialog.StopAction =
                dialog.StopTalk = dialog.TalkAttacked = dialog.AutoAttack = dialog.NoAction = new string[0];
            foreach (var pair in duel.Talk)
            {
                if (!TalkFields.TryGetValue(pair.Key, out var field))
                {
                    Plugin.Log.LogWarning($"duel '{duel.Id}': unknown talk kind '{pair.Key}' (normal / good / bad / attacked)");
                    continue;
                }
                var keys = pair.Value.Select((text, i) =>
                {
                    string key = $"LSA_{duel.Id}_{pair.Key}{i}";
                    Plugin.Content.AddRuntimeText("CombatTalking/" + key, text);
                    return key;
                }).ToArray();
                AccessTools.Field(typeof(CombatTalkingDialog), field).SetValue(dialog, keys);
            }
            return dialog;
        }

        private static CombatResultFlag[] ResultFlags(string flagName, int state)
        {
            var flag = PositionEvents.FindFlagPublic(flagName);
            if (flag == null)
                return new CombatResultFlag[0];
            var result = new CombatResultFlag();
            CharacterRegistry.SetField(result, "_flag", flag);
            CharacterRegistry.SetField(result, "_state", state);
            return new[] { result };
        }

        /// <summary>CombatEnemyController.SetData (複製元の絵を読み込む) の後で、追加キャラの絵に差し替える。</summary>
        internal static IEnumerator WrapEnemySetData(CombatEnemyController controller, IEnumerator original)
        {
            while (original.MoveNext())
                yield return original.Current;

            var stat = AccessTools.Field(typeof(CombatCharacterController), "_combatStat").GetValue(controller) as CombatStat;
            if (!IsOurs(stat))
                yield break;
            for (int i = 0; i < Renderers.Length; i++)
            {
                var renderer = AccessTools.Field(typeof(CombatEnemyController), Renderers[i]).GetValue(controller) as SpriteRenderer;
                if (renderer == null)
                    continue;
                var sprite = Plugin.Content.GetCombatSprite(stat.Name, RendererKinds[i], renderer.sprite);
                if (sprite != null)
                    renderer.sprite = sprite;
            }
            Plugin.Log.LogInfo($"duel: enemy sprites replaced for {stat.Name}");
        }

        /// <summary>CombatCharacterStatusUI.Setup (状態パネルの立ち絵) の後で、追加キャラの立ち絵に差し替える。</summary>
        internal static IEnumerator WrapStatusSetup(CombatCharacterStatusUI ui, CombatActionController controller, IEnumerator original)
        {
            while (original.MoveNext())
                yield return original.Current;

            var stat = controller != null && controller.Stat != null ? controller.Stat.Data : null;
            if (!IsOurs(stat))
                yield break;
            var images = Plugin.Content.GetCharacterImages(stat.Name);
            var avatar = AccessTools.Field(typeof(CombatCharacterStatusUI), "_avatar").GetValue(ui) as Image;
            if (avatar != null && images.Avatar != null)
                avatar.sprite = images.Avatar;
        }
    }
}
