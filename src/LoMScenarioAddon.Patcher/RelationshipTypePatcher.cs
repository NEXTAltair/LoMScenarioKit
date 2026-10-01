// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Preloader.Core.Patching;
using LoMScenarioAddon.Shared;
using Mono.Cecil;

namespace LoMScenarioAddon.Patcher
{
    /// <summary>
    /// 起動前に Mortal.Core.RelationshipStatType へ scenario.json のキャラ ID を足す。
    /// ディスク上の DLL は書き換えず、BepInEx がメモリ上で読み込む定義だけを変える。
    /// </summary>
    [PatcherPluginInfo("lom.scenarioaddon.patcher", "LoMScenarioAddon Patcher", "0.1.0")]
    public class RelationshipTypePatcher : BasePatcher
    {
        // 追加キャラの内部番号の開始値。公式は 0〜999 を使っている。
        // セーブには文字列 ID で保存されるので、この番号は起動ごとに変わってもよい。
        private const int FirstValue = 20001;

        [TargetType("Mortal.Core.dll", "Mortal.Core.RelationshipStatType")]
        public void PatchRelationshipType(TypeDefinition type)
        {
            string path = Path.Combine(Path.Combine(Path.Combine(Paths.PluginPath, "LoMScenarioAddon"), "scenario"), ScenarioManifest.FileName);
            if (!File.Exists(path))
            {
                Log.LogWarning($"{path} not found, no characters added");
                return;
            }

            ScenarioManifest manifest;
            var warnings = new List<string>();
            try
            {
                manifest = ScenarioManifest.Load(path, warnings);
            }
            catch (Exception ex)
            {
                Log.LogError($"failed to read scenario.json, no characters added: {ex.Message}");
                return;
            }
            foreach (var w in warnings)
                Log.LogWarning(w);

            // 既存メンバーから [StringValue("...")] のコンストラクタを借りる。
            var template = type.Fields
                .SelectMany(f => f.CustomAttributes)
                .FirstOrDefault(a => a.AttributeType.Name == "StringValueAttribute");
            if (template == null)
            {
                Log.LogError("StringValueAttribute not found on RelationshipStatType, abort");
                return;
            }

            var usedStrings = new HashSet<string>(type.Fields
                .SelectMany(f => f.CustomAttributes)
                .Where(a => a.AttributeType.Name == "StringValueAttribute" && a.ConstructorArguments.Count == 1)
                .Select(a => a.ConstructorArguments[0].Value as string)
                .Where(s => s != null));

            int value = FirstValue;
            foreach (var character in manifest.Characters)
            {
                if (usedStrings.Contains(character.Id))
                {
                    Log.LogError($"character id '{character.Id}' collides with an official character, skipped");
                    continue;
                }
                string memberName = ScenarioManifest.EnumMemberName(character.Id);
                var field = new FieldDefinition(
                    memberName,
                    FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                    type)
                {
                    Constant = value
                };
                var attr = new CustomAttribute(template.Constructor);
                attr.ConstructorArguments.Add(new CustomAttributeArgument(type.Module.TypeSystem.String, character.Id));
                field.CustomAttributes.Add(attr);
                type.Fields.Add(field);
                usedStrings.Add(character.Id);
                Log.LogInfo($"added RelationshipStatType.{memberName} = {value} [StringValue(\"{character.Id}\")]");
                value++;
            }
        }
    }
}
