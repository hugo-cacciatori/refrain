//! The C# app's recorded answers, replayed through the Rust core (SPEC §38 "Tested against the C# app's answers"):
//! every case of `conformance/cases/<area>.json` must give its answer in `changes/<area>.json` (a deliberate
//! difference, with its reason) or else in `answers/<area>.json` (what the C# app said). Every mismatch is listed.

use std::fs;
use std::path::{Path, PathBuf};

use refrain_core::formats;
use serde_json::{Map, Value, json};

fn conformance() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn read(path: &Path) -> Option<Value> {
    let text = fs::read_to_string(path).ok()?;
    Some(serde_json::from_str(&text).unwrap_or_else(|e| panic!("{} isn't valid JSON: {e}", path.display())))
}

fn answer(op: &str, input: &Value) -> Result<Value, String> {
    let text = |key: &str| input[key].as_str().ok_or_else(|| format!("no \"{key}\" text in {input}"));
    match op {
        "formats.classify" => {
            let ext = text("ext")?;
            Ok(json!({
                "audio": formats::is_audio(ext),
                "read": formats::is_read(ext),
                "replaygain": formats::is_replay_gain(ext),
                "label": formats::label(ext),
            }))
        }
        _ => Err(format!("the rewrite has no answer for \"{op}\" yet")),
    }
}

#[test]
fn every_case_gives_the_recorded_answer_or_its_declared_change() {
    let mut areas: Vec<PathBuf> = fs::read_dir(conformance().join("cases"))
        .expect("conformance/cases is missing")
        .map(|entry| entry.expect("a cases file can't be listed").path())
        .filter(|path| path.extension().is_some_and(|ext| ext == "json"))
        .collect();
    areas.sort();
    assert!(!areas.is_empty(), "conformance/cases has no areas");

    let mut failures = Vec::new();
    for cases_file in &areas {
        let area = cases_file.file_stem().and_then(|s| s.to_str()).expect("a cases file name isn't text");
        let cases = read(cases_file).expect("a listed cases file can't be read");
        let Some(recorded) = read(&conformance().join("answers").join(format!("{area}.json"))) else {
            failures.push(format!("{area}: no recorded answers (REFRAIN_RECORD=1 on the C# side)"));
            continue;
        };
        let changes = read(&conformance().join("changes").join(format!("{area}.json"))).unwrap_or(Value::Object(Map::new()));
        for case in cases.as_array().expect("a cases file is a list") {
            let id = case["id"].as_str().expect("a case has an id");
            let expected = match changes.get(id) {
                Some(change) => &change["answer"],
                None => &recorded[id],
            };
            if expected.is_null() {
                failures.push(format!("{area} / {id}: no recorded answer"));
                continue;
            }
            match answer(case["op"].as_str().expect("a case has an op"), &case["input"]) {
                Ok(got) if &got == expected => {}
                Ok(got) => failures.push(format!("{area} / {id}: {got} instead of {expected}")),
                Err(e) => failures.push(format!("{area} / {id}: {e}")),
            }
        }
        if let Some(changes) = changes.as_object() {
            for id in changes.keys().filter(|id| !cases.as_array().unwrap().iter().any(|c| c["id"] == id.as_str())) {
                failures.push(format!("{area} / {id}: a change for a case that doesn't exist"));
            }
        }
    }
    assert!(failures.is_empty(), "{} case(s) differ:\n{}", failures.len(), failures.join("\n"));
}
