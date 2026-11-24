# Apollo Manager

> **Beta (`v0.1.0-beta.1`)** — A modern, standalone desktop manager for classic Sony Network Walkman players (Windows 10/11), built from scratch to replace Sony SonicStage / X-app, OpenMG Jukebox, and MP3 File Manager.

---

## What Apollo Manager Does

- **Direct Music Transfers (`PC <-> Walkman`):** Transfer MP3 and ATRAC3plus (`48–352 kbps`, `44.1 kHz` Stereo) tracks directly to your player's `OMGAUDIO` database with full tag preservation, drag-and-drop staging and custom collection/folder ordering.
- **Built-In Standalone ATRAC3plus Encoder:** Encodes `48–352 kbps` ATRAC3plus natively in C#. On Gen 3 MagicGate players, Apollo safely recycles existing signed `0x0001` slots on your player while preserving `MACLIST0.DAT` integrity.
- **Hardware Safety First:** Automatic pre-write `OMGAUDIO` table backups, FAT16 Boot Parameter Block (BPB) protection against Windows-format `FORMAT ERROR` bricks, disk flushes, and strict volume-serial verification.
- **My Library & Audio Player:** Browse your local music library (`.mp3`, `.flac`, `.wav`, `.ogg`, `.opus`, `.oma`) with instant playback, `.LRC` synchronized lyrics, and a built-in **Tags & Covers** editor.
- **Batch Codec Converter:** Convert local tracks between ATRAC3plus, MP3, WAV, and FLAC.
- **`.NWM` Screensaver Utilities:** Import, validate, and export `.nwm` Walkman OLED animations (`NW-E400/E500`, `NW-A600`, `NW-A1000/A3000`).
- **Community Dump Wizard:** Export a scoped diagnostic `.zip` (USB IDs, FAT BPB info, Walkman system folder listing, and `OMGAUDIO` table headers — no tracks and zero unrelated personal USB files) to help add support for new Walkman models.

---

## Supported Walkman Models

| Series | Models | Support Tier | Notes |
| :--- | :--- | :---: | :--- |
| **NW-E400** | `NW-E403`, `NW-E405`, `NW-E407` | **Verified** | Tested on live hardware (MP3, ATRAC3+ via slot recycling, `.NWM` upload, delete/reorder). |
| **NW-E500** | `NW-E503`, `NW-E505`, `NW-E507` | **Verified** | Tested on live hardware (MP3, ATRAC3+ via slot recycling, delete/reorder). |
| **NW-HD3 / HD5** | `NW-HD3`, `NW-HD5` | **Experimental** | Pre-seeded `OMGAUDIO` v1 profile — not yet hardware-verified. Back up your drive before writing. |
| **NW-A600** | `NW-A605`, `NW-A607`, `NW-A608` | **Experimental** | Pre-seeded profile — not yet hardware-verified. Back up before writing. |
| **NW-A1000–A3000** | `NW-A1000`, `NW-A1200`, `NW-A3000` | **Experimental** | Pre-seeded profile — not yet hardware-verified. Back up before writing. |
| **NW-E000** | `NW-E002`, `NW-E003`, `NW-E005` | **Experimental** | Pre-seeded profile — not yet hardware-verified. Back up before writing. |

