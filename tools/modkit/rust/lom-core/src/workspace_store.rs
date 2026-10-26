// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
//! Shared GUI/agent saves with a content revision, file lock and immutable history.
use crate::{
    load_json,
    project::{atomic_write, Project},
    review::{self, Actor},
    stable_json,
};
use anyhow::{ensure, Context, Result};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeSet,
    fs::{self, File, OpenOptions},
    path::{Path, PathBuf},
};

#[derive(Debug)]
pub struct Conflict {
    pub current_revision: String,
}
impl std::fmt::Display for Conflict {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "別の変更が保存されています。最新版を取得して変更内容を比較してください"
        )
    }
}
impl std::error::Error for Conflict {}

pub fn document(project: &Project) -> Value {
    json!({"manifest": project.manifest, "scenario": project.scenario, "stories": project.stories})
}

pub fn revision(project: &Project) -> Result<String> {
    let mut digest = Sha256::new();
    let bytes = stable_json(&document(project))?;
    digest.update((bytes.len() as u64).to_le_bytes());
    digest.update(bytes);
    for (path, content) in &project.assets {
        digest.update((path.len() as u64).to_le_bytes());
        digest.update(path.as_bytes());
        digest.update((content.len() as u64).to_le_bytes());
        digest.update(content);
    }
    Ok(format!("{:x}", digest.finalize()))
}

pub fn root(project: &Project) -> Option<&Path> {
    project.source.as_deref().filter(|p| p.is_dir())
}

fn metadata_dir(root: &Path) -> Result<PathBuf> {
    let folder = root.join(".authoring");
    ensure!(
        !folder.is_symlink(),
        ".authoring にシンボリックリンクは使えません"
    );
    fs::create_dir_all(&folder)?;
    Ok(folder)
}

fn lock(root: &Path) -> Result<File> {
    let path = metadata_dir(root)?.join("workspace.lock");
    ensure!(
        !path.is_symlink(),
        "保存ロックにシンボリックリンクは使えません"
    );
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(path)?;
    file.lock()
        .context("制作プロジェクトの保存ロックを取得できません")?;
    Ok(file)
}

pub fn read(path: &Path) -> Result<(Project, String)> {
    ensure!(
        path.is_dir(),
        "共有編集には制作プロジェクトのフォルダを指定してください"
    );
    let _guard = lock(path)?;
    let project = Project::open(path)?;
    let token = revision(&project)?;
    Ok((project, token))
}

fn history_path(root: &Path, id: &str) -> Result<PathBuf> {
    uuid::Uuid::parse_str(id).context("履歴IDが不正です")?;
    let directory = metadata_dir(root)?.join("history");
    ensure!(
        !directory.is_symlink(),
        "履歴フォルダにシンボリックリンクは使えません"
    );
    fs::create_dir_all(&directory)?;
    let path = directory.join(format!("{id}.json"));
    ensure!(
        !path.is_symlink(),
        "履歴ファイルにシンボリックリンクは使えません"
    );
    Ok(path)
}

pub fn save(
    project: &mut Project,
    target: &Path,
    expected: Option<&str>,
    actor: Actor,
    name: &str,
    message: &str,
) -> Result<String> {
    save_with(
        project,
        target,
        expected,
        actor,
        name,
        message,
        |project, target| {
            if expected.is_some() || target.join("manifest.json").is_file() {
                let previous = Project::open(target)?;
                crate::project_files::save_project(
                    project,
                    target,
                    &previous.assets,
                    &previous.stories,
                    &previous.paths,
                )
            } else {
                project.save_to(target)
            }
        },
    )
}

