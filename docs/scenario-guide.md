# シナリオ制作ガイド

[README に戻る](../README.md) · [開発メモ](development.md)

シナリオを作る人向けの定義・素材・Lua の書き方と、検査・配布の手順です。
パスは特記がなければ `scenario/` 内、`src/`・`sample/`・`tools/` はリポジトリのルートを基準にしています。

まず [サンプル](../sample/scenario/) をコピーして編集し、LoMScenarioTool で検査してください。
本文で触れる `~/.cache/lom-mod-refs/` の TSV は開発時に手元のゲームから書き出した資料で、リポジトリ・配布 ZIP には含まれません。そこにある一覧の入手はサンプルを動かすためには不要です。
ゲームで試すときは、導入先の `BepInEx/plugins/LoMScenarioAddon/scenario/` に配置します。

## scenario フォルダの書き方

```
scenario/
  scenario.json
  text.ja.csv                     名前・肩書き・紹介文・列伝 (key,text)
  story/<シナリオ名>.lua           公式シナリオと同じ書き方の Lua
  characters/<キャラID>/
    icon.png                      小アイコン 101x100
    head.png                      社交パネルの縦長の顔 66x172
    avatar.png                    立ち絵 (約900x1700)。会話シーンの normal を兼ねる
    portraits/<表情>.png          会話シーンの表情差分 (左向きが基準)
    portraits/right/<表情>.png    左右非対称のデザイン用の右向きの絵 (任意)
```

```json
{
  "id": "altair.sample",
  "title": "試作シナリオ",
  "version": "0.1",
  "characters": [
    { "id": "lsa_test",  "affinity": { "default": 5 }, "social_panel": true },
    { "id": "lsa_test2", "affinity": { "default": 0 }, "social_panel": true }
  ],
  "flags": [
    { "id": "lsa_met", "default": 0 }
  ],
  "events": [
    {
      "id": "meet",
      "position": "Back_Free_001",
      "story": "lsa_test_meet",
      "rate": 60,
      "once": true,
      "when": {
        "logic": "AND",
        "items": [
          { "left": { "time": "now" },          "op": ">=", "right": { "time": [1, 4, 1] } },
          { "left": { "flag": "lsa_met" },      "op": "=",  "right": 0 },
          { "left": { "affinity": "brother4" }, "op": ">=", "right": 20 }
        ]
      },
      "boosts": [
        { "when": { "items": [ { "left": { "stat": "mental" }, "op": ">=", "right": 50 } ] }, "rate": 30 }
      ]
    }
  ]
}
```

(サンプルの `sample/scenario/scenario.json` は試験用に `rate` を 10000 にしている)

- キャラ ID は英小文字で始まり、英小文字・数字・`_` のみ。公式キャラの ID (`brother4` など) と同じものは使えない。
- テキストのキー: `Character/<ID>` 名前、`CharacterTitle/<ID>` 肩書き、`CharacterIntro0/<ID>` 紹介カード、`CharacterIntro1〜4/<ID>` 列伝。
- 立ち絵は左向きに描く。右向きの絵が無い表情は、右を向くときにゲームが自動で左右反転する。文字や紋章など反転すると困るものがあるなら `portraits/right/` に右向きの絵を置く。
- 好感度の増減は公式に合わせて ±1〜3 程度 (値は 0〜100、10 ごとに1レベル)。
- 台詞は `say("本文")` と直接書いてよい。読み込み時に `say(luamanager.GetStoryText("LSA_<シナリオ名>_<本文のハッシュ>"))` へ自動で置き換えるので、本編と同じく既読の一覧に載り、既読色・既読スキップが効く。キーは本文から作るので、行を足したり並べ替えたりしても既読はずれない (本文を変えた台詞だけ未読に戻る)。多言語にしたいときは、自分でキーを付けて `say(luamanager.GetStoryText("キー"))` と書き、`text.<言語>.csv` に `Story/<キー>,本文` を置く。

### 言語 (text.<言語>.csv)

