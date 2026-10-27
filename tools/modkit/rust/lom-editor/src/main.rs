// LoMScenarioKit modifications: Copyright (C) 2026 NEXTAltair, 2026-10-26.
// GPL-3.0-only with the repository game-linking exception; upstream portions retain MIT.
mod asset_extraction;
mod authoring;
mod folder_picker;
mod forms;
mod game_setup;
mod help;
mod i18n;
mod macos_window;
mod persistence;
mod preview;
mod shell;
mod tool_report;
mod tools_panel;
mod workspace;

fn main() -> eframe::Result {
    workspace::run()
}
mod advanced;

mod audio;
mod content_panel;
mod manifest_panel;
mod preview_music;
mod review_panel;
mod scenario_panel;
mod scenario_quick;
