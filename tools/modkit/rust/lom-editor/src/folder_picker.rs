// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the repository game-linking exception.
//! Directory selection that also works without a desktop portal on Linux/WSL.
use crate::i18n::tr;
use eframe::egui;
use std::path::{Path, PathBuf};

pub enum Selection {
    Pending,
    Cancelled,
    Chosen(PathBuf),
}

pub struct FolderPicker {
    directory: PathBuf,
    input: String,
    folders: Vec<PathBuf>,
    pub error: Option<String>,
}

impl FolderPicker {
    pub fn new(initial: &Path) -> Self {
        let directory = if initial.is_dir() {
            initial.to_path_buf()
        } else {
            std::env::var_os("HOME")
                .or_else(|| std::env::var_os("USERPROFILE"))
                .map(PathBuf::from)
                .filter(|p| p.is_dir())
                .unwrap_or_else(|| std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")))
        };
        let mut picker = Self {
            input: directory.display().to_string(),
            directory: directory.clone(),
            folders: Vec::new(),
            error: None,
        };
        picker.navigate(&directory);
        picker
    }

    fn candidate(&self) -> PathBuf {
        let input = PathBuf::from(&self.input);
        if input.is_absolute() {
            input
        } else {
            self.directory.join(input)
        }
    }

    fn navigate(&mut self, path: &Path) {
        let result = (|| -> std::io::Result<_> {
            let directory = path.canonicalize()?;
            let mut folders = Vec::new();
            for entry in std::fs::read_dir(&directory)? {
                let entry = entry?;
                if entry.path().is_dir() {
                    folders.push(entry.path());
                }
            }
            folders.sort();
            Ok((directory, folders))
        })();
        match result {
            Ok((directory, folders)) => {
                self.input = directory.display().to_string();
                self.directory = directory;
                self.folders = folders;
                self.error = None;
            }
            Err(error) => {
                self.error = Some(crate::i18n::text(
                    "无法读取文件夹：{error}",
                    &[("error", error.to_string())],
                ));
            }
        }
    }

    pub fn show(&mut self, ctx: &egui::Context, title: &str) -> Selection {
        let mut selection = Selection::Pending;
        let mut navigate = None;
        let response = egui::Modal::new(egui::Id::new("game-folder-picker"))
            .frame(crate::shell::dialog_frame())
            .show(ctx, |ui| {
                ui.set_width((ctx.content_rect().width() - 64.0).clamp(240.0, 640.0));
                if crate::shell::dialog_header(ui, &tr(title)) {
                    selection = Selection::Cancelled;
                }
                ui.separator();
                ui.label(tr("选择包含 Mortal_Data 的游戏目录"));
                let label = ui.label(tr("文件夹路径"));
                ui.horizontal(|ui| {
                    let edit = ui
                        .add(
                            egui::TextEdit::singleline(&mut self.input)
                                .id(egui::Id::new("game-folder-path"))
                                .desired_width((ui.available_width() - 80.0).max(120.0)),
                        )
                        .labelled_by(label.id);
                    if (edit.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)))
                        || ui.button(tr("前往文件夹")).clicked()
                    {
                        navigate = Some(self.candidate());
                    }
                });
                ui.horizontal_wrapped(|ui| {
                    if ui
                        .add_enabled(
                            self.directory.parent().is_some(),
                            egui::Button::new(tr("上一级文件夹")),
                        )
                        .clicked()
                    {
                        navigate = self.directory.parent().map(Path::to_path_buf);
                    }
                    ui.label(self.directory.display().to_string());
                });
                egui::ScrollArea::vertical()
                    .id_salt(("game-folder-children", &self.directory))
                    .max_height((ctx.content_rect().height() * 0.35).clamp(80.0, 260.0))
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        if self.folders.is_empty() {
                            ui.weak(tr("没有子文件夹"));
                        }
                        for folder in &self.folders {
                            let name = folder.file_name().unwrap_or_default().to_string_lossy();
                            if ui.selectable_label(false, format!("> {name}")).clicked() {
                                navigate = Some(folder.clone());
                            }
                        }
                    });
                if let Some(error) = &self.error {
                    ui.colored_label(egui::Color32::from_rgb(179, 55, 49), error);
                }
                ui.separator();
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(
                            !self.input.trim().is_empty(),
                            egui::Button::new(tr("使用此文件夹")),
                        )
                        .clicked()
                    {
                        selection = Selection::Chosen(self.candidate());
                    }
                    if ui.button(tr("取消")).clicked() {
                        selection = Selection::Cancelled;
                    }
                });
            });
        if let Some(path) = navigate {
            self.navigate(&path);
        }
        if response.should_close() {
            Selection::Cancelled
        } else {
            selection
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn typed_path_and_enter_navigate_without_confirming_selection() {
        let dir = tempfile::tempdir().unwrap();
        let child = dir.path().join("入力 folder");
        std::fs::create_dir(&child).unwrap();
        let mut picker = FolderPicker::new(dir.path());
        let ctx = egui::Context::default();
        let draw = |picker: &mut FolderPicker, events| {
            let mut selection = Selection::Pending;
            let _ = ctx.run(
                egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(
                        egui::Pos2::ZERO,
                        egui::vec2(1200.0, 900.0),
                    )),
                    events,
                    ..Default::default()
                },
                |ctx| selection = picker.show(ctx, "选择游戏目录"),
            );
            assert!(matches!(selection, Selection::Pending));
        };
        draw(&mut picker, vec![]);
        ctx.memory_mut(|memory| memory.request_focus(egui::Id::new("game-folder-path")));
        draw(
            &mut picker,
            vec![egui::Event::Key {
                key: egui::Key::A,
                physical_key: None,
                pressed: true,
                repeat: false,
                modifiers: egui::Modifiers::COMMAND,
            }],
        );
        draw(
            &mut picker,
            vec![egui::Event::Text(child.display().to_string())],
        );
        assert_eq!(picker.input, child.display().to_string());
        draw(
            &mut picker,
            vec![egui::Event::Key {
                key: egui::Key::Enter,
                physical_key: None,
                pressed: true,
                repeat: false,
                modifiers: egui::Modifiers::NONE,
            }],
        );
        assert_eq!(picker.directory, child.canonicalize().unwrap());
    }

    #[test]
    fn folder_navigation_lists_only_directories_and_retains_location_on_failure() {
        let dir = tempfile::tempdir().unwrap();
        let child = dir.path().join("日本語 folder");
        std::fs::create_dir(&child).unwrap();
        std::fs::write(dir.path().join("file.txt"), b"not a folder").unwrap();
        let mut picker = FolderPicker::new(dir.path());
        assert_eq!(picker.folders, [child.canonicalize().unwrap()]);
        picker.input = "日本語 folder".into();
        picker.navigate(&picker.candidate());
        assert_eq!(picker.directory, child.canonicalize().unwrap());
        assert!(picker.folders.is_empty());
        let previous = picker.directory.clone();
        picker.input = "missing".into();
        picker.navigate(&picker.candidate());
        assert_eq!(picker.directory, previous);
        assert_eq!(picker.input, "missing");
        assert!(picker.error.is_some());
        picker.navigate(&previous.parent().unwrap().to_path_buf());
        assert_eq!(picker.folders.len(), 1);
        assert!(picker.error.is_none());
    }
}