- `scenario/text.<言語>.csv` をすべて読む。言語コードは `ja` `zh-TW` (繁体字) `zh-CN` (簡体字) `ko` (韓国語) など。
- 使う言語は設定 `[Text] Language` (既定 `auto`)。`auto` なら、日本語化 MOD (`LOM_JP_StringVault`) が読み込まれていれば `ja`、無ければゲームの言語 (ゲーム本体の言語は繁体字・簡体字・韓国語の3つ)。`ja` などを書けば固定。
- キーは 今の言語 → `scenario.json` の `default_language` (既定 `ja`) → 言語の区別がないテキスト (自動でキーを付けた台詞、決闘の台詞) の順に探す。訳していないキーは既定の言語で出る。
- ゲーム中に言語を切り替えても、テキストを取り出すたびに見直すので反映される。

### flags

自前のフラグ。本編のフラグ一覧に足されるので、Lua の `statmodifymanager.SetFlag("lsa_met", 1)` や条件から使え、セーブにも名前で残る。本編に同名のフラグがあれば本編のものを使う。

### events (場所イベント)

本編の場所ごとのイベント抽選 (`PositionResultData`) に候補を1つ足す。抽選・条件判定・重みの計算は本編の処理がそのまま行う。

| 項目 | 意味 |
|---|---|
| `id` | イベント ID (英小文字・数字・`_`) |
| `position` | 抽選表の名前。例: `Back_Free_001` (裏山の修練)、`Back_Work_001`、`Center_Free_001` など。一覧は Binarizer の F3 書き出し `LuaEquivalent/Position/` のファイル名 |
| `story` | 当たったときに流す `story/<名前>.lua` |
| `rate` | 基本の重み。本編の候補は 40〜100 程度 |
| `once` | 既定 true。一度起きたら候補から外れる (フラグ `lsa_done_<id>` を自動で作って管理) |
| `when` | 登場条件。`logic` は `AND` か `OR`、`items` は `{ "left": 値, "op": 比較, "right": 値 }` の並び。省略すると無条件 |
| `boosts` | `{ "when": 条件, "rate": 加算 }` の並び。条件を満たすと重みを足す (負の値で減らす) |

比較 `op`: `=` `!=` `<` `<=` `>` `>=`

値の書き方:

| 書き方 | 意味 |
|---|---|
| `12` | 数値 |
| `{ "flag": "E10002" }` | フラグの値 (本編のフラグ、または `flags` で足したもの) |
| `{ "affinity": "brother4" }` | 好感度 (公式キャラ、または追加キャラ) |
| `{ "stat": "mental" }` | 主人公の能力値 (`mental` `money` `fame` `charisma` `literacy` など) |
| `{ "time": "now" }` / `{ "time": [年, 月, 旬] }` | ゲーム内の日付。旬は 1=上旬 2=中旬 3=下旬。ゲームは1年4月上旬から始まる |
| `{ "random": [最小, 最大] }` | 乱数 |

書き間違い (存在しないフラグ・キャラ・能力値・場所など) があると、そのイベントだけ無効になり、理由がログに出る。

### visits (場所選択時のシナリオ差し替え)

自由行動で場所をクリックしたとき、条件を満たしていれば、その場所の固定シナリオ (裏山なら薪割り・修練のメニュー) の代わりに指定したシナリオを流す。通常の行動へ進む場合だけ差し替え、本編のイベントへ進む場合は差し替えない。行動力の消費は通常の行動と同じ。

サンプルでは、この機能を追加キャラへの訪問・雑談メニューに使っている。差し替えるシナリオの内容は訪問に限らない。

```json
"visits": [
  { "id": "visit_test", "position": "BackMountain", "story": "lsa_test_visit",
    "when": { "items": [ { "left": { "flag": "lsa_met" }, "op": "=", "right": 1 } ] } }
]
```

| 項目 | 意味 |
|---|---|
| `id` | 差し替え定義の ID (events と共通の名前空間) |
| `position` | `Mall` (正心堂) `Center` (校場) `Alchemy` (煉丹房) `Forge` (鍛冶場) `BackMountain` (後山) `Room1` (弟子房) `Door` (大門) `Study` (講經堂) `Kitchen` (伙房) `Room2` (女弟子房) `Secret` |
| `story` | 流す `story/<名前>.lua` |
| `when` | 条件 (events と同じ書き方)。省略すると通常の行動へ進むたびに差し替える |
| `once` | 既定 false。true なら一度きり |

