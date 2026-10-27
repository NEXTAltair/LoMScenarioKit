//! Own the preview process so Stop and closing the editor really stop playback.
use anyhow::{ensure, Context, Result};
use std::{
    path::Path,
    process::{Child, Command, Stdio},
};
#[derive(Default)]
pub struct Player {
    child: Option<Child>,
    errors: Option<tempfile::NamedTempFile>,
    #[cfg(test)]
    pub test_program: Option<std::path::PathBuf>,
}
impl Player {
    /// Decode to the resume offset: converted Wwise OGGs lack reliable seeking
    /// granules. FFplay's -ss can otherwise exit successfully without any sound.
    pub fn play_music(&mut self, path: &Path, offset: f64, fade: Option<f64>) -> Result<()> {
        ensure!(path.is_file(), "{}", crate::i18n::tr("找不到音乐文件"));
        self.stop();
        let player = ffplay();
        #[cfg(test)]
        let player = self.test_program.clone().unwrap_or(player);
        let mut command = Command::new(player);
        command
            .args(["-nodisp", "-autoexit", "-nostats", "-loglevel", "error"])
            .args(["-loop", "1", "-i"])
            .arg(path);
        let mut filter = format!("atrim=start={},asetpts=PTS-STARTPTS", offset.max(0.0));
        if let Some(seconds) = fade {
            filter.push_str(&format!(",afade=t=out:st=0:d={}", seconds.max(0.001)));
        }
        command.arg("-af").arg(filter);
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000);
        }
        self.launch(&mut command).context(crate::i18n::tr(
            "无法播放音乐；请安装 FFplay，或用 LOM_FFPLAY 指定路径",
        ))?;
        Ok(())
    }
    pub fn stop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        self.errors = None;
    }
    fn launch(&mut self, command: &mut Command) -> Result<()> {
        let errors = tempfile::NamedTempFile::new()?;
        self.child = Some(
            command
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(errors.reopen()?)
                .spawn()?,
        );
        self.errors = Some(errors);
        Ok(())
    }
    pub fn update(&mut self) -> Result<()> {
        let Some(child) = self.child.as_mut() else {
            return Ok(());
        };
        let status = match child.try_wait() {
            Ok(status) => status,
            Err(error) => {
                self.stop();
                return Err(error).context(crate::i18n::tr("无法读取试听播放状态"));
            }
        };
        if let Some(status) = status {
            self.child = None;
            use std::io::Read;
            let mut details = String::new();
            if let Some(log) = self.errors.take() {
                log.reopen()?.take(8192).read_to_string(&mut details)?;
            }
            // FFplay can return success even when its decoder/audio device failed.
            ensure!(
                status.success() && details.trim().is_empty(),
                "{} ({status}): {}",
                crate::i18n::tr("音频播放失败"),
                details.trim()
            );
        }
        Ok(())
    }
    pub fn playing(&self) -> bool {
        self.child.is_some()
    }
    pub fn play(&mut self, path: &Path) -> Result<()> {
        ensure!(path.is_file(), "找不到试听文件");
        let ext = path
            .extension()
            .and_then(|s| s.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        ensure!(["ogg", "wav"].contains(&ext.as_str()), "试听只支持 OGG/WAV");
        self.stop();
        let mut command = if cfg!(target_os = "macos") && ext == "wav" {
            let mut c = Command::new("/usr/bin/afplay");
            c.arg(path);
            c
        } else if cfg!(windows) && ext == "wav" {
            let mut c = Command::new("powershell.exe");
            c.args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$p = New-Object System.Media.SoundPlayer $env:LOM_PREVIEW_AUDIO; $p.PlaySync()",
            ])
            .env("LOM_PREVIEW_AUDIO", path);
            c
        } else {
            let mut c = Command::new(ffplay());
            c.args([
                "-nodisp",
                "-autoexit",
                "-nostats",
                "-loglevel",
                "error",
                "-i",
            ])
            .arg(path);
            c
        };
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        }
        self.launch(&mut command)
            .context("无法启动试听；OGG 需要 FFplay（可用 LOM_FFPLAY 指定路径）")?;
        Ok(())
    }
}
fn ffplay() -> std::path::PathBuf {
    std::env::var_os("LOM_FFPLAY")
        .map(std::path::PathBuf::from)
        .or_else(|| {
            [
                "/opt/homebrew/bin/ffplay",
                "/usr/local/bin/ffplay",
                "/home/linuxbrew/.linuxbrew/bin/ffplay",
            ]
            .into_iter()
            .map(std::path::PathBuf::from)
            .find(|p| p.is_file())
        })
        .unwrap_or_else(|| "ffplay".into())
}
impl Drop for Player {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(all(test, unix))]
mod lifecycle_tests {
    use super::*;
    #[test]
    fn dropping_player_reaps_its_process() {
        let child = Command::new("sleep").arg("60").spawn().unwrap();
        let pid = child.id();
        let player = Player {
            child: Some(child),
            errors: None,
            test_program: None,
        };
        drop(player);
        assert!(!Command::new("kill")
            .args(["-0", &pid.to_string()])
            .stderr(Stdio::null())
            .status()
            .unwrap()
            .success());
    }

