// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.Linq;

namespace LoMScenarioTool
{
    internal enum Level { Error, Warning, Info }

    /// <summary>検査結果。エラーはゲームで確実に困るもの、警告はたぶん間違い、情報は念のための一覧。</summary>
    internal sealed class Report
    {
        private readonly List<(Level Level, string Where, string Message)> _items = new List<(Level, string, string)>();

        public void Error(string where, string message) => _items.Add((Level.Error, where, message));
        public void Warn(string where, string message) => _items.Add((Level.Warning, where, message));
        public void Info(string where, string message) => _items.Add((Level.Info, where, message));

        public int Count(Level level) => _items.Count(i => i.Level == level);

        public void Print(bool showInfo)
        {
            foreach (var level in new[] { Level.Error, Level.Warning, Level.Info })
            {
                if (level == Level.Info && !showInfo)
                    continue;
                foreach (var item in _items.Where(i => i.Level == level))
                {
                    var old = Console.ForegroundColor;
                    Console.ForegroundColor = level == Level.Error ? ConsoleColor.Red : level == Level.Warning ? ConsoleColor.Yellow : ConsoleColor.Gray;
                    Console.Write(level == Level.Error ? "[エラー] " : level == Level.Warning ? "[警告]   " : "[情報]   ");
                    Console.ForegroundColor = old;
                    Console.WriteLine(string.IsNullOrEmpty(item.Where) ? item.Message : $"{item.Where}: {item.Message}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"結果: エラー {Count(Level.Error)} / 警告 {Count(Level.Warning)} / 情報 {Count(Level.Info)}" +
                              (showInfo || Count(Level.Info) == 0 ? "" : " (情報は --info で表示)"));
        }
    }
}
