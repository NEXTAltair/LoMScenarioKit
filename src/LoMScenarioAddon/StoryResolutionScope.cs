// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
using System;

namespace LoMScenarioAddon
{
    /// <summary>1回の ExecuteLuaScript の間だけ、自作シナリオの判定を互換処理と共有する。</summary>
    internal sealed class StoryResolutionScope : IDisposable
    {
        [ThreadStatic]
        private static StoryResolutionScope _current;

        private readonly StoryResolutionScope _previous;
        private readonly object _owner;
        private string _name;
        private bool _resolved;
        private bool _isScenario;

        internal StoryResolutionScope(object owner)
        {
            _owner = owner;
            _previous = _current;
            _current = this;
        }

        internal void Record(string name, bool isScenario)
        {
            _name = name;
            _isScenario = isScenario;
            _resolved = true;
        }

        internal static bool IsScenario(object owner, string name, Func<string, bool> fallback)
        {
            var current = _current;
            if (current != null && current._resolved && ReferenceEquals(current._owner, owner)
                && string.Equals(current._name, name, StringComparison.Ordinal))
                return current._isScenario;

            // 訪問から本編への復帰や、別のパッチで名前が変わった場合は従来どおり判定する。
            return fallback(name);
        }

        public void Dispose()
        {
            // 入れ子の呼び出しが終わったら親へ戻す。例外時も次回のロードに判定を残さない。
            if (ReferenceEquals(_current, this))
                _current = _previous;
        }
    }
}
