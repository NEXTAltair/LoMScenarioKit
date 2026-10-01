// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace LoMScenarioTool
{
    /// <summary>
    /// 配布用 zip を作る。中身はゲームのフォルダにそのまま上書きで展開できる形:
    ///   BepInEx/patchers/LoMScenarioAddon/LoMScenarioAddon.Patcher.dll
    ///   BepInEx/plugins/LoMScenarioAddon/LoMScenarioAddon.dll
    ///   BepInEx/plugins/LoMScenarioAddon/scenario/...
    /// DLL は scenario フォルダの入っている導入先 (BepInEx) から取る。BepInEx 本体や設定ファイルは入れない。
    /// </summary>
    internal static class Packer
    {
        private const string PluginDll = "LoMScenarioAddon.dll";
        private const string PatcherDll = "LoMScenarioAddon.Patcher.dll";

        // 作業用のファイルは入れない
        private static readonly Regex Excluded = new Regex(
            @"(^|[\\/])(\.[^\\/]*|Thumbs\.db|desktop\.ini|~[^\\/]*)$|\.(psd|clip|kra|xcf|sai2?|bak|tmp|orig|swp|blend)$",
            RegexOptions.IgnoreCase);

        public static bool IsJunk(string relativePath) =>
            Excluded.IsMatch(relativePath) || relativePath.Replace('\\', '/').Split('/').Any(part => part.StartsWith("."));

        /// <summary>scenario フォルダから上へたどって BepInEx フォルダを探す (…/BepInEx/plugins/LoMScenarioAddon/scenario)。</summary>
        public static string FindBepInEx(string scenarioDir)
        {
            var dir = new DirectoryInfo(scenarioDir);
            for (var d = dir.Parent; d != null; d = d.Parent)
                if (string.Equals(d.Name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                    return d.FullName;
            return null;
        }

        public static bool Pack(string scenarioDir, string bepInEx, string output, Report report)
        {
            string plugin = Path.Combine(Path.Combine(Path.Combine(bepInEx, "plugins"), "LoMScenarioAddon"), PluginDll);
            string patcher = Path.Combine(Path.Combine(Path.Combine(bepInEx, "patchers"), "LoMScenarioAddon"), PatcherDll);
            string pluginDir = Path.GetDirectoryName(plugin);
            string[] legalFiles = {
                "LICENSE", "LICENSE-GAME-EXCEPTION.txt", "THIRD-PARTY-NOTICES.md",
                "licenses/GPL-3.0.txt", "licenses/CC-BY-NC-SA-4.0.txt",
                "licenses/MoonSharp.txt", "licenses/Plugin-DD2.txt", "licenses/lom_modkit.txt",
                "Source/LoMScenarioKit-source.zip", "Source/MoonSharp-2.0.0-source.zip"
            };
            foreach (var file in new[] { plugin, patcher }.Concat(legalFiles.Select(p => Path.Combine(pluginDir, p))))
            {
                if (!File.Exists(file))
                {
                    report.Error("pack", $"{file} が無い (配布 ZIP の DLL・ライセンス・Source を導入し、--bepinex でその BepInEx フォルダを指定する)");
                    return false;
                }
            }

            string tmp = output + ".tmp";
            if (File.Exists(tmp))
                File.Delete(tmp);
            int files = 0;
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                Add(zip, patcher, "BepInEx/patchers/LoMScenarioAddon/" + PatcherDll);
                Add(zip, plugin, "BepInEx/plugins/LoMScenarioAddon/" + PluginDll);
                foreach (string rel in legalFiles)
                    Add(zip, Path.Combine(pluginDir, rel), "BepInEx/plugins/LoMScenarioAddon/" + rel);
                foreach (var path in Directory.GetFiles(scenarioDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string rel = path.Substring(scenarioDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    if (IsJunk(rel))
                    {
                        report.Info("pack", $"{rel} は作業用のファイルとみなして入れない");
                        continue;
                    }
                    Add(zip, path, "BepInEx/plugins/LoMScenarioAddon/scenario/" + rel);
                    files++;
                }
            }
            if (File.Exists(output))
                File.Delete(output);
            File.Move(tmp, output);
            Console.WriteLine($"作成: {output} ({new FileInfo(output).Length / 1024} KB、scenario のファイル {files} 個 + DLL 2 個 + ライセンス・ソース)");
            return true;
        }

        private static void Add(ZipArchive zip, string path, string entryName)
        {
            // 区切りは必ず / (Windows の \ だと他の解凍ソフトでフォルダにならない)
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(path);
            using (var src = File.OpenRead(path))
            using (var dst = entry.Open())
                src.CopyTo(dst);
        }
    }
}
