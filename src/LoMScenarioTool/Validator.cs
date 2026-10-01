// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LoMScenarioAddon.Audio;
using LoMScenarioAddon.Shared;
using MoonSharp.Interpreter;

namespace LoMScenarioTool
{
    /// <summary>
    /// scenario フォルダの検査。ゲームを起動せずに分かる誤り (ファイルの不足・名前の打ち間違い・Lua の構文・画像の大きさ・訳の抜け) を探す。
    /// 本編の名前 (本編のシナリオ・天賦・曲など) は手元に一覧が無いので確かめられない。
    /// このシナリオの ID と同じ接頭辞 (例: lsa_) の名前だけ「打ち間違いの疑い」として警告し、それ以外は本編の名前とみなして情報に出す。
    /// </summary>
    internal sealed class Validator
    {
        private readonly string _dir;
        private readonly Report _r;
        private ScenarioManifest _m;
        private readonly Dictionary<string, Dictionary<string, string>> _texts = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, List<(string Story, int Line)>> _referencedStories = new Dictionary<string, List<(string, int)>>();
        private readonly HashSet<string> _usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _prefixes;

        public ScenarioManifest Manifest => _m;
        public long AudioBytes { get; private set; }

        public Validator(string dir, Report report)
        {
            _dir = dir;
            _r = report;
        }

        public void Run()
        {
            if (!LoadManifest())
                return;
            LoadTexts();
            _prefixes = OwnPrefixes();
            CheckCharacters();
            CheckManifestStories();
            CheckStories();
            CheckTalentsEffectsBooks();
            CheckDuels();
            CheckLibrary();
            CheckImages();
            CheckAudio();
            CheckTranslations();
            CheckUnused();
        }

        // ---- scenario.json ----

        private bool LoadManifest()
        {
            string path = Path.Combine(_dir, ScenarioManifest.FileName);
            if (!File.Exists(path))
            {
                _r.Error(ScenarioManifest.FileName, "見つからない");
                return false;
            }
            try
            {
                var warnings = new List<string>();
                _m = ScenarioManifest.Load(path, warnings);
                foreach (var w in warnings)
                    _r.Warn(ScenarioManifest.FileName, w);
                _usedFiles.Add(path);
                return true;
            }
            catch (Exception ex)
            {
                _r.Error(ScenarioManifest.FileName, "読めない: " + ex.Message);
                return false;
            }
        }

        // このシナリオの ID の接頭辞 (最初の _ まで)。これで始まる名前が見つからなければ打ち間違いの疑い
        private HashSet<string> OwnPrefixes()
        {
            var ids = _m.Characters.Select(c => c.Id)
                .Concat(_m.Flags.Select(f => f.Id)).Concat(_m.Talents.Select(t => t.Id)).Concat(_m.Books.Select(b => b.Id))
                .Concat(_m.Effects.Select(e => e.Key)).Concat(_m.Duels.Select(d => d.Id)).Concat(_m.Endings.Select(e => e.Id))
                .Concat(_m.Deaths).Concat(_m.Cgs).Concat(StoryNames());
            return new HashSet<string>(ids.Where(id => id != null && id.IndexOf('_') > 0).Select(id => id.Substring(0, id.IndexOf('_') + 1)));
        }

        private bool LooksOwn(string name) => _prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

        private void Unknown(string where, string kind, string name)
        {
            if (LooksOwn(name))
                _r.Warn(where, $"{kind} '{name}' はこのシナリオに無い (打ち間違い?)");
            else
                _r.Info(where, $"{kind} '{name}' は本編の名前として扱う (確かめられない)");
        }

        // ---- テキスト ----