/// The writer may add the editor's existing ownership checks for deleted files.
/// The same lock covers the comparison, write, and cleanup.
pub fn save_with<F>(
    project: &mut Project,
    target: &Path,
    expected: Option<&str>,
    actor: Actor,
    name: &str,
    message: &str,
    writer: F,
) -> Result<String>
where
    F: FnOnce(&mut Project, &Path) -> Result<()>,
{
    ensure!(
        !name.trim().is_empty() && name.chars().count() <= 100,
        "編集者名を指定してください"
    );
    ensure!(
        message.chars().count() <= 2000,
        "変更理由は2000文字以下にしてください"
    );
    ensure!(
        !target.is_symlink() && !target.is_file(),
        "共有編集は通常のプロジェクトフォルダへ保存してください"
    );
    fs::create_dir_all(target)?;
    let target = target.canonicalize()?;
    let _guard = lock(&target)?;
    let existing = if expected.is_some() || target.join("manifest.json").exists() {
        Some(Project::open(&target)?)
    } else {
        // A directory of chapter JSON files is also an existing project.
        // Never merge a new project into it just because it has no manifest.
        Project::open(&target).ok()
    };
    let before = if let Some(current) = existing {
        let actual = revision(&current)?;
        if expected != Some(actual.as_str()) {
            return Err(Conflict {
                current_revision: actual,
            }
            .into());
        }
        if actual == revision(project)? {
            return Ok(actual);
        }
        Some(current)
    } else {
        ensure!(
            expected.is_none(),
            "保存先が変更されています。元のプロジェクトを確認してください"
        );
        None
    };
    review::load(project)?;
    let previous_manifest = project.manifest.clone();
    if project.manifest.get("_editor").is_none() {
        project.manifest["_editor"] = json!({});
    }
    ensure!(
        project.manifest["_editor"].is_object(),
        "manifest._editor はオブジェクトにしてください"
    );
    let id = uuid::Uuid::new_v4().to_string();
    let parent = before
        .as_ref()
        .and_then(|p| p.manifest["_editor"]["authoring_head"].as_str())
        .map(str::to_owned);
    project.manifest["_editor"]["authoring_head"] = json!(id);
    let record = json!({
        "id": id, "parent": parent, "created_at_ms": review::now_ms(),
        "actor": actor, "name": name, "message": message,
        "before": before.as_ref().map(document), "after": document(project)
    });
    let path = history_path(&target, &id)?;
    ensure!(!path.exists(), "履歴IDが重複しました");
    // Write history before project files. Only the chain referenced by the saved
    // manifest is visible, so an interrupted save cannot publish phantom history.
    if let Err(error) =
        atomic_write(&path, &stable_json(&record)?).and_then(|_| writer(project, &target))
    {
        project.manifest = previous_manifest;
        return Err(error);
    }
    revision(project)
}

pub fn history(project: &Project, limit: usize) -> Result<Vec<Value>> {
    let root = root(project).context("履歴は保存済みのプロジェクトで利用できます")?;
    let mut id = project.manifest["_editor"]["authoring_head"]
        .as_str()
        .map(str::to_owned);
    let mut seen = BTreeSet::new();
    let mut rows = Vec::new();
    while let Some(current) = id {
        ensure!(seen.insert(current.clone()), "履歴の接続に循環があります");
        let entry = load_json(history_path(root, &current)?)?;
        ensure!(entry["id"] == current, "履歴IDとファイル内容が一致しません");
        id = entry["parent"].as_str().map(str::to_owned);
        rows.push(entry);
        if rows.len() >= limit.min(1000) {
            break;
        }
    }
    Ok(rows)
}

pub fn entry(project: &Project, id: &str) -> Result<Value> {
    history(project, 1000)?
        .into_iter()
        .find(|e| e["id"] == id)
        .context("このプロジェクトの履歴が見つかりません")
}

/// Restore one chapter while keeping current comments, verdicts and other chapters.
pub fn restore(project: &mut Project, id: &str, story: &str) -> Result<()> {
    let record = entry(project, id)?;
    let value = record["after"]["stories"]
        .get(story)
        .context("この履歴には対象の章がありません")?;
    project.stories.insert(story.to_owned(), value.clone());
    Ok(())
}

