// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace LoMScenarioAddon
{
    /// <summary>シナリオが所有する Sprite を、本編の Addressables 読み込みへ供給する。</summary>
    internal static class ScenarioSprites
    {
        // 未解放ハンドルが残るシーン遷移でも解除せず、ScenarioContent と同じ寿命で保持する。
        private static ResourceLocationMap _locator;

        internal static void Register(string address, Sprite sprite)
        {
            if (_locator == null)
            {
                var provider = new SpriteProvider();
                var locator = new ResourceLocationMap("LoMScenarioAddon.Sprites", 16);
                Addressables.ResourceManager.ResourceProviders.Add(provider);
                Addressables.AddResourceLocator(locator);
                _locator = locator;
            }

            var location = new ResourceLocationBase(address, address, typeof(SpriteProvider).FullName, typeof(Sprite))
            {
                Data = sprite
            };
            _locator.Add(address, location);
        }

        private sealed class SpriteProvider : ResourceProviderBase
        {
            public override Type GetDefaultType(IResourceLocation location) => typeof(Sprite);

            public override void Provide(ProvideHandle handle)
            {
                var sprite = handle.Location.Data as Sprite;
                bool available = sprite != null;
                handle.Complete<Sprite>(sprite, available,
                    available ? null : new InvalidOperationException("Scenario sprite is no longer available"));
            }

            public override void Release(IResourceLocation location, object asset)
            {
                // ハンドルは Addressables が解放する。共有 Sprite / Texture2D は
                // ScenarioContent が所有するため破棄せず、再読み込みでも同じ画像を返す。
            }
        }
    }
}
