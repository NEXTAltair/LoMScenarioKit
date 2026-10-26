// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
use serde_json::{json, Value};
use std::{
    io::Write,
    path::Path,
    process::{Command, Stdio},
};

fn agent(command: &str, root: &Path, input: Value, exit: i32) -> Value {
    let mut child = Command::new(env!("CARGO_BIN_EXE_lomc"))
        .args(["agent", command, "--project"])
        .arg(root)
        .args(["--input", "-"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(input.to_string().as_bytes())
        .unwrap();
    let output = child.wait_with_output().unwrap();
    let value: Value = serde_json::from_slice(&output.stdout)
        .unwrap_or_else(|e| panic!("JSON expected: {e}; {:?}", output));
    assert_eq!(output.status.code(), Some(exit), "{value}");
    assert!(value["purpose"].is_string());
    assert!(!value["next_actions"].as_array().unwrap().is_empty());
    value
}

#[test]
fn shared_authoring_protocol_handles_comments_conflicts_restore_and_export() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("制作");
    let init = agent("init", &root, json!({"title":"CLI試験"}), 0);
    let revision = init["data"]["revision"].clone();
    let original = init["data"]["document"]["stories"]["main"].clone();
    let first = init["data"]["document"]["manifest"]["_editor"]["authoring_head"].clone();
    let posted = agent(
        "comment",
        &root,
        json!({"revision":revision,"story":"main","node":"say1","text":"短くしてほしい","name":"Codex"}),
        0,
    );
    let id = posted["data"]["result"]["id"].clone();
    let stale = agent(
        "edit",
        &root,
        json!({"revision":revision,"story":"main","operations":[{"op":"update_node","node_id":"say1","fields":{"text":"古い編集"}}]}),
        2,
    );
    assert_eq!(stale["issues"][0]["code"], "revision_conflict");
    let edited = agent(
        "edit",
        &root,
        json!({"revision":posted["data"]["revision"],"story":"main","operations":[{"op":"update_node","node_id":"say1","fields":{"text":"修正済み"}}]}),
        0,
    );
    let reply = agent(
        "comment",
        &root,
        json!({"revision":edited["data"]["revision"],"story":"main","parent":id,"text":"反映しました"}),
        0,
    );
    let resolved = agent(
        "resolve",
        &root,
        json!({"revision":reply["data"]["revision"],"id":id,"resolved":true}),
        0,
    );
    let restored = agent(
        "restore",
        &root,
        json!({"revision":resolved["data"]["revision"],"id":first,"story":"main"}),
        0,
    );
    let checkout = agent("checkout", &root, json!({}), 0);
    assert_eq!(checkout["data"]["document"]["stories"]["main"], original);
    let comments = agent("comments", &root, json!({"story":"main"}), 0);
    assert_eq!(comments["data"]["result"]["comments"][0]["resolved"], true);
    assert_eq!(comments["data"]["result"]["comments"][1]["actor"], "agent");
    let dry = agent(
        "verdict",
        &root,
        json!({"revision":restored["data"]["revision"],"story":"main","verdict":"good","dry_run":true}),
        0,
    );
    assert_eq!(dry["data"]["revision"], checkout["data"]["revision"]);
    let again = agent("checkout", &root, json!({}), 0);
    assert_eq!(checkout["data"], again["data"]);
    agent("check", &root, json!({}), 0);
    let output = temp.path().join("scenario");
    agent("export", &root, json!({"output":output}), 0);
    let lua = std::fs::read_to_string(output.join("story/main.lua")).unwrap();
    assert!(lua.contains("say("));
    assert!(!lua.contains("mod_"));
    agent("export", &root, json!({"output":output}), 1);
    let chart = agent("chart", &root, json!({"story":"main"}), 0);
    assert_eq!(
        chart["data"]["result"]["nodes"].as_array().unwrap().len(),
        2
    );
    let history = agent("history", &root, json!({}), 0);
    assert_eq!(history["data"]["result"].as_array().unwrap().len(), 6);
}

#[test]
fn errors_and_discovery_are_json_and_failed_save_is_non_destructive() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("project");
    agent("commands", &root, json!({}), 0);
    agent("unknown", &root, json!({}), 1);
    let init = agent("init", &root, json!({}), 0);
    let mut document = init["data"]["document"].clone();
    document["stories"]["main"]["start"] = json!("missing");
    agent(
        "save",
        &root,
        json!({"revision":init["data"]["revision"],"document":document}),
        1,
    );
    let next = agent("checkout", &root, json!({}), 0);
    assert_eq!(next["data"]["document"], init["data"]["document"]);
    let output = Command::new(env!("CARGO_BIN_EXE_lomc"))
        .args(["agent", "checkout", "--oops"])
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(1));
    let error: Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(error["ok"], false);
}

#[test]
fn official_intro_created_by_edit_can_be_checked_and_exported() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("intro-project");
    let init = agent("init", &root, json!({}), 0);
    let edited = agent(
        "edit",
        &root,
        json!({
            "revision": init["data"]["revision"], "story": "main",
            "operations": [{"op":"add_node","node_type":"intro","after":"say1","fields":{"character":"brother1"}}]
        }),
        0,
    );
    agent("check", &root, json!({}), 0);
    let output = temp.path().join("scenario");
    agent("export", &root, json!({"output":output}), 0);
    assert!(std::fs::read_to_string(output.join("story/main.lua"))
        .unwrap()
        .contains("runwait(intropanel.Show(\"brother1\"))"));
    agent(
        "verdict",
        &root,
        json!({"revision":edited["data"]["revision"],"story":"main","verdict":"good","dry_run":"true"}),
        1,
    );
    let unchanged = agent("checkout", &root, json!({}), 0);
    assert_eq!(unchanged["data"]["revision"], edited["data"]["revision"]);
}

