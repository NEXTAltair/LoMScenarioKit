// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the repository game-linking exception.
//! BGM preparation and playback are polled without blocking the UI thread.
use crate::{audio::Player, i18n::tr};
use anyhow::{ensure, Context, Result};
use lom_core::project::Project;
use serde_json::Value;
use std::{
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    time::Instant,
};

#[derive(Clone, Debug, PartialEq)]
pub struct Cue {
    pub name: String,
    pub origin: String,
    pub fade: Option<f64>,
}

struct Preparation {
    child: Child,
    log: tempfile::NamedTempFile,
}
impl Drop for Preparation {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[derive(Default)]
pub struct Music {
    cue: Option<Cue>,
    game: PathBuf,
    player: Player,
    job: Option<Preparation>,
    ready: Option<PathBuf>,
    user_file: Option<tempfile::TempDir>,
    started: Option<Instant>,
    position: f64,
    failed: bool,
    finished: bool,
}
impl Music {
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    fn pause(&mut self) {
        if let Some(start) = self.started.take() {
            self.position += start.elapsed().as_secs_f64();
        }
        self.player.stop();
        self.job = None;
    }

    pub fn preparing(&self) -> bool {
        self.job.is_some()
    }

    pub fn sync(
        &mut self,
        cue: Option<Cue>,
        active: bool,
        project: &Project,
        game: &Path,
        cache: &Path,
        elapsed: f64,
    ) -> Result<bool> {
        let result = self.sync_inner(cue, active, project, game, cache, elapsed);
        if result.is_err() {
            self.pause();
            self.failed = true;
        }
        result
    }

