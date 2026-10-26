# 制作GUI・エージェント用CLI

`tools/authoring/` は、台本を書き、添削し、分岐を確認するための制作ツールです。ブラウザとエージェントが同じ原稿・コメント・編集履歴を使います。Python 3.10以降とブラウザで動き、制作ツール自体に追加のPythonパッケージやゲームのDLLは不要です。

現在の出力は原稿JSON・Markdown・共有データのバックアップです。ゲーム用のLuaや`scenario.json`を生成する機能は含みません。実行用データの作成・検査・配布は[シナリオ制作ガイド](scenario-guide.md)を参照してください。選択肢の条件文と演出の本文は制作メモとして保存し、条件の成否やゲームでの実行可否を推測しません。

## 起動

ソースを取得し、リポジトリのルートで実行します。`../my-story` はゲーム用の `scenario/` とは別の制作フォルダです。

```sh
python3 tools/authoring/author.py --project ../my-story init --title "私の追加シナリオ"
python3 tools/authoring/author.py --project ../my-story serve
```

2つ目のコマンドが返すJSONの`data.url`をブラウザで開きます。既定は`http://127.0.0.1:8765/`です。ポートが使用中なら`serve --port 0`で空きポートを選べます。サーバーはターミナルで動き続け、Ctrl+Cで停止します。停止しても保存した原稿は残ります。

```text
my-story/
  project.json                  作品名・ルート名・話者候補
  outline/                      設定資料のMarkdown
  .authoring/scripts.sqlite3     原稿・判定・コメント・履歴・表記ルール
```

`project.json`の`title`、`routes`、`speakers`を編集して画面を再読み込みすると、表示名や候補が変わります。既存の台本が持つルートIDは、設定から削除しても書き換えません。

```json
{
  "format": "lom-authoring-project",
  "version": 1,
  "title": "私の追加シナリオ",
  "routes": {"common": "共通", "north": "北方編"},
  "speakers": ["旅人", "門番"]
}
```

## GUIでできること

- 台詞・地の文・演出・選択肢・場面目印・移動・終了を行カードで編集し、選択肢を選んで読み合わせる。
- 「良い・要修正・保留／別案・ボツ」の判定を付け、台本一覧を絞り込む。執筆状況の「未着手・執筆中・推敲中・完了」とは別に保存する。
- イベント内チャートで選択肢・合流・戻り先を確認し、箱を押して該当行へ移動する。欠落した移動先と先頭から未接続の行を表示する。
- 台本全体や行に添削コメントを付け、返信・解決・再開を記録する。行を削除してもコメントの引用を残す。
- 編集者と差分を履歴で確認し、選んだ台本を以前の状態へ戻す。復元も新しい履歴として残す。
- コメントから表記ルールや通読時の注意事項を登録する。エージェントの提案は確認待ちになり、ユーザーが採用する。
- 「設定資料」から`outline/`内のMarkdownを検索し、見出しから移動する。資料は読み取り専用で、元のMarkdownを編集して更新する。本文は安全なテキストとして表示し、HTMLやMermaidを実行しない。

チャートは台本内の接続を表示します。作品全体のイベント発生順・ゲームの条件を評価する図は、まだ提供していません。

## エージェントの操作

CLIは成功・入力エラー・保存競合を含めてJSONを標準出力へ返します。`--json`も指定できますが、既定でJSONです。HTTPのアクセスログは標準エラー出力に出ます。

```sh
python3 tools/authoring/author.py commands
python3 tools/authoring/author.py describe --command save
python3 tools/authoring/author.py --project ../my-story show
python3 tools/authoring/author.py --project ../my-story new --title "最初の出会い" --revision 0
```

`commands`は用途と書き込みの有無、`describe`は引数の型・必須項目・例・実行条件を返します。台本を作ると、次の取得操作が`next_actions`に入ります。応答の形式は共通です。

