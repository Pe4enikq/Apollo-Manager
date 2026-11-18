# Apollo Manager — Project Roadmap

This roadmap tracks execution from repository setup and offline knowledge gathering through the first MVP (**"Project E403"**, targeting Windows 10/11 + Sony `NW-E400/E500` MSC Walkman series) and subsequent multi-generation expansion phases.

---

## Milestone Overview

| Milestone | Title | Status | Focus |
| :--- | :--- | :--- | :--- |
| **M0** | Repository Curation & Architectural Spine | **Complete** | Git setup, zero-dep `Walkman.Contracts`, custom L1 `Orchestrator`, dependency & ALC tests |
| **M0.5** | Local Knowledge Base & Reference Asset Dump | **Complete** | Local archive of 50 Sony manuals, SonicStage installers, OSS refs, and clean-room `SPEC_*.md` |
| **M1** | Hardware Discovery, Model ID & Transport Layer | **Complete** | `L1.DevicePoller` (`CsWin32` + 3-tier `DvID`), `L2.Transport.Msc`, `L1.FileManager` VFS mount, `L2.Telemetry.Dumper` |
| **M2** | `OMGAUDIO` v1 Database Engine & OpenMG Crypto | **Active (Next)** | Clean-room Big-Endian `Span<byte>` `.DAT` reader/writer + synchsafe `ea3`/`EA3` & `0xFFFE` `DvID` XOR |
| **M3** | Bidirectional Audio Codec & Sync Pipeline | Planned | `L2.Codec.Pipeline` + `L1.FileManager` (MP3 pass-through, LAME, `atracdenc` ATRAC3, `vgmstream` decode) |
| **M4** | Visual `.nwm` Screensaver Studio & MVP AppShell | Planned | `L2.Custom.NwmEngine` (`0xEC01` `48×120`), `L3.ScreensaverStudio` (`120×36` OLED preview), `L3.AppShell` UI |
| **Phase 2–4** | Multi-Generation Expansion & Native ATRAC3plus | Post-MVP | `OmgV2` (`NW-HD`/`NW-A`), color cover art & lyrics (`NW-S700`/`A800`), Legacy USB/CD (`D-NE`), native ATRAC3plus encoder |

---

## Detailed Milestones (To First MVP)

### M0: Repository Curation & Architectural Spine *(Complete)*
- **Goal:** Establish a clean repository, strict L1/L2/L3 isolation rules, and compiling `.NET 8/9` solution skeleton before feature code is written.
- **Deliverables:**
  - Initialized Git repository inside `codebase/` with `.gitignore`, `.gitattributes`, `Directory.Build.props`, `README.md`, `ROADMAP.md`, and `docs/ARCHITECTURE_AND_AUDIT.md`.
  - Solution skeleton (`WalkmanUtility.sln`, 14 projects) with zero-dependency `Walkman.Contracts` (`DeviceProfile`, `ITransportDriver`, `IDatabaseEngine`, `IOpenMgCryptoService`, `ICodecService`, `IScreensaverPacker`, `ITelemetryDumper`).
  - Zero-NuGet `Walkman.L1.Orchestrator` featuring collectible `AssemblyLoadContext` dynamic plugin loading (`PluginLoadContext`), custom lock-snapshot `WalkmanEventBus` & `WalkmanMediator`, and multi-factor `DeviceManifestRegistry`.
  - License isolation folders (`/tools/`, `/codecs/`, `/manifests/`).

