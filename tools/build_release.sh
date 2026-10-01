#!/usr/bin/env bash
# Copyright (C) 2026 NEXTAltair
# GPL-3.0-only with the additional permission in LICENSE-GAME-EXCEPTION.txt.
# 互換用入口。Windows では python tools/build_release.py を使用する。
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
exec python3 tools/build_release.py "$@"
