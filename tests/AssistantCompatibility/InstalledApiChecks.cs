// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InstalledApiChecks
{
    // Read metadata only. Never loads or invokes installed game / assistant code.
    internal static void Run(string gameDir, string addonPath, Action<bool, string> check)
    {
        using (var resolver = new DefaultAssemblyResolver())
        {
            resolver.AddSearchDirectory(Path.Combine(gameDir, "BepInEx/core"));
            resolver.AddSearchDirectory(Path.Combine(gameDir, "Mortal_Data/Managed"));
            var parameters = new ReaderParameters { AssemblyResolver = resolver };
            using (var game = AssemblyDefinition.ReadAssembly(Path.Combine(gameDir, "Mortal_Data/Managed/Mortal.Free.dll"), parameters))
            using (var assistant = AssemblyDefinition.ReadAssembly(Path.Combine(gameDir, "BepInEx/plugins/LOMAssistant/LOMAssistant.dll"), parameters))
            using (var addon = AssemblyDefinition.ReadAssembly(addonPath, parameters))
            {
                var position = game.MainModule.GetType("Mortal.Free.PositionController");
                check(position.Methods.Any(m => m.Name == "ExecuteActivityAction" && m.ReturnType.FullName == "System.Collections.IEnumerator" && m.Parameters.Count == 0)
                    && position.Fields.Any(f => f.Name == "_scriptName" && f.FieldType.FullName == "System.String"), "installed activity API matches the visit hook");
                var cache = assistant.MainModule.GetType("LOMAssistant.PositionEventCache");
                check(cache.Methods.Any(m => m.Name == "GetPositionEvents" && OneString(m)
                    && m.ReturnType.FullName == "System.Collections.Generic.List`1<Mortal.Story.PositionEventData>")
                    && cache.Fields.Any(f => f.Name == "positionEventDataCache"
                        && f.FieldType.FullName == "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<Mortal.Story.PositionEventData>>"),
                    "installed assistant event cache API matches");
                check(assistant.MainModule.GetType("LOMAssistant.DataHelper").Methods.Any(m => m.Name == "LoadLuaScript"
                    && m.IsStatic && OneString(m) && m.ReturnType.FullName == "System.String"), "installed assistant Lua preview API matches");
                var input = assistant.MainModule.GetType("LOMAssistant.InputGuard");
                check(new[] { "BlockKeyboard", "BlockPointer", "CapturingKey", "ModWindowOpen" }.All(name => input.Properties.Any(p => p.Name == name
                    && p.PropertyType.FullName == "System.Boolean" && p.GetMethod.IsPublic && p.GetMethod.IsStatic)), "installed assistant input guards match");
                check(assistant.MainModule.GetType("LOMAssistant.Features.Extras.SaveBrowser").Properties.Any(p => p.Name == "Typing"
                    && p.PropertyType.FullName == "System.Boolean" && p.GetMethod.IsPublic && p.GetMethod.IsStatic), "installed assistant note editor guard matches");
                check(assistant.MainModule.GetType("LOMAssistant.Config.ConfigManager").Fields.Count(f => f.IsPublic && f.IsStatic
                    && f.FieldType.FullName == "LOMAssistant.Config.ConfigElement`1<UnityEngine.KeyCode>") == 10, "installed assistant hotkey settings are discoverable");
                var plugin = addon.MainModule.GetType("LoMScenarioAddon.Plugin");
                check(plugin.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency"
                    && (string)a.ConstructorArguments[0].Value == "LOMAssistant" && Convert.ToInt32(a.ConstructorArguments[1].Value) == 2),
                    "assistant dependency is optional");
                check(!addon.MainModule.AssemblyReferences.Any(r => r.Name == "LOMAssistant"), "addon has no hard assembly reference to assistant");
                var ownTargets = Targets(addon).ToList();
                check(!ownTargets.Contains("Mortal.Free.PositionController:OnPositionClick")
                    && ownTargets.Contains("Mortal.Free.PositionController:ExecuteActivityAction"), "built addon moves the visit hook off click handling");
                check(!ownTargets.Intersect(Targets(assistant)).Any(), "installed assistant and built addon attribute patch targets do not overlap");
            }
        }

    }

    private static bool OneString(MethodDefinition method) => method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.String";

    private static IEnumerable<string> Targets(AssemblyDefinition assembly)
    {
        foreach (var type in assembly.MainModule.GetTypes())
        foreach (var attribute in type.CustomAttributes.Concat(type.Methods.SelectMany(m => m.CustomAttributes)))
        {
            if (attribute.AttributeType.FullName != "HarmonyLib.HarmonyPatch" || attribute.ConstructorArguments.Count < 2) continue;
            if (attribute.ConstructorArguments[0].Value is TypeReference target && attribute.ConstructorArguments[1].Value is string method)
                yield return target.FullName + ":" + method;
        }
    }
}
