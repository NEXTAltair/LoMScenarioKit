// Copyright (C) 2026 NEXTAltair
// GPL-3.0-only with the repository game-linking exception.
//! An embedded, editable example also works in portable editor builds.
use anyhow::Result;
use lom_core::project::Project;
use serde_json::{json, Value};
use std::{collections::BTreeMap, path::Path};

fn project() -> Result<Project> {
    let mut project = Project::new();
    project.manifest =
        serde_json::from_str(include_str!("../../../samples/first-scene/manifest.json"))?;
    let story: Value = serde_json::from_str(include_str!(
        "../../../samples/first-scene/story/lsa_first_scene_main.json"
    ))?;
    project.stories = BTreeMap::from([("lsa_first_scene_main".into(), story)]);
    project.paths.clear();
    Ok(project)
}

pub fn create(parent: &Path) -> Result<Project> {
    std::fs::create_dir_all(parent)?;
    // Publish only a complete copy. A failed save removes this private directory
    // and the caller keeps the currently open project.
    let directory = tempfile::Builder::new()
        .prefix("first-scene-")
        .tempdir_in(parent)?;
    let mut project = project()?;
    let id = directory
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .to_ascii_lowercase()
        .replace('-', "_");
    project.manifest["id"] = json!(id);
    project.manifest["_editor"]["scenario_addon"]["id"] = json!(id);
    project.manifest["_editor"]["scenario_addon"]["events"][0]["id"] = json!(format!("{id}_meet"));
    project.save_to(directory.path())?;
    std::fs::create_dir(directory.path().join("outline"))?;
    for (name, text) in [
        (
            "README.md",
            include_str!("../../../samples/first-scene/README.md"),
        ),
        (
            "outline/guide.md",
            include_str!("../../../samples/first-scene/outline/guide.md"),
        ),
        (
            "LICENSE",
            include_str!("../../../samples/first-scene/LICENSE"),
        ),
    ] {
        std::fs::write(directory.path().join(name), text)?;
    }
    let _ = directory.keep();
    Ok(project)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sample_exports_both_routes_and_uses_available_official_assets() {
        let sample = project().unwrap();
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../samples/first-scene");
        let source = Project::open(&root).unwrap();
        assert_eq!(sample.manifest, source.manifest);
        assert_eq!(sample.stories, source.stories);
        let story = &sample.stories["lsa_first_scene_main"];
        lom_core::validate::validate_story(story).unwrap();
        assert!(lom_core::release::find_stage_issues(story).is_empty());
        let files = lom_core::scenario_addon::output(&sample).unwrap();
        let lua = String::from_utf8(files["story/lsa_first_scene_main.lua"].clone()).unwrap();
        assert!(lua.contains("PlayMusic"));
        assert!(lua.contains("FadeOutMusic"));
        for target in ["rest_reply", "train_reply"] {
            assert!(crate::preview::route(story, target).is_some());
            let mut cursor = target.to_owned();
            let nodes = story["nodes"].as_array().unwrap();
            let mut seen = std::collections::BTreeSet::new();
            while cursor != "end" {
                assert!(seen.insert(cursor.clone()), "sample route loops");
                let index = nodes.iter().position(|n| n["id"] == cursor).unwrap();
                let next = lom_core::analysis::successors(story, index);
                assert_eq!(next.len(), 1);
                cursor = next[0].clone();
            }
        }
        let catalog = crate::forms::Catalog::new();
        let positions: Value =
            serde_json::from_str(include_str!("../../../data/stage_positions.json")).unwrap();
        for node in story["nodes"].as_array().unwrap() {
            if let Some(position) = node["position"].as_str() {
                assert!(
                    positions["positions"].get(position).is_some(),
                    "unknown stage position: {position}"
                );
            }
            if let Some(character) = node["character"].as_str() {
                if let Some(portrait) = node["portrait"].as_str() {
                    assert!(
                        catalog
                            .options("portrait", node, &[], &[])
                            .iter()
                            .any(|(id, _)| id == portrait),
                        "{character}/{portrait}"
                    );
                }
            }
        }
    }

    #[test]
    fn sample_copies_have_distinct_save_locations_and_keep_the_guide_and_license() {
        let root = tempfile::tempdir().unwrap();
        let mut first = create(root.path()).unwrap();
        let second = create(root.path()).unwrap();
        assert_ne!(first.source, second.source);
        assert_ne!(first.manifest["id"], second.manifest["id"]);
        let path = first.source.clone().unwrap();
        assert!(path.join("outline/guide.md").is_file());
        assert!(path.join("LICENSE").is_file());
        assert_eq!(Project::open(&path).unwrap().stories, first.stories);
        first.stories.get_mut("lsa_first_scene_main").unwrap()["nodes"][4]["text"] =
            json!("自分の台詞");
        first.save_to(&path).unwrap();
        assert_eq!(
            Project::open(&path).unwrap().stories["lsa_first_scene_main"]["nodes"][4]["text"],
            "自分の台詞"
        );
        assert_ne!(
            second.stories["lsa_first_scene_main"]["nodes"][4]["text"],
            "自分の台詞"
        );
        assert_ne!(
            project().unwrap().stories["lsa_first_scene_main"]["nodes"][4]["text"],
            "自分の台詞"
        );
        assert!(lom_core::scenario_addon::output(&first).is_ok());
        let blocked = root.path().join("file");
        std::fs::write(&blocked, "keep").unwrap();
        assert!(create(&blocked).is_err());
        assert_eq!(std::fs::read_to_string(&blocked).unwrap(), "keep");
    }
}
