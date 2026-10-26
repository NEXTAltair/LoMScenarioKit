// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
//! Agent interface: JSON on stdout on every path, including invalid arguments.
use anyhow::{bail, ensure, Context, Result};
use lom_core::{
    project::Project,
    review::{self, Actor},
    scenario_addon, workspace_store as store,
};
use serde_json::{json, Value};
use std::{collections::BTreeMap, io::Read, path::PathBuf};

fn catalog() -> Value {
    let entries = [
        ("commands", "コマンド一覧と入出力を調べる", false, "なし"),
        (
            "describe",
            "1コマンドの目的と入力を調べる",
            false,
            "command",
        ),
        (
            "init",
            "新しい制作プロジェクトを作る",
            true,
            "title?; --project は新しいフォルダ",
        ),
        (
            "checkout",
            "編集用の全文と revision を取得する",
            false,
            "なし",
        ),
        ("show", "章と添削の文脈を読む", false, "story"),
        (
            "save",
            "checkout の document を保存する",
            true,
            "revision, document{manifest,scenario,stories}",
        ),
        (
            "edit",
            "既存のノード編集APIを章に適用する",
            true,
            "revision, story, operations[]（story_api::apply）",
        ),
        (
            "verdict",
            "章またはノードの判定を変更する",
            true,
            "revision, story, node?, verdict: unreviewed|good|fix|hold|reject",
        ),
        ("comments", "章のコメントと返信を読む", false, "story"),
        (
            "comment",
            "引用付きの指摘または返信を追加する",
            true,
            "revision, story, node?, text, parent?",
        ),
        (
            "resolve",
            "指摘の解決状態を変更する",
            true,
            "revision, id, resolved: boolean",
        ),
        (
            "history",
            "保存履歴と編集者を読む",
            false,
            "limit?: 1..1000",
        ),
        ("entry", "保存履歴の変更前後を読む", false, "id"),
        (
            "restore",
            "指定履歴から1章を復元する",
            true,
            "revision, id, story（現在の添削は保持）",
        ),
        (
            "outline",
            "outline/*.md の資料・見出しを検索する",
            false,
            "query?",
        ),
        ("chart", "章の分岐と到達可能性を読む", false, "story"),
        (
            "check",
            "全章とAddon書き出しの対応状況を検査する",
            false,
            "なし",
        ),
        (
            "export",
            "scenario.json と公式形式のLuaを書き出す",
            true,
            "output: 新しい出力フォルダのパス",
        ),
    ];
    json!({"commands":entries.iter().map(|(command,purpose,mutates,input)|json!({"command":command,"purpose":purpose,"mutates":mutates,"input":input})).collect::<Vec<_>>(),
        "usage":"lomc agent <command> --project <folder> --input <JSON-file|->",
        "request":"入力はJSONオブジェクト。更新時は checkout の revision と、name（任意、既定 Codex）・message（任意）を渡す。dry_run:true は検証のみ。",
        "response":"{schema_version,command,ok,purpose,data,issues,next_actions}; エラー時もJSON、終了コード1、競合は2。",
        "edit_operations": ["add_node","update_node","delete_node","move_node","rename_node","set_start"],
        "edit_examples":[{"op":"update_node","node_id":"say1","fields":{"text":"変更後の本文"}},{"op":"add_node","node_type":"wait","after":"say1","fields":{"seconds":1}}],
        "supported_addon_nodes":scenario_addon::SUPPORTED})
}
fn spec(command: &str) -> Value {
    catalog()["commands"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["command"] == command)
        .cloned()
        .unwrap_or(json!({"purpose":"コマンドを確認する"}))
}
fn required<'a>(input: &'a Value, key: &str) -> Result<&'a str> {
    input[key]
        .as_str()
        .with_context(|| format!("入力 {key} は文字列が必要です"))
}
fn optional<'a>(input: &'a Value, key: &str, default: &'a str) -> &'a str {
    input[key].as_str().unwrap_or(default)
}
fn validate(project: &Project) -> Result<()> {
    ensure!(
        project.manifest.is_object(),
        "manifest はオブジェクトが必要です"
    );
    ensure!(!project.stories.is_empty(), "1つ以上の章が必要です");
    for (id, story) in &project.stories {
        ensure!(story["id"] == *id, "章IDが一致していません: {id}");
        lom_core::validate::validate_story(story).with_context(|| format!("章 {id}"))?;
    }
    ensure!(
        project.manifest["entry"]
            .as_str()
            .is_some_and(|id| project.stories.contains_key(id)),
        "開始する章 entry がありません"
    );
    review::load(project)?;
    Ok(())
}
fn execute(command: &str, path: Option<PathBuf>, input: &Value) -> Result<Value> {
    if command == "commands" {
        return Ok(catalog());
    }
    if command == "describe" {
        let name = required(input, "command")?;
        let entry = spec(name);
        ensure!(entry["command"] == name, "コマンドがありません: {name}");
        return Ok(json!({"spec":entry,"protocol":catalog()}));
    }
    ensure!(
        spec(command)["command"] == command,
        "不明なコマンド: {command}"
    );
    for key in ["dry_run", "resolved"] {
        if let Some(value) = input.get(key) {
            ensure!(value.is_boolean(), "{key} は boolean が必要です");
        }
    }
    for key in ["name", "message", "title", "node", "parent", "query"] {
        if let Some(value) = input.get(key) {
            ensure!(
                value.is_string() || (["node", "parent"].contains(&key) && value.is_null()),
                "{key} は文字列が必要です"
            );
        }
    }
    let root = path.context("--project <制作フォルダ> を指定してください")?;
    let name = optional(input, "name", "Codex");
    let message = optional(input, "message", command);
    let dry_run = input["dry_run"].as_bool().unwrap_or(false);
    if command == "init" {
        ensure!(
            !root.exists(),
            "作成先が存在します。新しいフォルダを指定してください"
        );
        let mut p = scenario_addon::new_project(optional(input, "title", "新しい物語"));
        let token = if dry_run {
            store::revision(&p)?
        } else {
            store::save(&mut p, &root, None, Actor::Agent, name, message)?
        };
        return Ok(
            json!({"project":root,"revision":token,"document":store::document(&p),"dry_run":dry_run}),
        );
    }
    let (mut project, token) = store::read(&root)?;
    let node = input["node"].as_str();
    let data = match command {
        "checkout" => return Ok(json!({"revision":token,"document":store::document(&project)})),
        "show" => {
            let story = required(input, "story")?;
            json!({"story":project.stories.get(story).context("章がありません")?,"review":review::context(&project,story)?})
        }
        "comments" => review::context(&project, required(input, "story")?)?,
        "outline" => json!(review::outline(&root, optional(input, "query", ""))?),
        "chart" => store::chart(&project, required(input, "story")?)?,
        "history" => {
            let limit = input["limit"].as_u64().unwrap_or(50);
            ensure!((1..=1000).contains(&limit), "limit は1〜1000です");
            json!(store::history(&project, limit as usize)?
                .iter()
                .map(|entry| {
                    let mut row = entry.clone();
                    row.as_object_mut().unwrap().remove("before");
                    row.as_object_mut().unwrap().remove("after");
                    row
                })
                .collect::<Vec<_>>())
        }
        "entry" => store::entry(&project, required(input, "id")?)?,
        "check" => {
            validate(&project)?;
            let files = scenario_addon::output(&project)?;
            json!({"valid":true,"files":files.keys().collect::<Vec<_>>(),"scope":"章の構造・対応ノード・章参照。配布前は LoMScenarioTool check でゲーム側の設定・素材も検査してください。"})
        }
        "export" => {
            let output = PathBuf::from(required(input, "output")?);
            let files = scenario_addon::output(&project)?;
            if !dry_run {
                scenario_addon::export(&project, &output)?;
            }
            json!({"output":output,"files":files.keys().collect::<Vec<_>>(),"dry_run":dry_run})
        }
        _ => {
            let expected = required(input, "revision")?;
            if expected != token {
                return Err(store::Conflict {
                    current_revision: token,
                }
                .into());
            }
            let result = match command {
                "save" => {
                    let document = input["document"]
                        .as_object()
                        .context("document がありません")?;
                    ensure!(
                        document.get("manifest").map(|m| &m["_editor"]["review"])
                            == Some(&project.manifest["_editor"]["review"]),
                        "添削データは verdict/comment/resolve で変更してください"
                    );
                    project.manifest = document
                        .get("manifest")
                        .context("manifest がありません")?
                        .clone();
                    project.scenario = document.get("scenario").filter(|s| !s.is_null()).cloned();
                    project.stories = serde_json::from_value(
                        document
                            .get("stories")
                            .context("stories がありません")?
                            .clone(),
                    )?;
                    json!({})
                }
                "edit" => {
                    let story = required(input, "story")?;
                    let next = lom_core::story_api::apply(
                        project.stories.get(story).context("章がありません")?,
                        &input["operations"],
                    )?;
                    project.stories.insert(story.into(), next);
                    json!({"story":story})
                }
                "verdict" => {
                    review::set_verdict(
                        &mut project,
                        required(input, "story")?,
                        node,
                        serde_json::from_value(input["verdict"].clone())?,
                    )?;
                    json!({"verdict":input["verdict"]})
                }
                "comment" => {
                    json!({"id":review::comment(&mut project,required(input,"story")?,node,required(input,"text")?,input["parent"].as_str(),Actor::Agent,name)?})
                }
                "resolve" => {
                    review::resolve(
                        &mut project,
                        required(input, "id")?,
                        input["resolved"]
                            .as_bool()
                            .context("resolved は boolean が必要です")?,
                    )?;
                    json!({"id":input["id"]})
                }
                "restore" => {
                    store::restore(
                        &mut project,
                        required(input, "id")?,
                        required(input, "story")?,
                    )?;
                    json!({"story":input["story"]})
                }
                _ => bail!("不明なコマンド: {command}"),
            };
            validate(&project)?;
            let revision = if dry_run {
                token
            } else {
                store::save(
                    &mut project,
                    &root,
                    Some(expected),
                    Actor::Agent,
                    name,
                    message,
                )?
            };
            return Ok(json!({"revision":revision,"result":result,"dry_run":dry_run}));
        }
    };
    Ok(json!({"revision":token,"result":data}))
}
pub fn run(args: &[String]) -> i32 {
    let command = args.first().map(String::as_str).unwrap_or("commands");
    let command = if ["--help", "-h"].contains(&command) {
        "commands"
    } else {
        command
    };
    let mut project = None;
    let response = (|| -> Result<Value> {
        let mut options = BTreeMap::new();
        let mut i = 1;
        while i < args.len() {
            let key = &args[i];
            ensure!(
                ["--project", "--input"].contains(&key.as_str()),
                "不明なオプション: {key}"
            );
            ensure!(
                !options.contains_key(key),
                "オプションが重複しています: {key}"
            );
            options.insert(
                key.clone(),
                args.get(i + 1)
                    .context("オプションの値がありません")?
                    .clone(),
            );
            i += 2;
        }
        project = options.get("--project").map(PathBuf::from);
        let input = if let Some(path) = options.get("--input") {
            if path == "-" {
                let mut bytes = Vec::new();
                std::io::stdin()
                    .take(16 * 1024 * 1024 + 1)
                    .read_to_end(&mut bytes)?;
                ensure!(
                    bytes.len() <= 16 * 1024 * 1024,
                    "入力は16MB以下にしてください"
                );
                serde_json::from_slice(&bytes)?
            } else {
                lom_core::load_json(path)?
            }
        } else {
            json!({})
        };
        ensure!(input.is_object(), "入力はJSONオブジェクトが必要です");
        execute(command, project.clone(), &input)
    })();
    let mut code = 0;
    let (data, issues) = match response {
        Ok(data) => (data, json!([])),
        Err(error) => {
            code = 1;
            let conflict = error.downcast_ref::<store::Conflict>();
            if conflict.is_some() {
                code = 2;
            }
            (
                Value::Null,
                json!([{"code":if conflict.is_some(){"revision_conflict"}else{"invalid_request"},"message":format!("{error:#}"),"current_revision":conflict.map(|e|&e.current_revision)}]),
            )
        }
    };
    let mut next = vec![
        json!({"argv":["lomc","agent","commands"],"reason":"利用可能な操作と入力を確認する","mutates":false}),
    ];
    if let Some(root) = project {
        next.insert(0,json!({"argv":["lomc","agent","checkout","--project",root],"reason":"最新の内容と revision を取得し、指摘・変更の前提を確認する","mutates":false}));
        next.push(json!({"argv":["lomc","agent","check","--project",root],"reason":"変更後に全章と書き出しを検査する","mutates":false}));
    }
    println!(
        "{}",
        json!({"schema_version":1,"command":command,"ok":code==0,"purpose":spec(command)["purpose"],"data":data,"issues":issues,"next_actions":next})
    );
    code
}
