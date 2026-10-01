# イベント画像の読み込み差し替え調査

2026-10-01。対象は `StoryPictures.cs` と `Patches.cs` のイベント画像表示。調査後、同日にプラグインの実装をこの方式へ置き換えた。その後、ゲームへ導入し、訪問・エンディング経路の動作報告とログを確認した。

## 結論

本編の `StoryPicture.Show` / `Hide` をそのまま実行し、Addressables に自前画像の供給処理を追加する方式を実装した。ゲーム同梱 DLL を参照したプラグインの Release ビルドは警告0・エラー0。新版 DLL の導入後、ユーザーから動作成功の報告があり、訪問・エンディングの実行ログにも画像関連エラーはなかった。下記の全チェック項目の確認完了を意味するものではない。

従来の表示・非表示コルーチンの複製、演出用 private フィールドへのアクセス、自前表示中インスタンスの集合を削除した。

## 確認した呼び出し経路

- `StoryMainUI.ShowPicture` / `ShowBigPicture` / `ShowItemPicture` は、それぞれの `StoryPicture.Show` を呼ぶ。対応する非表示も `StoryPicture.Hide` に集まる。
- 本編の `Show` は `pic_` を付けたキーで `Addressables.LoadAssetAsync<Sprite>` を呼び、取得したハンドルの結果を使って演出する。ロード失敗時には本編の既定画像を使う。
- 本編の `Hide` はフェード後に取得ハンドルを解放する。したがって画像フィールドだけを差し替える方法では、ハンドルの取得・解放との整合が残る。
- 現在の `ScenarioContent.GetPicture` は Sprite をキャッシュしている。Addressables 側からこのキャッシュの Sprite を返せば、画像ファイルの読み込みを二重実装する必要はない。

本編の解析済みコードはローカルで確認し、この文書には転載していない。

## 実装した構成

1. 自前画像用の locator と provider を一度登録する。locator は専用の画像キーだけを扱い、provider は `ScenarioContent` が保持する Sprite を返す。
2. `StoryPicture.Show` の Prefix は、自前画像が取得できたときだけ引数 `key` を専用キーへ変更する。元のメソッドは実行させ、戻り値のコルーチンは置き換えない。
3. 例えば元のキー `lsa_pic_fire` を内部的に `LoMScenarioAddon/pictures/lsa_pic_fire` へ変え、本編が要求する `pic_LoMScenarioAddon/pictures/lsa_pic_fire` を locator に登録する。Lua 側の記述は変更不要。
4. 自前画像がない場合はキーを変更せず、本編の読み込みへ進む。既存の画像キーと同名の自前画像も、専用キーへの変換で区別できる。
5. provider は `ProvideHandle.Complete<Sprite>` で結果を返す。本編が得るのは通常の取得ハンドルなので、`Hide` の解放処理をそのまま使う。
6. provider の `Release` ではキャッシュ所有の Sprite / Texture2D を破棄しない。画像本体の寿命は `ScenarioContent` 側に統一する。これはハンドルを解放しないという意味ではない。

同じ `pic_<キー>` を locator に追加するだけの方式は採らない。ゲーム同梱版では locator は追加順に探索され、読み込み可能な最初の候補が採用されるため、登録順によって公式画像が選ばれる。専用キーならその順序に依存しない。

## 変更範囲

- `StoryPictures.cs`: 演出の再実装を削除し、専用キーの登録と画像供給へ置き換える。
- `Patches.cs`: Show の Prefix を引数変更だけにする。Hide の Prefix を削除する。
- `Plugin.cs` は変更せず、最初に自前画像を表示するときに `StoryPictures` から一度だけ登録する。provider / locator は `ScenarioContent` と同じくゲーム起動中は保持し、シーン切り替えでは解除しない。Addressables の初期化前後の挙動は実機確認に残す。
- `LoMScenarioAddon.csproj`: ゲーム同梱の `Unity.Addressables.dll` と `Unity.ResourceManager.dll` を `Private=false` で参照する。配布物には同梱しない。

provider / locator の重複登録を防ぎ、位置情報もキーごとに再利用する。解除は未解放のハンドルがなくなった後に行う。現在の画像キャッシュには明示的な一括破棄処理がないため、シナリオのホットリロード対応まで今回の置き換えに含めない。

## 検証済みと未検証

確認済み:

- 置き換え後のプラグインの Release ビルド成功（警告0・エラー0）。

- ゲーム同梱 DLL の `AddResourceLocator`、`RemoveResourceLocator`、`ResourceLocationMap`、`ResourceLocationBase`、`ResourceProviderBase`、`ProvideHandle.Complete` の存在。
- locator の探索順、provider の基本 API、ハンドルの解放経路。
- .NET Framework 4.7.2 の参照アセンブリとゲーム同梱 DLL を使った最小 provider / locator 登録コードのコンパイル成功。試験コードと DLL はローカルの調査キャッシュに置き、ゲームには導入していない。

実装後にゲーム上で確認する項目:

- 通常・全画面・アイテムの各画像で、フェード、移動、サイズ調整が維持されること。
- 自前画像と公式画像を交互に表示し、同名キーでも選択が正しいこと。
- 同じ画像の繰り返し表示、複数表示枠での共有、非表示直後の再表示で、解放済みハンドルや破棄済み Sprite のエラーが出ないこと。
- 読み込み途中の非表示、シーン遷移、欠損・破損画像の挙動。繰り返し Hide や Show の重なりなど、本編自体のライフサイクル上の制約も区別すること。
- 他 MOD 併用時の Prefix の競合と、起動時の Addressables 初期化順。

コンパイル成功はゲーム上の動作確認の代わりにはならない。また、処理の複製を減らす方針の調査であり、ライセンス上の判断を確定するものではない。

## API 資料

Context7 で Addressables の資料を検索し、詳細が不足する部分は Unity 公式 API 資料とゲーム同梱 DLL を照合した。公式資料は 1.21 系の API 説明として参照しており、ゲームのパッケージ版が同一とは断定していない。

- [AddResourceLocator](https://docs.unity3d.com/Packages/com.unity.addressables@1.21/api/UnityEngine.AddressableAssets.Addressables.AddResourceLocator.html): ビルド済みカタログ外のファイルにも locator を追加できる。
- [ResourceProviderBase](https://docs.unity3d.com/Packages/com.unity.addressables@1.21/api/UnityEngine.ResourceManagement.ResourceProviders.ResourceProviderBase.html): 独自の供給・解放処理の基底クラス。

## 導入後の確認（2026-10-01）

- 16:17 にゲーム終了を確認し、旧 DLL を `LoMScenarioAddon-backups/20261001-161709-picture-provider/` へ退避して新版を導入した。導入先とビルド済み DLL の SHA-256 一致を確認。
- ユーザーから「動いた」と報告あり。16:21 時点の `LogOutput.log` と `Player.log` で、訪問シナリオ2回と試作エンディングの実行記録を確認。
- 画像キー未解決、無効ハンドル、自前画像供給失敗のエラーは両ログとも0件。
- 旧日本語化 DLL の退避失敗警告と、本編 `FlowchartPlaceholder.OnDestroy` の NullReferenceException が1件存在する。直前には終了中の `FungusManager.Instance` が null を返す旨のログがある。本編の `OnDestroy` がその値を null 確認なしに参照しており、終了順序による例外と整合する。画像供給処理やルビ MOD が直接の原因だと示すログはなく、MOD 無効化による切り分けは未実施。
- 専用 `lsa_picture_test` の実行記録はなく、アイテム画像など未確認の項目は残す。
