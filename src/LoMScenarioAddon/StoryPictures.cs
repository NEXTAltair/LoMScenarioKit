// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

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
        private static ResourceLocationMap _locator;

        // 最初の画像表示時に登録する。ScenarioContent と同じくゲームの起動中は保持する。
        // 未解放のハンドルが残り得るシーン切り替えでは provider / locator を解除しない。
        private static void EnsureRegistered()
        {
            if (_locator != null)
                return;
            var provider = new PictureProvider();
            var locator = new ResourceLocationMap("LoMScenarioAddon.Pictures", 16);
            Addressables.ResourceManager.ResourceProviders.Add(provider);
            Addressables.AddResourceLocator(locator);
            _locator = locator;
        }

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

            EnsureRegistered();
            string redirected = KeyPrefix + key;
            string address = "pic_" + redirected;
            var location = new ResourceLocationBase(address, address, typeof(PictureProvider).FullName, typeof(Sprite))
            {
                Data = sprite
            };
            _locator.Add(address, location);
            _keys.Add(key, redirected);
            key = redirected;
        }

        private sealed class PictureProvider : ResourceProviderBase
        {
            public override Type GetDefaultType(IResourceLocation location) => typeof(Sprite);

            public override void Provide(ProvideHandle handle)
            {
                var sprite = handle.Location.Data as Sprite;
                bool available = sprite != null;
                handle.Complete<Sprite>(sprite, available,
                    available ? null : new InvalidOperationException("Scenario picture is no longer available"));
            }

            public override void Release(IResourceLocation location, object asset)
            {
                // 取得ハンドルは Addressables が解放する。
                // Sprite / Texture2D は ScenarioContent の共有キャッシュ所有なので破棄しない。
            }
        }
    }
}
