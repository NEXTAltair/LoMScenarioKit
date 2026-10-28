// Copyright (C) 2026 NEXTAltair; modified 2026-10-28.
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
use crate::i18n::{text, tr};
use eframe::egui::{self, RichText};
use lom_core::{
    project::Project,
    review::{self, Actor, Verdict},
    workspace_store,
};
use serde_json::Value;
use std::collections::BTreeMap;

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Target {
    pub story: String,
    pub node: Option<String>,
}
impl Target {
    fn new(story: &str, node: Option<&str>) -> Self {
        Self {
            story: story.into(),
            node: node.map(str::to_owned),
        }
    }
    fn label(&self) -> String {
        match &self.node {
            Some(node) => format!("{} / {node}", self.story),
            None => text("章节：{story}", &[("story", self.story.clone())]),
        }
    }
}

#[derive(Default)]
struct Draft {
    text: String,
    reply: Option<String>,
    focus: bool,
}

#[derive(Default)]
pub struct Panel {
    tab: usize,
    drafts: BTreeMap<Target, Draft>,
    name: String,
    node_only: bool,
    query: String,
    selected_document: String,
    config: Option<String>,
    error: Option<String>,
    selected_history: Option<Value>,
}
impl Panel {
    pub fn has_pending(&self) -> bool {
        self.drafts.values().any(|draft| !draft.text.is_empty()) || self.config.is_some()
    }
    pub fn show_node(
        &mut self,
        ui: &mut egui::Ui,
        project: &mut Project,
        story: &str,
        node: &str,
    ) -> Option<Target> {
        ui.horizontal_wrapped(|ui| {
            ui.strong(tr("评定与评论"));
            ui.monospace(node);
        });
        let result = self.review(ui, project, story, Some(node), false);
        let jump = match result {
            Ok(jump) => jump,
            Err(error) => {
                self.error = Some(format!("{error:#}"));
                None
            }
        };
        if let Some(error) = &self.error {
            ui.colored_label(egui::Color32::RED, error);
        }
        jump
    }