差し替えたシナリオから本来の行動へ進むには `luamanager.SetNextScript("lsa_position_default")` → `luamanager.Init()`。

雑談メニューは本編と同じ書き方: `setmenudialog(menudialogs.Talk)`、選択肢は `"アイコン+文言+0+0"` (アイコン: `talk` `exit` など)、非表示は `"~x"`、`choose(talk)` の戻り値で分岐。例は `sample/scenario/story/lsa_test_visit.lua`。

### endings (エンディング)

本編のエンディング分岐 (シナリオ `ch8_6_3_2`) で、ヒロインごとの分岐 (`checkpointmanager.Switch("Ch_8_6_3_2_001")`) の直前に「`when` が成立すれば `story` へ進む」判定を差し込む。上から順に判定し、どれも成立しなければ本編のまま。共通部分 (列伝の `Ch_8_6_3_2_001/002` など) はそのまま残る。本編のルートフラグ (G11000〜) を立てないキャラなら、本編の判定とは衝突しない。公式の更新で差し込み先の文が見つからなければ、何もせず警告だけ出す。

```json
"endings": [
  { "id": "lsa_end_test", "story": "lsa_test_ending",
    "when": { "items": [ { "left": { "flag": "lsa_route" }, "op": "=", "right": 1 } ] } }
]
```

- `id` はエンディングカードの名前。汗青書の件数と列伝スロットの保存に、この名前で残る。
- 挿絵は `endings/<id>.png` (公式と同じ 480x700)。題名・本文は `text.ja.csv` の `Library/Title/<id>`・`Library/Desc/<id>`。
- `story` の中で本編と同じく締める: `statmodifymanager.AddStory("列伝キー")` (本文は `LegendInfo/列伝キー`) → `runwait(endgamepanel.Open("<id>"))` → `luamanager.ChangeScene("Title", "", "")`。例は `sample/scenario/story/lsa_test_ending.lua`。
- 汗青書の総数 (公式 54) が増え、到達件数に応じた周回特典にも数えられる。自前の実績 (Steam に通知されるもの) は作れない。

確認は設定 `TestStoryName = ch8_6_3_2` にして、自由行動の画面で `TestStoryKey` を押す (本編のエンディング直前の場面が流れ、最後はタイトルに戻る)。

### duels (決闘)

1対1の決闘に追加キャラを敵として出す。決闘の場面を読み込んだ直後に本編の決闘設定 (`CombatLevel`) と敵 (`CombatStat`) を複製し、`CL_<id>` として一覧に足す。敵の部品 (全キャラ共通)・技・音は借り物で、見た目は4枚の絵と立ち絵・顔を差し替える。複製元のキャラ固有の決闘イベント・台詞イベント・死亡エンディング・勝敗フラグは持ち込まない。

```json
"duels": [
  {
    "id": "lsa_test_spar",
    "character": "lsa_test",
    "template": "3001_01",
    "enemy": "Enemy_南宮淺_001",
    "rates": { "attack": 0.5, "talk": 0.2, "block": 0.4 },
    "stats": { "Dexterity": 70 },
    "talk": { "normal": ["腕試しといこうか。"], "good": ["やるじゃないか！"], "bad": ["おいおい、その程度か？"], "attacked": ["っ……効いたぜ。"] }
  }
]
```

