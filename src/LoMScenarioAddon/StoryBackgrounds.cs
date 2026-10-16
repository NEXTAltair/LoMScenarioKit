// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Mortal.Story;
using UnityEngine;

namespace LoMScenarioAddon
{
    internal static class StoryBackgrounds
    {
        internal static IEnumerator LoadAsset(StoryViewImage view, string name, IEnumerator original)
        {
            try
            {
                // 本編は言語変更時にキャッシュを消す。IEnumerator の取得時ではなく、
                // 最初の MoveNext の直前に補充し、そのまま本編の表示処理へ進む。
                if (name != null && Plugin.Content != null
                    && Plugin.Content.GetBackgrounds().TryGetValue(name, out var sprite))
                {
                    var images = AccessTools.Field(typeof(StoryViewImage), "_dicView").GetValue(view)
                        as Dictionary<string, Sprite>;
                    if (images != null)
                        images[name] = sprite;
                }

                while (original.MoveNext())
                    yield return original.Current;
            }
            finally
            {
                (original as IDisposable)?.Dispose();
            }
        }
    }
}
