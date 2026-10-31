//! Refrain's app (SPEC §38): the Tauri shell that opens the windows and answers the page's commands from
//! refrain-core.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::time::Duration;

use refrain_core::paths;
use tauri::{AppHandle, WebviewUrl, WebviewWindowBuilder};

/// How the app was started.
struct Start {
    /// `--self-test`: start hidden, exit 0 once the page answers (the release gate's start check).
    self_test: bool,
}

/// The longest a self-test waits for the page before it fails.
const SELF_TEST_LIMIT: Duration = Duration::from_secs(30);

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct AppInfo {
    version: &'static str,
    release: bool,
    data_folder: String,
}

#[tauri::command]
fn app_info() -> Result<AppInfo, String> {
    Ok(AppInfo {
        version: env!("CARGO_PKG_VERSION"),
        release: paths::RELEASE,
        data_folder: paths::data_folder().map_err(|e| e.to_string())?.display().to_string(),
    })
}

/// The page has started and reached the core.
#[tauri::command]
fn page_ready(app: AppHandle, start: tauri::State<'_, Start>) {
    if start.self_test {
        app.exit(0);
    }
}

fn main() {
    let self_test = std::env::args().skip(1).any(|arg| arg == "--self-test");
    // Off-screen checks never put a window on Hugo's screen or take his keyboard (AGENTS.md rule 11).
    let offscreen = self_test || std::env::var_os("REFRAIN_OFFSCREEN").is_some_and(|v| v == "1");

    tauri::Builder::default()
        .manage(Start { self_test })
        .invoke_handler(tauri::generate_handler![app_info, page_ready])
        .setup(move |app| {
            WebviewWindowBuilder::new(app, "search", WebviewUrl::default())
                .title(if paths::RELEASE { "Refrain" } else { "Refrain Dev" })
                .inner_size(1100.0, 720.0)
                .min_inner_size(640.0, 400.0)
                .data_directory(paths::webview_folder()?)
                .visible(!offscreen)
                .focused(!offscreen)
                .skip_taskbar(offscreen)
                .build()?;
            if self_test {
                let app = app.handle().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(SELF_TEST_LIMIT);
                    app.exit(1);
                });
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .unwrap_or_else(|e| {
            eprintln!("Refrain couldn't start: {e}");
            std::process::exit(1);
        });
}