    #[test]
    fn decoder_error_with_success_exit_is_reported_once() {
        let mut player = Player::default();
        let mut command = Command::new("sh");
        command.args(["-c", "echo 'invalid audio' >&2; exit 0"]);
        player.launch(&mut command).unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        loop {
            match player.update() {
                Err(error) => {
                    assert!(error.to_string().contains("invalid audio"));
                    break;
                }
                Ok(()) => {
                    assert!(std::time::Instant::now() < deadline);
                    std::thread::sleep(std::time::Duration::from_millis(10));
                }
            }
        }
        assert!(!player.playing());
        player.update().unwrap();
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    fn wait_for_completion(player: &mut Player) -> Result<()> {
        let deadline = Instant::now() + Duration::from_secs(10);
        while player.playing() {
            player.update()?;
            assert!(Instant::now() < deadline, "试听子进程没有及时结束");
            std::thread::sleep(Duration::from_millis(20));
        }
        Ok(())
    }

    #[test]
    fn invalid_windows_wav_reports_playback_failure_once() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("损坏 音频.wav");
        std::fs::write(&path, b"not a wave file").unwrap();
        let mut player = Player::default();
        player.play(&path).unwrap();
        let error = wait_for_completion(&mut player).unwrap_err();
        assert!(error.to_string().contains(&crate::i18n::tr("音频播放失败")));
        assert!(error.to_string().contains("exit code: 1"));
        assert!(!player.playing());
        player.update().unwrap();
    }

    #[test]
    fn windows_wav_completion_and_explicit_stop_are_not_failures() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("有效 音频.wav");
        // Two seconds of silent, mono, 16-bit PCM at 8000 Hz.
        let mut bytes = Vec::from(*b"RIFF");
        bytes.extend(32_036_u32.to_le_bytes());
        bytes.extend(b"WAVEfmt ");
        bytes.extend(16_u32.to_le_bytes());
        bytes.extend(1_u16.to_le_bytes());
        bytes.extend(1_u16.to_le_bytes());
        bytes.extend(8_000_u32.to_le_bytes());
        bytes.extend(16_000_u32.to_le_bytes());
        bytes.extend(2_u16.to_le_bytes());
        bytes.extend(16_u16.to_le_bytes());
        bytes.extend(b"data");
        bytes.extend(32_000_u32.to_le_bytes());
        bytes.resize(32_044, 0);
        std::fs::write(&path, bytes).unwrap();
        let mut player = Player::default();
        player.play(&path).unwrap();
        wait_for_completion(&mut player).unwrap();
        assert!(!player.playing());
        player.play(&path).unwrap();
        assert!(player.playing());
        player.stop();
        assert!(!player.playing());
        player.update().unwrap();
    }
}