    fn sync_inner(
        &mut self,
        cue: Option<Cue>,
        active: bool,
        project: &Project,
        game: &Path,
        cache: &Path,
        elapsed: f64,
    ) -> Result<bool> {
        let Some(cue) = cue else {
            self.reset();
            return Ok(false);
        };
        let same_track = self
            .cue
            .as_ref()
            .is_some_and(|old| old.name == cue.name && old.origin == cue.origin)
            && self.game == game;
        if !same_track {
            self.reset();
            self.game = game.into();
        } else if self.cue != Some(cue.clone()) {
            self.pause();
            self.finished = false;
        }
        self.cue = Some(cue.clone());
        if !active {
            self.pause();
            self.failed = false;
            return Ok(false);
        }
        if self.failed || self.finished {
            return Ok(false);
        }
        let was_playing = self.player.playing();
        self.player.update()?;
        if was_playing && !self.player.playing() {
            self.started = None;
            self.position = 0.0;
            if cue.fade.is_some() {
                self.finished = true;
                return Ok(false);
            }
        }
        if self.ready.is_none() && self.job.is_none() {
            if cue.name.starts_with("user:") {
                let (folder, path) = user_audio(project, &cue.name)?;
                self.user_file = Some(folder);
                self.ready = Some(path);
            } else {
                ensure!(
                    !game.as_os_str().is_empty(),
                    "{}",
                    tr("请先在设置中选择游戏文件夹，再播放音乐")
                );
                let helper = crate::asset_extraction::helper_path()?;
                std::fs::create_dir_all(cache)?;
                let log = tempfile::NamedTempFile::new_in(cache)?;
                let mut command = Command::new(helper);
                command
                    .arg("--game-dir")
                    .arg(game)
                    .arg("--output-dir")
                    .arg(cache)
                    .arg("--music-name")
                    .arg(&cue.name)
                    .stdin(Stdio::null())
                    .stdout(log.reopen()?)
                    .stderr(Stdio::null());
                #[cfg(windows)]
                {
                    use std::os::windows::process::CommandExt;
                    command.creation_flags(0x08000000);
                }
                self.job = Some(Preparation {
                    child: command.spawn().context(tr("无法启动素材提取组件"))?,
                    log,
                });
            }
        }
        if let Some(job) = self.job.as_mut() {
            let Some(status) = job.child.try_wait()? else {
                return Ok(true);
            };
            let output = std::fs::read_to_string(job.log.path())?;
            let event = output
                .lines()
                .filter_map(|line| serde_json::from_str::<Value>(line).ok())
                .filter(|v| v["event"] == "result" || v["event"] == "error")
                .last()
                .context(tr("音乐准备失败"))?;
            ensure!(
                status.success() && event["event"] == "result",
                "{}: {}",
                tr("音乐准备失败"),
                event["message"].as_str().unwrap_or("")
            );
            let path = PathBuf::from(event["path"].as_str().context(tr("音乐准备失败"))?);
            ensure!(
                path.is_file() && path.canonicalize()?.starts_with(cache.canonicalize()?),
                "{}",
                tr("音乐准备失败")
            );
            self.ready = Some(path);
            self.job = None;
        }
        if !self.player.playing() {
            let fade = cue.fade.map(|seconds| (seconds - elapsed).max(0.0));
            if fade == Some(0.0) {
                self.finished = true;
                return Ok(false);
            }
            self.player
                .play_music(self.ready.as_ref().unwrap(), self.position, fade)?;
            self.started = Some(Instant::now());
        }
        Ok(false)
    }
}

fn user_audio(project: &Project, name: &str) -> Result<(tempfile::TempDir, PathBuf)> {
    let id = name.strip_prefix("user:").unwrap_or("");
    for (key, bytes) in &project.assets {
        if !key.ends_with("/content.json") {
            continue;
        }
        let Ok(meta) = serde_json::from_slice::<Value>(bytes) else {
            continue;
        };
        if meta["id"] != id || meta["type"] != "audio" {
            continue;
        }
        let file = meta["files"]["main"]
            .as_str()
            .context(tr("找不到音乐文件"))?;
        let relative = format!("{}{file}", key.strip_suffix("content.json").unwrap());
        lom_core::package::canonical_archive_name(&relative)?;
        let bytes = project
            .assets
            .get(&relative)
            .context(tr("找不到音乐文件"))?;
        let extension = Path::new(file)
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        ensure!(
            ["ogg", "wav"].contains(&extension.as_str()),
            "{}",
            tr("试听只支持 OGG/WAV")
        );
        let folder = tempfile::tempdir()?;
        let path = folder.path().join(format!("music.{extension}"));
        std::fs::write(&path, bytes)?;
        return Ok((folder, path));
    }
    anyhow::bail!("{}: {name}", tr("找不到音乐文件"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn user_music_uses_its_own_bytes_and_rejects_missing_or_escaping_files() {
        let mut project = lom_core::scenario_addon::new_project("test");
        let key = "assets/user/music/test/content.json";
        project.assets.insert(
            key.into(),
            serde_json::to_vec(&json!({"id":"test","type":"audio","files":{"main":"song.ogg"}}))
                .unwrap(),
        );
        project.assets.insert(
            "assets/user/music/test/song.ogg".into(),
            b"OGG fixture".to_vec(),
        );
        let (folder, path) = user_audio(&project, "user:test").unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), b"OGG fixture");
        drop(folder);
        assert!(!path.exists());
        assert!(user_audio(&project, "user:missing").is_err());
        project.assets.insert(
            key.into(),
            serde_json::to_vec(
                &json!({"id":"test","type":"audio","files":{"main":"../../outside.ogg"}}),
            )
            .unwrap(),
        );
        assert!(user_audio(&project, "user:test").is_err());
    }

    #[test]
    fn preparation_error_is_reported_once_and_pause_allows_retry() {
        let project = lom_core::scenario_addon::new_project("test");
        let mut music = Music::default();
        let cue = Some(Cue {
            name: "主題_001".into(),
            origin: "music".into(),
            fade: None,
        });
        let temp = tempfile::tempdir().unwrap();
        assert!(music
            .sync(cue.clone(), true, &project, Path::new(""), temp.path(), 0.0)
            .is_err());
        assert!(!music
            .sync(cue.clone(), true, &project, Path::new(""), temp.path(), 0.0)
            .unwrap());
        music
            .sync(
                cue.clone(),
                false,
                &project,
                Path::new(""),
                temp.path(),
                0.0,
            )
            .unwrap();
        assert!(music
            .sync(cue, true, &project, Path::new(""), temp.path(), 0.0)
            .is_err());
    }

    #[test]
    #[cfg(unix)]
    fn pause_resume_stop_and_drop_own_the_player_process() {
        use std::os::unix::fs::PermissionsExt;
        let temp = tempfile::tempdir().unwrap();
        let program = temp.path().join("player");
        std::fs::write(
            &program,
            "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"$0.log\"\nexec sleep 60\n",
        )
        .unwrap();
        std::fs::set_permissions(&program, std::fs::Permissions::from_mode(0o755)).unwrap();
        let audio = temp.path().join("song.ogg");
        std::fs::write(&audio, "fixture").unwrap();
        let cue = Cue {
            name: "主題_001".into(),
            origin: "music".into(),
            fade: None,
        };
        let mut music = Music {
            cue: Some(cue.clone()),
            ready: Some(audio),
            ..Music::default()
        };
        music.player.test_program = Some(program.clone());
        let project = lom_core::scenario_addon::new_project("test");
        music
            .sync(
                Some(cue.clone()),
                true,
                &project,
                Path::new(""),
                temp.path(),
                0.0,
            )
            .unwrap();
        std::thread::sleep(std::time::Duration::from_millis(30));
        music
            .sync(
                Some(cue.clone()),
                false,
                &project,
                Path::new(""),
                temp.path(),
                1.0,
            )
            .unwrap();
        assert!(!music.player.playing());
        assert!(music.position > 0.0);
        music
            .sync(
                Some(cue.clone()),
                true,
                &project,
                Path::new(""),
                temp.path(),
                1.0,
            )
            .unwrap();
        std::thread::sleep(std::time::Duration::from_millis(30));
        assert!(music.player.playing());
        let args = std::fs::read_to_string(program.with_extension("log")).unwrap();
        let offsets: Vec<f64> = args
            .lines()
            .collect::<Vec<_>>()
            .windows(2)
            .filter(|w| w[0] == "-af")
            .map(|w| {
                w[1].strip_prefix("atrim=start=")
                    .unwrap()
                    .split(',')
                    .next()
                    .unwrap()
                    .parse()
                    .unwrap()
            })
            .collect();
        assert_eq!(offsets.len(), 2);
        assert_eq!(offsets[0], 0.0);
        assert!(offsets[1] > 0.0);
        music
            .sync(None, true, &project, Path::new(""), temp.path(), 0.0)
            .unwrap();
        assert!(!music.player.playing());
        assert!(music.ready.is_none());
    }
}
