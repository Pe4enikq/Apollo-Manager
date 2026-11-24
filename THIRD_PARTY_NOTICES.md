# Third-Party Software Notices

Apollo Manager is licensed under the **MIT License** (see `LICENSE`). It uses the following unmodified open-source libraries and external dynamic/CLI tools.

---

## 1. Managed .NET Dependencies (NuGet)

| Package | Version | License | Purpose | Upstream Source |
| :--- | :--- | :--- | :--- | :--- |
| **Avalonia** / **Avalonia.Desktop** / **Avalonia.Themes.Fluent** / **Avalonia.Fonts.Inter** | `11.3.23` | MIT | Cross-platform desktop UI framework | https://github.com/AvaloniaUI/Avalonia |
| **SkiaSharp** | `2.88.x` (via Avalonia) | MIT | 2D hardware/software graphics rendering | https://github.com/mono/SkiaSharp |
| **CommunityToolkit.Mvvm** | `8.4.0` | MIT | MVVM observable objects & commands | https://github.com/CommunityToolkit/dotnet |
| **NAudio** | `2.2.1` | MIT | Windows audio playback, WAV/MF decoding | https://github.com/naudio/NAudio |
| **NVorbis** | `0.10.5` | MIT | Ogg Vorbis audio decoding | https://github.com/NVorbis/NVorbis |
| **Concentus** | `2.2.2` | BSD-3-Clause | Pure-C# Opus audio decoding | https://github.com/lostromb/concentus |
| **Concentus.Oggfile** | `1.0.6` | MS-PL / BSD-3-Clause | Ogg Opus container demuxer | https://github.com/lostromb/concentus.oggfile |
| **z440.atl.core (ATL.NET)** | `6.14.0` | MIT | Audio tag & cover-art metadata reader/writer | https://github.com/Zeugma440/atldotnet |
| **Ude.NetStandard** *(transitive via `z440.atl.core`)* | `1.2.0` | **MPL-1.1 / LGPL-2.1** *(tri-licensed MPL-1.1/GPL-2.0/LGPL-2.1; used unmodified under MPL-1.1 / LGPL-2.1)* | Charset detection for legacy Shift-JIS / ID3 tags | https://github.com/yinyue200/Ude |
| **Microsoft.Windows.CsWin32** | `0.3.106` | MIT | Build-time Win32 P/Invoke source generator | https://github.com/microsoft/CsWin32 |
| **xUnit** *(test project only)* | `2.x` | Apache-2.0 | Unit & integration test runner | https://github.com/xunit/xunit |

---

## 2. External Dynamic Libraries & Out-of-Process CLI Tools

These components are **never** statically linked into Apollo Manager's managed `.NET` assemblies. They are distributed unmodified as separate files so users may inspect or replace them freely under LGPL terms:

| Component | Version | License | Isolation Mechanism | Upstream Source & Local License File |
| :--- | :--- | :--- | :--- | :--- |
| **`libmp3lame.dll`** (LAME MP3 Encoder) | `3.100` | LGPL-2.0+ *(redistributed under LGPL-2.1)* | Dynamic `NativeLibrary.Load` via `LameNativeEncoder` | https://sourceforge.net/projects/lame/files/lame/3.100/ · `codecs/COPYING.LAME` |
| **`atracdenc.exe`** | `v0.2.3` | LGPL-2.1 | Out-of-process CLI invocation | https://github.com/dcherednik/atracdenc/releases/tag/0.2.3 · `tools/atracdenc/LICENSE` |
| **`vgmstream-cli.exe`** | `r2168` | ISC | Out-of-process CLI invocation | https://github.com/vgmstream/vgmstream · `tools/vgmstream/COPYING` |
| **`avcodec-vgmstream-63.dll`**, **`avformat-vgmstream-63.dll`**, **`avutil-vgmstream-61.dll`**, **`swresample-vgmstream-7.dll`** (FFmpeg for vgmstream) | `n9.0.2` (LGPL build) | LGPL-2.1 | Dynamic DLLs loaded by `vgmstream-cli.exe` | https://ffmpeg.org/releases/ · https://github.com/vgmstream/vgmstream-deps · `tools/vgmstream/COPYING.LGPLv2.1` |
| **`libmpg123-0.dll`** (mpg123 for vgmstream) | vgmstream `r2168` build | LGPL-2.1 | Dynamic DLL loaded by `vgmstream-cli.exe` | https://www.mpg123.de/download/ · `tools/vgmstream/COPYING.LGPLv2.1` |
| **`libcelt-0061.dll`**, **`libcelt-0110.dll`**, **`libspeex-1.dll`**, **`libvorbis.dll`** (Xiph.Org codecs for vgmstream) | vgmstream `r2168` build | BSD-3-Clause | Dynamic DLLs loaded by `vgmstream-cli.exe` | https://xiph.org/downloads/ · `tools/vgmstream/THIRD_PARTY_LICENSES.txt` |

---

## 3. LGPL Source Offer & Replacement Notice

All LGPL-licensed components (`libmp3lame.dll`, `atracdenc.exe`, `FFmpeg` LGPL DLLs, `libmpg123-0.dll`, and unmodified NuGet `Ude.NetStandard.dll`) are distributed in unmodified form alongside their full license texts (`codecs/COPYING.LAME`, `tools/atracdenc/LICENSE`, `tools/vgmstream/COPYING.LGPLv2.1`) and direct links to their official upstream source archives listed above. Because Apollo Manager does not use single-file bundling, any user can directly drop in a replacement build of any LGPL DLL or CLI tool in `codecs/` or `tools/`.