    fn review(
        &mut self,
        ui: &mut egui::Ui,
        project: &mut Project,
        story: &str,
        node: Option<&str>,
        all_nodes: bool,
    ) -> anyhow::Result<Option<Target>> {
        let target = Target::new(story, node);
        ui.push_id(
            ("review", &target),
            |ui| -> anyhow::Result<Option<Target>> {
                let mut jump = None;
                let review = review::load(project)?;
                let mut verdict = review::verdict(&review, story, node);
                let old = verdict;
                ui.horizontal_wrapped(|ui| {
                    for value in Verdict::ALL {
                        ui.selectable_value(&mut verdict, value, verdict_label(value));
                    }
                });
                if verdict != old {
                    review::set_verdict(project, story, node, verdict)?;
                    self.error = None;
                }

                // Keep each unfinished comment bound to its original chapter/node,
                // including when the user moves between the inspector and review page.
                let pending: Vec<_> = self
                    .drafts
                    .iter()
                    .filter(|(key, draft)| *key != &target && !draft.text.is_empty())
                    .map(|(key, _)| key.clone())
                    .collect();
                if !pending.is_empty() {
                    ui.menu_button(
                        text(
                            "输入中的评论（{count}）",
                            &[("count", pending.len().to_string())],
                        ),
                        |ui| {
                            for key in pending {
                                ui.horizontal(|ui| {
                                    if ui.button(key.label()).clicked() {
                                        jump = Some(key.clone());
                                        ui.close();
                                    }
                                    if ui.small_button(tr("放弃草稿")).clicked() {
                                        self.drafts.remove(&key);
                                        ui.close();
                                    }
                                });
                            }
                        },
                    );
                }
                let draft = self.drafts.entry(target.clone()).or_default();
                if self.name.is_empty() {
                    self.name = tr("作者");
                }
                if let Some(id) = draft.reply.clone() {
                    ui.horizontal_wrapped(|ui| {
                        let quote = review
                            .comments
                            .iter()
                            .find(|comment| comment.id == id)
                            .map(|comment| comment.text.chars().take(60).collect::<String>())
                            .unwrap_or_else(|| id.clone());
                        ui.label(text("回复对象：{id}", &[("id", quote)]));
                        if ui.small_button(tr("取消回复")).clicked() {
                            draft.reply = None;
                        }
                    });
                }
                let composer = ui.add(
                    egui::TextEdit::multiline(&mut draft.text)
                        .id(ui.id().with("comment"))
                        .desired_rows(2)
                        .desired_width(f32::INFINITY)
                        .hint_text(tr("评论、建议或回复")),
                );
                if std::mem::take(&mut draft.focus) {
                    composer.request_focus();
                    composer.scroll_to_me(Some(egui::Align::Center));
                }
                let mut submit = false;
                ui.horizontal_wrapped(|ui| {
                    submit = ui
                        .add_enabled(
                            !draft.text.trim().is_empty(),
                            egui::Button::new(tr("添加评论")),
                        )
                        .clicked();
                    if !draft.text.is_empty() && ui.small_button(tr("放弃草稿")).clicked() {
                        *draft = Draft::default();
                    }
                    ui.label(tr("姓名"));
                    ui.add(egui::TextEdit::singleline(&mut self.name).desired_width(100.0));
                });
                if submit {
                    review::comment(
                        project,
                        story,
                        node,
                        &draft.text,
                        draft.reply.as_deref(),
                        Actor::Human,
                        &self.name,
                    )?;
                    *draft = Draft::default();
                    self.error = None;
                }
                ui.small(tr("评论与引用单独保存。修改后，请按顶部的保存按钮。"));
                let review = review::load(project)?;
                for comment in review.comments.iter().filter(|c| {
                    c.story == story
                        && c.parent.is_none()
                        && (all_nodes || c.node.as_deref() == node)
                }) {
                    ui.push_id(&comment.id, |ui| {
                        ui.separator();
                        ui.horizontal_wrapped(|ui| {
                            ui.strong(format!(
                                "{} · {}",
                                if comment.actor == Actor::Human {
                                    tr("人工")
                                } else {
                                    tr("智能代理")
                                },
                                comment.name
                            ));
                            if let Some(id) = &comment.node {
                                if ui.link(id).clicked() {
                                    jump = Some(Target::new(story, Some(id)));
                                }
                            }
                            ui.label(if comment.resolved {
                                tr("已解决")
                            } else {
                                tr("未解决")
                            });
                        });
                        ui.label(RichText::new(&comment.quote).weak());
                        ui.label(&comment.text);
                        ui.horizontal(|ui| {
                            if ui.small_button(tr("回复")).clicked() {
                                draft.reply = Some(comment.id.clone());
                                draft.focus = true;
                            }
                            if ui
                                .small_button(if comment.resolved {
                                    tr("重新打开")
                                } else {
                                    tr("标记为已解决")
                                })
                                .clicked()
                            {
                                match review::resolve(project, &comment.id, !comment.resolved) {
                                    Ok(()) => self.error = None,
                                    Err(error) => self.error = Some(error.to_string()),
                                }
                            }
                        });
                        for reply in review
                            .comments
                            .iter()
                            .filter(|r| r.parent.as_deref() == Some(comment.id.as_str()))
                        {
                            ui.indent(&reply.id, |ui| {
                                ui.strong(format!(
                                    "{} · {}",
                                    if reply.actor == Actor::Human {
                                        tr("人工")
                                    } else {
                                        tr("智能代理")
                                    },
                                    reply.name
                                ));
                                ui.label(&reply.text);
                            });
                        }
                    });
                }
                Ok(jump)
            },
        )
        .inner
    }

    pub fn open_chapter(&mut self) {
        self.tab = 0;
        self.node_only = false;
    }

