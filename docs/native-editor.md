# シナリオをGUIとエージェントで編集する

LoMScenarioKitの制作エディターは、lom_modkitの既存Rust GUIを基盤にしています。章一覧、ノードの入力フォーム、画面プレビュー、フロー図を使いながら、資料・判定・コメント・保存履歴を確認できます。GUIとJSON CLIは同じ制作フォルダを読み書きします。

初回は「起動する」と「GUIで編集する」を読んでください。エージェントとの共同編集や配布時は、該当する節を参照してください。

1. 制作データは専用のフォルダに保存します。
2. GUIの編集は保存操作で確定します。CLIの更新には、直前に取得した `revision` が必要です。
3. 配布時は新しいフォルダへ書き出し、LoMScenarioToolで検査します。

## 起動する

ソースから起動する場合はRust 1.89以降、Cargo、Python 3.12以降、C++コンパイラーが必要です。確認環境はRust 1.93.1、Python 3.12、Linux/X11です。LinuxではOpenGLとウィンドウ環境も必要です。音楽変換ツールのビルドには通常`c++`を使い、`CXX`で変更できます。WindowsではVisual Studioの開発者コマンドプロンプトから`cl`を使います。表示用のNoto Sans CJKは同梱しているため、追加のフォント設定は不要です。Windows向けのゲーム導入作業はエディターの起動には不要です。

Linux・macOS・WSLでは、リポジトリのルートで実行します。

```sh
./editor
```

初回は素材抽出ツールの依存をダウンロードし、専用のPython環境で実行ファイルを作ります。処理中は準備メッセージが出ます。準備済みなら再ビルドせず起動します。環境と成果物はCargoの`target/`内に保存し、システムのPython環境にはインストールしません。Pythonの場所を指定する場合は `EXTRACTOR_PYTHON=/path/to/python3.12 ./editor` とします。失敗時の詳細は`target/asset-extractor-build/build.log`に残ります。

WSLではX11とCPU描画を自動で選びます。`WAYLAND_DISPLAY` / `WAYLAND_SOCKET` を外し、未指定なら `LIBGL_ALWAYS_SOFTWARE=true`、`GALLIUM_DRIVER=llvmpipe`、`XCURSOR_SIZE=24` を設定します。WSL以外の描画設定は変更しません。Rustのビルドは必要に応じて自動で行います。ポインターの大きさを変える場合は `XCURSOR_SIZE=32 ./editor` のように指定できます。

表示言語は設定（`Ctrl+,`、macOSは`⌘,`）で、日本語・簡体字・繁体字・韓国語・英語に切り替えられます。切り替えはその場で反映され、次回起動時にも保持されます。台詞・作品名・人物名・コメント本文は書き換えません。英語表示で訳が用意されていないゲーム固有名は原表記を使います。表示言語の追加によって、ゲーム側のシナリオ翻訳対応言語が増えるわけではありません。

章・選択肢・フラグ・分岐を含むサンプルも開けます。自作の作品にはサンプルのコピーを使ってください。

```sh
./editor tools/modkit/samples/scenario-addon
./editor "/path/to/自分の作品"
```

別のディレクトリからスクリプトのパスを指定して呼ぶ場合も、制作フォルダの相対パスは呼び出し元が基準です。WindowsのPowerShellなど、shを使わない環境では次のコマンドで起動します。

```sh
cargo run --locked --manifest-path tools/modkit/Cargo.toml -p lom-editor
```

`cargo run`を直接使う場合は、`python tools/build_editor.py --prepare-extractor --print-target`で抽出ツールを準備し、表示されたターゲットを`cargo run`の`--target`に指定してください。

エディターと抽出ツールを同じ環境で動かすため、起動・配布スクリプトはRustコンパイラーのホストターゲットを明示します。Cargoの`build.target`や`CARGO_BUILD_TARGET`より、この指定を優先します。両方を`target/<ホストターゲット>/<debugまたはrelease>/`へ配置します。`CARGO_TARGET_DIR`などの出力先設定は引き継ぎます。

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

## ゲームの立ち絵・背景をプレビューする