        private void LoadTexts()
        {
            foreach (var path in Directory.GetFiles(_dir, "text.*.csv").OrderBy(p => p, StringComparer.Ordinal))
            {
                _usedFiles.Add(path);
                string file = Path.GetFileName(path);
                string lang = Path.GetFileNameWithoutExtension(path).Substring("text.".Length);
                var table = new Dictionary<string, string>();
                List<List<string>> rows;
                try
                {
                    rows = Csv.Parse(File.ReadAllText(path, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    _r.Error(file, "読めない: " + ex.Message);
                    continue;
                }
                int line = 0;
                foreach (var row in rows)
                {
                    line++;
                    if (row.Count == 0 || row.All(string.IsNullOrEmpty) || row[0].StartsWith("#"))
                        continue;
                    string key = row[0];
                    if (row.Count < 2)
                    {
                        _r.Warn($"{file} {line}件目", $"'{key}' に本文が無い (キー,本文 の形にする)");
                        continue;
                    }
                    if (row.Count > 2)
                        _r.Warn($"{file} {line}件目", $"'{key}' の本文にカンマがある。本文を \"…\" で囲まないと最初のカンマまでしか使われない");
                    if (table.ContainsKey(key))
                        _r.Warn($"{file} {line}件目", $"'{key}' が重複している (後のものが使われる)");
                    table[key] = row[1];
                }
                _texts[lang] = table;
            }
            if (!_texts.ContainsKey(_m.DefaultLanguage))
                _r.Warn("", $"既定の言語のテキスト text.{_m.DefaultLanguage}.csv が無い (scenario.json の default_language)");
        }

        private bool HasText(string key) => _texts.Values.Any(t => t.ContainsKey(key));

        private void RequireText(string where, string key, bool warn = true)
        {
            if (_texts.TryGetValue(_m.DefaultLanguage, out var table) && table.ContainsKey(key))
                return;
            string other = _texts.Where(t => t.Value.ContainsKey(key)).Select(t => t.Key).FirstOrDefault();
            string message = other != null ? $"テキスト '{key}' が既定の言語 ({_m.DefaultLanguage}) に無い ({other} にはある)" : $"テキスト '{key}' が無い";
            if (warn)
                _r.Warn(where, message);
            else
                _r.Info(where, message);
        }

        private void CheckTranslations()
        {
            if (!_texts.TryGetValue(_m.DefaultLanguage, out var main))
                return;
            foreach (var pair in _texts.Where(t => t.Key != _m.DefaultLanguage))
            {
                int missing = main.Keys.Count(k => !pair.Value.ContainsKey(k));
                if (missing > 0)
                    _r.Info($"text.{pair.Key}.csv", $"{missing} 件は訳が無く、既定の言語 ({_m.DefaultLanguage}) で出る");
                foreach (var extra in pair.Value.Keys.Where(k => !main.ContainsKey(k)))
                    _r.Info($"text.{pair.Key}.csv", $"'{extra}' は既定の言語 ({_m.DefaultLanguage}) に無い");
            }
        }

        // ---- 画像 ----

        private string CharDir(string id) => Path.Combine(Path.Combine(_dir, "characters"), id);

        private bool IsOwnCharacter(string id) => _m.Characters.Any(c => c.Id == id);

        private void CheckCharacters()
        {
            foreach (var c in _m.Characters)
            {
                string where = "characters/" + c.Id;
                if (!Directory.Exists(CharDir(c.Id)))
                {
                    _r.Warn(where, "フォルダが無い (顔アイコン・立ち絵が出ない)");
                    continue;
                }
                CheckPng(where + "/icon.png", 101, 100, true);
                CheckPng(where + "/head.png", 66, 172, true);
                CheckPng(where + "/avatar.png", 0, 0, true);
                foreach (var png in Directory.GetFiles(CharDir(c.Id), "*.png", SearchOption.AllDirectories))
                    _usedFiles.Add(png);
                RequireText(where, "Character/" + c.Id);
                RequireText(where, "CharacterTitle/" + c.Id);
                RequireText(where, "CharacterIntro0/" + c.Id);
                for (int i = 1; i <= 4; i++)
                    RequireText(where, $"CharacterIntro{i}/{c.Id}", false);
            }
        }

        /// <summary>PNG の大きさを確かめる。width/height が 0 なら大きさは問わない。</summary>
        private (int W, int H)? CheckPng(string relative, int width, int height, bool required, string sizeNote = null)
        {
            string path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                if (required)
                    _r.Warn(relative, "見つからない");
                return null;
            }
            _usedFiles.Add(path);
            var size = PngSize(path);
            if (size == null)
            {
                _r.Error(relative, "PNG として読めない (拡張子だけ png の別形式?)");
                return null;
            }
            if (width > 0 && (size.Value.W != width || size.Value.H != height))
                _r.Warn(relative, $"{size.Value.W}x{size.Value.H} (本編の大きさは {width}x{height}{sizeNote})");
            return size;
        }

        private static (int W, int H)? PngSize(string path)
        {
            var head = new byte[24];
            using (var f = File.OpenRead(path))
            {
                if (f.Read(head, 0, 24) < 24)
                    return null;
            }
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!signature.SequenceEqual(head.Take(8)) || Encoding.ASCII.GetString(head, 12, 4) != "IHDR")
                return null;
            int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return (w, h);
        }

