// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.IO;
using System.Security;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace LoMScenarioAddon.Logging
{
    internal static class LogSettings
    {
        internal const string ConfigName = "lom.scenarioaddon.logging.cfg";
        internal const int PlayFlushSeconds = 30;
        internal const int DebugFlushSeconds = 10;

        internal static BufferedFileLog Open(string component, ManualLogSource fallback)
        {
            string directory = Path.Combine(Paths.BepInExRootPath, "LogOutput", "LoMScenarioAddon");
            try
            {
                var config = new ConfigFile(Path.Combine(Paths.ConfigPath, ConfigName), false);
                return Open(config, directory, component, fallback);
            }
            catch (Exception ex) when (IsConfigurationError(ex))
            {
                return OpenDefault(directory, component, fallback, ex);
            }
        }

        // パッチャーと本体で設定を共有し、出力ファイルは分ける。Unity に依存しない。
        internal static BufferedFileLog Open(ConfigFile config, string directory, string component, ManualLogSource fallback)
        {
            try
            {
                var mode = config.Bind("Logging", "Mode", LogMode.Play,
                    "Play = Info/Warning/Error、Debug = 詳細も記録。変更はゲームの再起動後に反映。");
                var playSeconds = config.Bind("Logging", "PlayFlushSeconds", PlayFlushSeconds,
                    new ConfigDescription("プレイ用のまとめ保存間隔（秒）。新しいログが無ければ書き込まない。",
                        new AcceptableValueRange<int>(1, 300)));
                var debugSeconds = config.Bind("Logging", "DebugFlushSeconds", DebugFlushSeconds,
                    new ConfigDescription("デバッグ用のまとめ保存間隔（秒）。エラーと正常終了時は間隔を待たずに保存。",
                        new AcceptableValueRange<int>(1, 300)));
                int seconds = mode.Value == LogMode.Debug ? debugSeconds.Value : playSeconds.Value;
                return Create(directory, component, mode.Value, seconds, fallback);
            }
            catch (Exception ex) when (IsConfigurationError(ex))
            {
                return OpenDefault(directory, component, fallback, ex);
            }
        }

        private static BufferedFileLog OpenDefault(string directory, string component, ManualLogSource fallback, Exception error)
        {
            var log = Create(directory, component, LogMode.Play, PlayFlushSeconds, fallback);
            log.LogError($"Logging configuration unavailable: {error.Message}. Using Play / {PlayFlushSeconds}s until restart.");
            return log;
        }

        private static bool IsConfigurationError(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException;

        private static BufferedFileLog Create(string directory, string component, LogMode mode, int seconds, ManualLogSource fallback)
        {
            var log = new BufferedFileLog(Path.Combine(directory, component + ".log"), mode,
                TimeSpan.FromSeconds(seconds), (level, message) =>
                {
                    switch (level)
                    {
                        case LogSeverity.Debug: fallback.LogDebug(message); break;
                        case LogSeverity.Info: fallback.LogInfo(message); break;
                        case LogSeverity.Warning: fallback.LogWarning(message); break;
                        case LogSeverity.Error: fallback.LogError(message); break;
                    }
                });
            log.LogInfo($"logging: {mode}, flush interval={seconds}s, component={component}");
            return log;
        }
    }
}
