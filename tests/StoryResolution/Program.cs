// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.IO;
using System.Threading;
using LoMScenarioAddon;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lom-story-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckFiles(directory);
            CheckScopes();
            Console.WriteLine($"Story resolution regression checks passed: {_checks}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckFiles(string directory)
    {
        var owner = new object();
        int probes = 0;
        bool Exists(string name)
        {
            probes++;
            return File.Exists(Path.Combine(directory, name + ".lua"));
        }
        bool Resolve(string name) => StoryResolutionScope.IsScenario(owner, name, Exists);

        using (var scope = new StoryResolutionScope(owner))
        {
            scope.Record("official", Exists("official"));
            Check(!Resolve("official") && probes == 1, "official script checked once");
        }

        string path = Path.Combine(directory, "custom.lua");
        File.WriteAllText(path, "first");
        probes = 0;
        using (var scope = new StoryResolutionScope(owner))
        {
            scope.Record("custom", Exists("custom"));
            Check(File.ReadAllText(path) == "first", "new file available on next load");
            Check(Resolve("custom") && probes == 1, "custom script checked once");
            // 同じ呼び出しでは、実行側が既に下した判定を互換処理にも使う。
            File.Delete(path);
            Check(Resolve("custom") && probes == 1, "decision shared only within current call");
        }

        probes = 0;
        using (var scope = new StoryResolutionScope(owner))
        {
            scope.Record("custom", Exists("custom"));
            Check(!Resolve("custom") && probes == 1, "deleted file rechecked on next load");
        }
        File.WriteAllText(path, "edited");
        probes = 0;
        using (var scope = new StoryResolutionScope(owner))
        {
            scope.Record("custom", Exists("custom"));
            Check(Resolve("custom") && probes == 1 && File.ReadAllText(path) == "edited",
                "restored and edited file available on next load");
        }
        File.Delete(path);
        probes = 0;
        Check(!Resolve("custom") && probes == 1, "outside execution uses existing fallback");
    }

    private static void CheckScopes()
    {
        var owner = new object();
        var otherOwner = new object();
        int fallbacks = 0;
        bool Fallback(string name) { fallbacks++; return false; }
        bool Resolve(object instance, string name) => StoryResolutionScope.IsScenario(instance, name, Fallback);

        using (var outer = new StoryResolutionScope(owner))
        {
            // 訪問から本編へ戻る別名など、実行側が判定しない経路。
            Check(!Resolve(owner, "default_activity") && fallbacks == 1, "unrecorded call uses fallback");
            outer.Record("custom", true);
            Check(!Resolve(otherOwner, "custom") && fallbacks == 2, "different manager uses fallback");
            Check(!Resolve(owner, "changed") && fallbacks == 3, "changed script name uses fallback");
            Check(!Resolve(owner, "CUSTOM") && fallbacks == 4, "script name comparison is ordinal");

            using (var inner = new StoryResolutionScope(owner))
            {
                Check(!Resolve(owner, "custom") && fallbacks == 5, "nested call cannot reuse parent decision");
                inner.Record("custom", false);
                Check(!Resolve(owner, "custom") && fallbacks == 5, "nested negative decision shared");
            }
            Check(Resolve(owner, "custom") && fallbacks == 5, "parent decision restored after nested call");

            var failure = new InvalidOperationException("load failure");
            try
            {
                using (var inner = new StoryResolutionScope(otherOwner))
                {
                    inner.Record("custom", false);
                    throw failure;
                }
            }
            catch (InvalidOperationException ex)
            {
                Check(ReferenceEquals(ex, failure), "load exception preserved");
            }
            Check(Resolve(owner, "custom") && fallbacks == 5, "parent decision restored after exception");

            bool threadFallback = false;
            var thread = new Thread(() =>
                StoryResolutionScope.IsScenario(owner, "custom", _ => { threadFallback = true; return false; }));
            thread.Start();
            thread.Join();
            Check(threadFallback, "another thread cannot reuse current decision");

            var completed = new StoryResolutionScope(owner);
            completed.Record("custom", false);
            completed.Dispose();
            completed.Dispose();
            Check(Resolve(owner, "custom") && fallbacks == 5, "repeated cleanup keeps parent decision");
        }
        Check(!Resolve(owner, "custom") && fallbacks == 6, "completed call leaves no decision behind");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
}
