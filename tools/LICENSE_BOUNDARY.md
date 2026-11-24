# License Isolation Boundary: `/tools/`

This directory isolates external, unmodified CLI tools invoked out-of-process via standard process pipes (never statically linked into `.NET` assemblies).

- **Policy:** Never statically link or embed LGPL/GPL source code into managed `.NET` assemblies.
- **Expected Runtime Tools:**
  - `tools/atracdenc/atracdenc.exe` (**atracdenc v0.2.3**, LGPL-2.1) — Upstream source: https://github.com/dcherednik/atracdenc/releases/tag/0.2.3 · Full license: `tools/atracdenc/LICENSE`
  - `tools/vgmstream/vgmstream-cli.exe` (**vgmstream r2168**, ISC) + bundled FFmpeg `n9.0.2` (`avcodec`/`avformat`/`avutil`/`swresample`, LGPL-2.1), `libmpg123-0.dll` (LGPL-2.1), and Xiph.Org `libcelt`/`libspeex`/`libvorbis` (BSD-3-Clause) — Upstream sources: https://github.com/vgmstream/vgmstream , https://ffmpeg.org/releases/ , https://www.mpg123.de/download/ , https://xiph.org/downloads/ · Full licenses: `tools/vgmstream/COPYING`, `tools/vgmstream/COPYING.LGPLv2.1`, and `tools/vgmstream/THIRD_PARTY_LICENSES.txt`
- **Note:** Third-party binaries are excluded from Git via `.gitignore` while license files (`COPYING*`, `LICENSE*`, `THIRD_PARTY_LICENSES.txt`) are preserved and bundled into release packages.
