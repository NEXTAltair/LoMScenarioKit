// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LoMScenarioAddon;

// Unity は起動せず、導入済みの HarmonyX で prefix の組み合わせと __state の寿命を確認する。
internal sealed class Target
{
    internal string Name;
    internal int Probes, ForeignCalls, OriginalCalls;
    internal bool ForeignSkipsOriginal;
    internal Action OnCustom;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Execute()
    {
        OriginalCalls++;
        if (Name == "throw-original")
            throw new InvalidOperationException("original");
    }
}

internal static class Hooks
{
    internal static bool Prefix(Target __instance, out StoryResolutionScope __state)
    {
        __state = new StoryResolutionScope(__instance);
        if (__instance.Name == "throw-prefix")
            throw new InvalidOperationException("prefix");
        __instance.Probes++;
        bool custom = __instance.Name == "custom";
        __state.Record(__instance.Name, custom);
        if (custom)
            __instance.OnCustom?.Invoke();
        return !custom;
    }

    internal static void Finalizer(StoryResolutionScope __state) => __state?.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool Foreign(ref Target __instance)
    {
        __instance.ForeignCalls++;
        if (__instance.Name == "throw-foreign")
            throw new InvalidOperationException("foreign");
        return !__instance.ForeignSkipsOriginal;
    }

    internal static bool Skip(ref Target __0)
    {
        var owner = __0;
        return !StoryResolutionScope.IsScenario(owner, owner.Name, _ => { owner.Probes++; return false; });
    }
}

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        var harmony = new Harmony("lom.story-resolution.tests");
        var execute = AccessTools.Method(typeof(Target), nameof(Target.Execute));
        harmony.Patch(execute,
            prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Prefix)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Finalizer)));
        var foreign = AccessTools.Method(typeof(Hooks), nameof(Hooks.Foreign));
        harmony.Patch(execute, prefix: new HarmonyMethod(foreign));
        harmony.Patch(foreign, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));

        var target = new Target { Name = "official" };
        target.Execute();
        Check(target.Probes == 1 && target.ForeignCalls == 1 && target.OriginalCalls == 1,
            "official runs with one probe");
        target.Name = "custom";
        target.Execute();
        Check(target.Probes == 2 && target.ForeignCalls == 1 && target.OriginalCalls == 1,
            "custom skips original and foreign with one probe");
        CheckClean(target, "finalizer cleans up when original is skipped");

        foreach (var name in new[] { "throw-prefix", "throw-foreign", "throw-original" })
        {
            target.Name = name;
            try
            {
                target.Execute();
                throw new Exception("exception swallowed");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message == name.Substring("throw-".Length), "exception preserved: " + name);
            }
            CheckClean(target, "finalizer cleans up " + name);
        }

        target = new Target { Name = "custom" };
        target.OnCustom = () =>
        {
            target.Name = "official";
            target.Execute();
            target.Name = "custom";
        };
        target.Execute();
        Check(target.Probes == 2 && target.ForeignCalls == 1 && target.OriginalCalls == 1,
            "nested execution restores outer decision");
        CheckClean(target, "nested execution leaves no decision behind");

        target = new Target { Name = "official", ForeignSkipsOriginal = true };
        target.Execute();
        Check(target.Probes == 1 && target.ForeignCalls == 1 && target.OriginalCalls == 0,
            "foreign prefix can replace the original as Binarizer does");
        CheckClean(target, "finalizer cleans up when foreign prefix skips original");
        Console.WriteLine($"HarmonyX integration checks passed: {_checks}");
    }

    private static void CheckClean(Target target, string name)
    {
        bool fallback = false;
        StoryResolutionScope.IsScenario(target, target.Name, _ => { fallback = true; return false; });
        Check(fallback, name);
    }

    private static void Check(bool ok, string name)
    {
        if (!ok)
            throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
}
