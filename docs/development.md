# 開発メモ

[README に戻る](../README.md) · [シナリオ制作ガイド](scenario-guide.md)

アドオン本体を開発する人向けの内部構成、ビルド手順、過去の動作確認記録です。
パスは特記がなければリポジトリのルートを基準にしています。
動作確認記録は記載日の結果であり、現行版の再検証結果ではありません。

## 構成

| パス | 役割 |
|---|---|
| `src/LoMScenarioAddon.Patcher/` | プリロードパッチャー。起動前に `Mortal.Core.RelationshipStatType` へ ID を足す（メモリ上のみ。DLL ファイルは変更しない） |
| `src/LoMScenarioAddon/` | プラグイン本体 |
| `src/Shared/` | パッチャーとプラグインが共有する `scenario.json` の読み取り (`ScenarioManifest.cs`) と、依存ライブラリなしの JSON 読み取り (`MiniJson.cs`) |
| `sample/scenario/` | 試作キャラ `lsa_test`・`lsa_test2` の定義・テキスト・仮画像・登場シナリオ |
| `tools/make_placeholders.py` | 仮画像の生成スクリプト (Pillow) |

プラグイン本体の中身:

| ファイル | 役割 |
|---|---|
| `CharacterRegistry.cs` | 好感度データ (`RelationshipStat`) を作って `RelationshipData` に登録し、画像を割り当てる |
| `Patches.cs` | シナリオ読み込み (`LuaManager.ExecuteLuaScript`)、会話シーンのキャラ定義 (`StoryCharacterConfig.Get`) と立ち絵 (`StoryCharacterController.LoadPortrait*`)、社交パネルへのボタン複製 (`SocialStatPanel.Awake`)、公式に訳が無いキーだけテキストを埋める (`LeanLocalization.GetTranslationText`) |
| `PositionEvents.cs` | `flags` を本編のフラグ一覧に足し、`events` から本編と同じクラス (`PositionEventData` / `ConditionResultItem` / `StatCompareItem` / `StatValueReference`) を組み立てて、場所の抽選 (`CheckPointManager.Position`) の候補一覧に追加する |
| `StoryTextKeys.cs` | 既読判定のため、シナリオの `say("本文")` を読み込み時にキー付き (`GetStoryText`) へ変換し、本文をテキスト表に登録する |
| `Books.cs` | 秘笈。本編の秘笈を複製して `ItemDatabase.Books` に登録し、レベルデータと能力値加成 (「〜加成_秘笈」への変換データ) を組み立てる |
| `Talents.cs` | 天賦。本編の天賦を複製して `PlayerStatManagerData.Talents` に登録する |
| `SocialInheritance.cs` | 周回の引き継ぎ「繼承人際」に追加キャラの行を足す (`FateBonusPanel.InitSocial`) |
| `Duels.cs` | 決闘。決闘の場面の読み込み直後に本編の決闘設定と敵を複製して足し、敵の絵 (`CombatEnemyController.SetData`)・状態パネル (`CombatCharacterStatusUI.Setup`) を差し替える |
| `Endings.cs` | エンディング。本編の分岐シナリオ `ch8_6_3_2` の Switch 直前に自前ルートの判定を差し込む。講經堂のカード (`LibraryItemData`) を汗青書・奪命譜・結局CG に登録する |
| `StoryPictures.cs` | イベント CG。`StoryPicture.Show`/`Hide` で `pictures/*.png` を本編と同じ演出で出す |
| `StoryBackgrounds.cs` | 背景読み込みコルーチンの実行直前に自前画像をキャッシュへ補充する。言語変更でキャッシュが消えても再表示できる |
| `StoryResolutionScope.cs` | 1回の Lua 読み込みで行った自作シナリオの判定を Binarizer 互換処理と共有する。完了・例外時に破棄し、入れ子では親の判定に戻す |
| `ScenarioSprites.cs` | 追加キャラの社交パネル用立ち絵・イベント CG を本編の Addressables 読み込みへ供給する。画像は `ScenarioContent` が所有し、ハンドルの解放では破棄しない |
| `Visits.cs` | 場所選択時のシナリオ差し替え。`PositionController.OnPositionClick` の前後で、通常の行動に進んだときだけ行き先を指定したシナリオに差し替える |
| `ScenarioConditions.cs` | `conditions` を Lua の `checkpointmanager.Condition(名前)` で判定できるようにする |
| `StoryCharacters.cs` | 会話シーン用のキャラ定義 (`StoryCharacterData`) を作り、立ち絵を Addressables を通さずに渡す。逆向きの絵があれば向きに応じて差し替える (`PortraitController.Show` / `SetupPortrait`) |
| `ScenarioContent.cs` | `scenario/` の背景 (`backgrounds/*.png`、`StoryBackgrounds` から供給) ・テキスト (`text.ja.csv`)・画像 (PNG → Sprite)・シナリオ (`story/*.lua`、毎回読み直す) を読む |
| `Plugin.cs` | 起動処理と試作用のキー操作 |