    pub fn show(
        &mut self,
        ui: &mut egui::Ui,
        project: &mut Project,
        story: &str,
        node: Option<&str>,
    ) -> Option<Target> {
        let mut jump = None;
        ui.heading(tr("资料与审阅"));
        ui.horizontal_wrapped(|ui| {
            for (index, title) in [
                tr("评定与评论"),
                tr("资料"),
                tr("保存历史"),
                tr("Addon 设置"),
            ]
            .iter()
            .enumerate()
            {
                ui.selectable_value(&mut self.tab, index, title.as_str());
            }
        });
        ui.separator();
        let result = (|| -> anyhow::Result<()> {
            match self.tab {
                0 => {
                    ui.label(text("章节：{story}", &[("story", story.into())]));
                    ui.checkbox(&mut self.node_only, tr("针对选中的节点"));
                    let target = if self.node_only { node } else { None };
                    if let Some(id) = target {
                        ui.monospace(id);
                    }
                    // A missing node must never silently turn a node review into a chapter review.
                    if !self.node_only || target.is_some() {
                        jump = self.review(ui, project, story, target, !self.node_only)?;
                    }
                }
                1 => {
                    ui.add(
                        egui::TextEdit::singleline(&mut self.query)
                            .hint_text(tr("搜索资料标题与正文")),
                    );
                    if let Some(root) = workspace_store::root(project) {
                        let documents = review::outline(root, &self.query)?;
                        if documents.is_empty() {
                            ui.label(tr("请将 Markdown 资料放在项目的 outline/ 文件夹中。"));
                        }
                        for doc in &documents {
                            if ui
                                .selectable_label(self.selected_document == doc.path, &doc.title)
                                .on_hover_text(&doc.path)
                                .clicked()
                            {
                                self.selected_document = doc.path.clone();
                            }
                        }
                        if let Some(doc) = documents
                            .iter()
                            .find(|d| d.path == self.selected_document)
                            .or_else(|| documents.first())
                        {
                            ui.separator();
                            ui.label(&doc.path);
                            let mut target = None;
                            ui.collapsing(tr("目录"), |ui| {
                                for heading in &doc.headings {
                                    if ui
                                        .link(format!(
                                            "{}{}",
                                            "  ".repeat(heading.level - 1),
                                            heading.title
                                        ))
                                        .clicked()
                                    {
                                        target = Some(heading.line);
                                    }
                                }
                            });
                            for (index, line) in doc.markdown.lines().enumerate() {
                                let response = if let Some(heading) =
                                    doc.headings.iter().find(|h| h.line == index + 1)
                                {
                                    ui.label(
                                        RichText::new(&heading.title)
                                            .strong()
                                            .size(22.0 - heading.level as f32),
                                    )
                                } else {
                                    ui.label(line)
                                };
                                if target == Some(index + 1) {
                                    response.scroll_to_me(Some(egui::Align::Min));
                                }
                            }
                        }
                    } else {
                        ui.label(tr("请先将项目保存到文件夹，再查看资料。"));
                    }
                }
                2 => {
                    ui.label(tr("可查看每次保存的前后差异。恢复章节后，请再次保存。"));
                    if workspace_store::root(project).is_some() {
                        for entry in workspace_store::history(project, 50)? {
                            let label = format!(
                                "{} · {} · {}",
                                entry["actor"].as_str().unwrap_or(""),
                                entry["name"].as_str().unwrap_or(""),
                                entry["message"].as_str().unwrap_or("")
                            );
                            if ui
                                .selectable_label(
                                    self.selected_history
                                        .as_ref()
                                        .is_some_and(|v| v["id"] == entry["id"]),
                                    label,
                                )
                                .clicked()
                            {
                                self.selected_history = Some(entry);
                            }
                        }
                        if let Some(entry) = &self.selected_history {
                            ui.separator();
                            ui.monospace(entry["id"].as_str().unwrap_or(""));
                            if ui
                                .button(text(
                                    "从此记录恢复章节 {story}",
                                    &[("story", story.into())],
                                ))
                                .clicked()
                            {
                                workspace_store::restore(
                                    project,
                                    entry["id"].as_str().unwrap_or(""),
                                    story,
                                )?;
                            }
                            for (key, label) in [("before", tr("修改前")), ("after", tr("修改后"))]
                            {
                                ui.collapsing(label, |ui| {
                                    let mut text =
                                        serde_json::to_string_pretty(&entry[key]["stories"][story])
                                            .unwrap_or_default();
                                    ui.add(
                                        egui::TextEdit::multiline(&mut text)
                                            .code_editor()
                                            .interactive(false)
                                            .desired_width(f32::INFINITY),
                                    );
                                });
                            }
                        }
                    } else {
                        ui.label(tr("保存项目后会生成历史记录。"));
                    }
                }
                _ => {
                    if !lom_core::scenario_addon::enabled(project) {
                        ui.label(tr("此项目使用 MortalModHost 格式。"));
                        return Ok(());
                    }
                    ui.label(tr(
                        "这是 scenario.json 的设置。events 中的 story 指定章节 ID。",
                    ));
                    if self.config.is_none() && ui.button(tr("编辑设置")).clicked() {
                        self.config = Some(serde_json::to_string_pretty(
                            &project.manifest["_editor"]["scenario_addon"],
                        )?);
                    }
                    if let Some(text) = &mut self.config {
                        ui.add(
                            egui::TextEdit::multiline(text)
                                .code_editor()
                                .desired_width(f32::INFINITY)
                                .desired_rows(18),
                        );
                    }
                    if self.config.is_some() {
                        if ui.button(tr("应用设置")).clicked() {
                            let value: Value =
                                serde_json::from_str(self.config.as_deref().unwrap())?;
                            anyhow::ensure!(value.is_object(), tr("配置必须是对象"));
                            project.manifest["_editor"]["scenario_addon"] = value;
                            self.config = None;
                        }
                        if ui.button(tr("取消编辑")).clicked() {
                            self.config = None;
                        }
                    }
                    ui.label(text(
                        "支持导出的节点：{nodes}",
                        &[("nodes", lom_core::scenario_addon::SUPPORTED.join(", "))],
                    ));
                }
            }
            Ok(())
        })();
        if let Err(error) = result {
            self.error = Some(format!("{error:#}"));
        }
        if let Some(error) = &self.error {
            ui.colored_label(egui::Color32::RED, error);
        }
        jump
    }
}

