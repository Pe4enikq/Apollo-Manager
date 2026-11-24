# License Isolation Boundary: `/codecs/`

This directory isolates external, unmodified native dynamic libraries loaded dynamically at runtime via `NativeLibrary.Load` (never statically linked into `.NET` assemblies).

- **Policy:** Never compile LGPL native codec source directly into managed `.NET` assemblies or bundle GPL codecs (`ffmpeg.exe`).
- **Expected Runtime Library:**
  - `codecs/libmp3lame.dll` (**LAME 3.100**, LGPL-2.0+ / redistributed under LGPL-2.1, loaded dynamically via `NativeLibrary.Load` in `LameNativeEncoder.cs`).
  - Upstream source: https://sourceforge.net/projects/lame/files/lame/3.100/
  - Full license text: `codecs/COPYING.LAME`
- **Note:** Native `.dll` files are excluded from Git via `.gitignore` while license notices (`COPYING*`, `LICENSE*`) are preserved alongside staged release packages.
