# LoMScenarioKit native editor

[lom_modkit](https://github.com/mohui666/lom_modkit) の既存Rustエディターを基盤とする制作GUI・CLIです。章一覧・フォーム・プレビュー・フロー図を保持し、資料・添削・履歴とLoMScenarioAddon向け書き出しを追加しています。

起動・共同編集・書き出しは [制作エディターのガイド](../../docs/native-editor.md) を参照してください。

```sh
cargo run --locked -p lom-editor
cargo run --locked -p lomc -- agent commands
cargo test --locked --workspace
```

このディレクトリ内で実行します。初心者向けの `samples/first-scene` は、エディターの「ファイル → サンプルから始める」から編集用コピーを作れます。[使い方](samples/first-scene/outline/guide.md)に沿って、立ち絵・BGM・選択肢を試してください。`samples/scenario-addon` はフラグと条件分岐を使うAddon向けの制作サンプルです。他のsamplesは上流の互換テスト用です。

元のコードはMITです（[LICENSE](LICENSE)）。LoMScenarioKitの追加・改変部分には、リポジトリのGPL-3.0-onlyとゲーム連携の追加許諾を適用します。元ファイルの出所・取得時ハッシュは [UPSTREAM.json](UPSTREAM.json)、変更内容は [ADR](../../docs/adr/001-native-authoring.md) に記録しています。Unity Doorstopのバイナリとソースには、同梱のLGPL-2.1が適用されます。
