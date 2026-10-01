// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LoMScenarioAddon.Shared;
using UnityEngine;

namespace LoMScenarioAddon
{
    internal sealed class CharacterImages
    {
        public Sprite Icon;
        public Sprite Head;
        public Sprite Avatar;
    }

    /// <summary>
    /// scenario/ フォルダのテキストと画像を読む。
    /// </summary>
    internal sealed class ScenarioContent
    {
        private readonly string _dir;
        // 言語コード (ja, zh-TW, zh-CN, ko ...) → キー → 本文。text.<言語>.csv から読む。
        private readonly Dictionary<string, Dictionary<string, string>> _textsByLang = new Dictionary<string, Dictionary<string, string>>();
        // 実行時に作ったテキスト (台詞の自動キー、決闘の台詞)。言語の区別なし。
        private readonly Dictionary<string, string> _runtimeTexts = new Dictionary<string, string>();
        private readonly Dictionary<string, CharacterImages> _images = new Dictionary<string, CharacterImages>();
        private readonly Dictionary<string, Sprite> _portraits = new Dictionary<string, Sprite>();
        private string _defaultLanguage = "ja";
        private string _languageSetting = "auto";
        private string _lastLanguage;

        private ScenarioContent(string dir)
        {
            _dir = dir;
        }

        public static ScenarioContent Load(string dir, string defaultLanguage, string languageSetting)
        {
            var content = new ScenarioContent(dir)
            {
                _defaultLanguage = string.IsNullOrEmpty(defaultLanguage) ? "ja" : defaultLanguage,
                _languageSetting = string.IsNullOrEmpty(languageSetting) ? "auto" : languageSetting,
            };
            if (!Directory.Exists(dir))
            {
                Plugin.Log.LogWarning($"scenario folder not found: {dir}");
                return content;
            }
            foreach (var path in Directory.GetFiles(dir, "text.*.csv"))
            {
                string name = Path.GetFileNameWithoutExtension(path); // text.ja
                string lang = name.Substring("text.".Length);
                content.LoadTexts(lang, path);
            }
            if (!content._textsByLang.ContainsKey(content._defaultLanguage))
                Plugin.Log.LogWarning($"text.{content._defaultLanguage}.csv (default_language) not found");
            Plugin.Log.LogInfo($"languages: [{string.Join(", ", content._textsByLang.Keys)}], default={content._defaultLanguage}, setting={content._languageSetting}");
            return content;
        }

        /// <summary>
        /// 今使う言語。設定 Language が auto なら、日本語化 MOD が入っていれば ja、
        /// そうでなければゲームの言語 (繁体字 zh-TW / 簡体字 zh-CN / 韓国語 ko)。
        /// ゲーム中に言語を切り替えても、テキストを取り出すたびに見直すので追従する。
        /// </summary>
        public string CurrentLanguage
        {
            get
            {
                string lang = _languageSetting != "auto" ? _languageSetting : DetectLanguage();
                if (lang != _lastLanguage)
                {
                    Plugin.Log.LogInfo($"text language: {lang}");
                    _lastLanguage = lang;
                }
                return lang;
            }
        }

        private static bool? _jpModLoaded;

        private static string DetectLanguage()
        {
            if (_jpModLoaded == null)
                _jpModLoaded = System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "LOM_JP_StringVault");
            if (_jpModLoaded == true)
                return "ja";
            switch (Lean.Localization.LeanLocalization.CurrentLanguage)
            {
                case "ChineseTraditional": return "zh-TW";
                case "ChineseSimplified": return "zh-CN";
                case "Korean": return "ko";
                case "English": return "en";
                case "Japanese": return "ja";
                default: return Lean.Localization.LeanLocalization.CurrentLanguage;
            }
        }

        /// <summary>今の言語 → default_language → 実行時のテキストの順に探す。</summary>
        public bool TryGetText(string key, out string text)
        {
            if (TryGetLangText(CurrentLanguage, key, out text) || TryGetLangText(_defaultLanguage, key, out text)
                || _runtimeTexts.TryGetValue(key, out text))
                return true;
            // 決闘の名前・肩書きが無ければ、社交パネルの名前・肩書きを使う
            if (key.StartsWith("CombatCharacter/Name/"))
                return TryGetText("Character/" + key.Substring("CombatCharacter/Name/".Length), out text);
            if (key.StartsWith("CombatCharacter/Title/"))
                return TryGetText("CharacterTitle/" + key.Substring("CombatCharacter/Title/".Length), out text);
            // 秘笈の入手表示 (本編と同じく mainui.DisplayMessage("M_Add_Book_<ID>")) が無ければ、秘笈の名前から作る
            if (key.StartsWith(BookMessagePrefix) && TryGetText("Book/Name/" + key.Substring(BookMessagePrefix.Length), out var bookName))
            {
                text = string.Format(BookMessageFormat(CurrentLanguage), bookName);
                return true;
            }
            return false;
        }

        private const string BookMessagePrefix = "Story/M_Add_Book_";

