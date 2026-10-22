// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LoMScenarioAddon;
using LoMScenarioAddon.Shared;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;
using Patches = LoMScenarioAddon.Patches;

internal static class Program
{
    private static int _checks;
    private static int _hoverCalls;

    private static void Main(string[] args)
    {
        Check(AssistantCompatibility.CanUseHotkey(KeyCode.Home), "unconfigured optional integration allows addon hotkeys");
        var ours = new Harmony("lom.scenarioaddon");
        var assistant = new Harmony("LOMAssistant");
        assistant.Patch(AccessTools.Method(typeof(PositionController), nameof(PositionController.OnPositionClick)),
            prefix: new HarmonyMethod(typeof(Program), nameof(HoverPrefix)));
        ours.PatchAll(typeof(Visits));
        try
        {
            VisitsChecks();
            var cache = new LOMAssistant.PositionEventCache();
            var oldList = cache.GetPositionEvents("backmountain");
            var otherList = cache.GetPositionEvents("kitchen");
            AssistantCompatibility.Patch(ours);
            var overlap = assistant.GetPatchedMethods().Intersect(ours.GetPatchedMethods()).ToList();
            Check(overlap.Count == 0, "assistant click hook and addon activity hook do not overlap");
            EventChecks(cache, oldList, otherList);
            PreviewChecks();
            InputChecks();
        }
        finally { ours.UnpatchSelf(); assistant.UnpatchSelf(); }
        if (args.Length == 2) InstalledApiChecks.Run(args[0], args[1], Check);
        Console.WriteLine("Assistant compatibility checks passed: " + _checks);
    }

    private static void HoverPrefix() => _hoverCalls++;

    private static void VisitsChecks()
    {
        var visit = new VisitDef { Id = "test", Position = "backmountain", Story = "visit", Once = true, OnceFlag = "once" };
        Plugin.Manifest.Visits.Add(visit);
        Plugin.Content.Stories.Add("visit");
        var controller = new PositionController();
        controller.OnPositionClick(false);
        Check(controller.Script == "official" && PositionEvents.GetFlagState("once") == 0,
            "mission or blocked click keeps script and does not consume a visit");
        Check(_hoverCalls == 1, "assistant hover cleanup still runs");
        var pending = controller.BeginActivity();
        Check(PositionEvents.GetFlagState("once") == 0 && controller.Script == "official",
            "obtaining an activity iterator does not choose a visit");
        Check(pending.MoveNext() && ReferenceEquals(pending.Current, controller.Wait), "first original yield is preserved");
        Check(PlayerStatManagerData.Instance.Script == "visit" && controller.Script == "visit",
            "visit replaces the ordinary activity before its first yielded work");
        Check(Visits.LastDefaultScript == "official" && PositionEvents.GetFlagState("once") == 1,
            "default return target and once flag are recorded");
        Check(pending.MoveNext() && pending.Current == null, "later original yield is preserved");
        Check(!pending.MoveNext() && controller.Disposed, "activity completion disposes the original iterator");
        controller = new PositionController();
        pending = controller.BeginActivity();
        pending.MoveNext();
        Check(controller.Script == "official", "already consumed visit delegates to ordinary activity");
        ((IDisposable)pending).Dispose();
        Check(controller.Disposed, "activity cancellation disposes the original iterator");
        PositionEvents.Flags.Clear();
        Plugin.Content.Stories.Clear();
        controller = new PositionController();
        pending = controller.BeginActivity();
        pending.MoveNext();
        Check(controller.Script == "official" && PositionEvents.GetFlagState("once") == 0,
            "missing scenario delegates without consuming the visit");
        ((IDisposable)pending).Dispose();
        Plugin.Content.Stories.Add("visit");
        controller = new PositionController { Failure = new InvalidOperationException("original") };
        pending = controller.BeginActivity();
        try { pending.MoveNext(); throw new Exception("error swallowed"); }
        catch (InvalidOperationException ex) { Check(ReferenceEquals(ex, controller.Failure), "original activity exception is preserved"); }
        Check(controller.Disposed && PositionEvents.GetFlagState("once") == 0, "failed activity does not consume a visit");
        Plugin.Manifest.Visits.Clear();
        visit = new VisitDef { Id = "after-cost", Position = "backmountain", Story = "visit", When = new Dictionary<string, object>() };
        Plugin.Manifest.Visits.Add(visit);
        controller = new PositionController { UseActionUpdate = true };
        ConditionResultItem.Evaluate = () => controller.Actions == 1;
        pending = controller.BeginActivity();
        Check(pending.MoveNext() && controller.Script == "official" && controller.Actions == 2,
            "yielding the nested action update does not evaluate the visit too early");
        var action = (IEnumerator)pending.Current;
        Check(action.MoveNext() && action.Current == null && controller.Actions == 1 && controller.Script == "visit",
            "visit conditions see the consumed action and retain the original nested yield");
        ((IDisposable)pending).Dispose();
        Check(controller.ActionDisposed && controller.Disposed, "cancellation disposes both nested action and outer activity");
        ConditionResultItem.Evaluate = () => true;
        Plugin.Manifest.Visits.Clear();
        controller = new PositionController();
        pending = controller.BeginActivity();
        Check(pending.GetType().DeclaringType == typeof(PositionController), "no visits leaves the original iterator untouched");
    }

