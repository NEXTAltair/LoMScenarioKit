// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace LoMScenarioTool
{
    /// <summary>
    /// LoMScenarioAddon のシナリオを作る人向けの道具。
    ///   LoMScenarioTool check [scenario フォルダ] [--info]      … 検査
    ///   LoMScenarioTool pack  [scenario フォルダ] [--out x.zip] [--bepinex BepInEx フォルダ] [--force] … 検査して配布用 zip を作る
    /// フォルダを exe にドラッグ＆ドロップすると check。引数なしなら、近くの scenario フォルダを探して check。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            bool pause = args.Length <= 1 && !Console.IsInputRedirected && !args.Contains("--no-pause");
            int code;
            try
            {
                code = Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("予期しないエラー: " + ex);
                code = 3;
            }
            if (pause)
            {
                Console.WriteLine();
                Console.WriteLine("Enter キーで閉じる");
                Console.ReadLine();
            }
            return code;
        }

        private static int Run(string[] args)
        {
            string command = "check";
            var rest = args.Where(a => a != "--no-pause").ToList();
            if (rest.Count > 0 && (rest[0] == "check" || rest[0] == "pack"))
            {
                command = rest[0];
                rest.RemoveAt(0);
            }
            else if (rest.Count > 0 && (rest[0] == "-h" || rest[0] == "--help" || rest[0] == "help"))
            {
                Usage();
                return 0;
            }
            bool info = Take(rest, "--info");
            bool force = Take(rest, "--force");
            string output = TakeValue(rest, "--out");
            string bepInEx = TakeValue(rest, "--bepinex");
            if (rest.Any(a => a.StartsWith("--")))
            {
                Console.WriteLine("知らないオプション: " + string.Join(" ", rest.Where(a => a.StartsWith("--"))));
                Usage();
                return 2;
            }

            string dir = rest.Count > 0 ? rest[0] : FindScenario();
            if (dir == null)
            {
                Console.WriteLine("scenario フォルダが見つからない。フォルダを指定するか、exe にドラッグ＆ドロップする。");
                Usage();
                return 2;
            }
            dir = Path.GetFullPath(dir);
            if (File.Exists(dir) && Path.GetFileName(dir) == "scenario.json")
                dir = Path.GetDirectoryName(dir);
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("フォルダが無い: " + dir);
                return 2;
            }

            Console.WriteLine($"検査: {dir}");
            Console.WriteLine();
            var report = new Report();
            var validator = new Validator(dir, report);
            validator.Run();
            var m = validator.Manifest;
            if (m != null)
                Console.WriteLine($"「{m.Title ?? m.Id ?? "(無題)"}」 キャラ {m.Characters.Count} / イベント {m.Events.Count} / 訪問 {m.Visits.Count} / エンディング {m.Endings.Count} / 決闘 {m.Duels.Count} / 天賦 {m.Talents.Count} / 秘笈 {m.Books.Count} / 効果 {m.Effects.Count}" +
                                  (validator.AudioBytes > 0 ? $" / 音 {validator.AudioBytes / 1024.0 / 1024:F1}MB" : ""));
            Console.WriteLine();

            if (command == "check")
            {
                report.Print(info);
                return report.Count(Level.Error) > 0 ? 1 : 0;
            }

            if (report.Count(Level.Error) > 0 && !force)
            {
                report.Print(info);
                Console.WriteLine("エラーがあるので zip は作らない (--force で無視して作る)");
                return 1;
            }
            bepInEx = bepInEx ?? Packer.FindBepInEx(dir);
            if (bepInEx == null)
            {
                report.Error("pack", "LoMScenarioAddon を入れた BepInEx フォルダが分からない (--bepinex で指定する)");
                report.Print(info);
                return 1;
            }
            string name = Sanitize(m?.Id ?? "scenario") + (m?.Version != null ? "-" + Sanitize(m.Version) : "-" + DateTime.Now.ToString("yyyyMMdd"));
            output = Path.GetFullPath(output ?? name + ".zip");
            bool ok = Packer.Pack(dir, bepInEx, output, report);
            report.Print(info);
            return ok ? 0 : 1;
        }

        private static string FindScenario()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var root in new[] { Directory.GetCurrentDirectory(), exeDir, Path.Combine(exeDir, "..") })
            {
                foreach (var candidate in new[] { ".", "scenario", Path.Combine("BepInEx", "plugins", "LoMScenarioAddon", "scenario") })
                {
                    string dir = Path.GetFullPath(Path.Combine(root, candidate));
                    if (File.Exists(Path.Combine(dir, "scenario.json")))
                        return dir;
                }
            }
            return null;
        }

        private static bool Take(System.Collections.Generic.List<string> args, string flag)
        {
            return args.Remove(flag);
        }

        private static string TakeValue(System.Collections.Generic.List<string> args, string flag)
        {
            int i = args.IndexOf(flag);
            if (i < 0 || i + 1 >= args.Count)
                return null;
            string value = args[i + 1];
            args.RemoveRange(i, 2);
            return value;
        }

        private static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        }

        private static void Usage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  LoMScenarioTool check [scenario フォルダ] [--info]");
            Console.WriteLine("      scenario フォルダを検査する (--info で念のための情報も出す)");
            Console.WriteLine("  LoMScenarioTool pack [scenario フォルダ] [--out 名前.zip] [--bepinex BepInEx フォルダ] [--force]");
            Console.WriteLine("      検査して、ゲームのフォルダに上書きで展開できる配布用 zip を作る");
            Console.WriteLine("  フォルダを exe にドラッグ＆ドロップすると check。");
        }
    }
}
