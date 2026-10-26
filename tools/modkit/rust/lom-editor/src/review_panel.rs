// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
use eframe::egui::{self, RichText};
use lom_core::{
    project::Project,
    review::{self, Actor, Verdict},
    workspace_store,
};
use serde_json::Value;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_buttons_add_reply_and_resolve_without_changing_story_text() {
        let mut panel = Panel {
            text: "短くしてほしい".into(),
            node_only: true,
            ..Default::default()
        };
        let mut project = lom_core::scenario_addon::new_project("操作テスト");
        let original = project.stories.clone();
        let ctx = egui::Context::default();
        ctx.enable_accesskit();
        let draw = |panel: &mut Panel, project: &mut Project, events| {
            ctx.run(
                egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(
                        egui::Pos2::ZERO,
                        egui::vec2(1100.0, 1200.0),
                    )),
                    events,
                    ..Default::default()
                },
                |ctx| {
                    egui::CentralPanel::default().show(ctx, |ui| {
                        panel.show(ui, project, "main", Some("say1"));
                    });
                },
            )
        };
        let click = |output: egui::FullOutput, label: &str| {
            let target = output
                .platform_output
                .accesskit_update
                .unwrap()
                .nodes
                .into_iter()
                .find_map(|(id, n)| {
                    (n.label() == Some(label) && n.role() == egui::accesskit::Role::Button)
                        .then_some(id)
                })
                .unwrap_or_else(|| panic!("button missing: {label}"));
            vec![egui::Event::AccessKitActionRequest(
                egui::accesskit::ActionRequest {
                    action: egui::accesskit::Action::Click,
                    target,
                    data: None,
                },
            )]
        };
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, "コメントを追加"));
        let comments = review::load(&project).unwrap().comments;
        assert_eq!(comments.len(), 1);
        assert_eq!(comments[0].actor, Actor::Human);
        assert!(!panel.has_pending());
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, "返信"));
        panel.text = "反映しました".into();
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, "コメントを追加"));
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, "解決済みにする"));
        let comments = review::load(&project).unwrap().comments;
        assert!(comments[0].resolved);
        assert_eq!(comments[1].parent.as_deref(), Some(comments[0].id.as_str()));
        assert_eq!(project.stories, original);
    }
}