### M0.5: Local Knowledge Base, Reference Code & Asset Dump *(Complete)*
- **Goal:** Collect, download, and organize all technical references from the Master Reference Library onto local disk (**outside `codebase/`**, in the workspace's reference/assets folders) so coding agents and developers have verified offline specifications and fixtures.
- **Deliverables:**
  - **Author's `.nwm` Reference:** Cloned `Pe4enikq/walkman-E400-500-custom-screensavers` and documented `.nwm` `0xEC01` (`48×120` 3-block mangled) test vectors in `for agents/SPEC_NWM_SCREENSAVER.md`.
  - **Original Manuals & Service Manuals:** Downloaded 48 Sony user manuals plus official Service Manuals (`NW-E403-SW.pdf`, `NW-A3000-SW.pdf`) and OpenMG/SonicStage installers (`extra apps/installers/`).
  - **Clean-Room Spec References & Tools:** Archived GPL references strictly in `misc/spec-only-gpl-refs/` `[SPEC-REFERENCE ONLY]`, MIT/ISC refs (`netmd-js`, `vgmstream`), `usb.ids` (`0x054C`), `atracdenc 0.2.3` / `vgmstream r2168`, and authored 4 clean-room specs in `for agents/`.

### M1: Hardware Discovery, Model Identification & Transport Layer *(Complete)*
- **Goal:** Reliably detect connected players, identify the exact Sony model, mount its storage safely, and capture diagnostics for unknown hardware.
- **Deliverables:**
  - `L1.DevicePoller` using `Microsoft.Windows.CsWin32` (`0.3.106` MIT: `RegisterDeviceNotification` / `WM_DEVICECHANGE` + `SetupAPI` `VID_054C` + `GetVolumeInformationW`), plus 3-tier read-only hardware `DvID` extraction (`DvID.DAT` -> zero-elevation `0xFFFE` MP3 `.OMA` Xing/LAME known-plaintext recovery -> elevated `IOCTL_SCSI_PASS_THROUGH` `0xA4`).
  - **Verified Live Hardware Findings (`NW-E403`):** Resolved `NW-E403` (`0x054C:0x01FB`) with `DvID` key `0x09278120` (`72.9 ms` scan), confirmed real players store `NWWM/`, `NWWM-SCR/fading_wind.nwm`, empty playlist slot `01TREE22.DAT`/`03GINF22.DAT` tables (refined `DeviceManifestRegistry` so only `01TREE05`/`03GINF05` exclude `OmgV1`), and a dynamic `0x400`-byte `ea3` block.
  - `L2.Transport.Msc`: Mass Storage VFS driver enforcing volume serial binding, anti-traversal guards, uppercase ASCII 8.3 paths on writes (`OMGAUDIO`, `SCREEN`), atomic temp staging, and `FileStream.Flush(flushToDisk: true)`.
  - `L2.Telemetry.Dumper` + `L3.DumpWizard`: Sanitized ~10.3 KB diagnostic `.zip` export (verified `NW-E403_Diagnostic_Dump.zip` at `10,539 B`, zero full audio payloads).
- **PM Handoff / M2 Polish Items:** Warm JIT before timing assertion in `DevicePoller_IdentifiesBackedUpNwE403Volume_Under100ms_AndMountsVfs` (cold first-run took `248 ms`; subsequent run passed in `141 ms`), and correlate USB PnP device paths per volume in `ScanConnectedDevices` when multiple Sony USB devices are attached.

### M2: `OMGAUDIO` v1 Database Engine & OpenMG Crypto *(Active — Next)*
- **Goal:** Full clean-room read, incremental update, and from-scratch rebuild compatibility for `OMGAUDIO` v1 (`NW-E400/E500`, `NW-E100..300`, `NW-HD1..5`).
- **Deliverables:**
  - `L2.Database.OmgV1`: Clean-room `Span<byte>` / `BinaryPrimitives` Big-Endian parser & serializer for:
    - `00GTRLST.DAT` (global table registry)
    - `01TREE01.DAT` – `01TREE04.DAT` & `01TREE22.DAT` (navigation & playlist slot `0x22` trees)
    - `02TREINF.DAT` (tree node metadata)
    - `03GINF01.DAT` – `03GINF04.DAT` & `03GINF22.DAT` (group/album/playlist slot `0x22` details)
    - `04CNTINF.DAT` (content/track index, codec, bitrate, duration, frame count, `UTF-16BE` / `Shift-JIS` tags)
    - `05CIDLST.DAT` (Content ID list)
  - `L2.Crypto.OpenMG`: Dual-header `.oma` builder/parser honoring dynamic synchsafe `ea3` sizes (`0x400` observed on `NW-E403` / `0xC00` typical) with mandatory `0x02` `UTF-16BE` frames + 96-byte `EA3` descriptor supporting Gen 3 `0xFFFE` 4-byte `DvID` XOR scrambling (`((0x2465 + trackId * 0x5296E435) & 0xFFFFFFFF) ^ deviceKey`), `0x0001` MagicGate LSI DRM (`30GRCT/`), and Gen 4+ `0xFFFF` keyless mode.
  - Atomic batch commit and full **Database Rebuild from Scratch** verified against `test folder/NW-E403_Live_Backup/` before touching physical hardware.
- **Verification Gate:** Byte-exact round-trip binary verification against `NW-E403_Live_Backup` and hardware playback test confirming zero `NO DATA` or `FORMAT ERROR` failures.

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
    - Native Avalonia `WriteableBitmap` / `SKCanvas` logical `120×36` OLED (packed `48×120` 3-block column-stripe `.nwm`, `0xEC01`) 1-bit pixel grid editor with drawing tools, frame timeline, and onion-skinning.
    - Importer for GIF (`SkiaSharp` `SKCodec`), PNG sequences (`StbImageSharp`), and `.ase`/`.aseprite` (`AsepriteDotNet`) with 1-bit Atkinson, Floyd-Steinberg, Sierra, and Bayer dithering (`Cyotek.Drawing.Imaging.ColorReduction`).
    - Real-time simulated OLED screen preview (cyan/amber phosphor glow) + direct deployment to player `SCREEN/` & `NWWM-SCR/`.
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