        private static string BookMessageFormat(string lang)
        {
            switch (lang)
            {
                case "zh-CN": return "获得秘笈《{0}》";
                case "zh-TW": return "獲得秘笈《{0}》";
                case "ko": return "비급 《{0}》 획득";
                default: return "秘笈《{0}》を獲得した";
            }
        }

        private bool TryGetLangText(string lang, string key, out string text)
        {
            text = null;
            return lang != null && _textsByLang.TryGetValue(lang, out var table) && table.TryGetValue(key, out text);
        }

        /// <summary>実行時に作ったテキスト (台詞の自動キーなど)。各言語の CSV に同じキーがあればそちらが優先される。</summary>
        public void AddRuntimeText(string key, string text)
        {
            if (!_runtimeTexts.ContainsKey(key))
                _runtimeTexts[key] = text;
        }

        private string StoryPath(string name)
        {
            return Path.Combine(Path.Combine(_dir, "story"), name + ".lua");
        }

        public bool HasStory(string name)
        {
            return File.Exists(StoryPath(name));
        }

        /// <summary>
        /// シナリオの Lua。試作中の書き換えがすぐ効くよう、毎回ファイルから読む。
        /// </summary>
        public bool TryGetStory(string name, out string code)
        {
            string path = StoryPath(name);
            code = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            return code != null;
        }

        public CharacterImages GetCharacterImages(string characterKey)
        {
            if (_images.TryGetValue(characterKey, out var cached))
                return cached;
            string charDir = Path.Combine(Path.Combine(_dir, "characters"), characterKey);
            var images = new CharacterImages
            {
                Icon = LoadSprite(Path.Combine(charDir, "icon.png")),
                Head = LoadSprite(Path.Combine(charDir, "head.png")),
                Avatar = LoadSprite(Path.Combine(charDir, "avatar.png")),
            };
            _images[characterKey] = images;
            return images;
        }

        /// <summary>
        /// 会話シーン用の表情名の一覧。characters/&lt;id&gt;/portraits/&lt;表情&gt;.png。
        /// normal を先頭にする (公式も最初の表情を既定として読む)。portraits が無ければ avatar.png を normal として使う。
        /// </summary>
        public List<string> GetPortraitMoods(string characterKey)
        {
            string dir = Path.Combine(Path.Combine(Path.Combine(_dir, "characters"), characterKey), "portraits");
            var moods = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.png").Select(Path.GetFileNameWithoutExtension).OrderBy(m => m == "normal" ? 0 : 1).ThenBy(m => m).ToList()
                : new List<string>();
            if (!moods.Contains("normal"))
                moods.Insert(0, "normal");
            return moods;
        }

        public Sprite GetPortrait(string characterKey, string mood)
        {
            string cacheKey = characterKey + "/" + mood;
            if (_portraits.TryGetValue(cacheKey, out var cached))
                return cached;
            string charDir = Path.Combine(Path.Combine(_dir, "characters"), characterKey);
            string path = Path.Combine(Path.Combine(charDir, "portraits"), mood + ".png");
            if (!File.Exists(path) && mood == "normal")
                path = Path.Combine(charDir, "avatar.png");
            // 公式の立ち絵とは別名にする (StoryCharacterController は名前で重複を判定する)。
            var sprite = LoadSprite(path, $"{characterKey}_{mood}");
            _portraits[cacheKey] = sprite;
            return sprite;
        }

        /// <summary>
        /// 逆向きの立ち絵 (左右非対称のデザイン用)。portraits/&lt;right|left&gt;/&lt;表情&gt;.png。
        /// 無ければ null で、その場合はゲームが基準の絵を自動で左右反転する。
        /// </summary>
        public Sprite GetPortraitVariant(string characterKey, string mood, string direction)
        {
            string cacheKey = characterKey + "/" + mood + "@" + direction;
            if (_portraits.TryGetValue(cacheKey, out var cached))
                return cached;
            string path = Path.Combine(Path.Combine(Path.Combine(Path.Combine(Path.Combine(_dir, "characters"), characterKey), "portraits"), direction), mood + ".png");
            var sprite = File.Exists(path) ? LoadSprite(path, $"{characterKey}_{mood}_{direction}") : null;
            _portraits[cacheKey] = sprite;
            return sprite;
        }

