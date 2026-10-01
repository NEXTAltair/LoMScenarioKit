// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections;
using System.Linq;
using HarmonyLib;
using Lean.Localization;
using Mortal.Core;
using LoMScenarioAddon.Shared;
using Mortal.Story;
using OBB.Framework.Extensions;
using UnityEngine;

namespace LoMScenarioAddon
{
    internal static class Patches
    {
        /// <summary>
        /// 公式の訳が無いキーだけ、シナリオのテキストで埋める。公式・日本語化MODの訳は上書きしない。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LeanLocalization), nameof(LeanLocalization.GetTranslationText))]
        private static void GetTranslationTextPostfix(string name, ref string __result)
        {
            if (!string.IsNullOrEmpty(__result) || name == null || Plugin.Content == null)
                return;
            if (Plugin.Content.TryGetText(name, out var text))
                __result = text;
        }

        private static readonly System.Reflection.FieldInfo LuaEnvironmentField =
            typeof(LuaManager).GetField("_luaEnvironment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// ストーリー画面が開始するシナリオの読み込み。scenario/story/&lt;名前&gt;.lua があればそれを流す。
        /// 公式シナリオ名と同名なら公式を上書きすることになるので、追加シナリオは固有の名前を付けること。
        /// Binarizer 系プラグインも同じメソッドに割り込むので、先に動くよう優先度を上げる。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(LuaManager), "ExecuteLuaScript")]
        private static bool ExecuteLuaScriptPrefix(LuaManager __instance)
        {
            string name = __instance.ScriptName;
            // 訪問シナリオから本来の行動へ: 差し替えたシナリオ名に戻して本編 (と Binarizer) に任せる
            if (name == Visits.DefaultActivityAlias)
            {
                if (string.IsNullOrEmpty(Visits.LastDefaultScript))
                {
                    Plugin.Log.LogError($"{Visits.DefaultActivityAlias}: no default activity recorded");
                    return true;
                }
                PlayerStatManagerData.Instance.SetStoryScript(Visits.LastDefaultScript);
                Plugin.Log.LogInfo($"story: back to default activity {Visits.LastDefaultScript}");
                return true;
            }
            if (!TryResolveStory(name, out var story, out var ev, out var code))
            {
                if (ev != null)
                    Plugin.Log.LogError($"event '{ev.Id}': scenario/story/{story}.lua not found");
                return true;
            }
            if (ev != null)
                PositionEvents.MarkStarted(ev);
            var env = (Fungus.LuaEnvironment)LuaEnvironmentField.GetValue(__instance);
            var fn = env.LoadLuaFunction(code, name + ".LuaScript");
            env.RunLuaFunction(fn, runAsCoroutine: true);
            Plugin.Log.LogInfo(ev != null ? $"story: {story} started by event '{ev.Id}' ({name})" : $"story: {story} started from scenario/story");
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mortal.Free.PositionController), nameof(Mortal.Free.PositionController.OnPositionClick))]
        private static void OnPositionClickPrefix(Mortal.Free.PositionController __instance)
        {
            Visits.BeforeClick(__instance);
        }

        /// <summary>場所をクリックした直後。通常の行動に進んでいて、条件を満たす訪問があれば差し替える。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Mortal.Free.PositionController), nameof(Mortal.Free.PositionController.OnPositionClick))]
        private static void OnPositionClickPostfix(Mortal.Free.PositionController __instance)
        {
            Visits.AfterClick(__instance);
        }

