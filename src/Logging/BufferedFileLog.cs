// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;

namespace LoMScenarioAddon.Logging
{
    internal enum LogMode { Play, Debug }
    internal enum LogSeverity { Debug, Info, Warning, Error }

    /// <summary>自作アドオン専用。通常はメモリにため、タイマー・エラー・終了時に保存する。</summary>
    internal sealed class BufferedFileLog : IDisposable
    {
        internal const int MaxBufferedCharacters = 64 * 1024;
        internal const int MaxMessageCharacters = 16 * 1024;
        internal const long MaxFileBytes = 5 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly Action<LogSeverity, string> _fallback;
        private readonly string _path;
        private readonly Func<string, Stream> _openFile;
        private Timer _timer;
        private StreamWriter _writer;
        private FileStream _lease;
        private long _fileBytes;
        private bool _failed;
        private bool _disposed;

        internal bool IsDebugEnabled { get; }
        internal TimeSpan FlushInterval { get; }

        internal BufferedFileLog(string path, LogMode mode, TimeSpan flushInterval,
            Action<LogSeverity, string> fallback, Func<string, Stream> openFile = null)
        {
            if (flushInterval < TimeSpan.FromMilliseconds(1) || flushInterval > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(flushInterval));
            _path = path;
            _fallback = fallback;
            _openFile = openFile ?? (p => new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read));
            IsDebugEnabled = mode == LogMode.Debug;
            FlushInterval = flushInterval;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 別インスタンスの実行中ファイルを世代移動しない。
                _lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                RotateFiles();
                OpenWriter();
                _timer = new Timer(_ => Flush(), null, flushInterval, flushInterval);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                Fail(ex);
            }
        }

        internal void LogDebug(string message) => Write(LogSeverity.Debug, message);
        internal void LogInfo(string message) => Write(LogSeverity.Info, message);
        internal void LogWarning(string message) => Write(LogSeverity.Warning, message);
        internal void LogError(string message) => Write(LogSeverity.Error, message);

        private void Write(LogSeverity severity, string message)
        {
            if (severity == LogSeverity.Debug && !IsDebugEnabled)
                return;
            message = message ?? string.Empty;
            if (message.Length > MaxMessageCharacters)
                message = message.Substring(0, MaxMessageCharacters) + " [truncated]";
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (_failed)
                {
                    Fallback(severity, message);
                    return;
                }
                _pending.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append(" [").Append(severity).Append("] ").AppendLine(message);
                // 上限でも保存し、設定間隔の間にメモリが増え続けないようにする。
                if (severity == LogSeverity.Error || _pending.Length >= MaxBufferedCharacters)
                    FlushLocked();
            }
        }

        internal void Flush()
        {
            lock (_gate)
            {
                if (!_disposed)
                    FlushLocked();
            }
        }

        private void FlushLocked()
        {
            if (_pending.Length == 0 || _failed)
                return;
            string batch = _pending.ToString();
            try
            {
                int bytes = Utf8.GetByteCount(batch);
                if (_fileBytes + bytes > MaxFileBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    RotateFiles();
                    OpenWriter();
                }
                _writer.Write(batch);
                _writer.Flush();
                _fileBytes += bytes;
                _pending.Clear();
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                Fail(ex);
                // 途中まで成功した場合は重複し得るが、未保存分を捨てない。
                Fallback(LogSeverity.Error, "Unconfirmed dedicated log batch:\n" + batch);
            }
        }

        private void OpenWriter()
        {
            _writer = new StreamWriter(_openFile(_path), Utf8);
            _fileBytes = 0;
        }

        private void RotateFiles()
        {
            if (!File.Exists(_path))
                return;
            if (File.Exists(_path + ".2"))
                File.Delete(_path + ".2");
            if (File.Exists(_path + ".1"))
                File.Move(_path + ".1", _path + ".2");
            File.Move(_path, _path + ".1");
        }

        private void Fail(Exception ex)
        {
            _failed = true;
            _pending.Clear();
            _timer?.Dispose();
            _timer = null;
            try { _writer?.Dispose(); }
            catch (Exception closeError) when (IsFileError(closeError)) { }
            _writer = null;
            try { _lease?.Dispose(); }
            catch (Exception closeError) when (IsFileError(closeError)) { }
            _lease = null;
            Fallback(LogSeverity.Error, $"Dedicated log '{_path}' is unavailable: {ex.Message}. " +
                "Using the BepInEx logger until restart.");
        }

        private void Fallback(LogSeverity severity, string message)
        {
            // ログ経路自体の障害でゲームやタイマーを止めない。
            try { _fallback?.Invoke(severity, message); }
            catch { }
        }

        private static bool IsFileError(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
            ex is ObjectDisposedException || ex is ArgumentException || ex is NotSupportedException;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                FlushLocked();
                try { _writer?.Dispose(); }
                catch (Exception ex) when (IsFileError(ex)) { Fail(ex); }
                _writer = null;
                try { _lease?.Dispose(); }
                catch (Exception ex) when (IsFileError(ex)) { Fail(ex); }
                _lease = null;
            }
        }
    }
}