## ビルド

必要なもの:

- .NET SDK 8（`dotnet --version` で確認。手元では 8.0.423）
- Python 3.9 以降（配布スクリプトは標準ライブラリのみ）
- 活俠傳のインストール済みファイル（現行64bit版）と BepInEx 6 Unity Mono / Windows x64
- 初回ビルド時の NuGet 接続（参照アセンブリと MoonSharp の取得）

ゲーム DLL はリポジトリに含めません。各自の導入先を指定して参照します。配布版は .NET Framework 4.7.2 向け、制作ツールの検査用ビルドは .NET 8 向けです。

Windows（PowerShell）:

```powershell
$env:LOM_GAME_DIR = 'D:\SteamLibrary\steamapps\common\LegendOfMortal'
python tools/build_release.py
```

Linux / WSL（.NET SDK を PATH に設定済みの場合）:

```bash
export LOM_GAME_DIR='/path/to/LegendOfMortal'
python3 tools/build_release.py
```

`--game-dir <場所>` でも指定できます。既存の `tools/build_release.sh` は同じ処理への入口で、PATH に dotnet がなければ `~/.dotnet/dotnet` を探します。

ビルドとサンプル検査がすべて成功すると `dist/package/` と `dist/LoMScenarioAddon-<版>.zip` を更新します。版は `Plugin.cs` の `BepInPlugin` から取ります。ビルド・検査が失敗した場合、以前の配布物は残りますが新しい版として更新されません。配布時は必ずコマンドの成功を確認してください。

公開版は対応する日本語化 MOD の系列に合わせ、Git タグ・`BepInPlugin`・ZIP 名を `2.5.00` の形式に揃えます。先頭の `2.5` は対応する日本語化 MOD v2.5、末尾の `00` はアドオンの更新番号です。同じ系列向けの更新は `2.5.01`、`2.5.02` と増やし、v2.6 対応時は `2.6.00` に戻します。日本語化 MOD の runtime の末尾（確認環境の `2.5.52` の `52`）には連動させません。末尾は最低2桁で表記します。BepInEx のログでは数値に正規化されて `2.5.0` と表示される場合があります。

`--install` を付けると Windows / WSL でゲーム終了を確認し、既存のアドオン・制作ツール・文書をゲーム内の `LoMScenarioAddon-backups/<日時>/` へ退避して導入します。プロセス一覧を取得できない環境では導入を中止します。サンプルが導入されるため、自作シナリオは必要に応じて退避先から戻してください。

配布用の並び（ゲームフォルダからの相対パス）:

```text
BepInEx/patchers/LoMScenarioAddon/LoMScenarioAddon.Patcher.dll
BepInEx/plugins/LoMScenarioAddon/LoMScenarioAddon.dll
BepInEx/plugins/LoMScenarioAddon/LICENSE
BepInEx/plugins/LoMScenarioAddon/LICENSE-GAME-EXCEPTION.txt
BepInEx/plugins/LoMScenarioAddon/THIRD-PARTY-NOTICES.md
BepInEx/plugins/LoMScenarioAddon/licenses/
BepInEx/plugins/LoMScenarioAddon/Source/
BepInEx/plugins/LoMScenarioAddon/scenario/
LoMScenarioTool/
LoMScenarioAddon-docs/             README・制作ガイド・ライセンス
```

制作ツールだけを検査する場合、ゲーム DLL は不要です。

```bash
dotnet run --project src/LoMScenarioTool -c Release -f net8.0 -- check sample/scenario --no-pause
python3 -m unittest discover -s tests -v
```

背景読み込みの回帰テストもゲーム DLL なしで実行できます。Unity の表示確認とは別に、キャッシュ消去とコルーチンの実行順を再現します。

```bash
dotnet run --project tests/BackgroundLoading -c Release
```

Lua 判定の共有は、ゲーム DLL なしで実行できる回帰テストで確認します。

```bash
dotnet run --project tests/StoryResolution -c Release
```

HarmonyX による割り込みと `__state` の後片付けは、導入済みの BepInEx の DLL を参照する別のテストで確認できます。`LOM_GAME_DIR` を設定してビルドし、生成した exe を Windows の .NET Framework 4.7.2 以降で実行してください。ゲーム本体は起動しません。

```powershell
dotnet build tests/StoryResolution.Harmony -c Release
.\tests\StoryResolution.Harmony\bin\Release\StoryResolution.Harmony.exe
```

仮画像・仮音声は `sample/scenario/` に用意済みです。再生成はビルドには不要です。画像生成には Pillow と日本語フォントが必要で、`LOM_PLACEHOLDER_FONT` でフォントのファイルパスを指定できます。音声生成は Python 標準ライブラリのみを使います。

