-- Copyright (C) 2026 NEXTAltair
-- Code: GPL-3.0-only with the project game-linking exception.
-- Dialogue/narrative: CC BY-NC-SA 4.0. See scenario/LICENSE.
-- 試作キャラ lsa_test の登場シナリオ。
-- 裏山の「修練」の抽選 (Back_Free_001) で1回だけ起きる (scenario.json の events.meet)。設定した TestStoryKey でも直接始められる (既定は無効)。
-- 書き方は公式シナリオ (Story/ChineseTraditional/*) と同じ。台詞は原文を直接書いている。

-- 背景 (裏山)
runwait(flowcharts.LoadView("backmountain2"))
getvar(flowcharts.view, "ViewName").value = "backmountain2"
runblock(flowcharts.view, "view")

-- 登場
runwait(characters.LoadCharacterAsset("lsa_test"))
stage.show{character=characters.Get("lsa_test"), portrait=characters.GetPortrait("lsa_test", "normal"), fromPosition="R2", toPosition="R2", facing="left", fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)

setsaydialog(saydialogs.character)
characters.Focus("lsa_test")
setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "normal"))
say("……ここが唐門か。噂より静かなところだな。")

-- 表情差分
characters.LoadCharacterPortrait("lsa_test", "smile")
stage.showPortrait(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
say("おっと、名乗りが遅れた。しばらく世話になるよ。")

-- 初登場の紹介カード (未出会いのときだけ出る。出ると社交パネルに載る)
runwait(intropanel.Show("lsa_test"))

-- 二人目の登場
runwait(characters.LoadCharacterAsset("lsa_test2"))
stage.show{character=characters.Get("lsa_test2"), portrait=characters.GetPortrait("lsa_test2", "normal"), fromPosition="L2", toPosition="L2", facing="right", fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)
characters.Focus("lsa_test2")
setcharacter(characters.Get("lsa_test2"), characters.GetPortrait("lsa_test2", "normal"))
say("……置いていくな。")
characters.Focus("lsa_test")
setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
say("悪い悪い。こいつは俺の連れだ。")
runwait(intropanel.Show("lsa_test2"))

-- 向きの切り替え
--   客人: 絵は左向きの1枚だけ → 右を向くとゲームが自動で左右反転する (文字は鏡文字になる)
--   連れ: 右向きの絵 (portraits/right/) がある → 右向きのときはそれを使い、左を向くと基準の絵に戻る
stage.show{character=characters.Get("lsa_test"), portrait=characters.GetPortrait("lsa_test", "smile"), fromPosition="R2", toPosition="R2", facing="right", fadeDuration=0.3, useDefaultSettings=false}
stage.show{character=characters.Get("lsa_test2"), portrait=characters.GetPortrait("lsa_test2", "normal"), fromPosition="L2", toPosition="L2", facing="left", fadeDuration=0.3, useDefaultSettings=false}
wait(0.3)
setsaydialog(saydialogs.narrative)
setcharacter(narrative)
say("二人はそろって、そっぽを向いた。")
setsaydialog(saydialogs.character)

-- 好感度 +1 (公式・既存MODの増減は ±1〜3。画面に増減の表示を出す)
statmodifymanager.Character("lsa_test", 1, 1)
wait(1)
statmodifymanager.Character("lsa_test2", 1, 1)
wait(1)

-- 自前のフラグ (scenario.json の flags) を Lua から立てる。セーブにも残る
statmodifymanager.SetFlag("lsa_met", 1)

-- 退場して自由行動へ戻る
stage.hide{character=characters.Get("lsa_test"), fadeDuration=0.5, useDefaultSettings=false}
stage.hide{character=characters.Get("lsa_test2"), fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)
runblock(flowcharts.view, "out")
luamanager.ChangeScene("Free", "", "")