        private void CheckImages()
        {
            foreach (var path in Files("backgrounds", "*.png"))
            {
                string rel = "backgrounds/" + Path.GetFileName(path);
                var size = PngSize(path);
                if (size == null)
                    _r.Error(rel, "PNG として読めない");
                else if (Math.Abs(size.Value.W / (double)size.Value.H - 16.0 / 9) > 0.02)
                    _r.Info(rel, $"{size.Value.W}x{size.Value.H} (本編の背景は 16:9 の 1920x1080。高さを合わせて表示される)");
            }
        }

        private IEnumerable<string> Files(string folder, string pattern)
        {
            string dir = Path.Combine(_dir, folder);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal) : Enumerable.Empty<string>();
        }

        // ---- シナリオ (Lua) ----

        private IEnumerable<string> StoryNames() => Files("story", "*.lua").Select(Path.GetFileNameWithoutExtension);

        private bool StoryExists(string name) => File.Exists(Path.Combine(Path.Combine(_dir, "story"), name + ".lua"));

        private void CheckManifestStories()
        {
            foreach (var e in _m.Events)
                RequireStory($"events.{e.Id}", e.Story);
            foreach (var v in _m.Visits)
                RequireStory($"visits.{v.Id}", v.Story);
            foreach (var e in _m.Endings)
                RequireStory($"endings.{e.Id}", e.Story);
        }

        private void RequireStory(string where, string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (StoryExists(name))
                MarkStory(name, where, 0);
            else
                _r.Error(where, $"シナリオ story/{name}.lua が無い");
        }

        private void MarkStory(string name, string from, int line)
        {
            if (!_referencedStories.TryGetValue(name, out var list))
                _referencedStories[name] = list = new List<(string, int)>();
            list.Add((from, line));
        }

