-- Copyright (C) 2026 NEXTAltair
-- Code: GPL-3.0-only with the project game-linking exception.
-- Dialogue/narrative: CC BY-NC-SA 4.0. See scenario/LICENSE.
-- 試作ルートのエンディング (scenario.json の endings.lsa_end_test)。
-- 本編のエンディング分岐 (ch8_6_3_2) の直前で、ルートフラグ lsa_route = 1 ならここへ来る。
-- 終わり方は本編の各エンディングと同じ: 列伝に積む → エンディングカード → タイトルへ。

-- 本編の共通部分で出ている人物を下げる (直接このシナリオを始めた場合は居ないので pcall)
pcall(function() stage.hide{character=characters.Get("lady1"), fadeDuration=0.5, useDefaultSettings=false} end)
pcall(function() stage.hide{character=characters.Get("sir1"), fadeDuration=0.5, useDefaultSettings=false} end)

runwait(flowcharts.LoadView("backmountain2"))
getvar(flowcharts.view, "ViewName").value = "backmountain2"
runblock(flowcharts.view, "view")

runwait(characters.LoadCharacterAsset("lsa_test"))
runwait(characters.LoadCharacterAsset("lsa_test2"))
stage.show{character=characters.Get("lsa_test"), portrait=characters.GetPortrait("lsa_test", "smile"), fromPosition="R2", toPosition="R2", facing="left", fadeDuration=0.5, useDefaultSettings=false}
stage.show{character=characters.Get("lsa_test2"), portrait=characters.GetPortrait("lsa_test2", "normal"), fromPosition="L2", toPosition="L2", facing="right", fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)

setsaydialog(saydialogs.character)
characters.Focus("lsa_test")
setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
say("さて、そろそろ次の土地へ行くとするか。……一緒に来るか？")
characters.Focus("lsa_test2")
setcharacter(characters.Get("lsa_test2"), characters.GetPortrait("lsa_test2", "normal"))
say("……好きにしろ。")

setsaydialog(saydialogs.narrative)
setcharacter(narrative)
-- 全画面のイベント CG (pictures/lsa_pic_road.png)
runwait(mainui.ShowBigPicture("lsa_pic_road"))
say("こうして三人は、唐門を後にした。")
runwait(mainui.HideBigPicture())

-- 列伝に積む (本文は text.ja.csv の LegendInfo/<キー>)
statmodifymanager.AddStory("lsa_end_test_legend")

-- CG 画面 (scenario.json の cgs、絵は cgs/lsa_cg_test.png)。結局CG の一覧に記録される
runwait(cgpanel.Open("lsa_cg_test"))

-- エンディングカード (本編と同じ画面。汗青書の件数と列伝スロットの保存も本編と同じ)
luamanager.SetEnableActions(0)
runwait(endgamepanel.Open("lsa_end_test"))
luamanager.SetEnableActions(1)

luamanager.FadeOutMusic(2)
wait(2)
runwait(transitionblack.TransitionIn("lr"))
luamanager.ChangeScene("Title", "", "")
