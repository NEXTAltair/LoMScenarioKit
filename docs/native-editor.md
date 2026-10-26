# シナリオをGUIとエージェントで編集する

LoMScenarioKitの制作エディターは、lom_modkitの既存Rust GUIを基盤にしています。章一覧、ノードの入力フォーム、画面プレビュー、フロー図を使いながら、資料・判定・コメント・保存履歴を確認できます。GUIとJSON CLIは同じ制作フォルダを読み書きします。

初回は「起動する」と「GUIで編集する」を読んでください。エージェントとの共同編集や配布時は、該当する節を参照してください。

1. 制作データは専用のフォルダに保存します。
2. GUIの編集は保存操作で確定します。CLIの更新には、直前に取得した `revision` が必要です。
3. 配布時は新しいフォルダへ書き出し、LoMScenarioToolで検査します。

## 起動する

Rust 1.89以降とCargoが必要です。確認環境はRust 1.93.1、Linux/X11です。LinuxではC/C++ビルド環境、OpenGLとウィンドウ環境、日本語を表示できるフォントも必要です。Windows向けのゲーム導入作業はエディターの起動には不要です。

Linux・macOS・WSLでは、リポジトリのルートで実行します。

```sh
./editor
```

WSLではX11とCPU描画を自動で選びます。`WAYLAND_DISPLAY` / `WAYLAND_SOCKET` を外し、未指定なら `LIBGL_ALWAYS_SOFTWARE=true` と `GALLIUM_DRIVER=llvmpipe` を設定します。WSL以外の描画設定は変更しません。Rustのビルドは必要に応じて自動で行います。

章・選択肢・フラグ・分岐を含むサンプルも開けます。自作の作品にはサンプルのコピーを使ってください。

```sh
./editor tools/modkit/samples/scenario-addon
./editor "/path/to/自分の作品"
```

別のディレクトリからスクリプトのパスを指定して呼ぶ場合も、制作フォルダの相対パスは呼び出し元が基準です。WindowsのPowerShellなど、shを使わない環境では次のコマンドで起動します。

```sh
cargo run --locked --manifest-path tools/modkit/Cargo.toml -p lom-editor
```

既存のMortalModHost制作フォルダ・JSON・`.lommod` も読み込めます。新規プロジェクトはLoMScenarioAddon向けです。互換用のMortalModHostプロジェクトはファイルメニューから作成できます。両形式のランタイムや書き出し先は異なります。

## GUIで編集する

左側で章とノードを選び、中央のフォームで本文や分岐先を編集します。右側のプレビューとフロー図は元のエディターの機能です。ゲーム内の完全な動作再現ではありません。

左側の **資料・添削・履歴** には、次のページがあります。

| ページ | 操作 |
|---|---|
| 判定・コメント | 章または選択中のノードを、未判定・良い・要修正・保留・不採用に分類。指摘の追加、返信、解決状態の変更 |
| 資料 | 制作フォルダの `outline/` にあるMarkdownを、本文・見出しで検索。目次から該当箇所へ移動 |
| 保存履歴 | 編集者と変更前後を確認し、選択した履歴から現在の章を復元 |
| Addon設定 | 書き出す `scenario.json` のJSONを編集。イベントの `story` には章IDを指定 |

コメントは投稿時の引用を保持します。本文を変更・削除しても引用は残り、人間とエージェントの投稿を区別できます。返信は親の引用を引き継ぎます。「解決済み」は指摘を消す操作ではありません。

入力中のコメントは「コメントを追加」、設定は「設定を反映」を押した後、ファイルメニューから保存してください。履歴から復元した章も、保存するまでは未確定です。復元時には現在のコメント・判定と、ほかの章を保持します。

## JSON CLIで編集する

```sh
cargo build --locked --manifest-path tools/modkit/Cargo.toml -p lomc
 tools/modkit/target/debug/lomc agent commands
 tools/modkit/target/debug/lomc agent init --project /tmp/my-story
 tools/modkit/target/debug/lomc agent checkout --project /tmp/my-story
```

Windowsでは実行ファイル名が `lomc.exe` になります。以降の例はビルド済みの `lomc` をPATHに置いた場合です。

応答は常にJSONです。成功時は終了コード0、入力・検査の失敗は1、revisionの競合は2を返します。

```json
{
  "schema_version": 1,
  "command": "checkout",
  "ok": true,
  "purpose": "編集用の全文と revision を取得する",
  "data": {"revision": "...", "document": {"manifest": {}, "scenario": null, "stories": {}}},
  "issues": [],
  "next_actions": [{"argv": ["lomc", "agent", "commands"], "reason": "利用可能な操作と入力を確認する", "mutates": false}]
}
```

`commands` は各操作の目的・入力・更新の有無を返します。`describe` はJSON入力の `command` で指定した操作を説明します。ほかの入力も、`--input request.json` または `--input -` で渡します。

本文を直す `request.json` の例です。revisionは `checkout` の実際の値に置き換えます。