| 項目 | 意味 |
|---|---|
| `id` | 決闘キー。Lua: `luamanager.SetCurrentStoryScript("戻り先")` → `luamanager.ChangeScene("Combat", "<id>", "Story")` |
| `character` | 敵として出す追加キャラ (このシナリオの characters のどれか) |
| `template` | 背景・曲・部品を借りる本編の決闘 (`CL_` を除いたキー)。省略すると三師兄との手合わせ (`3001_01`)。開発用の `0000` 系は使わない |
| `enemy` | 能力値・行動の重み・絶招・才能を借りる本編の敵 (例: `Enemy_南宮淺_001`)。省略すると `template` の敵 |
| `music` | 決闘の曲。本編の曲名 (例: `戰鬥_001`) か自前の `music/<名前>.wav` の名前。省略すると `template` の曲 |
| `health` | 体力。本編の敵は 100〜1000 程度 (三師兄の手合わせ 600、大師兄 850〜1000) |
| `rates` | 行動の重み (`attack` 攻撃 / `talk` 口撃 / `block` 防御 / `weapon` 暗器 / `ultimate` 絶招)。毎ターン、使える行動の中から重みで抽選する。本編の既定は口撃 0.9、攻撃・暗器・絶招 0.25、防御 0.5 |
| `stats` | 能力値の上書き (`CombatStat` の数値項目名: `Strength` `Stamina` `Dexterity` `Talking` `Defence` `Disposition` (行動の消費・気力回復) `Training` (怒りやすさ) など) |
| `talk` | 決闘中の台詞 (`normal` / `good` 善悪値が50より大きいときの追加候補 / `bad` 50より小さいときの追加候補 / `attacked` 口撃を受けた際の反応)。本文を直接書く |

- 勝敗はフラグ `<id>_won` (勝ち 1 / 負け 0)。戻り先で `luamanager.GetFlagData("<id>_won") == "1"` などで分岐する。
- 絵は `characters/<キャラID>/combat/normal.png` `attack.png` `hurt.png` `defence.png` (左向き)。無ければ normal → 立ち絵 (`avatar.png`) で代用。縮尺と基準点は複製元の絵に自動で合わせる。
- 名前・肩書きは `CombatCharacter/Name|Title/<キャラID>`、無ければ社交パネルの名前・肩書き。
- 決闘の開始時に本編がオートセーブする。アドオンを外した状態でそのオートセーブを読むと、決闘設定が見つからず止まる恐れがある。
- 本編の決闘の一覧 (キー・相手・体力) は `~/.cache/lom-mod-refs/combat_levels.tsv` (手元のゲームから書き出したもの。配布しない)。

### talents (天賦・技能)

本編の天賦を複製して、ID・名前・説明を差し替えた天賦を足す。取得・人物画面・セーブ・決闘での効果は本編の処理。

```json
"talents": [
  { "id": "lsa_tal_guard", "base": "0019", "effect": "lsa_eff_guard" }
]
```

| 項目 | 意味 |
|---|---|
| `id` | 天賦 ID。Lua: `statmodifymanager.AddTalent("<id>", 1)` (負の値で下げる) |
| `base` | 複製元の本編の天賦 ID (例: `0019` 肩甲)。一覧は `~/.cache/lom-mod-refs/talents.tsv` (ID・効果キー・最大レベル・日本語名) |
| `effect` | 戦闘の効果キー (省略すると `base` の効果)。`display_level` が true なら効果は `<キー>_<レベル>` で引かれる |
| `max_level` / `display_level` / `priority` | 省略すると `base` のまま |

- 名前・説明は `PlayerTalent/Name/<id>`・`PlayerTalent/Desc/<id>`。
- 集団戦用の技の印と、上位天賦を得たときに消える下位天賦は持ち込まない。
- 決闘の敵にも `duels[].talents: [ { "id": "0001", "level": 3 } ]` で持たせられる (本編・追加の天賦どちらも可。書くと借りた敵の技構成を置き換える)。
- 新しい効果は下の `effects` で作り、`effect` にそのキーを書く。

### effects (決闘の効果・新しい天賦の効果)

本編の効果 (状態) を複製し、中の部品 (Unit) を差し替えて新しい効果を作る。部品は数値パラメータ付きの汎用品
(能力値の増減・ダメージ軽減・追加攻撃・状態付与など約100種) なので、組み合わせと数値の書き換えだけで新しい効果になる。

```json
"effects": [
  {
    "key": "lsa_eff_guard",
    "base": "B0019",
    "units": [
      { "base": "B_Stat_003_自身_防禦_加20", "set": { "_rate": 0.25 } },
      { "base": "B_Stat_003_自身_輕功_減10", "set": { "_statType": ["迴避"], "_rate": 0.1 } }
    ]
  }
]
```

