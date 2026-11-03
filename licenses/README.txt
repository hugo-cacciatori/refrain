Refrain's licence is LICENSE.txt in this folder.

Refrain ships with these third-party components. Their licence texts are in this folder. The libraries the app is
built from (the Rust crates in Refrain.exe and its right-click menu, the page's packages) are listed, each with its
licence text, in THIRD-PARTY.txt, made with each installer; the WebView2 loader's licence is WebView2-BSD.txt.

  The bundled tools
  rsgain 3.6                      BSD-2-Clause          rsgain-BSD-2-Clause.txt
  CRC++ (built into rsgain)       BSD-3-Clause          CRCpp-BSD.txt
  FFmpeg 7.1 (built into rsgain)  LGPL-2.1-or-later     LGPL-2.1.txt
  Fraunhofer FDK AAC (built into  FDK AAC licence       FDK-AAC-NOTICE.txt
    rsgain)
  flac 1.5.0                      GPL-2.0-or-later      flac-GPL-2.0.txt
  libFLAC 1.5.0 (with flac)       BSD-3-Clause          libFLAC-Xiph-BSD.txt
                                  (Xiph.Org)

rsgain and flac are separate programs that Refrain runs; neither is linked into Refrain.

rsgain 3.6's Windows build gets FFmpeg from vcpkg with the avcodec, avformat, swresample and fdk-aac features,
and without "gpl" (see rsgain's vcpkg.json at https://github.com/complexlogic/rsgain), so FFmpeg is under the
LGPL. Its source code is at https://ffmpeg.org/, and rsgain's at https://github.com/complexlogic/rsgain.
The FDK AAC library's source code is at https://github.com/mstorsjo/fdk-aac.

flac 1.5.0 is the official Xiph.Org Win64 build (flac-1.5.0-win.zip from https://github.com/xiph/flac/releases);
Refrain runs flac.exe to cut single-file albums into tracks. Its source code is at https://github.com/xiph/flac.

On request, the complete corresponding source code of the exact versions of FFmpeg and flac shipped with Refrain
is provided, for three years after that version of Refrain was last distributed: ask at
https://github.com/hugo-cacciatori/refrain/issues.
