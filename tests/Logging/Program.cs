// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LoMScenarioAddon.Logging;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lom-logging-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static readonly List<string> UnexpectedFallbacks = new List<string>();

    private static void Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            Run("Play filters Debug and buffers ordinary messages", () =>
            {
                string path = LogPath("play");
                using var log = Open(path);
                log.LogDebug("hidden");
                log.LogInfo("visible");
                Check(new FileInfo(path).Length == 0, "ordinary message reached file before flush");
                log.Flush();
                string text = ReadText(path);
                Check(text.Contains("visible") && !text.Contains("hidden"), "wrong Play filter");
            });
            Run("Debug includes detailed messages", () =>
            {
                string path = LogPath("debug");
                using (var log = Open(path, LogMode.Debug)) log.LogDebug("detail");
                Check(ReadText(path).Contains("[Debug] detail"), "debug message lost");
            });
            Run("Error saves the preceding batch immediately", () =>
            {
                string path = LogPath("error");
                using var log = Open(path);
                log.LogInfo("before-error");
                log.LogError("error-now");
                string text = ReadText(path);
                Check(text.Contains("before-error") && text.Contains("error-now"), "error did not flush batch");
            });
            Run("Warning waits for the ordinary interval", () =>
            {
                string path = LogPath("warning");
                using var log = Open(path);
                log.LogWarning("warning-buffered");
                Check(new FileInfo(path).Length == 0, "warning forced disk output");
                log.Flush();
                Check(ReadText(path).Contains("warning-buffered"), "warning lost");
            });
            Run("Timer saves pending messages", () =>
            {
                string path = LogPath("timer");
                using var log = new BufferedFileLog(path, LogMode.Play, TimeSpan.FromMilliseconds(50), NoFallback);
                log.LogInfo("timed-message");
                Check(SpinWait.SpinUntil(() => ReadText(path).Contains("timed-message"), 5000), "timer never saved");
            });
            Run("Idle timer does not modify the file", () =>
            {
                string path = LogPath("idle");
                using var log = new BufferedFileLog(path, LogMode.Play, TimeSpan.FromMilliseconds(20), NoFallback);
                log.LogError("initial");
                var before = File.GetLastWriteTimeUtc(path);
                long length = new FileInfo(path).Length;
                Thread.Sleep(100);
                Check(File.GetLastWriteTimeUtc(path) == before && new FileInfo(path).Length == length, "empty timer wrote data");
            });
            Run("Dispose saves pending messages and is idempotent", () =>
            {
                string path = LogPath("dispose");
                var log = Open(path);
                log.LogInfo("at-shutdown");
                Parallel.For(0, 20, _ => log.Dispose());
                log.LogError("after-close");
                log.Flush();
                string text = ReadText(path);
                Check(text.Contains("at-shutdown") && !text.Contains("after-close"), "wrong shutdown behavior");
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            });
            Run("Concurrent logging and timer preserve every accepted message", () =>
            {
                string path = LogPath("threads");
                using (var log = new BufferedFileLog(path, LogMode.Play, TimeSpan.FromMilliseconds(2), NoFallback))
                    Parallel.For(0, 4000, i => log.LogInfo("entry-" + i));
                string[] messages = File.ReadAllLines(path).Select(s => s.Substring(s.IndexOf("entry-", StringComparison.Ordinal))).ToArray();
                Check(messages.Length == 4000 && messages.Distinct().Count() == 4000, "concurrent entries duplicated or lost");
            });
            Run("Startup keeps only the previous two generations", () =>
            {
                string path = LogPath("sessions");
                for (int i = 1; i <= 5; i++)
                    using (var log = Open(path)) log.LogInfo("session-" + i);
                Check(ReadText(path).Contains("session-5"), "wrong current generation");
                Check(ReadText(path + ".1").Contains("session-4"), "wrong first backup");
                Check(ReadText(path + ".2").Contains("session-3"), "wrong second backup");
                Check(!File.Exists(path + ".3"), "too many backups");
            });
            Run("Buffer limit saves a large burst before the timer", () =>
            {
                string path = LogPath("buffer-limit");
                using var log = Open(path);
                for (int i = 0; i < 100; i++) log.LogInfo(new string('x', 1024));
                Check(new FileInfo(path).Length > 0, "buffer grew beyond the flush threshold");
            });
            Run("Long messages are capped with a visible marker", () =>
            {
                string path = LogPath("long-message");
                using (var log = Open(path)) log.LogInfo(new string('x', 100000));
                string text = ReadText(path);
                Check(text.Contains("[truncated]") && text.Length < 17000, "message cap failed");
            });
            Run("Unicode byte size rotates long sessions within the file limit", () =>
            {
                string path = LogPath("size-limit");
                using (var log = Open(path))
                {
                    string payload = new string('語', 1024);
                    for (int i = 0; i < 7000; i++) log.LogInfo(i + " " + payload);
                    log.LogInfo("last-message");
                }
                foreach (string file in new[] { path, path + ".1", path + ".2" })
                    Check(File.Exists(file) && new FileInfo(file).Length <= BufferedFileLog.MaxFileBytes, "file size not bounded");
                Check(ReadText(path).Contains("last-message"), "newest message not retained");
            });
            Run("A second logger cannot rotate an active file", () =>
            {
                string path = LogPath("lease");
                var fallback = new List<string>();
                using var first = Open(path);
                first.LogError("first-instance");
                using (var second = new BufferedFileLog(path, LogMode.Play, TimeSpan.FromSeconds(30), (_, m) => fallback.Add(m)))
                    second.LogInfo("second-instance");
                Check(fallback.Count == 2 && fallback[1] == "second-instance", "second instance did not fall back");
                Check(!File.Exists(path + ".1") && ReadText(path).Contains("first-instance"), "active log was rotated");
            });
            Run("Open failure reports once and preserves later messages through fallback", () =>
            {
                string parent = Path.Combine(Root, "blocked");
                File.WriteAllText(parent, "not a directory");
                var events = new List<string>();
                using var log = new BufferedFileLog(Path.Combine(parent, "addon.log"), LogMode.Play,
                    TimeSpan.FromSeconds(30), (_, m) => events.Add(m));
                log.LogDebug("filtered");
                log.LogInfo("fallback-info");
                log.LogError("fallback-error");
                Check(events.Count == 3 && events[0].Contains("unavailable") && events[1] == "fallback-info" &&
                    events[2] == "fallback-error", "wrong failure routing");
            });
            Run("Write failure preserves the batch and subsequent messages", () =>
            {
                var events = new List<string>();
                using var log = new BufferedFileLog(LogPath("write-failure"), LogMode.Debug, TimeSpan.FromSeconds(30),
                    (_, m) => events.Add(m), _ => new FailingStream());
                log.LogInfo("pending-before-failure");
                log.Flush();
                log.LogDebug("after-failure");
                Check(events.Count == 3 && events[1].Contains("pending-before-failure") && events[2] == "after-failure",
                    "failed batch or subsequent entries lost");
            });
            Run("Fallback failure cannot escape into game code", () =>
            {
                using var log = new BufferedFileLog(Path.Combine(Root, "blocked", "fallback.log"), LogMode.Play,
                    TimeSpan.FromSeconds(30), (_, __) => throw new IOException("fallback also failed"));
                log.LogError("still-running");
            });
            Console.WriteLine($"{_passed} logging checks passed");
        }
        finally { Directory.Delete(Root, true); }
    }

    private static BufferedFileLog Open(string path, LogMode mode = LogMode.Play) =>
        new BufferedFileLog(path, mode, TimeSpan.FromSeconds(30), NoFallback);
    private static string LogPath(string name) => Path.Combine(Root, name, "Addon.log");
    private static void NoFallback(LogSeverity level, string message)
    {
        lock (UnexpectedFallbacks) UnexpectedFallbacks.Add(message);
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
    private static void Run(string name, Action test)
    {
        test();
        lock (UnexpectedFallbacks)
            Check(UnexpectedFallbacks.Count == 0, "Unexpected fallback: " + string.Join("\n", UnexpectedFallbacks));
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("simulated disk full");
#if NET8_0_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("simulated disk full");
#endif
    }
}
