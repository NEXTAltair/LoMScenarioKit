// Copyright (C) 2026 NEXTAltair; modified 2026-10-27.
// GPL-3.0-only with the additional permission in the repository LICENSE-GAME-EXCEPTION.txt.
use crate::i18n::{text, tr};
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
        draw(&mut panel, &mut project, click(output, &tr("添加评论")));
        let comments = review::load(&project).unwrap().comments;
        assert_eq!(comments.len(), 1);
        assert_eq!(comments[0].actor, Actor::Human);
        assert!(!panel.has_pending());
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, &tr("回复")));
        panel.text = "反映しました".into();
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, &tr("添加评论")));
        let output = draw(&mut panel, &mut project, vec![]);
        draw(&mut panel, &mut project, click(output, &tr("标记为已解决")));
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
                    let review = review::load(project)?;
                    let mut verdict = review::verdict(&review, story, target);
                    let old = verdict;
                    egui::ComboBox::from_id_salt("review-verdict")
                        .selected_text(verdict_label(verdict))
                        .show_ui(ui, |ui| {
                            for value in Verdict::ALL {
                                ui.selectable_value(&mut verdict, value, verdict_label(value));
                            }
                        });
                    if verdict != old {
                        review::set_verdict(project, story, target, verdict)?;
                    }
                    ui.label(tr("评论与引用单独保存。修改后，请按顶部的保存按钮。"));
                    if self.name.is_empty() {
                        self.name = tr("作者");
                    }
                    ui.horizontal(|ui| {
                        ui.label(tr("姓名"));
                        ui.text_edit_singleline(&mut self.name);
                    });
                    if let Some(id) = self.reply.clone() {
                        ui.horizontal(|ui| {
                            ui.label(text("回复对象：{id}", &[("id", id.clone())]));
                            if ui.small_button(tr("取消回复")).clicked() {
                                self.reply = None;
                            }
                        });
                    }
                    ui.add(
                        egui::TextEdit::multiline(&mut self.text)
                            .desired_rows(3)
                            .desired_width(f32::INFINITY)
                            .hint_text(tr("评论、建议或回复")),
                    );
                    if ui
                        .add_enabled(
                            !self.text.trim().is_empty(),
                            egui::Button::new(tr("添加评论")),
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
                                    tr("人工")
                                } else {
                                    tr("智能代理")
                                },
                                comment.name
                            ));
                            if let Some(id) = &comment.node {
                                if ui.link(id).clicked() {
                                    jump = Some(id.clone());
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
                                self.reply = Some(comment.id.clone());
                            }
                            if ui
                                .small_button(if comment.resolved {
                                    tr("重新打开")
                                } else {
                                    tr("标记为已解决")
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
                                        tr("人工")
                                    } else {
                                        tr("智能代理")
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
