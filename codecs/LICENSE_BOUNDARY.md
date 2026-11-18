# License Isolation Boundary: `/codecs/`

This directory isolates external, unmodified native dynamic libraries loaded dynamically at runtime by `L2.Engines` (never statically linked into `.NET` assemblies).

- **Policy:** Never compile LGPL native codec source directly into managed assemblies or bundle GPL codecs (`ffmpeg.exe`) into the core distribution.
- **Expected Runtime Libraries:**
  - `codecs/libmp3lame.dll` (LGPL-2.0, accessed through permissive `NAudio.Lame` MIT wrapper) + accompanying `COPYING` / `LICENSE` notice.
- Note: Native `.dll` files are excluded from Git via `.gitignore` while license notices (`COPYING*`, `LICENSE*`) may be preserved alongside staged redistributables.