確認用のキー操作は [シナリオ制作ガイド](scenario-guide.md#確認用のキー操作) を参照してください。公開前の検証項目は [配布確認](release-checks.md) にまとめています。

## Lua 判定の共有（2.5.01）

従来は `ExecuteLuaScript` のアドオン側の処理と、Binarizer の二重実行を防ぐ処理が、それぞれ自作 Lua の存在を確認していた。2.5.01 では1回の `ExecuteLuaScript` 呼び出しの間だけ、その判定結果を共有する。通常の公式・追加シナリオでは存在確認が2回から1回になる。

実行順を保つため2か所の割り込みは維持し、対象の `LuaManager` とシナリオ名が一致する場合だけ結果を再利用する。訪問から本編への復帰など未判定の経路や、別のパッチで名前が変わった場合は、従来の判定に戻す。Harmony の finalizer で結果を破棄し、例外時や本編処理を省略した場合も次の読み込みへ持ち越さない。入れ子の呼び出しが終わると親の判定へ戻る。

Lua 本文は従来どおり開始時にファイルから読む。次の読み込みでは再判定するため、ファイルの追加・編集・削除の反映は変わらない。シナリオフォルダを削除した後の継続動作を保証する変更ではない。Binarizer が見つからない環境では共有用オブジェクトを作らない。実ゲームの読み込み時間は未測定であり、体感速度の改善量は未確認。

## 日本語化 MOD v2.5 環境への対応 (2026-10-16)

導入済み環境はゲーム Steam Build ID `25957922`、Windows x64、Unity `2020.3.49f1`、BepInEx `6.0.0-be.692`（commit `851521c`）、日本語化 MOD runtime `2.5.52` / 翻訳 `2026.10.15.378`。`Mortal.exe` の形式と起動ログで64bit化を確認した。過去の32bit環境（Build ID `20337760`）とは区別する。

0.14.0 の起動ログでは `Language = auto` が `ja` と判定される一方、`RelationshipStat._avatar` が存在しないためキャラ登録が中断していた。後続のフラグ・天賦登録に進めず、登場・訪問イベントの無効化と登録の毎秒再試行が発生していた。日本語化 MOD の検出名 `LOM_JP_StringVault` は維持されており、この例外は現行ゲームの画像取得方式との不整合による。

0.14.1 ではキャラの立ち絵を `_avatarAsset`（`AssetReferenceCache<Sprite>`）へ登録する。画像がある場合は起動中だけの GUID を発行し、Addressables の locator / provider から `avatar.png` の共有 Sprite を返す。社交パネルは本編の `AvatarReference.LoadSync()` を使う。画像が無い場合は空のキャッシュを渡し、null 参照による中断を防ぐ。イベント CG と画像の供給処理を共通化し、解放・再読み込み時も共有画像は破棄しない。

0.14.1 の実機ログでキャラ2人・フラグ4件・天賦2件・秘笈1件の登録完了、社交パネルへの2ボタン追加、訪問シナリオ開始まで確認した。ただし Save60 から裏山へ進むと黒画面で停止し、`StoryViewImage.LoadAsset` が null の Addressables キーを渡す例外が発生した。

現行ゲームの背景キャッシュは言語変更時に消去される。Awake 後に自前画像を一度だけ入れる方式では、その後の読み込みに使えない。0.14.2 では `LoadAsset` のコルーチンを包み、最初の `MoveNext` の直前に要求された自前背景だけを補充する。本編のコルーチンをその場で進めるため、コルーチン取得から実行までの間にキャッシュが消えても対応できる。公式背景の読み込み・待機・例外は本編へ委ねる。

現行 DLL のフィールド型、社交パネルの呼び出し経路、`AssetReference` の GUID 検証、ロードと解放の処理を確認した。v2.4 の過去の動作確認は下記に残すが、現行アドオンを旧ゲームへ導入した場合の互換性は未検証。

検証・導入状況:

- 現行ゲーム DLL を参照したパッチャー・プラグイン・Windows 向け制作ツールと .NET 8 検査ツールの Release ビルド: 警告0・エラー0。
- サンプルシナリオの検査: 警告0・エラー0。配布スクリプトの既存テスト8件も成功。
- 背景の回帰テスト: コルーチン実行前のキャッシュ消去、再読み込み、公式画像・待機オブジェクトの維持、例外伝播・中断時の破棄、未設定時の委譲を確認。
- `LoMScenarioAddon-0.14.2.zip` の生成成功。
- ゲーム終了を確認して0.14.2のプラグイン DLL のみ差し替え、旧 DLL と修正前ログを `LoMScenarioAddon-backups/` に退避した。導入先とビルド済み DLL の SHA-256 一致、既存シナリオ・設定30ファイルの不変を確認。
- 0.14.2 導入後、利用者が Save60 から裏山の訪問シナリオの読み込み成功と BGM の切り替えを確認した。13:30:42 起動のログではキャラ2人・フラグ4件・天賦2件・秘笈1件、社交パネルの2ボタン、`lsa_test_visit`、背景 `lsa_camp` と立ち絵の読み込みを確認。BepInEx の警告・エラー、Player.log の例外はいずれも0件だった。
- 公開版2.5.00は、実機確認済み0.14.2からプラグインの版番号のみを変更したもの。公開版の再ビルド・配布確認は [配布確認](release-checks.md#2026-10-16-の確認状況2500) に記録する。

実機での追加確認項目（上記の確認範囲以外）:

- 社交パネルの立ち絵、公式キャラとの切り替え、パネルを閉じて開き直したときの画像表示。
- 裏山の登場イベント、好感度・フラグのセーブとロード。
- イベント CG の表示・非表示・繰り返し表示とシーン遷移。

## 確認済み (2026-09-28, 日本語化 MOD v2.4 環境)

- キャラ一覧の拡張 (`IsDefined` / `TryParseByStringValue` / `GetStringValue` がすべて通る)
- Lua からの好感度増減、セーブ・ロードの往復 (`RelationshipSave.Key = "lsa_test"`)
- 社交パネルの33個目のボタン、顔アイコン・立ち絵・名前・肩書き・列伝の表示
- 試作 ID の無い古いセーブの読み込み
- 2人のキャラの同時追加 (社交パネル34人、掛け合い、2人分の紹介カード)
- 左右非対称の立ち絵: 右向きの絵があれば差し替え、無ければ自動反転
- 場所イベント: 裏山の修練 (`Back_Free_001`) の抽選に追加 → 当選して試作シナリオ → 2回目は本編のイベント (1回きり)。セーブ・ロード後も保たれる
- 自前のフラグの Lua からの設定とセーブ
- 訪問: 出会い済みで裏山をクリック → 雑談メニュー。好感度による選択肢の出し分け (`conditions`)、本来の行動 (`back_001`) への移行
- 周回の引き継ぎ: 新規ゲームの「繼承人際」に試作キャラ2人が並び、天命点での加算が反映される
- 天賦: 決闘に勝って `AddTalent` → 取得表示・人物画面・説明、ロード後も保持。決闘の敵に本編の天賦 (防御強化 Lv3) を持たせる
- イベント CG: `ShowPicture` (訪問の「旅の話」) と `ShowBigPicture` (エンディング) で自前の絵、フェード・消去も本編どおり
- CG 画面: `cgpanel.Open` で絵と縦書きの本文 (左の余白に並ぶ)、結局CG の一覧に記録
- 死亡エンディング: `ChangeScene("GameOver", ...)` で題名・本文 → タイトル、奪命譜に記録
- 背景: 訪問シナリオの背景を自前の `lsa_camp` に → 画面いっぱいに表示、立ち絵・会話も問題なし
- 秘笈: 入手 → 所持品・修練の画面 → 3段のレベルアップ (能力値・天賦) → 能力値加成の欄と実際の能力値、ロード後も保持
- 言語: `Language = zh-CN` で社交パネルの名前・肩書きが簡体字に切り替わり、簡体字に無いキー (列伝) は既定の日本語で出る。`auto` では日本語化 MOD を検出して `ja`
- 決闘: 訪問から手合わせ → 敵の4枚の絵・状態パネル・台詞・名前の差し替え、勝敗フラグ、戻り先での分岐。`enemy` / `rates` の反映 (口撃だけの重みにすると口撃ばかりになり、怒り状態では攻撃に切り替わる)
- エンディング: ルートフラグを立てて `ch8_6_3_2` を流す → 共通部分の後で自前エンディングへ → エンディングカード (挿絵・題名・本文) → 列伝の保存と列伝本文の表示 → タイトル
- 曲・効果音: 訪問で自前の曲がループ再生、効果音、本編の曲への切替、音量スライダーの反映、決闘の曲の指定、自由行動に戻ると本編の曲
- 自由行動からの登場シナリオ: 背景、立ち絵、表情差分 (`portraits/smile.png`)、名前入りの台詞、初登場の紹介カード、好感度の増減表示、自由行動への復帰

## 未着手

- 集団戦 (Battle) への追加キャラの登場 (敵が門派単位のアニメーション部品で組まれているため見送り)
- 圧縮音声 (ogg など)。今は wav を無圧縮で埋め込む (Wwise の ADPCM なら 1/4 だが符号化器が要る)

配布スクリプトは、そのビルドのプロジェクトソースと MoonSharp 2.0.0 のソースを ZIP に同梱する。MoonSharp ソースの取得にはネットワーク接続が必要。ライセンスの適用範囲は [ライセンスと再配布](licensing.md) を参照。