| 項目 | 意味 |
|---|---|
| `key` | 効果キー。天賦の `effect` から使う。名前・説明は `CombatSkill/Name/<key>`・`CombatSkill/Desc/<key>` |
| `base` | 複製元の本編の効果キー (種類・アイコン・回合数などを引き継ぐ)。一覧は `~/.cache/lom-mod-refs/combat_effects.tsv` |
| `set` | 効果そのものの値の上書き (例: `{ "_rounds": 3 }`。-1 で無制限) |
| `units` | 部品の並び。省略すると `base` の部品のまま。`base` は本編の部品のアセット名 (一覧と引数は `combat_units.tsv`)、`set` はその部品の値の上書き |

- `set` の値はフィールドの型に合わせて変換する: 数値・真偽・文字列、列挙は名前 (`"迴避"`) か数値 (`203`)、配列は JSON の配列、入れ子の設定は `{}` で部分的に上書き。本編の画像・部品への参照は書き換えられない。
- 部品は本編と共有しないよう必ず複製するので、本編の効果は変わらない。
- 能力値の種類 (`CombatStatType`): 體力=1 內力=2 輕功=3 暗器=4 拳掌=5 刀劍=6 嘴力=100 抗毒=101 抗麻=102 毒藥=103 麻痺=104 心相=105 防禦=200 命中=201 暗器命中=202 迴避=203 捅人格檔=204 備揍格檔=205 暗器傷害=301 嘴功傷害=302 所有傷害=303 額外暗器骰=304 額外武器骰=305 捅人傷害=306 絕招傷害=307。
- 決闘の場面を読み込むたびに作り直す。フィールド名の誤り・見つからない部品はログにエラーを出し、その効果だけ無効になる。
- `display_level` の天賦に使うなら `<キー>_1`, `<キー>_2`, … をそれぞれ作る。

### 曲・効果音・環境音 (music/ sounds/ env/)

wav を置くと、起動時に Wwise の SoundBank をその場で組み立てて読み込む (Wwise のツールは不要)。
本編と同じ Wwise で鳴らすので、音量スライダー・フェード・曲の切替・`StopMusic` は本編どおり効く。

| フォルダ | Lua | 扱い |
|---|---|---|
| `music/<名前>.wav` | `luamanager.PlayMusic("<名前>")` | 無限ループ。鳴らすと今の曲 (本編・自前) を止めて切り替わる。`FadeOutMusic` 可 |
| `sounds/<名前>.wav` | `luamanager.PlaySound("<名前>")` | 1回再生 |
| `env/<名前>.wav` | `luamanager.PlayEnvSound("<名前>")` | 無限ループの環境音。`FadeOutEnvSound` 可 |

- 名前は小文字の英字・数字・`_` (例: `lsa_theme`)。本編の曲名 (`普通_001` など) もそのまま使える。
- wav は PCM 8/16/24/32bit か 32bit float、モノラルかステレオ。中では 16bit PCM で埋め込むので、ステレオ 44.1kHz で約 10MB/分になる (長い曲はメモリに注意)。
- wav に smpl チャンクのループ位置があれば引き継ぐ (イントロ付きループ。未確認)。
- 決闘の曲は `duels[].music` で指定する。

### 背景 (backgrounds/)

`scenario/backgrounds/<背景名>.png` を置けば、本編と同じ書き方で使える。

```lua
runwait(flowcharts.LoadView("lsa_camp"))
getvar(flowcharts.view, "ViewName").value = "lsa_camp"
runblock(flowcharts.view, "view")
```

- 本編の背景は 1920x1080 (PPU 100、中央基準)。自前の画像は解像度が違っても同じ高さで描くよう縮尺を合わせる。横幅は 16:9 を推奨。
- 本編の背景名 (`backmountain2`、`kitchen` など。一覧は `DataTable/AddressableData` の `_key`) と同じ名前にすると、本編の背景を差し替えることになる。追加する背景には固有の名前を付ける。

### イベント CG・CG 画面・死亡エンディング