    private static void EventChecks(LOMAssistant.PositionEventCache cache, List<PositionEventData> oldList, List<PositionEventData> otherList)
    {
        Plugin.Manifest.Events.Add(new EventDef { Position = "backmountain" });
        var list = cache.GetPositionEvents("backmountain");
        Check(list.Count == 2 && list.Contains(PositionEvents.Custom), "cached assistant list receives the added event before selection");
        Check(!ReferenceEquals(list, oldList), "only a stale list is rebuilt");
        Check(ReferenceEquals(otherList, cache.GetPositionEvents("kitchen")), "other position cache is preserved");
        Check(ReferenceEquals(list, cache.GetPositionEvents("backmountain")), "complete list is reused without duplicates");
        var fresh = new LOMAssistant.PositionEventCache().GetPositionEvents("backmountain");
        Check(fresh.Contains(PositionEvents.Custom), "first uncached read also sees the added event");
        CharacterRegistry.Failure = true;
        Check(ReferenceEquals(list, cache.GetPositionEvents("backmountain")), "registration failure does not stop assistant list access");
        CharacterRegistry.Failure = false;
        Plugin.Manifest.Events.Clear();
        Check(ReferenceEquals(list, cache.GetPositionEvents("backmountain")), "no custom events leaves assistant cache intact");
    }

    private static void PreviewChecks()
    {
        Check(LOMAssistant.DataHelper.LoadLuaScript("visit") == "custom Lua" && LOMAssistant.DataHelper.OriginalCalls == 0,
            "custom preview reads the addon script without running the game loader");
        Check(LOMAssistant.DataHelper.LoadLuaScript("official") == "game Lua" && LOMAssistant.DataHelper.OriginalCalls == 1,
            "official preview is delegated to assistant");
        Patches.Failure = true;
        Check(LOMAssistant.DataHelper.LoadLuaScript("visit") == "game Lua", "preview lookup failure preserves the original loader");
        Patches.Failure = false;
    }

    private static void InputChecks()
    {
        Check(AssistantCompatibility.CanUseHotkey(KeyCode.Home), "non-conflicting key works with assistant installed");
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.F8), "assistant binding wins even before its window opens");
        LOMAssistant.Config.ConfigManager.PanelKey.Value = KeyCode.Home;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home) && AssistantCompatibility.CanUseHotkey(KeyCode.F8),
            "changed assistant key bindings are read without changing settings");
        LOMAssistant.Config.ConfigManager.PanelKey.Value = KeyCode.F8;
        LOMAssistant.InputGuard.BlockKeyboard = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "typing blocks addon debug keys");
        LOMAssistant.InputGuard.BlockKeyboard = false;
        LOMAssistant.InputGuard.CapturingKey = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "key assignment blocks addon debug keys");
        LOMAssistant.InputGuard.CapturingKey = false;
        LOMAssistant.InputGuard.ModWindowOpen = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "assistant window blocks addon debug keys");
        LOMAssistant.InputGuard.ModWindowOpen = false;
        LOMAssistant.InputGuard.BlockPointer = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "assistant pointer ownership blocks addon debug keys");
        LOMAssistant.InputGuard.BlockPointer = false;
        LOMAssistant.Features.Extras.SaveBrowser.Typing = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "save-note editor blocks addon debug keys");
        LOMAssistant.Features.Extras.SaveBrowser.Typing = false;
        LOMAssistant.InputGuard.Failure = true;
        Check(!AssistantCompatibility.CanUseHotkey(KeyCode.Home), "input API failure prevents unintended addon actions");
        LOMAssistant.InputGuard.Failure = false;
        Check(AssistantCompatibility.CanUseHotkey(KeyCode.Home), "addon hotkeys resume when input ownership ends");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }
}
