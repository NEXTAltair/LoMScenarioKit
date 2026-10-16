// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using LoMScenarioAddon;
using Mortal.Story;
using UnityEngine;

internal static class Program
{
    private static void Main()
    {
        var custom = new Sprite();
        Plugin.Content = new ScenarioContent();
        Plugin.Content.Backgrounds["camp"] = custom;
        var view = new StoryViewImage();

        // 言語変更が、コルーチン取得後・実行前にキャッシュを消すケース。
        view.Images["camp"] = custom;
        var original = new Probe(() => Check(view.Images["camp"] == custom, "custom background restored"));
        var wrapped = StoryBackgrounds.LoadAsset(view, "camp", original);
        view.Images.Clear();
        Check(!wrapped.MoveNext(), "custom load completes");
        Check(original.Disposed, "custom coroutine disposed");

        // シーンや言語を変えた後の再表示も、前回の登録に依存しない。
        view.Images.Clear();
        original = new Probe(() => Check(view.Images["camp"] == custom, "background restored again"));
        Check(!StoryBackgrounds.LoadAsset(view, "camp", original).MoveNext(), "reload completes");

        // 公式背景のキャッシュ・待機オブジェクトはそのまま本編に渡す。
        var official = new Sprite();
        var wait = new object();
        view.Images["mountain"] = official;
        original = new Probe(() => Check(view.Images["mountain"] == official, "official cache preserved"), wait);
        wrapped = StoryBackgrounds.LoadAsset(view, "mountain", original);
        Check(wrapped.MoveNext() && ReferenceEquals(wrapped.Current, wait), "original yield forwarded");
        ((IDisposable)wrapped).Dispose();
        Check(original.Disposed, "cancelled coroutine disposed");

        // 未知の背景の失敗を握りつぶさず、元の例外を伝える。
        var failure = new InvalidOperationException("unknown background");
        original = new Probe(() => throw failure);
        wrapped = StoryBackgrounds.LoadAsset(view, "unknown", original);
        try
        {
            wrapped.MoveNext();
            throw new Exception("original error was swallowed");
        }
        catch (InvalidOperationException ex)
        {
            Check(ReferenceEquals(ex, failure), "original exception preserved");
        }
        Check(original.Disposed, "failed coroutine disposed");

        original = new Probe(() => { });
        Check(!StoryBackgrounds.LoadAsset(view, null, original).MoveNext(), "null name delegated");
        Plugin.Content = null;
        original = new Probe(() => { });
        Check(!StoryBackgrounds.LoadAsset(view, "mountain", original).MoveNext(), "unconfigured content delegated");
        Console.WriteLine("Background loading regression checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class Probe : IEnumerator, IDisposable
    {
        private readonly Action _onStart;
        private readonly object _wait;
        private bool _started;
        internal bool Disposed;
        internal Probe(Action onStart, object wait = null) { _onStart = onStart; _wait = wait; }
        public object Current => _wait;
        public bool MoveNext()
        {
            if (_started) return false;
            _started = true;
            _onStart();
            return _wait != null;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}

// Unity のネイティブ実行環境を使わず、キャッシュ消去とコルーチンの実行順を再現する。
namespace UnityEngine { internal sealed class Sprite { } }
namespace Mortal.Story
{
    internal sealed class StoryViewImage
    {
        private readonly Dictionary<string, Sprite> _dicView = new Dictionary<string, Sprite>();
        internal Dictionary<string, Sprite> Images => _dicView;
    }
}
namespace LoMScenarioAddon
{
    internal static class Plugin { internal static ScenarioContent Content; }
    internal sealed class ScenarioContent
    {
        internal readonly Dictionary<string, Sprite> Backgrounds = new Dictionary<string, Sprite>();
        internal Dictionary<string, Sprite> GetBackgrounds() => Backgrounds;
    }
}
namespace HarmonyLib
{
    internal static class AccessTools
    {
        internal static FieldInfo Field(Type type, string name) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
