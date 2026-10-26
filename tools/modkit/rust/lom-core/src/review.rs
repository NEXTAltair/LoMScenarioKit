// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
//! Authoring features adapted from addonLoMScenario-ac; no web UI is reused.
use crate::project::Project;
use anyhow::{ensure, Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    fs,
    path::Path,
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Verdict {
    #[default]
    Unreviewed,
    Good,
    Fix,
    Hold,
    Reject,
}

impl Verdict {
    pub const ALL: [Self; 5] = [
        Self::Unreviewed,
        Self::Good,
        Self::Fix,
        Self::Hold,
        Self::Reject,
    ];
    pub fn label(self) -> &'static str {
        match self {
            Self::Unreviewed => "未判定",
            Self::Good => "良い",
            Self::Fix => "要修正",
            Self::Hold => "保留・別案",
            Self::Reject => "不採用",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Actor {
    Human,
    Agent,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Comment {
    pub id: String,
    pub story: String,
    pub node: Option<String>,
    pub quote: String,
    pub text: String,
    pub actor: Actor,
    pub name: String,
    pub created_at_ms: u64,
    pub parent: Option<String>,
    pub resolved: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Review {
    pub version: u32,
    #[serde(default)]
    pub verdicts: BTreeMap<String, Verdict>,
    #[serde(default)]
    pub comments: Vec<Comment>,
}

impl Default for Review {
    fn default() -> Self {
        Self {
            version: 1,
            verdicts: BTreeMap::new(),
            comments: vec![],
        }
    }
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

pub fn load(project: &Project) -> Result<Review> {
    let value = &project.manifest["_editor"]["review"];
    if value.is_null() {
        return Ok(Review::default());
    }
    let review: Review =
        serde_json::from_value(value.clone()).context("添削データの形式が不正です")?;
    ensure!(
        review.version == 1,
        "この添削データのバージョンには対応していません"
    );
    Ok(review)
}

fn write(project: &mut Project, review: &Review) -> Result<()> {
    if project.manifest.get("_editor").is_none() {
        project.manifest["_editor"] = json!({});
    }
    ensure!(
        project.manifest["_editor"].is_object(),
        "manifest._editor はオブジェクトが必要です"
    );
    project.manifest["_editor"]["review"] = serde_json::to_value(review)?;
    Ok(())
}

fn key(story: &str, node: Option<&str>) -> String {
    json!([story, node]).to_string()
}

pub fn verdict(review: &Review, story: &str, node: Option<&str>) -> Verdict {
    review
        .verdicts
        .get(&key(story, node))
        .copied()
        .unwrap_or_default()
}

fn target<'a>(project: &'a Project, story: &str, node: Option<&str>) -> Result<&'a Value> {
    let chapter = project.stories.get(story).context("対象の章がありません")?;
    match node {
        None => Ok(chapter),
        Some(id) => chapter["nodes"]
            .as_array()
            .context("nodes がありません")?
            .iter()
            .find(|n| n["id"] == id)
            .context("対象のノードがありません"),
    }
}

pub fn set_verdict(
    project: &mut Project,
    story: &str,
    node: Option<&str>,
    value: Verdict,
) -> Result<()> {
    target(project, story, node)?;
    let mut review = load(project)?;
    if value == Verdict::Unreviewed {
        review.verdicts.remove(&key(story, node));
    } else {
        review.verdicts.insert(key(story, node), value);
    }
    write(project, &review)
}

pub fn comment(
    project: &mut Project,
    story: &str,
    node: Option<&str>,
    text: &str,
    parent: Option<&str>,
    actor: Actor,
    name: &str,
) -> Result<String> {
    ensure!(
        !text.trim().is_empty() && text.chars().count() <= 20_000,
        "コメントは1〜20000文字で入力してください"
    );
    ensure!(
        !name.trim().is_empty() && name.chars().count() <= 100,
        "編集者名は1〜100文字で入力してください"
    );
    let mut review = load(project)?;
    let (story, node, quote) = if let Some(id) = parent {
        let root = review
            .comments
            .iter()
            .find(|c| c.id == id)
            .context("返信先がありません")?;
        ensure!(
            root.parent.is_none() && root.story == story,
            "同じ章のコメントへ返信してください"
        );
        (root.story.clone(), root.node.clone(), root.quote.clone())
    } else {
        let value = target(project, story, node)?;
        let quote = value["text"]
            .as_str()
            .or_else(|| value["title"].as_str())
            .map(str::to_owned)
            .unwrap_or_else(|| value.to_string());
        (story.to_owned(), node.map(str::to_owned), quote)
    };
    let id = uuid::Uuid::new_v4().to_string();
    review.comments.push(Comment {
        id: id.clone(),
        story,
        node,
        quote,
        text: text.to_owned(),
        actor,
        name: name.to_owned(),
        created_at_ms: now_ms(),
        parent: parent.map(str::to_owned),
        resolved: false,
    });
    write(project, &review)?;
    Ok(id)
}

pub fn resolve(project: &mut Project, id: &str, resolved: bool) -> Result<()> {
    let mut review = load(project)?;
    let comment = review
        .comments
        .iter_mut()
        .find(|c| c.id == id)
        .context("コメントがありません")?;
    ensure!(
        comment.parent.is_none(),
        "返信ではなく親コメントの解決状態を変更してください"
    );
    comment.resolved = resolved;
    write(project, &review)
}

pub fn context(project: &Project, story: &str) -> Result<Value> {
    let review = load(project)?;
    Ok(json!({
        "verdict": verdict(&review, story, None),
        "comments": review.comments.iter().filter(|c| c.story == story).collect::<Vec<_>>(),
        "note": "引用と指摘は本文とは別です。解決済みは指摘の撤回を意味しません。"
    }))
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Heading {
    pub level: usize,
    pub title: String,
    pub line: usize,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Document {
    pub path: String,
    pub title: String,
    pub markdown: String,
    pub headings: Vec<Heading>,
}

pub fn outline(project_root: &Path, query: &str) -> Result<Vec<Document>> {
    let root = project_root.canonicalize()?;
    let folder = root.join("outline");
    if !folder.exists() {
        return Ok(vec![]);
    }
    ensure!(
        !folder.is_symlink() && folder.is_dir(),
        "outline はプロジェクト内の通常のフォルダにしてください"
    );
    let mut paths = Vec::new();
    let mut pending = vec![folder];
    while let Some(directory) = pending.pop() {
        for entry in fs::read_dir(directory)? {
            let entry = entry?;
            let path = entry.path();
            let kind = entry.file_type()?;
            if kind.is_symlink() {
                continue;
            }
            if kind.is_dir() {
                pending.push(path);
            } else if kind.is_file()
                && path.extension().and_then(|e| e.to_str()) == Some("md")
                && entry.metadata()?.len() <= 2 * 1024 * 1024
            {
                paths.push(path);
            }
        }
    }
    paths.sort();
    let needle = query.to_lowercase();
    let mut documents = Vec::new();
    for path in paths {
        let path = path.canonicalize()?;
        ensure!(
            path.starts_with(root.join("outline")),
            "資料のパスが outline の外を指しています"
        );
        let markdown = fs::read_to_string(&path)?;
        let relative = path
            .strip_prefix(&root)?
            .to_string_lossy()
            .replace('\\', "/");
        if !format!("{relative}\n{markdown}")
            .to_lowercase()
            .contains(&needle)
        {
            continue;
        }
        let mut headings = Vec::new();
        let mut fence: Option<(char, usize)> = None;
        for (index, line) in markdown.lines().enumerate() {
            let trimmed = line.trim_start();
            if let Some(character) = trimmed
                .chars()
                .next()
                .filter(|c| *c == char::from(96) || *c == '~')
            {
                let count = trimmed.chars().take_while(|c| *c == character).count();
                if count >= 3 {
                    match fence {
                        None => fence = Some((character, count)),
                        Some((open, size)) if open == character && count >= size => fence = None,
                        _ => (),
                    }
                    continue;
                }
            }
            if fence.is_some() {
                continue;
            }
            let level = line.chars().take_while(|c| *c == '#').count();
            if (1..=6).contains(&level) && line.as_bytes().get(level) == Some(&b' ') {
                headings.push(Heading {
                    level,
                    title: line[level..].trim().trim_end_matches('#').trim().to_owned(),
                    line: index + 1,
                });
            }
        }
        let title = headings
            .first()
            .map(|h| h.title.clone())
            .unwrap_or_else(|| relative.clone());
        documents.push(Document {
            path: relative,
            title,
            markdown,
            headings,
        });
    }
    Ok(documents)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn quote_and_reply_survive_deleted_node_and_verdict_is_independent() {
        let mut p = Project::new();
        let id = comment(
            &mut p,
            "main",
            Some("say1"),
            "短く",
            None,
            Actor::Human,
            "作者",
        )
        .unwrap();
        set_verdict(&mut p, "main", None, Verdict::Hold).unwrap();
        p.stories.get_mut("main").unwrap()["nodes"] = json!([{"id":"end1","type":"end"}]);
        comment(
            &mut p,
            "main",
            None,
            "確認しました",
            Some(&id),
            Actor::Agent,
            "Codex",
        )
        .unwrap();
        resolve(&mut p, &id, true).unwrap();
        let r = load(&p).unwrap();
        assert_eq!(verdict(&r, "main", None), Verdict::Hold);
        assert_eq!(r.comments[0].quote, r.comments[1].quote);
        assert_eq!(r.comments[1].node.as_deref(), Some("say1"));
        assert!(r.comments[0].resolved);
        assert_eq!(
            context(&p, "main").unwrap()["comments"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
    }
    #[test]
    fn markdown_headings_search_and_fenced_code() {
        let temp = tempfile::tempdir().unwrap();
        fs::create_dir(temp.path().join("outline")).unwrap();
        fs::write(
            temp.path().join("outline/world.md"),
            "# 世界\n港へ\n~~~rust\n# 見出しではない\n~~~\n## 次へ\n",
        )
        .unwrap();
        let docs = outline(temp.path(), "港").unwrap();
        assert_eq!(docs.len(), 1);
        assert_eq!(
            docs[0].headings.iter().map(|h| h.line).collect::<Vec<_>>(),
            [1, 6]
        );
        assert!(outline(temp.path(), "存在しない語").unwrap().is_empty());
    }
}