| 種類 | 置き場所・定義 | Lua (本編と同じ書き方) |
|---|---|---|
| イベント CG (会話中の絵) | `pictures/<キー>.png` | `runwait(mainui.ShowPicture("<キー>"))` → `runwait(mainui.HidePicture())`。全画面は `ShowBigPicture`/`HideBigPicture`、アイテムの絵は `ShowItemPicture`/`HideItemPicture` |
| CG 画面 (本文付き・結局CG の一覧に記録) | `"cgs": ["<キー>"]`、絵は `cgs/<キー>.png`、本文は `Library/Desc/<キー>` | `runwait(cgpanel.Open("<キー>"))` |
| 死亡エンディング (奪命譜に記録) | `"deaths": ["<キー>"]`、題名・本文は `Library/Title/<キー>`・`Library/Desc/<キー>` | `luamanager.ChangeScene("GameOver", "<キー>", "")` |

- イベント CG は元の大きさで表示される。本編のサイズは `ShowPicture` 480x700、`ShowBigPicture` 1920x1080、`ShowItemPicture` 312x300 / 520x520。16:10 の画面では 1920x1080 の全画面 CG の上下に帯が出る (本編の表示処理のまま)。
- CG 画面の絵は 1100x1080。ゲームの言語が中国語 (日本語化 MOD は簡体字の枠で遊ぶ) のときは、本文が改行ごとに1列の縦書きで絵の左側に重なって並ぶ。本編の CG と同じく、本文は1行15〜20文字程度で改行し、絵の左側を明るい余白にしておく (見本 `cgs/lsa_cg_test.png`)。

### books (秘笈)

本編の秘笈を複製して、ID・アイコン・名前・レベルごとのデータを差し替えた秘笈を足す。所持品・修練の画面・レベルアップの表示・セーブは本編の処理。

```json
"books": [
  {
    "id": "lsa_book_test",
    "base": "2001",
    "tier": 1,
    "levels": [
      { "points": 1, "stats": { "m-sword": 3 } },
      { "points": 2, "stats": { "m-sword": 3, "dexterity": 2 } },
      { "points": 2, "stats": { "m-sword": 4 }, "talents": { "lsa_tal_sword": 1 } }
    ],
    "bonus": { "m-sword": [2, 5, 10], "dexterity": [0, 1, 3] }
  }
]
```

| 項目 | 意味 |
|---|---|
| `id` | 秘笈 ID。Lua: `statmodifymanager.AddBook("<id>", 1)` → 入手の表示は本編と同じく別の命令 `mainui.DisplayMessage("M_Add_Book_<id>")` |
| `base` | 複製元の本編の秘笈 ID (種類・修練画面の振り分けなどを借りる。例: `2001` 刀劍) |
| `tier` | 階級 1〜5。本編の決まりで、武功系の能力値は「能力値÷25＋1 ≧ 階級」のときだけ上がる |
| `levels` | レベルごとのデータ。`points` 必要な修練回数、`stats` 能力値の増減 (レベルアップ時に一度だけ)、`talents` 天賦のレベル増減、`affinity` 好感度の増減。修練の条件は付かない |
| `bonus` | 能力値加成 (今のレベルに応じて常にかかる上乗せ)。能力値ごとにレベル1〜最大の値を表で書く。秘笈の画面の「能力値加成」欄にも出る |

- 名前などは `Book/Title|Name|Desc/<id>`、アイコンは `books/<id>.png` (公式 312x300)。入手の表示の文 `Story/M_Add_Book_<id>` を書かなければ、名前から「秘笈《…》を獲得した」を作る。
- 本編の能力値加成は曲線 (レベル÷最大 の割合で読む) なので、伸びが一定にならないことがある。アドオンは表の値が各レベルでちょうど出る折れ線に直して渡す。
- 加成を持てる能力値は、本編に「〜加成_秘笈」がある13種 (`m-sword` `m-fist` `m-projectile` `dexterity` `internal` `stamina` `life` `literacy` `talking` `poison-resistance` `paralysis-resistance`、防禦、攻撃爆撃)。
- 秘笈の画面の欄は最大8行 (加成の数 + レベルアップで上がる能力値の種類)。超える加成は切り捨てる。
- 複製元のレベルデータ・能力値加成・入手時の任務処理・分冊は持ち込まない。
- 主人公の絶招は秘笈と無関係 (本編の共通の仕組み) なので、秘笈で新しい絶招は覚えられない。技は最終レベルで天賦を付ける形で与える。