```json
{
  "schema_version": 1,
  "command": "check",
  "ok": false,
  "summary": "検査完了",
  "data": {"reminders": [], "gameValidated": false},
  "issues": [{"code": "TARGET_NOT_FOUND", "severity": "error", "eventUid": "e1", "rowId": "r2", "field": "target", "message": "移動先の場面目印がありません。"}],
  "next_actions": []
}
```

実際の`next_actions`には、問題の台本を取得する引数配列`argv`、その`reason`、書き込みの有無`mutates`を返します。操作候補を返すだけで、自動実行はしません。

原稿の編集手順は次のとおりです。

1. `checkout --event <uid>`の応答をJSONファイルへ保存する。
2. `data.reviewContext`のルール・注意事項・指摘を読む。
3. `data.event`を編集する。`uid`、既存の行ID・選択肢ID、`revision`を保つ。
4. `save --input edit.json --dry-run`で差分と表記ルールを確認する。
5. `save --input edit.json --message "変更理由"`で保存し、`check`で台本を検査する。

応答全体と、その中の`data`だけのどちらも`save`に渡せます。標準入力から渡す場合は`--input -`を指定します。`--name`は履歴の表示名で、CLIからの変更は常にエージェントとして記録します。

古い更新番号での保存は`REVISION_CONFLICT`で拒否します。最新版を取得し、変更内容を比較して編集をやり直してください。番号だけを差し替えると他の編集を失うため、行わないでください。

| 操作 | 用途 |
|---|---|
| `show` / `checkout` | 全体または台本の取得 |
| `new` / `save` / `import` | 作成・保存・旧形式の取り込み |
| `verdict` | 判定タグの設定 |
| `comments` / `comment` / `resolve` | 添削・返信・解決／再開 |
| `history` / `entry` / `restore` | 履歴・差分・復元 |
| `rules` / `propose-rule` | 表記ルールの確認・提案 |
| `check` / `chart` / `outline` | 検査・接続図データ・資料検索 |
| `export` | 原稿JSONまたは共有DBのバックアップ |

終了コードは成功`0`、台本の検査エラー`1`、入力・ファイル操作エラー`2`、保存競合`3`、確定した表記ルールへの違反`4`です。文体の意味やゲーム動作まで自動で保証する検査ではありません。

## AC側から原稿を移す

AC側の「書き出す」で取得した`lom-event-scripts`形式1の原稿JSONを、GUIの「読み込む」またはCLIの`import --input <file> --revision <現在の番号>`で取り込めます。台本・行・選択肢のIDとルートIDを保持し、未設定の判定タグは「未判定」にします。取り込みは原稿全体の置き換えなので、`--dry-run`で変更範囲を確認できます。

原稿JSONにはコメント・履歴・表記ルールは入りません。それらも移す場合はAC側で共有SQLiteのバックアップを取り、制作サーバーを停止した状態で、新しい制作プロジェクトの`.authoring/scripts.sqlite3`へコピーします。元のDBは残してください。アウトラインは必要なMarkdownを新しい`outline/`へコピーします。ACビューアのブラウザ内コメント・判定はSQLiteとは別で、この版の自動移行対象には含めていません。

## 検証と取込元

```sh
python3 -m unittest discover -s tools/authoring/tests -p 'test_*.py'
node --test tools/authoring/tests/script-editor.test.cjs
uv run --with playwright python tools/authoring/tests/browser.py
```

ブラウザ試験のみPlaywrightとChromiumが必要です。既存のChromiumを使う場合は`AUTHORING_CHROMIUM`に実行ファイルを指定します。試験データは一時フォルダに作成します。

取込元は`addonLoMScenario-ac`の2026-10-26時点のローカル実装です。台本のモデル、編集画面、チャート、共有保存、コメント、表記ルールを改変して使用しています。原稿や制作DBは同梱していません。元ファイルのコミットと取り込み時の内容ハッシュは[upstream.json](../tools/authoring/upstream.json)、設計判断は[ADR](adr/0001-authoring-workspace.md)に記録しています。
