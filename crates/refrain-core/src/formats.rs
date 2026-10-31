//! The audio formats (SPEC §1): the one list of what counts as audio, and which files have their tags read.

/// Every audio extension: listed, searched by name and played. Monkey's Audio (`.ape`, SPEC §1 "Ignored") and WMA
/// (`.wma`, SPEC §38) are plain files on purpose.
pub const AUDIO: &[&str] = &[
    ".flac", ".ogg", ".oga", ".opus", ".spx", ".mp3", ".mp2", ".wav", ".aif", ".aiff", ".aifc", ".m4a", ".mp4", ".m4b",
    ".wv", ".mpc", ".tak", ".ofr", ".ofs", ".tta", ".dsf", ".dff", ".aac",
];

/// Audio the tag library can't read: Ogg FLAC and True Audio are listed and found by name only.
pub const NAME_ONLY: &[&str] = &[".oga", ".tta"];

/// What rsgain measures and tags (SPEC §1's ReplayGain column).
pub const REPLAY_GAIN: &[&str] = &[
    ".flac", ".ogg", ".oga", ".opus", ".spx", ".mp3", ".mp2", ".wav", ".aif", ".aiff", ".aifc", ".m4a", ".mp4", ".wv",
    ".mpc", ".tak",
];

fn listed(list: &[&str], ext: &str) -> bool {
    list.iter().any(|e| e.eq_ignore_ascii_case(ext))
}

pub fn is_audio(ext: &str) -> bool {
    listed(AUDIO, ext)
}

/// Whether the indexer reads this file's tags and stream info.
pub fn is_read(ext: &str) -> bool {
    is_audio(ext) && !listed(NAME_ONLY, ext)
}

pub fn is_replay_gain(ext: &str) -> bool {
    listed(REPLAY_GAIN, ext)
}

/// The Format column's text: the extension without its dots, in capitals ("FLAC"). Each letter maps to one capital
/// or stays as it is ("ß" stays), as the C# app's invariant upper case did.
pub fn label(ext: &str) -> String {
    ext.trim_start_matches('.')
        .chars()
        .map(|c| {
            let mut upper = c.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(one), None) => one,
                _ => c,
            }
        })
        .collect()
}