#[test]
fn agent_can_edit_an_imported_folder_without_a_manifest() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path();
    let project = lom_core::project::Project::new();
    std::fs::write(
        root.join("chapter.json"),
        project.stories["main"].to_string(),
    )
    .unwrap();
    let checkout = agent("checkout", root, json!({}), 0);
    agent(
        "edit",
        root,
        json!({
            "revision":checkout["data"]["revision"],"story":"main",
            "operations":[{"op":"update_node","node_id":"say1","fields":{"text":"読み込んだ章を編集"}}]
        }),
        0,
    );
    let saved = agent("show", root, json!({"story":"main"}), 0);
    assert_eq!(
        saved["data"]["result"]["story"]["nodes"][0]["text"],
        "読み込んだ章を編集"
    );
    assert!(root.join("manifest.json").is_file());
}

#[test]
fn localized_project_stays_editable_but_check_and_export_reject_it() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("localized-project");
    let init = agent("init", &root, json!({}), 0);
    let mut document = init["data"]["document"].clone();
    document["stories"]["main"]["localization"] = json!({
        "default_locale":"ja", "fallback_locale":"ja",
        "translations":{"cht":{"say1.text":"繁體台詞"}}
    });
    agent(
        "save",
        &root,
        json!({"revision":init["data"]["revision"],"document":document}),
        0,
    );
    let before = agent("checkout", &root, json!({}), 0);
    let output = temp.path().join("scenario");
    for command in ["check", "export"] {
        let rejected = agent(command, &root, json!({"output":output}), 1);
        assert_eq!(rejected["ok"], false);
        assert!(rejected["issues"].to_string().contains("localization"));
        assert!(rejected["issues"].to_string().contains("章 main"));
    }
    assert!(!output.exists());
    let after = agent("checkout", &root, json!({}), 0);
    assert_eq!(before["data"], after["data"]);
    assert_eq!(
        after["data"]["document"]["stories"]["main"]["localization"],
        document["stories"]["main"]["localization"]
    );
}

#[test]
fn adding_metadata_named_chapters_to_a_flat_import_fails_without_changing_the_project() {
    for id in ["scenario", "manifest"] {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path();
        let original = lom_core::project::Project::new().stories["main"].to_string();
        std::fs::write(root.join("chapter.json"), &original).unwrap();
        let checkout = agent("checkout", root, json!({}), 0);
        let mut document = checkout["data"]["document"].clone();
        let mut chapter = document["stories"]["main"].clone();
        chapter["id"] = json!(id);
        document["stories"][id] = chapter;
        let rejected = agent(
            "save",
            root,
            json!({"revision":checkout["data"]["revision"],"document":document}),
            1,
        );
        assert!(rejected["issues"].to_string().contains("予約ファイル名"));
        let after = agent("checkout", root, json!({}), 0);
        assert_eq!(after["data"], checkout["data"]);
        assert_eq!(
            std::fs::read_to_string(root.join("chapter.json")).unwrap(),
            original
        );
        assert!(!root.join("manifest.json").exists());
        assert!(!root.join("scenario.json").exists());
        assert_eq!(
            agent("history", root, json!({}), 0)["data"]["result"],
            json!([])
        );
    }
}

#[test]
fn renaming_localized_nodes_preserves_all_locale_catalogs() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("localized-project");
    let init = agent("init", &root, json!({}), 0);
    let mut document = init["data"]["document"].clone();
    let story = &mut document["stories"]["main"];
    story["nodes"] = json!([
        {"id":"say1","type":"say","mode":"narrative","text":"本文"},
        {"id":"say10","type":"say","mode":"narrative","text":"別の本文"},
        {"id":"choice1","type":"choice","options":[{"text":"戻る","goto":"say1"},{"text":"終わる","goto":"end1"}]},
        {"id":"end1","type":"end"}
    ]);
    story["localization"] = json!({"default_locale":"ja","translations":{
        "cht":{"say1.text":"繁體本文","say10.text":"別の繁體本文","choice1.options.0.text":"戻る繁體","story.title":"繁體章"},
        "ko":{"say1.text":"한국어","choice1.options.0.text":"돌아가기","story.title":"제목"}
    }});
    let original_catalogs = story["localization"]["translations"].clone();
    let saved = agent(
        "save",
        &root,
        json!({"revision":init["data"]["revision"],"document":document}),
        0,
    );
    agent(
        "edit",
        &root,
        json!({"revision":saved["data"]["revision"],"story":"main","operations":[
            {"op":"rename_node","node_id":"say1","new_id":"opening"},
            {"op":"rename_node","node_id":"choice1","new_id":"decision"}
        ]}),
        0,
    );
    let checkout = agent("checkout", &root, json!({}), 0);
    let story = &checkout["data"]["document"]["stories"]["main"];
    lom_core::validate::validate_story(story).unwrap();
    assert_eq!(story["start"], "opening");
    assert_eq!(story["nodes"][2]["options"][0]["goto"], "opening");
    for locale in ["cht", "ko"] {
        let catalog = &story["localization"]["translations"][locale];
        assert_eq!(
            catalog["opening.text"],
            original_catalogs[locale]["say1.text"]
        );
        assert_eq!(
            catalog["decision.options.0.text"],
            original_catalogs[locale]["choice1.options.0.text"]
        );
        assert_eq!(
            catalog["story.title"],
            original_catalogs[locale]["story.title"]
        );
        assert_eq!(
            catalog["say10.text"],
            original_catalogs[locale]["say10.text"]
        );
        assert!(catalog.get("say1.text").is_none());
        assert!(catalog.get("choice1.options.0.text").is_none());
    }
}