/// Returns the existing native graph, retaining node identifiers for GUI navigation.
pub fn chart(project: &Project, story: &str) -> Result<Value> {
    let story = project.stories.get(story).context("章がありません")?;
    let graph = crate::analysis::story_graph(story);
    let reached =
        crate::analysis::reachable(&graph, [story["start"].as_str().unwrap_or("").to_owned()]);
    let nodes = story["nodes"].as_array().context("nodes がありません")?;
    Ok(json!({
        "nodes": nodes.iter().map(|n| json!({"id":n["id"],"type":n["type"],"text":n["text"],"reachable":reached.contains(n["id"].as_str().unwrap_or(""))})).collect::<Vec<_>>(),
        "edges": graph.iter().flat_map(|(source, targets)| targets.iter().map(move |target| json!({"source":source,"target":target}))).collect::<Vec<_>>(),
        "conditions_evaluated":false
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manifestless_folders_compare_revisions_and_keep_deleted_chapters_deleted() {
        for nested in [false, true] {
            let temp = tempfile::tempdir().unwrap();
            let folder = if nested {
                temp.path().join("story")
            } else {
                temp.path().to_owned()
            };
            fs::create_dir_all(&folder).unwrap();
            let original = Project::new();
            let mut extra = original.stories["main"].clone();
            extra["id"] = json!("extra");
            fs::write(
                folder.join("chapter-a.json"),
                stable_json(&original.stories["main"]).unwrap(),
            )
            .unwrap();
            fs::write(folder.join("chapter-b.json"), stable_json(&extra).unwrap()).unwrap();
            let (mut editing, token) = read(temp.path()).unwrap();
            assert!(!editing.has_manifest);
            let mut stale = editing.clone();
            let error = save(
                &mut Project::new(),
                temp.path(),
                None,
                Actor::Agent,
                "Codex",
                "新規作成",
            )
            .unwrap_err();
            assert!(error.downcast_ref::<Conflict>().is_some());
            editing.stories.get_mut("main").unwrap()["title"] = json!("既存の章を編集");
            editing.stories.remove("extra");
            let saved = save(
                &mut editing,
                temp.path(),
                Some(&token),
                Actor::Human,
                "作者",
                "編集",
            )
            .unwrap();
            let (disk, revision) = read(temp.path()).unwrap();
            assert_eq!(revision, saved);
            assert_eq!(disk.stories["main"]["title"], "既存の章を編集");
            assert!(!disk.stories.contains_key("extra"));
            assert!(!folder.join("chapter-b.json").exists());
            assert!(temp.path().join("manifest.json").is_file());
            stale.stories.get_mut("main").unwrap()["title"] = json!("古い編集");
            let error = save(
                &mut stale,
                temp.path(),
                Some(&token),
                Actor::Agent,
                "Codex",
                "競合",
            )
            .unwrap_err();
            assert!(error.downcast_ref::<Conflict>().is_some());
        }
    }
    #[test]
    fn stale_gui_or_agent_save_cannot_overwrite_and_history_preserves_both_authors() {
        let temp = tempfile::tempdir().unwrap();
        let mut original = Project::new();
        let first = save(
            &mut original,
            temp.path(),
            None,
            Actor::Human,
            "作者",
            "作成",
        )
        .unwrap();
        let (mut agent, revision) = read(temp.path()).unwrap();
        assert_eq!(first, revision);
        let mut human = agent.clone();
        human.stories.get_mut("main").unwrap()["title"] = json!("人間の編集");
        save(
            &mut human,
            temp.path(),
            Some(&first),
            Actor::Human,
            "作者",
            "編集",
        )
        .unwrap();
        agent.stories.get_mut("main").unwrap()["title"] = json!("古い編集");
        let error = save(
            &mut agent,
            temp.path(),
            Some(&first),
            Actor::Agent,
            "Codex",
            "競合",
        )
        .unwrap_err();
        assert!(error.downcast_ref::<Conflict>().is_some());
        let (mut fresh, token) = read(temp.path()).unwrap();
        assert_eq!(fresh.stories["main"]["title"], "人間の編集");
        review::comment(
            &mut fresh,
            "main",
            None,
            "確認しました",
            None,
            Actor::Agent,
            "Codex",
        )
        .unwrap();
        save(
            &mut fresh,
            temp.path(),
            Some(&token),
            Actor::Agent,
            "Codex",
            "添削",
        )
        .unwrap();
        let history = history(&fresh, 20).unwrap();
        assert_eq!(history.len(), 3);
        assert_eq!(history[0]["actor"], "agent");
        assert_eq!(history[1]["actor"], "human");
    }
    #[test]
    fn failed_writer_does_not_publish_history_or_change_in_memory_revision() {
        let temp = tempfile::tempdir().unwrap();
        let mut project = Project::new();
        let token = save(
            &mut project,
            temp.path(),
            None,
            Actor::Human,
            "作者",
            "作成",
        )
        .unwrap();
        let original = project.manifest.clone();
        project.stories.get_mut("main").unwrap()["title"] = json!("編集");
        let result = save_with(
            &mut project,
            temp.path(),
            Some(&token),
            Actor::Agent,
            "Codex",
            "失敗",
            |_, _| anyhow::bail!("書き込み失敗"),
        );
        assert!(result.is_err());
        assert_eq!(project.manifest, original);
        let (stored, actual) = read(temp.path()).unwrap();
        assert_eq!(actual, token);
        assert_eq!(history(&stored, 20).unwrap().len(), 1);
    }
}
