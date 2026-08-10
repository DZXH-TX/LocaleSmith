use std::collections::HashSet;

use serde_json::Value as JsonValue;

use crate::{
    model::{MetadataSource, ModLoader, ModMetadataSummary},
    path_safety::{is_safe_mod_id, sanitize_jar_namespace},
};

pub(crate) const DETECTION_PRECEDENCE: [(&str, ModLoader, MetadataFormat); 5] = [
    (
        "META-INF/neoforge.mods.toml",
        ModLoader::NeoForge,
        MetadataFormat::TomlMods,
    ),
    (
        "fabric.mod.json",
        ModLoader::Fabric,
        MetadataFormat::FabricJson,
    ),
    (
        "quilt.mod.json",
        ModLoader::Quilt,
        MetadataFormat::QuiltJson,
    ),
    (
        "META-INF/mods.toml",
        ModLoader::Forge,
        MetadataFormat::TomlMods,
    ),
    (
        "mcmod.info",
        ModLoader::Forge,
        MetadataFormat::LegacyForgeJson,
    ),
];

#[derive(Debug, Clone, Copy)]
pub(crate) enum MetadataFormat {
    TomlMods,
    FabricJson,
    QuiltJson,
    LegacyForgeJson,
}

#[derive(Debug)]
pub(crate) struct ParsedMetadata {
    pub loader: ModLoader,
    pub path: String,
    pub raw_ids: Vec<String>,
    pub parse_error: Option<String>,
}

pub(crate) fn parse_metadata(
    path: &str,
    loader: ModLoader,
    format: MetadataFormat,
    text: Result<&str, &str>,
) -> ParsedMetadata {
    let parsed = match text {
        Ok(text) => match format {
            MetadataFormat::TomlMods => parse_toml_ids(text),
            MetadataFormat::FabricJson => parse_fabric_ids(text),
            MetadataFormat::QuiltJson => parse_quilt_ids(text),
            MetadataFormat::LegacyForgeJson => parse_legacy_forge_ids(text),
        },
        Err(error) => Err(error.to_owned()),
    };

    match parsed {
        Ok(raw_ids) => ParsedMetadata {
            loader,
            path: path.to_owned(),
            raw_ids,
            parse_error: None,
        },
        Err(error) => ParsedMetadata {
            loader,
            path: path.to_owned(),
            raw_ids: Vec::new(),
            parse_error: Some(error),
        },
    }
}

pub(crate) fn summarize_metadata(
    archive_path: &std::path::Path,
    parsed: Vec<ParsedMetadata>,
) -> ModMetadataSummary {
    let filename_fallback_namespace = sanitize_jar_namespace(archive_path);
    let mut sources = Vec::with_capacity(parsed.len());
    let mut all_ids = Vec::new();
    let mut seen = HashSet::new();
    let mut primary_loader = None;

    for parsed_source in parsed {
        let mut accepted = Vec::new();
        let mut rejected = Vec::new();
        for candidate in parsed_source.raw_ids {
            if is_safe_mod_id(&candidate) {
                if seen.insert(candidate.clone()) {
                    if primary_loader.is_none() {
                        primary_loader = Some(parsed_source.loader);
                    }
                    accepted.push(candidate.clone());
                    all_ids.push(candidate);
                }
            } else if !candidate.trim().is_empty() {
                rejected.push(candidate);
            }
        }
        sources.push(MetadataSource {
            path: parsed_source.path,
            loader: parsed_source.loader,
            mod_ids: accepted,
            rejected_mod_ids: rejected,
            parse_error: parsed_source.parse_error,
        });
    }

    let used_filename_fallback = all_ids.is_empty();
    let primary_mod_id = all_ids
        .first()
        .cloned()
        .unwrap_or_else(|| filename_fallback_namespace.clone());

    ModMetadataSummary {
        detection_precedence: DETECTION_PRECEDENCE
            .iter()
            .map(|(path, _, _)| *path)
            .collect(),
        sources,
        primary_loader,
        primary_mod_id,
        mod_ids: all_ids,
        used_filename_fallback,
        filename_fallback_namespace,
    }
}

