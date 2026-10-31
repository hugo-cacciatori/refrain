//! Where the app keeps its own files (Ground rule 1; SPEC §38 "Data folder, builds and identity"):
//! `%LOCALAPPDATA%\Refrain\` for the release build, `%LOCALAPPDATA%\Refrain\dev\` for "Refrain Dev", so a dev build
//! never touches the installed app's files. `REFRAIN_DATA` overrides both, so tests and checks never touch either.

use std::ffi::OsString;
use std::io;
use std::path::PathBuf;

/// Whether this is the release build (`--features release`), not "Refrain Dev".
pub const RELEASE: bool = cfg!(feature = "release");

/// Whether the data folder is the real one, not `REFRAIN_DATA`'s (a test's, the release gate's, an agent's check):
/// only the real one may take an older app's data.
pub fn is_real_data_folder() -> bool {
    std::env::var_os("REFRAIN_DATA").is_none_or(|set| set.is_empty())
}

/// The data folder, created when missing.
pub fn data_folder() -> io::Result<PathBuf> {
    let folder = resolve(std::env::var_os("REFRAIN_DATA"), std::env::var_os("LOCALAPPDATA"), RELEASE)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "LOCALAPPDATA isn't set."))?;
    std::fs::create_dir_all(&folder)?;
    Ok(folder)
}

/// WebView2's own files: inside the data folder, never in a folder of its own beside it (Ground rule 1).
pub fn webview_folder() -> io::Result<PathBuf> {
    Ok(data_folder()?.join("webview"))
}

fn resolve(set: Option<OsString>, local: Option<OsString>, release: bool) -> Option<PathBuf> {
    match set {
        Some(set) if !set.is_empty() => Some(set.into()),
        _ => {
            let base = PathBuf::from(local?).join("Refrain");
            Some(if release { base } else { base.join("dev") })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_release_uses_refrains_own_folder_and_dev_builds_its_dev_folder() {
        let local = Some(OsString::from(r"C:\Users\x\AppData\Local"));
        assert_eq!(resolve(None, local.clone(), true), Some(PathBuf::from(r"C:\Users\x\AppData\Local\Refrain")));
        assert_eq!(resolve(None, local, false), Some(PathBuf::from(r"C:\Users\x\AppData\Local\Refrain\dev")));
    }

    #[test]
    fn refrain_data_overrides_both_builds_unless_empty() {
        let local = Some(OsString::from(r"C:\L"));
        let set = Some(OsString::from(r"C:\T\data"));
        assert_eq!(resolve(set.clone(), local.clone(), true), Some(PathBuf::from(r"C:\T\data")));
        assert_eq!(resolve(set, local.clone(), false), Some(PathBuf::from(r"C:\T\data")));
        assert_eq!(resolve(Some(OsString::new()), local, true), Some(PathBuf::from(r"C:\L\Refrain")));
    }

    #[test]
    fn no_local_app_data_is_no_folder() {
        assert_eq!(resolve(None, None, true), None);
    }
}
