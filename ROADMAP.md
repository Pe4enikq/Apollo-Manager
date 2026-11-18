# Apollo Manager — Project Roadmap

This roadmap tracks execution from repository setup and offline knowledge gathering through the first MVP (**"Project E403"**, targeting Windows 10/11 + Sony `NW-E400/E500` MSC Walkman series) and subsequent multi-generation expansion phases.

---

## Milestone Overview

| Milestone | Title | Status | Focus |
| :--- | :--- | :--- | :--- |
| **M0** | Repository Curation & Architectural Spine | Active | Git setup, `Walkman.Contracts`, solution skeleton, dependency governance |
| **M0.5** | Local Knowledge Base & Reference Asset Dump | Next | Local archive of manuals, SonicStage detection artifacts, OSS reference code, and `.nwm` assets |
| **M1** | Hardware Discovery, Model ID & Transport Layer | Planned | `L1.DevicePoller` (`CsWin32`), SonicStage/manual detection tables, `L2.Transport.Msc`, `L2.Telemetry.Dumper` |
| **M2** | `OMGAUDIO` v1 Database Engine & OpenMG Crypto | Planned | Clean-room Big-Endian `Span<byte>` `.DAT` reader/writer + dual `ea3`/`EA3` & `DvId.dat` handling |
| **M3** | Bidirectional Audio Codec & Sync Pipeline | Planned | `L2.Codec.Pipeline` + `L1.FileManager` (MP3 pass-through, LAME, `atracdenc` ATRAC3, `vgmstream` decode) |
| **M4** | Visual `.nwm` Screensaver Studio & MVP AppShell | Planned | `L2.Custom.NwmEngine`, `L3.ScreensaverStudio` (`128×36` OLED editor/preview), `L3.AppShell` dual-pane UI |
| **Phase 2–4** | Multi-Generation Expansion & Native ATRAC3plus | Post-MVP | `OmgV2` (`NW-HD`/`NW-A`), color cover art & lyrics (`NW-S700`/`A800`), Legacy USB/CD (`D-NE`), native ATRAC3plus encoder |

---

## Detailed Milestones (To First MVP)

### M0: Repository Curation & Architectural Spine
- **Goal:** Establish a clean repository, strict L1/L2/L3 isolation rules, and compiling `.NET 8/9` solution skeleton before feature code is written.
- **Deliverables:**
  - Initialized Git repository inside `codebase/` with `.gitignore`, `.gitattributes`, `README.md`, `ROADMAP.md`, and `docs/ARCHITECTURE_AND_AUDIT.md`.
  - Solution skeleton (`WalkmanUtility.sln`) with `Walkman.Contracts` (DTOs, `DeviceProfile`, `ITransportDriver`, `IDatabaseEngine`, `ICodecService`, `IScreensaverPacker`).
  - `L1.Orchestrator` featuring `AssemblyLoadContext` dynamic plugin loading and MIT Mediator/Messenger (`CommunityToolkit.Mvvm` `StrongReferenceMessenger` or `martinothamar/Mediator`).
  - License isolation folders (`/tools/`, `/codecs/`, `/manifests/`).