fn parse_toml_ids(text: &str) -> Result<Vec<String>, String> {
    let root: toml::Value = toml::from_str(text).map_err(|error| error.to_string())?;
    let ids = root
        .get("mods")
        .and_then(toml::Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|item| item.get("modId").and_then(toml::Value::as_str))
        .map(ToOwned::to_owned)
        .collect();
    Ok(ids)
}

fn parse_fabric_ids(text: &str) -> Result<Vec<String>, String> {
    let root: JsonValue = serde_json::from_str(text).map_err(|error| error.to_string())?;
    Ok(root
        .get("id")
        .and_then(JsonValue::as_str)
        .map(|id| vec![id.to_owned()])
        .unwrap_or_default())
}

fn parse_quilt_ids(text: &str) -> Result<Vec<String>, String> {
    let root: JsonValue = serde_json::from_str(text).map_err(|error| error.to_string())?;
    Ok(root
        .get("quilt_loader")
        .and_then(|loader| loader.get("id"))
        .and_then(JsonValue::as_str)
        .map(|id| vec![id.to_owned()])
        .unwrap_or_default())
}

fn parse_legacy_forge_ids(text: &str) -> Result<Vec<String>, String> {
    let root: JsonValue = serde_json::from_str(text).map_err(|error| error.to_string())?;
    let records: Vec<&JsonValue> = match &root {
        JsonValue::Array(records) => records.iter().collect(),
        JsonValue::Object(object) => object
            .get("modList")
            .and_then(JsonValue::as_array)
            .map_or_else(|| vec![&root], |records| records.iter().collect()),
        _ => Vec::new(),
    };

    Ok(records
        .into_iter()
        .filter_map(|record| {
            record
                .get("modid")
                .or_else(|| record.get("modId"))
                .and_then(JsonValue::as_str)
        })
        .map(ToOwned::to_owned)
        .collect())
}

#[cfg(test)]
mod tests {
    use super::{MetadataFormat, parse_metadata, parse_toml_ids, summarize_metadata};
    use crate::model::ModLoader;
    use std::path::Path;

    #[test]
    fn parses_forge_mod_arrays() {
        let ids = parse_toml_ids(
            r#"
                modLoader="javafml"
                [[mods]]
                modId="first_mod"
                [[mods]]
                modId="second_mod"
            "#,
        )
        .unwrap();
        assert_eq!(ids, ["first_mod", "second_mod"]);
    }

    #[test]
    fn precedence_selects_first_usable_loader_id() {
        let parsed = vec![
            parse_metadata(
                "META-INF/neoforge.mods.toml",
                ModLoader::NeoForge,
                MetadataFormat::TomlMods,
                Ok("[[mods]]\nmodId='neo_mod'"),
            ),
            parse_metadata(
                "fabric.mod.json",
                ModLoader::Fabric,
                MetadataFormat::FabricJson,
                Ok(r#"{"id":"fabric_mod"}"#),
            ),
        ];
        let summary = summarize_metadata(Path::new("fallback.jar"), parsed);
        assert_eq!(summary.primary_loader, Some(ModLoader::NeoForge));
        assert_eq!(summary.primary_mod_id, "neo_mod");
        assert_eq!(summary.mod_ids, ["neo_mod", "fabric_mod"]);
    }

    #[test]
    fn invalid_or_missing_metadata_uses_filename() {
        let parsed = vec![parse_metadata(
            "fabric.mod.json",
            ModLoader::Fabric,
            MetadataFormat::FabricJson,
            Err("not UTF-8"),
        )];
        let summary = summarize_metadata(Path::new("Demo Mod-2.0.jar"), parsed);
        assert!(summary.used_filename_fallback);
        assert_eq!(summary.primary_mod_id, "demo_mod-2.0");
        assert_eq!(summary.sources[0].parse_error.as_deref(), Some("not UTF-8"));
    }
}
