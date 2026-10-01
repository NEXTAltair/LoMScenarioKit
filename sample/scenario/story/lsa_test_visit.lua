-- Copyright (C) 2026 NEXTAltair
-- Code: GPL-3.0-only with the project game-linking exception.
-- Dialogue/narrative: CC BY-NC-SA 4.0. See scenario/LICENSE.
-- 試作キャラへの訪問 (scenario.json の visits.visit_test)。
-- 出会い済み (フラグ lsa_met = 1) なら、自由行動で裏山をクリックしたときに本来の行動の代わりにここへ来る。
-- 雑談メニューは本編と同じ menudialogs.Talk。選択肢は "アイコン+文言+0+0"、"~x" で非表示。

-- 背景は自前の backgrounds/lsa_camp.png。書き方は本編の背景と同じ
runwait(flowcharts.LoadView("lsa_camp"))
getvar(flowcharts.view, "ViewName").value = "lsa_camp"
runblock(flowcharts.view, "view")

-- 自前の曲 (music/lsa_theme.wav)。書き方は本編の曲と同じ。本編の曲名 (例: "普通_001") もそのまま使える
luamanager.PlayMusic("lsa_theme")

runwait(characters.LoadCharacterAsset("lsa_test"))
stage.show{character=characters.Get("lsa_test"), portrait=characters.GetPortrait("lsa_test", "normal"), fromPosition="R2", toPosition="R2", facing="left", fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)

setsaydialog(saydialogs.character)
characters.Focus("lsa_test")
setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "normal"))
say("よう、来たか。")

-- 雑談メニュー。「旅の話」は好感度が一定以上 (scenario.json の conditions.lsa_test_close) のときだけ出る
setmenudialog(menudialogs.Talk)
local talk = {}
talk[1] = "exit+立ち去る+0+0"
talk[2] = "talk+近況を聞く+0+0"
talk[3] = checkpointmanager.Condition("lsa_test_close") and "talk+旅の話を聞く+0+0" or "~x"
talk[4] = "talk+本来の用事を済ませる+0+0"
talk[5] = "talk+手合わせを願う+0+0"
talk[6] = "talk+（試験）死亡エンディング+0+0"
talk[7] = "talk+（試験）本編の曲に切り替える+0+0"
local choice = choose(talk)
menudialogs.Talk.SetActive(false)

setsaydialog(saydialogs.character)
if choice == 2 then
	setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "normal"))
	-- 自前の効果音 (sounds/lsa_chime.wav)
	luamanager.PlaySound("lsa_chime")
	say("相変わらずさ。連れは無口だし、俺は暇だし。")
	statmodifymanager.Character("lsa_test", 1, 1)
	wait(1)
elseif choice == 3 then
	characters.LoadCharacterPortrait("lsa_test", "smile")
	stage.showPortrait(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
	setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
	-- イベント CG (pictures/lsa_pic_fire.png)。書き方は本編と同じ
	runwait(mainui.ShowPicture("lsa_pic_fire"))
	say("お前とは長い付き合いになりそうだ。……実はな、俺たちは……いや、また今度にしよう。")
	runwait(mainui.HidePicture())
	statmodifymanager.Character("lsa_test", 2, 1)
	wait(1)
	-- 試作ルートに入る (エンディングの条件。scenario.json の endings)
	statmodifymanager.SetFlag("lsa_route", 1)
elseif choice == 7 then
	-- 本編の曲へ。自前の曲は本編と同じく止まって切り替わる
	luamanager.PlayMusic("普通_001")
	setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "normal"))
	say("……いつもの調子に戻ったな。")
end

stage.hide{character=characters.Get("lsa_test"), fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)

if choice == 6 then
	-- 死亡エンディング (scenario.json の deaths)。奪命譜に記録され、タイトルへ戻る
	luamanager.ChangeScene("GameOver", "lsa_dead_test", "")
elseif choice == 5 then
	-- 決闘 (scenario.json の duels.lsa_test_spar)。終わったら lsa_test_duel_after へ戻る
	luamanager.SetCurrentStoryScript("lsa_test_duel_after")
	luamanager.ChangeScene("Combat", "lsa_test_spar", "Story")
elseif choice == 4 then
	-- 差し替えた本来の行動 (裏山なら薪割り・修練のメニュー) へ
	luamanager.SetNextScript("lsa_position_default")
	luamanager.Init()
else
	runblock(flowcharts.view, "out")
	luamanager.ChangeScene("Free", "", "")
end