### M0.5: Local Knowledge Base, Reference Code & Asset Dump
- **Goal:** Collect, download, and organize all technical references from the Master Reference Library onto local disk (**outside `codebase/`**, in the workspace's reference/assets folders) so coding agents and developers have verified offline specifications and fixtures.
- **Deliverables:**
  - **Author's `.nwm` Reference:** Clone `Pe4enikq/walkman-E400-500-custom-screensavers` and verify `.nwm` format test vectors.
  - **Original Manuals & Service Manuals:** Download Sony Operating Instructions & Level 2/3 Service Manuals (Sony Support, Internet Archive, Electrotanya, ManualsLib) for target generations (`NW-E403/405/407`, `NW-E50x`, `NW-HD1..5`, `NW-A60x`, `NW-A1000/3000`, `NW-S70x`, `NW-A80x`).
  - **SonicStage / Hardware Identification Artifacts:** Inspect original SonicStage installation tables, INF/INI/XML/DLL device mappings, and Linux USB IDs (`0x054C`) to extract exact hardware detection rules and capability flags.
  - **Clean-Room Spec References (Read-Only Reference Archive):**
    - Waider's `OMGAUDIO` documentation & `FFmpeg` `omadec.c` / `omaenc.c` byte-offset references.
    - `JSymphonic`, `NW-E00X MP3 File Manager`, and `linux-minidisc` strictly as offline `[SPEC-REFERENCE ONLY]` archives (never copied directly into C#).
    - `cybercase/netmd-js` (MIT) and `vgmstream` `oma.c` (ISC) references.
  - **Device Test Fixtures:** Real `OMGAUDIO` `.DAT` dumps (and sample `.oma` headers) placed into local test fixtures (excluded from Git).

### M1: Hardware Discovery, Model Identification & Transport Layer
- **Goal:** Reliably detect connected players, identify the exact Sony model, mount its storage safely, and capture diagnostics for unknown hardware.
- **Deliverables:**
  - `L1.DevicePoller` using `Microsoft.Windows.CsWin32` (`RegisterDeviceNotification` / `WM_DEVICECHANGE` + `SetupAPI`) + `DriveInfo.GetDrives()` (<5 ms hot-plug detection; no WMI).
  - **Device Identification Pipeline:**
    1. Primary identification using hardware tables/rules extracted from Sony manuals and original SonicStage device manifests.
    2. Fallback multi-factor fingerprinting (`0x054C` `VID:PID` + USB `iProduct` string + FAT partition size + `OMGAUDIO`/`SCREEN`/`DvId.dat` markers) against `manifests/devices.json`.
  - `L2.Transport.Msc`: Mass Storage VFS driver enforcing uppercase ASCII 8.3 paths (`OMGAUDIO`, `SCREEN`), safe temp staging, and `FileStream.Flush(flushToDisk: true)`.
  - `L2.Telemetry.Dumper` + `L3.DumpWizard`: Read-only diagnostic `.zip` collector (~50 KB: USB descriptors, directory listing, `.DAT` files, first 64 bytes of `.oma` headers — zero copyrighted audio).
- **Verification Gate:** Connecting an `NW-E403` (or mounting a byte-accurate virtual FAT image) identifies the exact model profile in <100 ms and mounts its VFS tree.

### M2: `OMGAUDIO` v1 Database Engine & OpenMG Crypto
- **Goal:** Full clean-room read, incremental update, and from-scratch rebuild compatibility for `OMGAUDIO` v1 (`NW-E400/E500`, `NW-E100..300`, `NW-HD1..5`).
- **Deliverables:**
  - `L2.Database.OmgV1`: Clean-room `Span<byte>` / `BinaryPrimitives` Big-Endian parser & serializer for:
    - `00GTRLST.DAT` (global table registry)
    - `01TREE01.DAT` – `01TREE04.DAT` (navigation trees)
    - `02TREINF.DAT` (tree node metadata)
    - `03GINF01.DAT` – `03GINF04.DAT` (group/album details)
    - `04CNTINF.DAT` (content/track index, codec, bitrate, duration, frame count, `UTF-16BE` / `Shift-JIS` tags)
    - `05CIDLST.DAT` (Content ID list)
  - `L2.Crypto.OpenMG`: Dual-header `.oma` builder/parser (`ea3` ID3v2-like block with mandatory `0x02` `UTF-16BE` encoding + 96-byte `EA3` descriptor supporting `0xFFFE` keyless mode and `DvId.dat` XOR/DES key masking).
  - Atomic batch commit and full **Database Rebuild from Scratch** mode.
- **Verification Gate:** Round-trip binary verification against real `OMGAUDIO` dumps and hardware playback test confirming zero `NO DATA` or `FORMAT ERROR` failures.

### M3: Bidirectional Audio Codec Pipeline & File Interchange
- **Goal:** 100% reliable audio conversion and bidirectional transfer (`PC <-> Player` and `Player <-> Player`) with metadata preservation.
- **Deliverables:**
  - **Input Decoding & Normalization:**
    - `NAudio` (`MediaFoundationReader`, MIT) on Windows + `dr_libs` / `miniaudio` / `NLayer` / `NVorbis` / `Concentus` for cross-platform/exotic decoding.
    - Strict `44.1 kHz / 16-bit Stereo` resampling via `WdlResamplingSampleProvider` / `NWaves`.
    - Metadata extraction/writing via `ATL.NET` (MIT).
  - **Player-Bound Pathways (`PC -> Player`):**
    1. **MP3 Pass-Through Wrapper:** Strip ID3v2/APIC frames, align MPEG frames, wrap into `ea3`/`EA3` `.OMA` (`0x03`) without re-encoding.
    2. **MP3 Transcode:** Resample to `44.1 kHz / 16-bit` $\rightarrow$ external `libmp3lame.dll` (LGPL-2.0 via `NAudio.Lame`) $\rightarrow$ `.OMA`.
    3. **ATRAC3 Transcode:** Resample $\rightarrow$ external unmodified `atracdenc.exe` (LGPL-2.1 in `/tools/atracdenc/`) at `66 / 105 / 132 kbps` $\rightarrow$ `.OMA` (`0x00`).
    4. **ATRAC3plus MVP Handling:** Since `atracdenc` only encodes ATRAC1/ATRAC3 and reverse-engineering a native ATRAC3plus encoder is a post-MVP milestone, MVP will either route through an optional temporary external Sony DLL/ACM patch bridge (if available locally) or clearly gate ATRAC3plus *encoding* while preserving full `.OMA` ATRAC3plus *decoding/ripping* and device-to-device raw `.OMA` passthrough.
  - **Reverse Extraction & Device-to-Device (`Player -> PC` & `Player A -> Player B`):**
    - Unwrap MP3 `.OMA` to standard `.mp3` with ID3 tags reconstructed from `04CNTINF.DAT` / `ea3`.
    - Decode ATRAC3 / ATRAC3plus `.OMA` to `.wav` / `.flac` via `vgmstream` (ISC).
    - Direct `.OMA` payload transfer/re-keying between Walkman devices.
  - `L1.FileManager` bounded parallel transcode queue (`System.Threading.Channels`) with single atomic `.DAT` commit at batch end.
- **Verification Gate:** Automated round-trip matrix testing FLAC, WAV, Hi-Res (96 kHz / 24-bit), MP3, AAC/M4A, OGG, and Opus transfers to/from the player VFS with 100% playability and intact tags.

### M4: Visual `.nwm` Screensaver Studio Prototype & MVP AppShell
- **Goal:** Ship an interactive desktop UI and visual animation studio prototype for Walkman OLED screens.
- **Deliverables:**
  - `L2.Custom.NwmEngine`: C# port of `Pe4enikq/walkman-E400-500-custom-screensavers` binary packer/unpacker (1-bit frames, frame rate header, loop flags).
  - `L3.ScreensaverStudio` (Prototype):
    - Native Avalonia `WriteableBitmap` / `SKCanvas` `128×36` (and `120×36`) 1-bit pixel grid editor with drawing tools, frame timeline, and onion-skinning.
    - Importer for GIF (`SkiaSharp` `SKCodec`), PNG sequences (`StbImageSharp`), and `.ase`/`.aseprite` (`AsepriteDotNet`) with 1-bit Atkinson, Floyd-Steinberg, Sierra, and Bayer dithering (`Cyotek.Drawing.Imaging.ColorReduction`).
    - Real-time simulated OLED screen preview (cyan/amber phosphor glow) + direct deployment to player `SCREEN/`.
  - `L3.AppShell`: Avalonia dual-pane (Local Library + Device VFS) workspace, transfer queue gauge, device status bar, and base 2007 Frutiger Aero theme (`SkiaSharp`).

---

## Post-MVP Roadmap

### Phase 2: Native ATRAC3plus Reverse-Engineering & `OmgV2` HDD/Flash Expansion
- Reverse-engineer and implement a clean-room / community-backed **native ATRAC3plus encoder** (`48–352 kbps`) so zero proprietary Sony DLLs are required.
- Implement `L2.Database.OmgV2` (`01TREE05` Artist Link, `03GINF22` Intelligent Shuffle / play history) for `NW-HD1..5`, `NW-A600`, and `NW-A1000/A1200/A3000`.
- Expand `L1.DevicePoller` and `manifests/devices.json` using M0.5 manuals/SonicStage tables and `L3.DumpWizard` community dumps.

### Phase 3: Color Screen Era & Content-Aware Studios (`NW-S700`, `NW-A800`, `NW-E010`)
- `L3.MetadataStudio`: Strict Baseline JPEG (`SOF0`, `4:2:0`, `200×200`, stripped EXIF/ICC via `PhotoSauce.MagicScaler`) cover art packer + batch tag editor + Cyrillic/Kanji transliteration (`AnyAscii`, `WanaKana.Net`).
- `L3.LyricsSubStudio` + `L2.ContentAware`: Waveform-assisted `.lrc` lyrics sync and subtitle timeline editor (`NWaves`, `Whisper.net`).
- Full Frutiger Aero **Chameleon Theme Engine** matching the physical Walkman casing color.

### Phase 4: Legacy Pre-MSC USB & Optical ATRAC CD (`NW-MS`, Early `NW-E`, `D-NE` Discman)
- `L2.Transport.NetUsb`: `WinUSBNet` / `CsWin32` `winusb.dll` bulk & control transfers (`0x054C`) + MagicGate/OpenMG session framing ported from `cybercase/netmd-js` (MIT) and Wireshark/USBPcap captures.
- `L2.Transport.Optical` + `L3.Studio.DiscBurner`: `LTRData/DiscUtils` (MIT) ISO9660 ATRAC CD builder (`00GTRLST.MSF` / `.oma` disc trees) for `D-NE1`–`D-NE920`.
