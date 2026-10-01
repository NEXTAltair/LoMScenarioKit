-- Copyright (C) 2026 NEXTAltair
-- Code: GPL-3.0-only with the project game-linking exception.
-- Dialogue/narrative: CC BY-NC-SA 4.0. See scenario/LICENSE.
-- 決闘 lsa_test_spar の後。勝敗はフラグ lsa_test_spar_won (勝ち 1 / 負け 0)。
runwait(flowcharts.LoadView("backmountain2"))
getvar(flowcharts.view, "ViewName").value = "backmountain2"
runblock(flowcharts.view, "view")

runwait(characters.LoadCharacterAsset("lsa_test"))
stage.show{character=characters.Get("lsa_test"), portrait=characters.GetPortrait("lsa_test", "normal"), fromPosition="R2", toPosition="R2", facing="left", fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)

setsaydialog(saydialogs.character)
characters.Focus("lsa_test")
if luamanager.GetFlagData("lsa_test_spar_won") == "1" then
	characters.LoadCharacterPortrait("lsa_test", "smile")
	stage.showPortrait(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
	setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "smile"))
	say("参った参った。唐門の弟子は伊達じゃないな。")
	statmodifymanager.Character("lsa_test", 1, 1)
	wait(1)
	say("勝った褒美だ。こいつを持っていけ。")
	-- 天賦 (scenario.json の talents.lsa_tal_guard)。取得の表示・人物画面・決闘の効果は本編と同じ
	statmodifymanager.AddTalent("lsa_tal_guard", 1)
	wait(1)
	-- 秘笈 (scenario.json の books.lsa_book_test)。修練の画面から読み進められる
	statmodifymanager.AddBook("lsa_book_test", 1)
	-- 入手の表示は本編と同じく別の命令。文 (Story/M_Add_Book_<ID>) が無ければ秘笈の名前から自動で作る
	mainui.DisplayMessage("M_Add_Book_lsa_book_test")
	wait(1)
else
	setcharacter(characters.Get("lsa_test"), characters.GetPortrait("lsa_test", "normal"))
	say("まだまだだな。また相手してやるよ。")
end

stage.hide{character=characters.Get("lsa_test"), fadeDuration=0.5, useDefaultSettings=false}
wait(0.5)
runblock(flowcharts.view, "out")
luamanager.ChangeScene("Free", "", "")
