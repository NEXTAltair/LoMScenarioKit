// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
// Minimal, independently written boundaries for testing actual addon code with HarmonyX, without launching Unity.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mortal.Core;
using Mortal.Story;

namespace UnityEngine { public enum KeyCode { None, Home, F8 } }
namespace LoMScenarioAddon.Shared
{
    internal sealed class ScenarioManifest
    {
        internal const string PositionDefaultAlias = "lsa_position_default";
        internal readonly List<VisitDef> Visits = new List<VisitDef>();
        internal readonly List<EventDef> Events = new List<EventDef>();
    }
    internal sealed class VisitDef
    {
        internal string Id, Position, Story, OnceFlag;
        internal bool Once;
        internal Dictionary<string, object> When = null;
    }
    internal sealed class EventDef { internal string Position; }
}
namespace Mortal.Story
{
    public sealed class PositionEventData { }
    internal sealed class CheckPointManager { internal static CheckPointManager Instance = new CheckPointManager(); }
}
namespace Mortal.Core
{
    internal sealed class ConditionResultItem { internal bool Check() => true; }
    internal enum PositionType { backmountain }
    internal sealed class PlayerStatManagerData
    {
        internal static PlayerStatManagerData Instance = new PlayerStatManagerData();
        internal string Script;
        internal void SetStoryScript(string value) => Script = value;
    }
}
namespace OBB.Framework.Extensions
{
    internal static class EnumExtensions { internal static string GetStringValue(this Mortal.Core.PositionType value) => value.ToString(); }
}
namespace Mortal.Free
{
    internal sealed class PositionController
    {
        private PositionType _position = PositionType.backmountain;
        private string _scriptName = "official";
        internal string Script => _scriptName;
        internal PositionType Position => _position;
        internal readonly object Wait = new object();
        internal bool Disposed;
        internal Exception Failure;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void OnPositionClick(bool normal)
        {
            if (normal) BeginActivity().MoveNext();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IEnumerator BeginActivity() => ExecuteActivityAction();
        [MethodImpl(MethodImplOptions.NoInlining)]
        private IEnumerator ExecuteActivityAction()
        {
            try
            {
                if (Failure != null) throw Failure;
                PlayerStatManagerData.Instance.SetStoryScript(_scriptName);
                yield return Wait;
                yield return null;
            }
            finally { Disposed = true; }
        }
    }
}
namespace LoMScenarioAddon
{
    internal static class Plugin
    {
        internal static Shared.ScenarioManifest Manifest = new Shared.ScenarioManifest();
        internal static Content Content = new Content();
        internal static Log Log = new Log();
    }
    internal sealed class Content
    {
        internal readonly HashSet<string> Stories = new HashSet<string>();
        internal bool HasStory(string name) => Stories.Contains(name);
    }
    internal sealed class Log
    {
        internal bool IsDebugEnabled => false;
        internal void LogInfo(string text) { }
        internal void LogWarning(string text) { }
        internal void LogError(string text) { }
        internal void LogDebug(string text) { }
    }
    internal static class CharacterRegistry
    {
        internal static bool Failure;
        internal static bool TryRegister() => Failure ? throw new InvalidOperationException("register") : true;
    }
    internal static class PositionEvents
    {
        internal static readonly Dictionary<string, int> Flags = new Dictionary<string, int>();
        internal static readonly PositionEventData Custom = new PositionEventData();
        internal static readonly List<PositionEventData> Pool = new List<PositionEventData> { new PositionEventData() };
        internal static int GetFlagState(string name) => Flags.TryGetValue(name, out var value) ? value : 0;
        internal static void SetFlagState(string name, int value) => Flags[name] = value;
        internal static ConditionResultItem BuildCondition(Dictionary<string, object> when, string where, List<string> errors) => new ConditionResultItem();
        internal static void EnsureInjected(CheckPointManager manager, string position)
        {
            if (position == "backmountain" && !Pool.Contains(Custom)) Pool.Add(Custom);
        }
        internal static IEnumerable<PositionEventData> RegisteredEvents(string position) => new[] { Custom };
    }
    internal static class Patches
    {
        internal static bool Failure;
        internal static bool TryResolveStory(string name, out string story, out Shared.EventDef ev, out string code)
        {
            if (Failure) throw new InvalidOperationException("preview");
            story = name; ev = null; code = "custom Lua";
            return name == "visit";
        }
    }
}
namespace LOMAssistant
{
    public sealed class LOMAssistantPlugin { }
    public sealed class PositionEventCache
    {
        private readonly Dictionary<string, List<PositionEventData>> positionEventDataCache = new Dictionary<string, List<PositionEventData>>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public List<PositionEventData> GetPositionEvents(string position)
        {
            if (!positionEventDataCache.TryGetValue(position, out var list))
                positionEventDataCache[position] = list = new List<PositionEventData>(LoMScenarioAddon.PositionEvents.Pool);
            return list;
        }
    }
    public static class DataHelper
    {
        public static int OriginalCalls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string LoadLuaScript(string name) { OriginalCalls++; return "game Lua"; }
    }
    public static class InputGuard
    {
        public static bool Failure;
        private static bool _keyboard;
        public static bool BlockKeyboard { get => Failure ? throw new InvalidOperationException("input") : _keyboard; set => _keyboard = value; }
        public static bool BlockPointer { get; set; }
        public static bool CapturingKey { get; set; }
        public static bool ModWindowOpen { get; set; }
    }
}
namespace LOMAssistant.Features.Extras { public static class SaveBrowser { public static bool Typing { get; set; } } }
namespace LOMAssistant.Config
{
    public sealed class ConfigElement<T> { public T Value { get; set; } }
    public static class ConfigManager { public static ConfigElement<UnityEngine.KeyCode> PanelKey = new ConfigElement<UnityEngine.KeyCode> { Value = UnityEngine.KeyCode.F8 }; }
}
