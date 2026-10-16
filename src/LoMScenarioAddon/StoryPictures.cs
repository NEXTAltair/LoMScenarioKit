// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 自前のイベント画像を Addressables に供給する。
    /// 表示・非表示の演出と取得ハンドルの解放は、本編の StoryPicture に任せる。
    /// </summary>
    internal static class StoryPictures
    {
        private const string KeyPrefix = "LoMScenarioAddon/pictures/";
        private static readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void RedirectKey(ref string key)
        {
            if (string.IsNullOrEmpty(key) || Plugin.Content == null)
                return;
            if (_keys.TryGetValue(key, out var registered))
            {
                key = registered;
                return;
            }

            // 存在しない・壊れた画像なら元のキーを残し、本編の読み込みへ進む。
            Sprite sprite;
            try
            {
                sprite = Plugin.Content.GetPicture(key);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"picture '{key}' could not be read: {ex.Message}");
                return;
            }
            if (sprite == null)
                return;

            string redirected = KeyPrefix + key;
            string address = "pic_" + redirected;
            ScenarioSprites.Register(address, sprite);
            _keys.Add(key, redirected);
            key = redirected;
        }
    }
}