1. 「ファイル → ゲームからプレビュー素材を抽出…」を開きます。
2. 初回は`Mortal_Data`を含むゲームのインストールフォルダーを選びます。WSLではWindowsのドライブを`/mnt/c/`などから開きます。
3. 抽出の進捗が表示されます。ウィンドウを閉じて編集を続けることも、抽出をキャンセルすることもできます。
4. 完了すると素材が自動で読み込まれ、次回起動にも引き継がれます。「人物登場」で人物・表情、「公式背景切替」で背景を選ぶと、右側で表示を確認できます。

ゲームフォルダーはエディター内の選択画面で指定します。フォルダー一覧をクリックして開くか、「フォルダーのパス」に場所を入力し、「このフォルダーを使う」で確定します。`Mortal_Data`自体を選んだ場合は、親のゲームフォルダーを使います。変更する場合は「素材管理」または「ゲームとMOD」の「ゲームフォルダーを変更」を押してください。キャンセルや入力エラーでは現在の設定を保持します。この選択画面は、OS側のファイル選択サービスがないWSL環境でも使えます。

画像の一括抽出は立ち絵・表情・背景が対象で、原則長辺1024pxに縮小します。音楽は再生時に曲ごとに取り出すため、画像を抽出し直す必要はありません。ゲーム内では素材IDを使って本体の素材を読み込みます。

画像はWindowsでは`%APPDATA%/LoMScenarioKit/game-previews/`、Linux・WSLでは`~/.local/share/LoMScenarioKit/game-previews/`、macOSでは`~/Library/Application Support/LoMScenarioKit/game-previews/`へ保存します。ゲーム本体は読み取り専用で扱い、抽出画像を作品や配布物へ自動で追加しません。以前の素材フォルダーも上書きしません。

抽出済みの場合は「ファイル → プレビュー素材フォルダーを選択…」で、`preview_map.json`と`assets/`を含むフォルダーを指定できます。

## プレビューの立ち絵と音楽

人物の台詞では「人物登場」がなくても話者の立ち絵を表示します。登場済みなら位置・向きを保って表情を変えます。語り・中央表示の台詞では人物を追加しません。

右下の再生ボタンを押すと、「音楽」ノードで指定したBGMを再生します。初回は設定済みのゲームフォルダーから必要な曲だけを取り出してOGGへ変換し、準備が終わってから進行します。曲は`LoMScenarioKit/rust/preview-music/`に保存し、次回から再利用します。ゲームの更新や変換ツールの変更時には作り直します。ゲーム本体や制作データは変更しません。

再生にはFFplayが必要です。PATHに置くか、`LOM_FFPLAY=/path/to/ffplay ./editor`のように指定してください。起動できない場合はプレビューを止めてエラーを表示します。プロジェクトに取り込んだOGG/WAVの音楽も再生できます。

一時停止では音も止まり、再開すると停止位置付近から再生します。曲の終了後は先頭から繰り返します。音楽の停止・フェードアウト、章や選択ノードの切り替え、プレビュー終了、作品の開き直し、エディター終了でも再生を停止します。任意のノードから再生した場合は、そのノードに至る経路の曲を先頭から再生します。効果音・台詞の音声やゲーム内のミキサー設定は再現していません。

## エディターの配布フォルダーを作る

配布先と同じOS・CPUで、Python 3.12以降を使って実行します。

```sh
python3.12 tools/build_editor.py --output-dir dist/LoMScenarioKit-editor
```

Windowsでは`py -3.12 tools/build_editor.py --output-dir dist/LoMScenarioKit-editor`です。Rustのreleaseビルドと抽出ツールの準備を行い、エディター、JSON CLI、抽出ツール、ライセンス、文書、再ビルド用ソースZIPを新しいフォルダーへまとめます。既存の出力先は上書きしません。ビルドしたOSと互換性のある環境で使用してください。

利用者はフォルダー内の`lom-editor`（Windowsでは`lom-editor.exe`）を起動します。`asset-extractor/`を含むフォルダー全体を渡してください。配布版の実行にはPython・Cargoのインストールは不要です。ゲーム本体や抽出済み画像は配布フォルダーに含めません。

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