### 周回の引き継ぎ (繼承人際)

追加キャラは、周回の引き継ぎ画面「繼承人際」(天命点で好感度を上げる) に公式25人の後ろへ並ぶ。外すにはキャラの定義に `"inherit": false`。

### conditions (名前付きの条件)

Lua には好感度を読む関数が無いので、好感度などによる分岐は条件に名前を付けて `checkpointmanager.Condition("名前")` で判定する。本編と同名の条件があれば本編を優先する。

```json
"conditions": {
  "lsa_test_close": { "items": [ { "left": { "affinity": "lsa_test" }, "op": ">=", "right": 17 } ] }
}
```

## 作る人向けの道具 (LoMScenarioTool)

ゲームのフォルダの `LoMScenarioTool/LoMScenarioTool.exe` (Windows 標準の .NET Framework 4.7.2 で動く)。

| 使い方 | すること |
|---|---|
| exe をダブルクリック | `BepInEx/plugins/LoMScenarioAddon/scenario` を探して検査 |
| scenario フォルダを exe にドラッグ＆ドロップ | そのフォルダを検査 |
| `LoMScenarioTool check <フォルダ> [--info]` | 検査。`--info` で念のための情報 (本編の名前とみなしたもの、音の大きさ、訳の抜けの件数) も出す |
| `LoMScenarioTool pack <フォルダ> [--out 名前.zip] [--bepinex BepInEx フォルダ] [--force]` | 検査して、ゲームのフォルダに上書きで展開できる配布用 zip を作る。エラーがあれば作らない |

検査すること:

- `scenario.json` の読み取りと、定義から参照しているシナリオ (`story/*.lua`) の有無
- Lua の構文 (ゲームと同じ MoonSharp で読む)
- Lua から渡している名前: キャラ・表情の絵・背景・イベント CG・CG 画面・エンディング・死亡・決闘・シナリオ・曲・効果音・天賦・秘笈・フラグ・条件・テキストキー
- 必要なテキスト (名前・肩書き・紹介・天賦・秘笈・効果・図鑑) が既定の言語にあるか、CSV の重複・カンマの入れ忘れ
- 画像の大きさ (本編と違えば警告)、PNG として読めるか
- wav が読めるか、埋め込みの大きさ
- 使われていないファイル

本編の名前 (本編のシナリオ・天賦・曲など) は手元に一覧が無いので確かめない。このシナリオの ID と同じ接頭辞 (例: `lsa_`) で見つからない名前だけ、打ち間違いの疑いとして警告する。

`pack` の zip には `BepInEx/patchers|plugins/LoMScenarioAddon/`（DLL・ライセンス一式・`Source/` は scenario の入っている導入先から取る）と `scenario/` の中身が入る。旧版でライセンスやソース ZIP がない場合は、新しい配布 ZIP から導入し直す。DLL を改変した場合は対応するソース ZIP も更新する。独自シナリオの利用条件は制作者が別途記載する。サンプルを流用する場合は [ライセンスの適用範囲](licensing.md) に従う。作業用のファイル (`.psd` `.clip` `.kra` `Thumbs.db` `.` で始まるものなど) は入れない。名前は `<id>-<version>.zip` (`scenario.json` の `"version"`、無ければ日付)。

## 確認用のキー操作

既定では無効。`BepInEx/config/lom.scenarioaddon.cfg` でキーを割り当てる。

| 設定 | 動作 |
|---|---|
| `TestStoryKey` (例: `Home`) | 自由行動の画面で、`TestStoryName` のシナリオ (既定 `lsa_test_meet`) を始める |
| `ToggleActiveKey` (例: `End`) | 追加キャラ全員の「出会い済み」(Active) を切り替える。ステータス画面を開き直すと反映 |

F キーは日本語化 MOD (ルビ F5〜F7、フォント F7〜F12) と Binarizer (F1〜F6)、Ctrl は会話スキップが使うので避ける。

結果は `BepInEx/LogOutput.log` の `LoMScenarioAddon` 行に出る。
