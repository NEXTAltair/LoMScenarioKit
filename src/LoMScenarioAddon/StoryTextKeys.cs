// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Text;
using System.Text.RegularExpressions;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 既読判定のための自動変換。
    /// 本編の既読判定は luamanager.GetStoryText(キー) を通った台詞だけが対象 (既読の一覧に載り、既読色・既読スキップが効く)。
    /// シナリオの say("本文") を読み込み時に say(luamanager.GetStoryText("LSA_&lt;シナリオ名&gt;_&lt;ハッシュ&gt;")) に置き換え、
    /// 本文をテキスト表の Story/&lt;キー&gt; に登録する。キーは本文から作るので、行の追加や並べ替えで既読がずれない。
    /// </summary>
    internal static class StoryTextKeys
    {
        // say("...") / say('...') で、引数が文字列リテラル1つだけのもの。
        private static readonly Regex SayLiteral = new Regex(
            @"\bsay\(\s*(?:""((?:[^""\\\n]|\\.)*)""|'((?:[^'\\\n]|\\.)*)')\s*\)",
            RegexOptions.Compiled);

        internal static string Convert(string storyName, string code, ScenarioContent content)
        {
            int count = 0;
            string converted = SayLiteral.Replace(code, m =>
            {
                string literal = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                string text = Unescape(literal);
                string key = $"LSA_{storyName}_{Fnv1a(text):x8}";
                content.AddRuntimeText("Story/" + key, text);
                count++;
                return $"say(luamanager.GetStoryText(\"{key}\"))";
            });
            if (count > 0)
                Plugin.Log.LogInfo($"story: {storyName}: {count} line(s) keyed for read tracking");
            return converted;
        }

        /// <summary>Lua の文字列エスケープのうち、台詞で使うもの (\n \t \" \' \\) を戻す。</summary>
        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>FNV-1a 32bit。実行環境によらず同じ値になるハッシュ (string.GetHashCode は使わない)。</summary>
        private static uint Fnv1a(string text)
        {
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash *= 16777619;
            }
            return hash;
        }
    }
}