        private static readonly Regex Str = new Regex(@"""((?:[^""\\\n]|\\.)*)""|'((?:[^'\\\n]|\\.)*)'");

        private sealed class Call
        {
            public string Name;
            public List<string> Args;
            public int Line;
        }

        // 本編の Lua の呼び出しのうち、名前 (文字列) を渡すもの
        private static readonly Regex CallPattern = new Regex(
            @"\b(characters\.(?:LoadCharacterAsset|Get|GetPortrait|LoadCharacterPortrait)|flowcharts\.LoadView|mainui\.(?:Show(?:Big|Item)?Picture|DisplayMessage)|" +
            @"cgpanel\.Open|endgamepanel\.Open|luamanager\.(?:ChangeScene|SetNextScript|SetCurrentStoryScript|SetTempScript|Play(?:Music|Sound|EnvSound)|GetStoryText)|" +
            @"statmodifymanager\.(?:AddTalent|AddBook|Character|SetFlag|AddFlag|AddStory)|checkpointmanager\.Condition)\s*\(([^()]*)\)");

        private void CheckStories()
        {
            foreach (var path in Files("story", "*.lua"))
            {
                _usedFiles.Add(path);
                string name = Path.GetFileNameWithoutExtension(path);
                string where = $"story/{name}.lua";
                if (!ScenarioManifest.IdPattern.IsMatch(name))
                    _r.Warn(where, "シナリオ名は英小文字で始まり、英小文字・数字・_ だけにする (本編の名前と重ならないよう接頭辞を付ける)");
                string code = File.ReadAllText(path, Encoding.UTF8);
                try
                {
                    new Script(CoreModules.None).LoadString(code, null, name);
                }
                catch (SyntaxErrorException ex)
                {
                    _r.Error(where, "Lua の構文エラー: " + ex.DecoratedMessage);
                }
                foreach (var call in FindCalls(StripComments(code)))
                    CheckCall(where, name, call);
            }
            foreach (var story in StoryNames().Where(s => !_referencedStories.ContainsKey(s)))
                _r.Info($"story/{story}.lua", "scenario.json からも他のシナリオからも呼ばれていない (確認用に TestStoryName で呼ぶなら問題ない)");
        }

        private static IEnumerable<Call> FindCalls(string code)
        {
            foreach (Match m in CallPattern.Matches(code))
            {
                var args = new List<string>();
                foreach (var part in SplitArgs(m.Groups[2].Value))
                {
                    var s = Str.Match(part.Trim());
                    args.Add(s.Success && s.Index == 0 && s.Length == part.Trim().Length ? (s.Groups[1].Success ? s.Groups[1].Value : s.Groups[2].Value) : null);
                }
                yield return new Call { Name = m.Groups[1].Value, Args = args, Line = code.Take(m.Index).Count(c => c == '\n') + 1 };
            }
        }

        private static IEnumerable<string> SplitArgs(string args)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            foreach (char c in args)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                }
                else if (c == '"' || c == '\'')
                    quote = c;
                else if (c == ',')
                {
                    parts.Add(sb.ToString());
                    sb.Length = 0;
                    continue;
                }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>-- のコメントを空白にする (行番号は保つ)。文字列の中の -- は残す。</summary>
        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            char quote = '\0';
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < code.Length) { sb.Append(code[++i]); continue; }
                    if (c == quote || c == '\n') quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == '-' && i + 1 < code.Length && code[i + 1] == '-')
                {
                    bool block = i + 3 < code.Length && code[i + 2] == '[' && code[i + 3] == '[';
                    string end = block ? "]]" : "\n";
                    int stop = code.IndexOf(end, i + 2, StringComparison.Ordinal);
                    if (stop < 0) stop = code.Length;
                    for (int j = i; j < stop; j++)
                        sb.Append(code[j] == '\n' ? '\n' : ' ');
                    i = stop - 1;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private void CheckCall(string file, string story, Call call)
        {
            string where = $"{file} {call.Line}行目";
            string a0 = call.Args.Count > 0 ? call.Args[0] : null;
            string a1 = call.Args.Count > 1 ? call.Args[1] : null;
            if (a0 == null && call.Name != "luamanager.SetTempScript")
                return; // 変数を渡しているものは確かめない
            switch (call.Name)
            {
                case "characters.LoadCharacterAsset":
                case "characters.Get":
                case "statmodifymanager.Character":
                    if (!IsOwnCharacter(a0))
                        Unknown(where, "キャラ", a0);
                    break;
                case "characters.GetPortrait":
                case "characters.LoadCharacterPortrait":
                    if (IsOwnCharacter(a0) && a1 != null && a1 != "normal")
                    {
                        string rel = $"characters/{a0}/portraits/{a1}.png";
                        if (!File.Exists(Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar))))
                            _r.Warn(where, $"表情 '{a1}' の絵 {rel} が無い (立ち絵 avatar.png で出る)");
                    }
                    break;
                case "flowcharts.LoadView":
                    string background = Path.Combine(Path.Combine(_dir, "backgrounds"), a0 + ".png");
                    if (File.Exists(background))
                        _usedFiles.Add(background);
                    else
                        Unknown(where, "背景", a0);
                    break;
                case "mainui.ShowPicture":
                case "mainui.ShowBigPicture":
                case "mainui.ShowItemPicture":
                    if (!File.Exists(Path.Combine(Path.Combine(_dir, "pictures"), a0 + ".png")))
                        Unknown(where, "イベント CG", a0);
                    else
                        _usedFiles.Add(Path.Combine(Path.Combine(_dir, "pictures"), a0 + ".png"));
                    break;
                case "mainui.DisplayMessage":
                    if (!HasText("Story/" + a0) && !(a0.StartsWith("M_Add_Book_") && _m.Books.Any(b => "M_Add_Book_" + b.Id == a0)))
                        Unknown(where, "テキスト Story/", a0);
                    break;
                case "cgpanel.Open":
                    if (!_m.Cgs.Contains(a0))
                        Unknown(where, "CG (scenario.json の cgs)", a0);
                    break;
                case "endgamepanel.Open":
                    if (!_m.Endings.Any(e => e.Id == a0))
                        Unknown(where, "エンディング (scenario.json の endings)", a0);
                    break;
                case "luamanager.ChangeScene":
                    if (a0 == "GameOver" && a1 != null && !_m.Deaths.Contains(a1))
                        Unknown(where, "死亡エンディング (scenario.json の deaths)", a1);
                    else if (a0 == "Combat" && a1 != null && !_m.Duels.Any(d => d.Id == a1))
                        Unknown(where, "決闘 (scenario.json の duels)", a1);
                    break;
                case "luamanager.SetNextScript":
                case "luamanager.SetCurrentStoryScript":
                    StoryCall(where, story, a0, call.Line);
                    break;
                case "luamanager.SetTempScript":
                    if (a1 != null)
                        StoryCall(where, story, a1, call.Line);
                    break;
                case "luamanager.PlayMusic":
                    AudioCall(where, "music", "曲", a0);
                    break;
                case "luamanager.PlaySound":
                    AudioCall(where, "sounds", "効果音", a0);
                    break;
                case "luamanager.PlayEnvSound":
                    AudioCall(where, "env", "環境音", a0);
                    break;
                case "luamanager.GetStoryText":
                    if (!HasText("Story/" + a0))
                        Unknown(where, "テキスト Story/", a0);
                    break;
                case "statmodifymanager.AddTalent":
                    if (!_m.Talents.Any(t => t.Id == a0))
                        Unknown(where, "天賦", a0);
                    break;
                case "statmodifymanager.AddBook":
                    if (!_m.Books.Any(b => b.Id == a0))
                        Unknown(where, "秘笈", a0);
                    break;
                case "statmodifymanager.SetFlag":
                case "statmodifymanager.AddFlag":
                    if (!IsOwnFlag(a0))
                        Unknown(where, "フラグ", a0);
                    break;
                case "statmodifymanager.AddStory":
                    RequireText(where, "LegendInfo/" + a0);
                    break;
                case "checkpointmanager.Condition":
                    if (!_m.Conditions.ContainsKey(a0) && !_m.Endings.Any(e => e.ConditionName == a0))
                        Unknown(where, "条件 (scenario.json の conditions)", a0);
                    break;
            }
        }

        private bool IsOwnFlag(string name) =>
            _m.Flags.Any(f => f.Id == name) || _m.Events.Any(e => e.OnceFlag == name) || _m.Visits.Any(v => v.OnceFlag == name) || _m.Duels.Any(d => d.WonFlag == name);

        private void StoryCall(string where, string story, string target, int line)
        {
            if (target == ScenarioManifest.PositionDefaultAlias)
                return;
            if (StoryExists(target))
                MarkStory(target, story, line);
            else
                Unknown(where, "シナリオ", target);
        }

        private void AudioCall(string where, string folder, string kind, string name)
        {
            if (!File.Exists(Path.Combine(Path.Combine(_dir, folder), name + ".wav")))
                Unknown(where, kind, name);
        }

        // ---- 天賦・効果・秘笈・決闘・図鑑 ----

        private void CheckTalentsEffectsBooks()
        {
            foreach (var t in _m.Talents)
            {
                string where = "talents." + t.Id;
                RequireText(where, "PlayerTalent/Name/" + t.Id);
                RequireText(where, "PlayerTalent/Desc/" + t.Id);
                if (t.Effect != null && !_m.Effects.Any(e => e.Key == t.Effect))
                    Unknown(where, "効果", t.Effect);
                if (t.Effect != null && t.DisplayLevel == true)
                    _r.Info(where, $"display_level なので効果は '{t.Effect}_<レベル>' で引かれる");
            }
            foreach (var e in _m.Effects)
            {
                string where = "effects." + e.Key;
                RequireText(where, "CombatSkill/Name/" + e.Key);
                RequireText(where, "CombatSkill/Desc/" + e.Key);
                if (!_m.Talents.Any(t => t.Effect == e.Key || (t.DisplayLevel == true && t.Effect != null && e.Key.StartsWith(t.Effect + "_"))))
                    _r.Info(where, "どの天賦の effect にも使われていない");
            }
            foreach (var b in _m.Books)
            {
                string where = "books." + b.Id;
                RequireText(where, "Book/Title/" + b.Id);
                RequireText(where, "Book/Name/" + b.Id);
                RequireText(where, "Book/Desc/" + b.Id);
                CheckPng($"books/{b.Id}.png", 312, 300, true);
                foreach (var level in b.Levels)
                    foreach (var talent in level.Talents.Keys.Where(k => !_m.Talents.Any(t => t.Id == k)))
                        Unknown(where, "天賦", talent);
                foreach (var ch in b.Levels.SelectMany(l => l.Affinity.Keys).Distinct().Where(k => !IsOwnCharacter(k)))
                    Unknown(where, "キャラ", ch);
            }
        }

        private void CheckDuels()
        {
            foreach (var d in _m.Duels)
            {
                string where = "duels." + d.Id;
                if (!IsOwnCharacter(d.Character))
                {
                    _r.Warn(where, $"character '{d.Character}' はこのシナリオのキャラではない (絵を差し替えられない)");
                    continue;
                }
                foreach (var kind in new[] { "normal", "attack", "hurt", "defence" })
                    if (!File.Exists(Path.Combine(Path.Combine(CharDir(d.Character), "combat"), kind + ".png")))
                        _r.Info(where, $"characters/{d.Character}/combat/{kind}.png が無い ({(kind == "normal" ? "avatar.png" : "normal.png か avatar.png")} で出る)");
                foreach (var t in d.Talents.Where(t => !_m.Talents.Any(x => x.Id == t.Id)))
                    Unknown(where, "天賦", t.Id);
                if (d.Music != null && !File.Exists(Path.Combine(Path.Combine(_dir, "music"), d.Music + ".wav")))
                    Unknown(where, "曲", d.Music);
            }
        }

        private void CheckLibrary()
        {
            foreach (var e in _m.Endings)
            {
                string where = "endings." + e.Id;
                RequireText(where, "Library/Title/" + e.Id);
                RequireText(where, "Library/Desc/" + e.Id);
                CheckPng($"endings/{e.Id}.png", 480, 700, true);
            }
            foreach (var id in _m.Deaths)
            {
                RequireText("deaths." + id, "Library/Title/" + id);
                RequireText("deaths." + id, "Library/Desc/" + id);
            }
            foreach (var id in _m.Cgs)
            {
                RequireText("cgs." + id, "Library/Desc/" + id);
                CheckPng($"cgs/{id}.png", 1100, 1080, true);
            }
            foreach (var path in Files("pictures", "*.png"))
            {
                var size = PngSize(path);
                string rel = "pictures/" + Path.GetFileName(path);
                if (size == null)
                    _r.Error(rel, "PNG として読めない");
                else if (!new[] { (480, 700), (1920, 1080), (312, 300), (520, 520) }.Contains(size.Value))
                    _r.Info(rel, $"{size.Value.W}x{size.Value.H} (元の大きさで表示される。本編は ShowPicture 480x700 / ShowBigPicture 1920x1080 / ShowItemPicture 312x300・520x520)");
            }
        }

        // ---- 音 ----

        private void CheckAudio()
        {
            foreach (var folder in new[] { "music", "sounds", "env" })
            {
                foreach (var path in Files(folder, "*.*").Where(p => !p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !Packer.IsJunk(Path.GetFileName(p))))
                    _r.Warn($"{folder}/{Path.GetFileName(path)}", "wav 以外は読まれない");
                foreach (var path in Files(folder, "*.wav"))
                {
                    _usedFiles.Add(path);
                    string rel = $"{folder}/{Path.GetFileName(path)}";
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!ScenarioManifest.IdPattern.IsMatch(name))
                    {
                        _r.Error(rel, "ファイル名は英小文字で始まり、英小文字・数字・_ だけにする (読み込まれない)");
                        continue;
                    }
                    try
                    {
                        var wav = WavFile.Read(path);
                        AudioBytes += wav.Pcm16.Length;
                        _r.Info(rel, $"{wav.Channels}ch {wav.SampleRate}Hz {wav.Seconds:F1}秒 → 埋め込み {wav.Pcm16.Length / 1024.0 / 1024:F1}MB" +
                                     (wav.SampleChunk != null ? " (ループ位置あり)" : ""));
                    }
                    catch (Exception ex)
                    {
                        _r.Error(rel, "読めない: " + ex.Message);
                    }
                }
            }
            if (AudioBytes > 200L * 1024 * 1024)
                _r.Warn("", $"音の合計が {AudioBytes / 1024 / 1024}MB。無圧縮でメモリに載るので、曲を短くするかモノラル・低いサンプリングレートにする");
        }

        // ---- 使われていないファイル ----

        private void CheckUnused()
        {
            string chars = Path.Combine(_dir, "characters");
            foreach (var dir in Directory.Exists(chars) ? Directory.GetDirectories(chars) : new string[0])
                if (!IsOwnCharacter(Path.GetFileName(dir)))
                    _r.Info("characters/" + Path.GetFileName(dir), "scenario.json の characters に無いキャラのフォルダ (使われない)");
            foreach (var folder in new[] { "books", "endings", "cgs", "pictures", "backgrounds" })
                foreach (var path in Files(folder, "*.png").Where(p => !_usedFiles.Contains(p)))
                    _r.Info($"{folder}/{Path.GetFileName(path)}", "どこからも使われていない");
        }
    }
}
