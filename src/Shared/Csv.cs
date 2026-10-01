// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections.Generic;
using System.Text;

namespace LoMScenarioAddon.Shared
{
    /// <summary>text.&lt;言語&gt;.csv の読み込み (RFC 4180 相当、BOM 可)。プラグインと作る人向けの道具で共用。</summary>
    internal static class Csv
    {
        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            if (text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else field.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"': quoted = true; break;
                    case ',': row.Add(field.ToString()); field.Length = 0; break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString()); field.Length = 0;
                        rows.Add(row); row = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }
    }
}
