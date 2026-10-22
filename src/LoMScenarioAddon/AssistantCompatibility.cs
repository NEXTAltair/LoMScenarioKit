// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mortal.Story;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>LOMAssistantを必須参照にせず、候補一覧・Lua参照・確認用キーだけ連携する。</summary>
    internal static class AssistantCompatibility
    {
        private static FieldInfo _eventCache;
        private static readonly List<Func<bool>> InputGuards = new List<Func<bool>>();
        private static readonly List<Func<KeyCode>> AssistantKeys = new List<Func<KeyCode>>();
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static bool _present;
        private static bool _inputReady;

        internal static void Patch(Harmony harmony)
        {
            if (AccessTools.TypeByName("LOMAssistant.LOMAssistantPlugin") == null)
                return;
            _present = true;
            TrySetup("event list", () =>
            {
                var type = AccessTools.TypeByName("LOMAssistant.PositionEventCache");
                var method = type?.GetMethod("GetPositionEvents", new[] { typeof(string) });
                _eventCache = type?.GetField("positionEventDataCache", BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null || method.ReturnType != typeof(List<PositionEventData>) || _eventCache == null
                    || !typeof(IDictionary).IsAssignableFrom(_eventCache.FieldType))
                    throw new MissingMemberException("PositionEventCache API");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(AssistantCompatibility), nameof(BeforeEventList)));
            });
            TrySetup("Lua preview", () =>
            {
                var type = AccessTools.TypeByName("LOMAssistant.DataHelper");
                var method = type?.GetMethod("LoadLuaScript", new[] { typeof(string) });
                if (method == null || !method.IsStatic || method.ReturnType != typeof(string))
                    throw new MissingMemberException("DataHelper.LoadLuaScript");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(AssistantCompatibility), nameof(BeforeLuaPreview)));
            });
            TrySetup("hotkeys", () =>
            {
                InputGuards.Clear();
                AssistantKeys.Clear();
                var guard = AccessTools.TypeByName("LOMAssistant.InputGuard");
                foreach (var name in new[] { "BlockKeyboard", "BlockPointer", "CapturingKey", "ModWindowOpen" })
                    BindGuard(guard, name);
                BindGuard(AccessTools.TypeByName("LOMAssistant.Features.Extras.SaveBrowser"), "Typing");
                var config = AccessTools.TypeByName("LOMAssistant.Config.ConfigManager");
                if (config == null) throw new MissingMemberException("ConfigManager");
                foreach (var field in config.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var value = field.FieldType.GetProperty("Value");
                    if (value?.PropertyType != typeof(KeyCode)) continue;
                    AssistantKeys.Add(() => field.GetValue(null) is object element
                        ? (KeyCode)value.GetValue(element, null) : KeyCode.None);
                }
                if (AssistantKeys.Count == 0) throw new MissingMemberException("hotkey settings");
                _inputReady = true;
            });
            Plugin.Log.LogInfo("LOMAssistant compatibility initialized (optional event list, Lua preview, hotkey guards)");
        }

        private static void BindGuard(Type type, string name)
        {
            var getter = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (getter == null || getter.ReturnType != typeof(bool))
                throw new MissingMemberException(type?.FullName ?? "LOMAssistant", name);
            InputGuards.Add((Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter));
        }

        internal static bool CanUseHotkey(KeyCode key)
        {
            if (!_present) return true;
            // 連携先の入力状態を読めない版では、ゲーム進行を変える確認用キーだけ止める。
            if (!_inputReady) return false;
            try
            {
                if (InputGuards.Any(blocked => blocked())) return false;
                if (AssistantKeys.Any(read => read() == key))
                {
                    WarnOnce("hotkey " + key, "reserved by LOMAssistant; addon debug action skipped");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce("hotkeys", ex.Message);
                return false;
            }
        }

        private static void BeforeEventList(object __instance, string __0)
        {
            if (Plugin.Manifest == null || !Plugin.Manifest.Events.Any(e => e.Position == __0)) return;
            try
            {
                var manager = CheckPointManager.Instance;
                if (manager == null || !CharacterRegistry.TryRegister()) return;
                PositionEvents.EnsureInjected(manager, __0);
                var cache = _eventCache.GetValue(__instance) as IDictionary;
                // 既にコピー済みでも、追加候補が欠けた場所だけ再取得させる。他の場所や説明のキャッシュは保つ。
                if (cache != null && cache[__0] is IList list
                    && PositionEvents.RegisteredEvents(__0).Any(item => !list.Contains(item)))
                    cache.Remove(__0);
            }
            catch (Exception ex) { WarnOnce("event list", ex.Message); }
        }

        private static bool BeforeLuaPreview(string __0, ref string __result)
        {
            try
            {
                if (!Patches.TryResolveStory(__0, out _, out _, out var code)) return true;
                __result = code;
                return false;
            }
            catch (Exception ex)
            {
                WarnOnce("Lua preview", ex.Message);
                return true;
            }
        }

        private static void TrySetup(string feature, Action action)
        {
            try { action(); }
            catch (Exception ex) { WarnOnce(feature, ex.Message); }
        }

        private static void WarnOnce(string feature, string message)
        {
            if (Warned.Add(feature)) Plugin.Log.LogWarning($"LOMAssistant {feature}: {message}");
        }
    }
}
