// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using LoMScenarioAddon.Logging;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lom-bepinex-logging-" + Guid.NewGuid().ToString("N"));
    private static int _passed;

    private static void Main()
    {
        Directory.CreateDirectory(Root);
        var listener = new Recorder();
        Logger.Listeners.Add(listener);
        using var fallback = Logger.CreateLogSource("addon-test-fallback");
        try
        {
            Run("Default config selects Play and a 30 second interval", () =>
            {
                var config = Config("defaults");
                using var log = LogSettings.Open(config, DirectoryFor("defaults"), "Addon", fallback);
                Check(!log.IsDebugEnabled && log.FlushInterval == TimeSpan.FromSeconds(30), "wrong default mode");
                string text = ReadText(config.ConfigFilePath);
                Check(text.Contains("Mode = Play") && text.Contains("PlayFlushSeconds = 30") && text.Contains("DebugFlushSeconds = 10"),
                    "defaults not persisted");
            });
            Run("Both components read the same Debug setting", () =>
            {
                string path = Path.Combine(Root, "shared.cfg");
                File.WriteAllText(path, "[Logging]\nMode = Debug\nPlayFlushSeconds = 45\nDebugFlushSeconds = 12\n");
                using var patcher = LogSettings.Open(new ConfigFile(path, false), DirectoryFor("shared"), "Patcher", fallback);
                using var addon = LogSettings.Open(new ConfigFile(path, false), DirectoryFor("shared"), "Addon", fallback);
                Check(patcher.IsDebugEnabled && addon.IsDebugEnabled && patcher.FlushInterval == TimeSpan.FromSeconds(12) &&
                    addon.FlushInterval == TimeSpan.FromSeconds(12), "components disagree on settings");
                patcher.LogDebug("patcher-only");
                addon.LogDebug("addon-only");
                patcher.Flush(); addon.Flush();
                Check(ReadText(Path.Combine(DirectoryFor("shared"), "Patcher.log")).Contains("patcher-only"), "patcher output missing");
                Check(!ReadText(Path.Combine(DirectoryFor("shared"), "Addon.log")).Contains("patcher-only"), "component logs mixed");
            });
            Run("Dedicated messages never enter global BepInEx listeners", () =>
            {
                listener.Events.Clear();
                int sourceCount = Logger.Sources.Count;
                int listenerCount = Logger.Listeners.Count;
                using (var log = LogSettings.Open(Config("isolation"), DirectoryFor("isolation"), "Addon", fallback))
                {
                    log.LogInfo("private-info");
                    log.LogError("private-error");
                }
                Check(listener.Events.Count == 0, "dedicated messages leaked into global logging");
                Check(Logger.Sources.Count == sourceCount && Logger.Listeners.Count == listenerCount, "global logger collections changed");
                fallback.LogInfo("other-mod-still-works");
                Check(listener.Events.Count == 1 && listener.Events[0].Contains("other-mod-still-works"), "global logging was disabled");
            });
            Run("Default Debug interval is 10 seconds", () =>
            {
                string path = Path.Combine(Root, "debug-default.cfg");
                File.WriteAllText(path, "[Logging]\nMode = Debug\n");
                using var log = LogSettings.Open(new ConfigFile(path, false), DirectoryFor("debug-default"), "Addon", fallback);
                Check(log.IsDebugEnabled && log.FlushInterval == TimeSpan.FromSeconds(10), "wrong Debug default interval");
            });
            Run("Configured interval is clamped to 1 through 300 seconds", () =>
            {
                foreach (var value in new[] { (Input: "-1", Expected: 1), (Input: "900", Expected: 300) })
                {
                    string path = Path.Combine(Root, "range-" + value.Input + ".cfg");
                    File.WriteAllText(path, "[Logging]\nPlayFlushSeconds = " + value.Input + "\n");
                    using var log = LogSettings.Open(new ConfigFile(path, false), DirectoryFor("range-" + value.Input), "Addon", fallback);
                    Check(log.FlushInterval == TimeSpan.FromSeconds(value.Expected), "unsafe interval was accepted");
                }
            });
            Run("An invalid interval falls back to the mode default", () =>
            {
                string path = Path.Combine(Root, "nan.cfg");
                File.WriteAllText(path, "[Logging]\nPlayFlushSeconds = NaN\n");
                using var log = LogSettings.Open(new ConfigFile(path, false), DirectoryFor("nan"), "Addon", fallback);
                Check(log.FlushInterval == TimeSpan.FromSeconds(30), "NaN reached timer configuration");
            });
            Run("An unwritable config uses safe defaults without stopping the plugin", () =>
            {
                string parent = Path.Combine(Root, "blocked-config");
                File.WriteAllText(parent, "not a directory");
                var config = new ConfigFile(Path.Combine(parent, "settings.cfg"), false);
                using var log = LogSettings.Open(config, DirectoryFor("config-failure"), "Addon", fallback);
                Check(!log.IsDebugEnabled && log.FlushInterval == TimeSpan.FromSeconds(30), "config failure did not use defaults");
                Check(ReadText(Path.Combine(DirectoryFor("config-failure"), "Addon.log")).Contains("configuration unavailable"),
                    "config failure not reported");
            });
            Run("Only a file failure invokes the supplied BepInEx fallback", () =>
            {
                listener.Events.Clear();
                string blocked = Path.Combine(Root, "blocked-log");
                File.WriteAllText(blocked, "not a directory");
                using var log = LogSettings.Open(Config("fallback"), blocked, "Addon", fallback);
                log.LogError("error-after-file-failure");
                Check(listener.Events.Any(s => s.Contains("unavailable")) && listener.Events.Any(s => s.Contains("error-after-file-failure")),
                    "fallback did not preserve diagnostics");
            });
            Console.WriteLine($"{_passed} BepInEx logging checks passed");
        }
        finally
        {
            Logger.Sources.Remove(fallback);
            Logger.Listeners.Remove(listener);
            Directory.Delete(Root, true);
        }
    }

    private static ConfigFile Config(string name) => new ConfigFile(Path.Combine(Root, name + ".cfg"), false);
    private static string DirectoryFor(string name) => Path.Combine(Root, name);
    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }
    private static string ReadText(string path)
    {
        // 実行中ログの読取側も、既存の書込ハンドルを共有する必要がある（Windows）。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private sealed class Recorder : ILogListener
    {
        internal readonly List<string> Events = new List<string>();
        public LogLevel LogLevelFilter => LogLevel.All;
        public void LogEvent(object sender, LogEventArgs args) => Events.Add(args.ToString());
        public void Dispose() { }
    }
}