        /// <summary>
        /// 自前の背景。backgrounds/&lt;背景名&gt;.png をすべて読む。本編の背景は 1920x1080・PPU 100・中央基準なので、
        /// どの解像度でも同じ高さで描かれるよう PPU を合わせる (横幅は 16:9 を推奨)。
        /// </summary>
        public Dictionary<string, Sprite> GetBackgrounds()
        {
            if (_backgrounds != null)
                return _backgrounds;
            _backgrounds = new Dictionary<string, Sprite>();
            string dir = Path.Combine(_dir, "backgrounds");
            if (!Directory.Exists(dir))
                return _backgrounds;
            foreach (var path in Directory.GetFiles(dir, "*.png"))
            {
                var tex = LoadTexture(path);
                if (tex == null)
                    continue;
                float ppu = 100f * tex.height / 1080f;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), ppu);
                string name = Path.GetFileNameWithoutExtension(path);
                sprite.name = "screen_" + name;
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _backgrounds[name] = sprite;
            }
            Plugin.Log.LogInfo($"backgrounds: [{string.Join(", ", _backgrounds.Keys)}]");
            return _backgrounds;
        }

        private Dictionary<string, Sprite> _backgrounds;

        /// <summary>秘笈のアイコン。books/&lt;秘笈ID&gt;.png (公式は 312x300)。無ければ null (複製元のアイコンのまま)。</summary>
        public Sprite GetBookImage(string bookId)
        {
            string path = Path.Combine(Path.Combine(_dir, "books"), bookId + ".png");
            return File.Exists(path) ? LoadSprite(path, "book_" + bookId) : null;
        }

        /// <summary>CG 画面の絵。cgs/&lt;キー&gt;.png (公式は 1100x1080)。</summary>
        public Sprite GetCgPicture(string id)
        {
            string path = Path.Combine(Path.Combine(_dir, "cgs"), id + ".png");
            return File.Exists(path) ? LoadSprite(path, "cg_" + id) : null;
        }

        /// <summary>
        /// イベント CG (mainui.ShowPicture / ShowBigPicture / ShowItemPicture のキー)。pictures/&lt;キー&gt;.png。
        /// 本編は元の大きさで表示する (ShowPicture 480x700、ShowBigPicture 1920x1080、ShowItemPicture 312x300 など)。
        /// </summary>
        public Sprite GetPicture(string key)
        {
            string cacheKey = "picture/" + key;
            if (_portraits.TryGetValue(cacheKey, out var cached))
                return cached;
            string path = Path.Combine(Path.Combine(_dir, "pictures"), key + ".png");
            var sprite = File.Exists(path) ? LoadSprite(path, "pic_" + key) : null;
            _portraits[cacheKey] = sprite;
            return sprite;
        }

        /// <summary>エンディングの挿絵。endings/&lt;エンディングID&gt;.png (公式は 480x700)。無ければ null。</summary>
        public Sprite GetEndingPicture(string endingId)
        {
            string path = Path.Combine(Path.Combine(_dir, "endings"), endingId + ".png");
            return File.Exists(path) ? LoadSprite(path, "ending_" + endingId) : null;
        }

        /// <summary>
        /// 決闘の敵の絵。characters/&lt;id&gt;/combat/&lt;normal|attack|hurt|defence&gt;.png。
        /// 無ければ normal、それも無ければ立ち絵 (avatar.png)。
        /// 決闘の絵はワールド座標で描かれるので、複製元の絵と同じ高さ・基準点になるよう縮尺 (PPU) とピボットを合わせる。
        /// </summary>
        public Sprite GetCombatSprite(string characterKey, string kind, Sprite template)
        {
            string cacheKey = characterKey + "/combat/" + kind;
            if (_portraits.TryGetValue(cacheKey, out var cached) && cached != null)
                return cached;
            string charDir = Path.Combine(Path.Combine(_dir, "characters"), characterKey);
            string combatDir = Path.Combine(charDir, "combat");
            string path = new[]
            {
                Path.Combine(combatDir, kind + ".png"),
                Path.Combine(combatDir, "normal.png"),
                Path.Combine(charDir, "avatar.png"),
            }.FirstOrDefault(File.Exists);
            if (path == null)
                return null;
            var tex = LoadTexture(path);
            if (tex == null)
                return null;

            float ppu = 100f;
            var pivot = new Vector2(0.5f, 0.5f);
            if (template != null && template.rect.height > 0)
            {
                ppu = template.pixelsPerUnit * tex.height / template.rect.height;
                pivot = new Vector2(template.pivot.x / template.rect.width, template.pivot.y / template.rect.height);
            }
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu);
            sprite.name = $"{characterKey}_combat_{kind}";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _portraits[cacheKey] = sprite;
            return sprite;
        }

        private void LoadTexts(string lang, string path)
        {
            if (!_textsByLang.TryGetValue(lang, out var table))
                _textsByLang[lang] = table = new Dictionary<string, string>();
            foreach (var row in Csv.Parse(File.ReadAllText(path, Encoding.UTF8)))
            {
                if (row.Count < 2 || string.IsNullOrEmpty(row[0]) || row[0].StartsWith("#"))
                    continue;
                table[row[0]] = row[1];
            }
            Plugin.Log.LogInfo($"texts: {table.Count} entries from {Path.GetFileName(path)}");
        }


        private static Texture2D LoadTexture(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path)))
            {
                Plugin.Log.LogWarning($"image decode failed: {path}");
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            // シーン切り替え時の UnloadUnusedAssets で破棄されないようにする。
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return tex;
        }

        private static Sprite LoadSprite(string path, string name = null)
        {
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"image not found: {path}");
                return null;
            }
            var tex = LoadTexture(path);
            if (tex == null)
                return null;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name ?? Path.GetFileNameWithoutExtension(path);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
    }
}