#[derive(Default)]
pub struct Panel {
    tab: usize,
    text: String,
    name: String,
    reply: Option<String>,
    node_only: bool,
    query: String,
    selected_document: String,
    config: Option<String>,
    error: Option<String>,
    selected_history: Option<Value>,
}
impl Panel {
    pub fn has_pending(&self) -> bool {
        !self.text.is_empty() || self.config.is_some()
    }
    pub fn show(
        &mut self,
        ui: &mut egui::Ui,
        project: &mut Project,
        story: &str,
        node: Option<&str>,
    ) -> Option<String> {
        let mut jump = None;
        ui.heading("資料・添削");
        ui.horizontal_wrapped(|ui| {
            for (index, title) in ["判定・コメント", "資料", "保存履歴", "Addon設定"]
                .iter()
                .enumerate()
            {
                ui.selectable_value(&mut self.tab, index, *title);
            }
        });
        ui.separator();
        let result = (|| -> anyhow::Result<()> {
            match self.tab {
                0 => {
                    ui.label(format!("章: {story}"));
                    ui.checkbox(&mut self.node_only, "選択中のノードを対象にする");
                    let target = if self.node_only { node } else { None };
                    if let Some(id) = target {
                        ui.monospace(id);
                    }
                    let review = review::load(project)?;
                    let mut verdict = review::verdict(&review, story, target);
                    let old = verdict;
                    egui::ComboBox::from_id_salt("review-verdict")
                        .selected_text(verdict.label())
                        .show_ui(ui, |ui| {
                            for value in Verdict::ALL {
                                ui.selectable_value(&mut verdict, value, value.label());
                            }
                        });
                    if verdict != old {
                        review::set_verdict(project, story, target, verdict)?;
                    }
                    ui.label("指摘と引用は本文と別に保存されます。変更後は画面上部の保存を押してください。");
                    if self.name.is_empty() {
                        self.name = "作者".into();
                    }
                    ui.horizontal(|ui| {
                        ui.label("名前");
                        ui.text_edit_singleline(&mut self.name);
                    });
                    if let Some(id) = self.reply.clone() {
                        ui.horizontal(|ui| {
                            ui.label(format!("返信先: {id}"));
                            if ui.small_button("返信をやめる").clicked() {
                                self.reply = None;
                            }
                        });
                    }
                    ui.add(
                        egui::TextEdit::multiline(&mut self.text)
                            .desired_rows(3)
                            .desired_width(f32::INFINITY)
                            .hint_text("指摘・提案・返信"),
                    );
                    if ui
                        .add_enabled(
                            !self.text.trim().is_empty(),
                            egui::Button::new("コメントを追加"),
                        )
                        .clicked()
                    {
                        review::comment(
                            project,
                            story,
                            target,
                            &self.text,
                            self.reply.as_deref(),
                            Actor::Human,
                            &self.name,
                        )?;
                        self.text.clear();
                        self.reply = None;
                    }
                    for comment in review.comments.iter().filter(|c| {
                        c.story == story
                            && c.parent.is_none()
                            && (!self.node_only || c.node.as_deref() == target)
                    }) {
                        ui.separator();
                        ui.horizontal_wrapped(|ui| {
                            ui.strong(format!(
                                "{} · {}",
                                if comment.actor == Actor::Human {
                                    "人間"
                                } else {
                                    "エージェント"
                                },
                                comment.name
                            ));
                            if let Some(id) = &comment.node {
                                if ui.link(id).clicked() {
                                    jump = Some(id.clone());
                                }
                            }
                            ui.label(if comment.resolved {
                                "解決済み"
                            } else {
                                "未解決"
                            });
                        });
                        ui.label(RichText::new(&comment.quote).weak());
                        ui.label(&comment.text);
                        ui.horizontal(|ui| {
                            if ui.small_button("返信").clicked() {
                                self.reply = Some(comment.id.clone());
                            }
                            if ui
                                .small_button(if comment.resolved {
                                    "未解決に戻す"
                                } else {
                                    "解決済みにする"
                                })
                                .clicked()
                            {
                                if let Err(error) =
                                    review::resolve(project, &comment.id, !comment.resolved)
                                {
                                    self.error = Some(error.to_string());
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
                                        "人間"
                                    } else {
                                        "エージェント"
                                    },
                                    reply.name
                                ));
                                ui.label(&reply.text);
                            });
                        }
                    }
                }
                1 => {
                    ui.add(
                        egui::TextEdit::singleline(&mut self.query)
                            .hint_text("資料の見出し・本文を検索"),
                    );
                    if let Some(root) = workspace_store::root(project) {
                        let documents = review::outline(root, &self.query)?;
                        if documents.is_empty() {
                            ui.label("プロジェクト内の outline/ にMarkdown資料を置いてください。");
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
                            ui.collapsing("目次", |ui| {
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
                        ui.label("資料を読むにはプロジェクトをフォルダへ保存してください。");
                    }
                }
                2 => {
                    ui.label("保存ごとの変更前後を確認できます。章の復元は未保存の編集として反映されます。");
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
                                .button(format!("この履歴から章 {story} を復元"))
                                .clicked()
                            {
                                workspace_store::restore(
                                    project,
                                    entry["id"].as_str().unwrap_or(""),
                                    story,
                                )?;
                            }
                            for (key, label) in [("before", "変更前"), ("after", "変更後")] {
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
                        ui.label("プロジェクトを保存すると履歴が作成されます。");
                    }
                }
                _ => {
                    if !lom_core::scenario_addon::enabled(project) {
                        ui.label("このプロジェクトはMortalModHost形式です。");
                        return Ok(());
                    }
                    ui.label("scenario.json の設定です。events の story は章IDを指定します。");
                    if self.config.is_none() && ui.button("設定を編集").clicked() {
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
                        if ui.button("設定を反映").clicked() {
                            let value: Value =
                                serde_json::from_str(self.config.as_deref().unwrap())?;
                            anyhow::ensure!(value.is_object(), "設定はJSONオブジェクトが必要です");
                            project.manifest["_editor"]["scenario_addon"] = value;
                            self.config = None;
                        }
                        if ui.button("編集を取り消す").clicked() {
                            self.config = None;
                        }
                    }
                    ui.label(format!(
                        "書き出し対応ノード: {}",
                        lom_core::scenario_addon::SUPPORTED.join(", ")
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