fn verdict_label(verdict: Verdict) -> String {
    tr(match verdict {
        Verdict::Unreviewed => "未评定",
        Verdict::Good => "良好",
        Verdict::Fix => "需要修改",
        Verdict::Hold => "保留或替代方案",
        Verdict::Reject => "不采用",
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn draw(
        ctx: &egui::Context,
        panel: &mut Panel,
        project: &mut Project,
        target: &Target,
        inline: bool,
        events: Vec<egui::Event>,
    ) -> egui::FullOutput {
        ctx.run(
            egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    egui::vec2(700.0, 1000.0),
                )),
                events,
                ..Default::default()
            },
            |ctx| {
                egui::CentralPanel::default().show(ctx, |ui| {
                    if inline {
                        panel.show_node(
                            ui,
                            project,
                            &target.story,
                            target.node.as_deref().unwrap(),
                        );
                    } else {
                        panel.show(ui, project, &target.story, target.node.as_deref());
                    }
                });
            },
        )
    }

    fn click(output: egui::FullOutput, label: &str) -> Vec<egui::Event> {
        let target = output
            .platform_output
            .accesskit_update
            .unwrap()
            .nodes
            .into_iter()
            .find_map(|(id, node)| (node.label() == Some(label)).then_some(id))
            .unwrap_or_else(|| panic!("control missing: {label}"));
        vec![egui::Event::AccessKitActionRequest(
            egui::accesskit::ActionRequest {
                action: egui::accesskit::Action::Click,
                target,
                data: None,
            },
        )]
    }

    #[test]
    fn node_buttons_add_reply_resolve_and_persist_without_changing_story() {
        let mut panel = Panel::default();
        let target = Target::new("main", Some("say1"));
        panel.drafts.entry(target.clone()).or_default().text = "短くしてほしい".into();
        let mut project = lom_core::scenario_addon::new_project("操作テスト");
        let original = project.stories.clone();
        let ctx = egui::Context::default();
        ctx.enable_accesskit();
        for label in [verdict_label(Verdict::Fix), tr("添加评论")] {
            let output = draw(&ctx, &mut panel, &mut project, &target, true, vec![]);
            draw(
                &ctx,
                &mut panel,
                &mut project,
                &target,
                true,
                click(output, &label),
            );
        }
        let review = review::load(&project).unwrap();
        assert_eq!(review::verdict(&review, "main", Some("say1")), Verdict::Fix);
        assert_eq!(review::verdict(&review, "main", None), Verdict::Unreviewed);
        assert_eq!(review.comments.len(), 1);
        assert_eq!(review.comments[0].node.as_deref(), Some("say1"));
        assert_eq!(review.comments[0].actor, Actor::Human);
        assert!(!panel.has_pending());

        let output = draw(&ctx, &mut panel, &mut project, &target, true, vec![]);
        draw(
            &ctx,
            &mut panel,
            &mut project,
            &target,
            true,
            click(output, &tr("回复")),
        );
        // Reply focuses the composer so typing does not need another click.
        draw(&ctx, &mut panel, &mut project, &target, true, vec![]);
        draw(
            &ctx,
            &mut panel,
            &mut project,
            &target,
            true,
            vec![egui::Event::Text("反映しました".into())],
        );
        assert_eq!(panel.drafts[&target].text, "反映しました");
        for label in [tr("添加评论"), tr("标记为已解决")] {
            let output = draw(&ctx, &mut panel, &mut project, &target, true, vec![]);
            draw(
                &ctx,
                &mut panel,
                &mut project,
                &target,
                true,
                click(output, &label),
            );
        }
        let comments = review::load(&project).unwrap().comments;
        assert!(comments[0].resolved);
        assert_eq!(comments[1].parent.as_deref(), Some(comments[0].id.as_str()));
        assert_eq!(comments[1].quote, comments[0].quote);
        assert!(!panel.has_pending());
        assert_eq!(project.stories, original);

        let directory = tempfile::tempdir().unwrap();
        workspace_store::save(
            &mut project,
            directory.path(),
            None,
            Actor::Human,
            "作者",
            "添削",
        )
        .unwrap();
        let (reopened, _) = workspace_store::read(directory.path()).unwrap();
        let review = review::load(&reopened).unwrap();
        assert_eq!(review::verdict(&review, "main", Some("say1")), Verdict::Fix);
        assert_eq!(review.comments.len(), 2);
        assert!(review.comments[0].resolved);
        assert_eq!(reopened.stories, original);
    }

    #[test]
    fn drafts_and_reply_targets_stay_with_their_node_across_views_and_chapters() {
        let mut panel = Panel {
            node_only: true,
            ..Default::default()
        };
        let mut project = lom_core::scenario_addon::new_project("対象切替");
        let mut extra = project.stories["main"].clone();
        extra["id"] = json!("extra");
        project.stories.insert("extra".into(), extra);
        let first = Target::new("main", Some("say1"));
        let second = Target::new("main", Some("end1"));
        // Use a valid second node regardless of the starter project's node IDs.
        project.stories.get_mut("main").unwrap()["nodes"]
            .as_array_mut()
            .unwrap()
            .push(json!({"id":"end1", "type":"end"}));
        let other_chapter = Target::new("extra", Some("say1"));
        let parent = review::comment(
            &mut project,
            "main",
            Some("say1"),
            "指摘",
            None,
            Actor::Human,
            "作者",
        )
        .unwrap();
        panel.drafts.insert(
            first.clone(),
            Draft {
                text: "最初の返信".into(),
                reply: Some(parent.clone()),
                focus: false,
            },
        );
        let ctx = egui::Context::default();
        ctx.enable_accesskit();
        for target in [&second, &other_chapter] {
            draw(&ctx, &mut panel, &mut project, target, true, vec![]);
            assert!(panel.drafts[target].text.is_empty());
            assert!(panel.drafts[target].reply.is_none());
            panel.drafts.get_mut(target).unwrap().text = target.label();
            let output = draw(&ctx, &mut panel, &mut project, target, true, vec![]);
            draw(
                &ctx,
                &mut panel,
                &mut project,
                target,
                true,
                click(output, &tr("添加评论")),
            );
        }
        assert_eq!(panel.drafts[&first].text, "最初の返信");
        assert!(panel.has_pending());
        // Post the inspector's pending reply from the existing review page.
        let output = draw(&ctx, &mut panel, &mut project, &first, false, vec![]);
        draw(
            &ctx,
            &mut panel,
            &mut project,
            &first,
            false,
            click(output, &tr("添加评论")),
        );
        assert!(!panel.has_pending());
        let comments = review::load(&project).unwrap().comments;
        assert_eq!(comments.len(), 4);
        for (comment, target) in comments[1..3].iter().zip([&second, &other_chapter]) {
            assert_eq!(comment.story, target.story);
            assert_eq!(comment.node, target.node);
            assert!(comment.parent.is_none());
        }
        assert_eq!(comments[3].parent.as_deref(), Some(parent.as_str()));
        assert_eq!(comments[3].node.as_deref(), Some("say1"));
        assert_eq!(comments[3].text, "最初の返信");
    }

    #[test]
    fn chapter_review_keeps_its_scope_and_failed_comments_keep_their_draft() {
        let mut panel = Panel::default();
        let mut project = lom_core::scenario_addon::new_project("章全体");
        let chapter = Target::new("main", None);
        panel.drafts.entry(chapter.clone()).or_default().text = "章への指摘".into();
        let ctx = egui::Context::default();
        ctx.enable_accesskit();
        for label in [verdict_label(Verdict::Good), tr("添加评论")] {
            let output = draw(&ctx, &mut panel, &mut project, &chapter, false, vec![]);
            draw(
                &ctx,
                &mut panel,
                &mut project,
                &chapter,
                false,
                click(output, &label),
            );
        }
        let review = review::load(&project).unwrap();
        assert_eq!(review::verdict(&review, "main", None), Verdict::Good);
        assert_eq!(
            review::verdict(&review, "main", Some("say1")),
            Verdict::Unreviewed
        );
        assert_eq!(review.comments[0].node, None);
        panel.name = " ".into();
        panel.drafts.entry(chapter.clone()).or_default().text = "未投稿".into();
        let output = draw(&ctx, &mut panel, &mut project, &chapter, false, vec![]);
        draw(
            &ctx,
            &mut panel,
            &mut project,
            &chapter,
            false,
            click(output, &tr("添加评论")),
        );
        assert!(panel.error.is_some());
        assert!(panel.has_pending());
        assert_eq!(panel.drafts[&chapter].text, "未投稿");
        assert_eq!(review::load(&project).unwrap().comments.len(), 1);
        let output = draw(&ctx, &mut panel, &mut project, &chapter, false, vec![]);
        draw(
            &ctx,
            &mut panel,
            &mut project,
            &chapter,
            false,
            click(output, &tr("放弃草稿")),
        );
        assert!(!panel.has_pending());
    }
}
