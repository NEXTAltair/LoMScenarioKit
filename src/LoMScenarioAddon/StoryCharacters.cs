// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System.Collections;
using Fungus;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mortal.Core;
using Mortal.Story;
using OBB.Framework.Data;
using UnityEngine;

namespace LoMScenarioAddon
{
    /// <summary>
    /// 会話シーン用のキャラ定義 (StoryCharacterData) を作り、立ち絵を Addressables を通さずに渡す。
    /// </summary>
    internal static class StoryCharacters
    {
        private static readonly Dictionary<string, StoryCharacterData> _data = new Dictionary<string, StoryCharacterData>();

        private static readonly FieldInfo DicPortraitField =
            typeof(StoryCharacterController).GetField("_dicPortrait", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo AddPortraitObjectMethod =
            typeof(StoryCharacterController).GetMethod("AddPortraitObject", BindingFlags.Instance | BindingFlags.NonPublic);

        // 追加キャラの立ち絵 → (表情, 向き)。基準の絵は向き None。
        private static readonly Dictionary<Sprite, (string Mood, FacingDirection Direction)> _spriteInfo =
            new Dictionary<Sprite, (string, FacingDirection)>();
        // 逆向きの絵を表示中で、ゲームによる左右反転を打ち消すキャラ。
        private static readonly HashSet<Character> _noFlip = new HashSet<Character>();

        internal static bool IsOurs(StoryCharacterData data)
        {
            return data != null && _data.TryGetValue(data.Id, out var ours) && ours == data;
        }

        internal static StoryCharacterData Get(string id)
        {
            if (!CharacterRegistry.IsAdded(id))
                return null;
            if (_data.TryGetValue(id, out var cached))
                return cached;

            var moods = Plugin.Content.GetPortraitMoods(id);
            var items = moods.Select(mood =>
            {
                var item = new StoryCharaterImageItem();
                CharacterRegistry.SetField(item, "_mapping", CreateMapping(mood));
                // Addressables には存在しないキー。読み込みは LoadPortrait の差し替えで行う。
                CharacterRegistry.SetField(item, "_addressKey", $"LoMScenarioAddon/{id}/{mood}");
                return item;
            }).ToArray();

            var data = ScriptableObject.CreateInstance<StoryCharacterData>();
            data.name = "StoryCharacter_" + id;
            data.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CharacterRegistry.SetField(data, "_mapping", CreateMapping(id));
            CharacterRegistry.SetField(data, "_portraitResourceList", items);
            _data[id] = data;
            Plugin.Log.LogInfo($"story character: {id} with portraits [{string.Join(", ", moods)}]");
            return data;
        }

        /// <summary>
        /// StoryCharacterController.LoadPortrait / LoadPortraitSync の代わりに、シナリオの PNG を立ち絵として登録する。
        /// </summary>
        internal static void LoadPortrait(StoryCharacterController controller, string mood)
        {
            var dic = (Dictionary<string, string>)DicPortraitField.GetValue(controller);
            if (dic == null || dic.ContainsKey(mood))
                return;
            if (!controller.Data.PortraitResourceList.Any(x => x.Mapping.Value == mood))
                return;

            var sprite = Plugin.Content.GetPortrait(controller.Data.Id, mood);
            if (sprite == null)
            {
                Plugin.Log.LogWarning($"portrait not found: {controller.Data.Id}/{mood}");
                return;
            }
            RegisterSprite(controller, sprite);
            _spriteInfo[sprite] = (mood, FacingDirection.None);
            dic[mood] = sprite.name;
        }

        private static void RegisterSprite(StoryCharacterController controller, Sprite sprite)
        {
            if (controller.Portraits.Exists(x => x == sprite))
                return;
            controller.Portraits.Add(sprite);
            if (controller.State != null && controller.State.holder != null)
                AddPortraitObjectMethod.Invoke(controller, new object[] { sprite });
        }

        /// <summary>
        /// PortraitController.Show の直前。基準と逆向きで表示するとき、逆向きの絵があればそれに差し替え、
        /// 左右反転を打ち消す印を付ける。無ければ基準の絵のまま (ゲームが自動で反転する)。
        /// </summary>
        internal static void BeforeShow(PortraitOptions options)
        {
            var controller = options.character as StoryCharacterController;
            if (controller == null || !IsOurs(controller.Data))
                return;

            var baseFace = controller.PortraitsFace;
            var facing = options.facing != FacingDirection.None ? options.facing
                : controller.State.facing != FacingDirection.None ? controller.State.facing
                : baseFace;
            var current = options.portrait != null ? options.portrait : controller.State.portrait;
            if (current == null || !_spriteInfo.TryGetValue(current, out var info))
            {
                _noFlip.Remove(controller);
                return;
            }

            if (facing != baseFace)
            {
                string dir = facing == FacingDirection.Right ? "right" : "left";
                var variant = Plugin.Content.GetPortraitVariant(controller.Data.Id, info.Mood, dir);
                if (variant != null)
                {
                    _spriteInfo[variant] = (info.Mood, facing);
                    RegisterSprite(controller, variant);
                    options.portrait = variant;
                    _noFlip.Add(controller);
                    return;
                }
            }

            // 基準の向き、または逆向きの絵が無い: 基準の絵に戻す
            if (info.Direction != FacingDirection.None)
            {
                var baseSprite = Plugin.Content.GetPortrait(controller.Data.Id, info.Mood);
                if (baseSprite != null)
                {
                    RegisterSprite(controller, baseSprite);
                    options.portrait = baseSprite;
                }
            }
            _noFlip.Remove(controller);
        }

        /// <summary>PortraitController.SetupPortrait の直後。逆向きの絵を表示中なら反転を打ち消す。</summary>
        internal static void AfterSetupPortrait(PortraitOptions options)
        {
            var character = options.character;
            if (character != null && _noFlip.Contains(character) && character.State.holder != null)
                character.State.holder.localScale = Vector3.one;
        }

        internal static IEnumerator Empty()
        {
            yield break;
        }

        private static StoryMappingItem CreateMapping(string value)
        {
            var mapping = ScriptableObject.CreateInstance<StoryMappingItem>();
            mapping.name = value;
            mapping.Key = value;
            mapping.Value = value;
            mapping.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mapping;
        }
    }
}