```json
{
  "revision": "checkoutで取得した値",
  "story": "main",
  "name": "Codex",
  "message": "冒頭の表現を短くした",
  "operations": [{"op": "update_node", "node_id": "say1", "fields": {"text": "裏山に着いた。"}}]
}
```

```sh
lomc agent edit --project /tmp/my-story --input request.json
```

| コマンド | 入力の要点 |
|---|---|
| `init` / `checkout` / `show` | 新規作成 / 全文とrevisionを取得 / `story` の本文と添削を読む |
| `edit` | `revision`, `story`, `operations`。既存のノード編集APIを利用 |
| `save` | `revision`, `document`。checkoutで取得したmanifest・scenario・storiesを保存 |
| `verdict` | `revision`, `story`, 任意の `node`, `verdict`（`unreviewed/good/fix/hold/reject`） |
| `comments` / `comment` | 閲覧は `story`。追加は `revision`, `story`, `text`。`parent` に親コメントIDを指定すると返信 |
| `resolve` | `revision`, `id`, `resolved`（boolean） |
| `history` / `entry` | 履歴一覧 / `id` の変更前後。取得上限は直近1000件 |
| `restore` | `revision`, `id`, `story`。現在の添削を保持して1章を復元 |
| `outline` / `chart` | 資料検索の `query` / フロー図を取得する `story` |
| `check` / `export` | 章の構造・書き出し対応・章参照を検査 / `output` の新しいフォルダへ書き出す |

更新操作は `dry_run: true` を受け付けます。`save` でコメントや判定を書き換えることはできません。専用コマンドを使ってください。コメントは本文と別のデータなので、指摘を読んだだけで本文へ混ぜる必要はありません。

GUIが保存済みなら、CLIの更新を約2秒ごとに取り込みます。GUIに未保存の変更や入力中のコメントがある場合は、読み替えを止めて競合を表示します。古いrevisionでのCLI更新も拒否します。最新版を取得し、差分を確認してから編集し直してください。

## Addonへ書き出す

GUIの「Addonを書き出す」、またはCLIの `export` を使います。章IDは英小文字で始まる英小文字・数字・`_` の64文字以内とし、`lsa_my_story_01` のように自分の接頭辞を付けて本編との衝突を避けてください。

書き出し先には `scenario.json`、`story/<章ID>.lua`、空の `text.<既定言語>.csv` ができます。既存フォルダへの上書きはしません。コメントや履歴は制作データに残り、書き出しには入りません。

対応ノードは `scene`, `show`, `hide`, `move`, `face`, `focus`, `say`, `choice`, `wait`, `game_flag`, `branch`, `music`, `sound`, `intro`, `end` です。`say` はcharacter/narrative/center、`choice` はOptions、`branch` はflag_value/conditionに対応します。次の章への移動も変換します。

MortalModHostの `user:` 素材、ボイス、専用ノードや未対応のフィールドがあると、章・ノードを示して書き出しを止めます。Addon向けの新規ノード一覧には対応する種別を表示します。フォームには互換形式の項目もあるため、詳細な対応は検査結果で確認してください。画像・音声などのAddon素材は、[シナリオ制作ガイド](scenario-guide.md)の形式で書き出し先に追加します。

章の多言語設定（`localization`）の変換は未対応です。設定のある章は検査・書き出しで拒否し、翻訳を制作データに保持します。多言語の画面にもこの制限を表示します。

配布前に、既存の検査ツールで設定・素材・Lua構文を確認します。

```sh
dotnet run --project src/LoMScenarioTool -f net8.0 -- check /tmp/my-scenario-export --no-pause
```

`lomc agent check` はゲーム側の全設定や素材の検査を置き換えません。ゲーム内でのイベント発生や演出は別途確認してください。

## 制作フォルダと保存の範囲

```text
my-story/
  manifest.json       作品情報、Addon設定、判定、コメント
  story/*.json        章とノード
  outline/*.md        設定資料
  .authoring/
    workspace.lock    GUI・CLI共通の保存ロック
    history/*.json    保存ごとの変更前後
```

バックアップは `.authoring/` を含むフォルダ全体をコピーします。資料は閲覧専用で、コメント・章の復元履歴に資料や素材の内容は含めません。履歴はmanifestからつながる保存だけを表示します。保存ロックとrevisionは、このGUI・agent CLI同士の更新を調整します。複数ファイルを保存中の強制終了まで、フォルダ全体の原子的な更新を保証するものではありません。

章の保存ファイル名には、管理用の `manifest.json` と `scenario.json` を使えません。新規の章IDにも `manifest` と `scenario` を避けてください。既存の章が別のファイル名で保存されている場合は、その名前を保持します。

従来の `lomc edit/pack` などは互換用に残っていますが、共同編集では `lomc agent ...` を使ってください。別のエディターから直接JSONを変更した場合もGUIは変更を検出しますが、共通のロックや保存履歴は使われません。

`revision` は制作データと素材の内容から計算する保存版の識別子、`node` は章内のノードID、`parent` は返信先の親コメントIDです。不具合は[Issue](https://github.com/NEXTAltair/LoMScenarioKit/issues)に、操作とJSONエラーを添えて報告してください。