        /// <summary>Lua の checkpointmanager.Condition(名前)。scenario.json の conditions にあればそれで判定する。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CheckPointManager), nameof(CheckPointManager.Condition))]
        private static bool ConditionPrefix(CheckPointManager __instance, string name, ref bool __result)
        {
            if (!ScenarioConditions.TryEvaluate(__instance, name, out var result))
                return true;
            __result = result;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Mortal.Combat.CombatEnemyController), nameof(Mortal.Combat.CombatEnemyController.SetData))]
        private static void CombatEnemySetDataPostfix(Mortal.Combat.CombatEnemyController __instance, ref IEnumerator __result)
        {
            __result = Duels.WrapEnemySetData(__instance, __result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Mortal.Combat.CombatCharacterStatusUI), nameof(Mortal.Combat.CombatCharacterStatusUI.Setup))]
        private static void CombatStatusSetupPostfix(Mortal.Combat.CombatCharacterStatusUI __instance, Mortal.Combat.CombatActionController controller, ref IEnumerator __result)
        {
            __result = Duels.WrapStatusSetup(__instance, controller, __result);
        }

        /// <summary>
        /// 背景。本編の背景部品は、背景名ごとに読み込んだ画像を辞書 (_dicView) に持ち、辞書にある名前は読み込まずにそれを使う。
        /// 部品の初期化直後に自前の背景を辞書へ入れておけば、Lua の flowcharts.LoadView("自前の背景名") がそのまま効く。
        /// (Awake は Unity が直接呼ぶので JIT のインライン化で割り込みが外れる心配が無い)
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StoryViewImage), "Awake")]
        private static void StoryViewImageAwakePostfix(StoryViewImage __instance)
        {
            if (Plugin.Content == null)
                return;
            var dic = AccessTools.Field(typeof(StoryViewImage), "_dicView").GetValue(__instance) as System.Collections.Generic.Dictionary<string, Sprite>;
            if (dic == null)
                return;
            foreach (var pair in Plugin.Content.GetBackgrounds())
                dic[pair.Key] = pair.Value;
        }

        /// <summary>自前のイベント画像だけ専用キーへ変換し、本編の表示処理を実行する。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoryPicture), nameof(StoryPicture.Show))]
        private static void StoryPictureShowPrefix(ref string key)
        {
            StoryPictures.RedirectKey(ref key);
        }

        /// <summary>周回の引き継ぎ「繼承人際」の行を作る直前に、追加キャラの行を足す。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FateBonusPanel), "InitSocial")]
        private static void FateBonusInitSocialPrefix(FateBonusPanel __instance)
        {
            SocialInheritance.Extend(__instance);
        }

        /// <summary>汗青書などの件数 (全セーブ共通データ) を読み込む直前に、自前のエンディングカードを登録しておく。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LibrarySystem), nameof(LibrarySystem.Load))]
        private static void LibraryLoadPrefix(LibrarySystem __instance)
        {
            Endings.RegisterCards(__instance);
        }

        /// <summary>場所の抽選の直前に、その場所の候補一覧へ追加イベントを足す。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CheckPointManager), nameof(CheckPointManager.Position))]
        private static void PositionPrefix(CheckPointManager __instance, string name)
        {
            PositionEvents.EnsureInjected(__instance, name);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CheckPointManager), nameof(CheckPointManager.Position))]
        private static void PositionPostfix(string name, string __result)
        {
            var ev = PositionEvents.MatchScript(__result);
            if (ev != null)
                Plugin.Log.LogInfo($"position {name}: event '{ev.Id}' was drawn");
        }

        /// <summary>
        /// シナリオ名を scenario/story の Lua に解決する。直接のシナリオ名 (TestStoryKey など) か、
        /// 場所イベントの候補名 (本編が「場所の接頭辞_候補名」にしたもの)。
        /// </summary>
        private static bool TryResolveStory(string name, out string story, out EventDef ev, out string code)
        {
            story = name;
            ev = null;
            code = null;
            if (string.IsNullOrEmpty(name) || Plugin.Content == null)
                return false;
            // 本編のエンディング分岐シナリオには自前エンディングの判定を差し込む
            if (Endings.TryGetPatchedScript(name, out code))
                return true;
            ev = PositionEvents.MatchScript(name);
            if (ev != null)
                story = ev.Story;
            if (!Plugin.Content.TryGetStory(story, out code))
                return false;
            code = StoryTextKeys.Convert(story, code, Plugin.Content);
            return true;
        }

        /// <summary>TryResolveStory が true を返すシナリオか (本文の読み込み・変換はしない)。</summary>
        private static bool IsScenarioScript(string name)
        {
            if (string.IsNullOrEmpty(name) || Plugin.Content == null)
                return false;
            if (Endings.TryGetPatchedScript(name, out _))
                return true;
            var ev = PositionEvents.MatchScript(name);
            return Plugin.Content.HasStory(ev != null ? ev.Story : name);
        }

        /// <summary>
        /// Binarizer 系プラグインも ExecuteLuaScript に割り込み、前の割り込みが本来の処理を止めても自分の処理を行う。
        /// こちらのシナリオのときは Binarizer 側を飛ばす (同名シナリオの二重実行と「找不到劇情腳本」のログを防ぐ)。
        /// Binarizer が入っていない、または版が違って見つからない場合は何もしない。
        /// </summary>
        internal static void PatchBinarizerStoryRedirect(Harmony harmony)
        {
            var type = AccessTools.TypeByName("Mortal.HookCustomStory");
            var method = type != null ? AccessTools.Method(type, "LuaScriptRedirect") : null;
            if (method == null)
                return;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Patches), nameof(SkipBinarizerForOurStory)));
            Plugin.Log.LogInfo("Binarizer story redirect found; it is skipped for scenario stories");
        }

        // Binarizer の LuaScriptRedirect(ref LuaManager __instance) は静的メソッドなので、引数は位置 (__0) で受け取る。
        private static bool SkipBinarizerForOurStory(ref LuaManager __0)
        {
            return !IsScenarioScript(__0 != null ? __0.ScriptName : null);
        }

        /// <summary>
        /// 会話シーンのキャラ定義の検索。追加キャラの ID なら自前の定義を返す (公式側の「未定義」エラーログも出さない)。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoryCharacterConfig), nameof(StoryCharacterConfig.Get))]
        private static bool StoryCharacterConfigGetPrefix(string id, ref StoryCharacterData __result)
        {
            var data = StoryCharacters.Get(id);
            if (data == null)
                return true;
            __result = data;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoryCharacterController), nameof(StoryCharacterController.LoadPortrait))]
        private static bool LoadPortraitPrefix(StoryCharacterController __instance, string key, ref IEnumerator __result)
        {
            if (!StoryCharacters.IsOurs(__instance.Data))
                return true;
            StoryCharacters.LoadPortrait(__instance, key);
            __result = StoryCharacters.Empty();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoryCharacterController), nameof(StoryCharacterController.LoadPortraitSync))]
        private static bool LoadPortraitSyncPrefix(StoryCharacterController __instance, string key)
        {
            if (!StoryCharacters.IsOurs(__instance.Data))
                return true;
            StoryCharacters.LoadPortrait(__instance, key);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StoryCharacterController), nameof(StoryCharacterController.LoadPortraitAssetByAddressable))]
        private static bool LoadPortraitAssetByAddressablePrefix(StoryCharacterController __instance, StoryCharaterImageItem[] list, ref IEnumerator __result)
        {
            if (!StoryCharacters.IsOurs(__instance.Data))
                return true;
            foreach (var item in list)
                StoryCharacters.LoadPortrait(__instance, item.Mapping.Value);
            __result = StoryCharacters.Empty();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fungus.PortraitController), nameof(Fungus.PortraitController.Show), new[] { typeof(Fungus.PortraitOptions) })]
        private static void PortraitShowPrefix(Fungus.PortraitOptions options)
        {
            StoryCharacters.BeforeShow(options);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fungus.PortraitController), "SetupPortrait")]
        private static void SetupPortraitPostfix(Fungus.PortraitOptions options)
        {
            StoryCharacters.AfterSetupPortrait(options);
        }

        /// <summary>
        /// 社交パネルの Awake がボタン一覧を集める前に、追加キャラのボタンを複製して差し込む。
        /// 並びは Content の GridLayoutGroup が自動で整える。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SocialStatPanel), "Awake")]
        private static void SocialStatPanelAwakePrefix(SocialStatPanel __instance)
        {
            if (!CharacterRegistry.TryRegister())
            {
                Plugin.Log.LogWarning("social panel: characters not registered yet, skip");
                return;
            }

            var buttons = __instance.GetComponentsInChildren<SocialCharacterButton>(true);
            // 複製元は公式の最後のボタン。追加キャラ同士で見た目の枠がそろう。
            var template = buttons.LastOrDefault(b => !CharacterRegistry.IsAdded(b.Data != null ? b.Data.Type.GetStringValue() : null));
            if (template == null)
            {
                Plugin.Log.LogWarning("social panel: no template button found");
                return;
            }

            int added = 0;
            foreach (var pair in CharacterRegistry.Stats)
            {
                var def = CharacterRegistry.GetDef(pair.Key);
                if (def == null || !def.SocialPanel || buttons.Any(b => b.Data == pair.Value))
                    continue;
                var clone = Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = "Character_" + pair.Key;
                clone.SetActive(true);
                CharacterRegistry.SetField(clone.GetComponent<SocialCharacterButton>(), "_data", pair.Value);
                added++;
            }
            if (added > 0)
                Plugin.Log.LogInfo($"social panel: added {added} button(s) after {template.name} (total={buttons.Length + added})");
        }
    }
}
