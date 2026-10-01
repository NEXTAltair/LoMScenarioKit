# 第三者のソフトウェア

## MoonSharp 2.0.0

LoMScenarioTool の Lua 構文検査に使用し、配布 ZIP に `MoonSharp.Interpreter.dll` を同梱しています。
同梱版のソースは配布 ZIP 内の `BepInEx/plugins/LoMScenarioAddon/Source/MoonSharp-2.0.0-source.zip` に保存します。対象は Interpreter のソース・ビルド定義と原ライセンスで、上流のサンプルや依存 DLL は含めません（タグ `v2.0.0.0`、コミット `3154416e535ab96c0d52bf12e3e472985a1532f4`）。
著作権表示と利用条件の全文は [licenses/MoonSharp.txt](licenses/MoonSharp.txt) を参照してください。

- プロジェクト: https://www.moonsharp.org/
- 同梱版のライセンス原文: https://github.com/moonsharp-devs/moonsharp/blob/v2.0.0.0/LICENSE

## 実行環境・ゲーム本体

BepInEx、Harmony、Mono.Cecil、およびゲームに含まれる Unity・Wwise・その他の DLL は、導入先のファイルを参照します。これらの DLL とゲームの画像・シナリオは本プロジェクトの配布 ZIP に同梱しません。それぞれの利用条件が適用されます。

## 調査資料とサンプル

ゲームから書き出した内部データ・逆コンパイル資料・調査用に取得した第三者 MOD は参照専用とし、リポジトリと配布 ZIP に含めません。Wiki や他プロジェクトのライセンスを、ゲーム本体や別の MOD の再配布許可として扱いません。

同梱の仮画像・仮音声には `tools/make_*` の生成スクリプトがあります。画像生成で参照するフォントファイル自体は同梱しません。独自シナリオを配布する場合、追加した画像・音声・文章の出所と利用条件は制作者が別途確認・記載してください。

## 参考にした公開 MOD と解析ツール

- [Binarizer / Plugin-DD2](https://github.com/Binarizer/Plugin-DD2/tree/Mortal): Lua 実行フックと既存 MOD との互換処理の参考。確認版の MIT 表示を [licenses/Plugin-DD2.txt](licenses/Plugin-DD2.txt) に保存しています。
- [mohui666 / lom_modkit](https://github.com/mohui666/lom_modkit): 追加シナリオの実装・比較資料。`MiniJson.cs` の短い JSON エスケープ処理には一致がありますが、パーサー全体の転用を意味しません。参考元の表示を保持するため、確認版の MIT 表示を [licenses/lom_modkit.txt](licenses/lom_modkit.txt) に保存しています。
- [bnnm / wwiser](https://github.com/bnnm/wwiser): ゲームの音声バンク形式の調査に使用。ツールのコードやゲームのバンクファイルは同梱しません。

立ち絵の登録手順、ゲーム API、音声バンクの形式・定数など、本編を参考にした互換処理を含みます。詳しい対象・比較結果・限界は [由来の監査レポート](docs/provenance-audit.md) に記載しています。これらの記載は、ゲーム本体や参考 MOD の素材への再配布許諾を与えるものではありません。