> **Have an untested or unrecognized Sony Walkman?** Connect it, open **Device & System -> Dump Wizard**, and submit the generated diagnostic `.zip` via our [Walkman Telemetry Submission Form](https://tally.so/r/ArBeRN) (or attach it to a GitHub Issue).

---

## Download & Quick Start (Windows 10 / 11 x64)

Both release packages are **self-contained** — you do **not** need to install `.NET` or SonicStage.

1. Download either package from **GitHub Releases**:
   - **Portable:** `ApolloManager-0.1.0-beta.1-win-x64-portable.zip` — extract anywhere and run **`Apollo Manager.exe`**.
   - **Installer:** `ApolloManager-0.1.0-beta.1-win-x64-setup.exe` — installs with Start Menu / Desktop shortcuts and a standard Windows Uninstaller.
   - *(Optional)* Verify your download against **`SHA256SUMS.txt`**.
2. **Windows SmartScreen Notice:** Because Apollo Manager is a free, unsigned open-source beta, Windows Defender SmartScreen may display *"Windows protected your PC"* on first run. Click **More info -> Run anyway** after verifying the SHA-256 checksum.
3. **Back Up Your Player First:** Before making your first transfer on any vintage Walkman, copy its entire USB drive to a safe backup folder on your PC.
4. **Connect & Transfer:** Plug in your Walkman over USB, select it in **File Manager**, drag tracks or folders from your PC library, and click **Transfer** (or **Update** after reordering collections/tags).
   - *Note on `NW-E400`/`NW-E500` ATRAC3plus (`0x0001`):* MP3 transfers work on any initialized player. Native ATRAC3plus transfers recycle signed "slots" already present on the player (if your player was freshly formatted from its menu with zero slots, transfer placeholder ATRAC3+ tracks once in SonicStage to allocate new slots, or use MP3 mode).

> **Beta Notice:** Apollo Manager `v0.1.0-beta.1` is an early beta. Features marked with a **`WIP`** badge (such as the interactive OLED pixel editor, Firmware tools, Artist Link, and AAL encoding) are planned for later releases.

---

## Building From Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows x64:

```powershell
dotnet test "WalkmanUtility.sln" -c Release
.\build\package-win-x64.ps1
```

> *External codec binaries (`codecs/libmp3lame.dll`, `tools/atracdenc/atracdenc.exe`, and `tools/vgmstream/vgmstream-cli.exe`) are excluded from Git and distributed in the official Release archives — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for exact upstream versions and download links.*

Build outputs are staged in `dist/`. For engineering and dependency governance details, see [docs/ARCHITECTURE_AND_AUDIT.md](docs/ARCHITECTURE_AND_AUDIT.md).

---

## Acknowledgments & Prior Art

Apollo Manager stands on the shoulders of decades of open-source Walkman, ATRAC, and MiniDisc preservation work:

- **[minidisc.wiki](https://www.minidisc.wiki/)** — Comprehensive hardware, format, and Sony Walkman/MiniDisc community documentation.
- **[FFmpeg](https://ffmpeg.org/)** — Publicly documented ATRAC3/ATRAC3plus and OpenMG `.oma` container/format specifications (`omadec` / `atrac3plus`).
- **[vgmstream](https://github.com/vgmstream/vgmstream)** — Universal game & proprietary audio decoding (`.oma` playback/export support).
- **[atracdenc](https://github.com/dcherednik/atracdenc)** — Open-source ATRAC1/ATRAC3 encoder and psychoacoustic reference work.
- **[JSymphonic](https://sourceforge.net/projects/symphonic/)**, **[linux-minidisc](https://github.com/glaubitz/linux-minidisc)**, and **[netmd-js / Web MiniDisc Pro](https://github.com/cybercase/netmd-js)** — Pioneering open-source `OMGAUDIO` and Sony portable audio interoperability projects.
- **[nwm-manager](https://github.com/asivery/nwm-manager)** — Documentation of Sony Walkman OLED animation containers.

---

## License & Third-Party Notices

- Apollo Manager's source code is licensed under the **MIT License** — see [LICENSE](LICENSE).
- Third-party libraries and external codec tools (`LAME`, `atracdenc`, `vgmstream`, `FFmpeg`, `mpg123`, `Xiph.Org` codecs, and managed NuGet packages) are documented with full upstream source links and license texts in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [codecs/LICENSE_BOUNDARY.md](codecs/LICENSE_BOUNDARY.md), and [tools/LICENSE_BOUNDARY.md](tools/LICENSE_BOUNDARY.md).
