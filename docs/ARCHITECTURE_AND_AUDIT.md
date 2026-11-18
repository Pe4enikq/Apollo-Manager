# Architecture Reconciliation & Dependency Governance

This document reconciles the **Global Software Architecture Specification (L1–L3)** with the audited **Master Reference Library & Technical Resources**, establishing binding technical and licensing rules for all Apollo Manager contributors and AI coding agents.

> **Precedence Rule:** Whenever the early draft tech stack table in the L1–L3 Specification conflicts with the **Master Reference Library's Audited Matrix**, the **Audited Matrix wins**.

---

## 1. Approved Permissive Tech Stack (Superseding Draft Stack)

| Layer / Responsibility | Superseded Draft Reference | Approved Production Stack (License) | Notes & Enforcement |
| :--- | :--- | :--- | :--- |
| **Runtime & UI** | C# 12 / `.NET 8/9`, Avalonia UI + SkiaSharp | **C# 12 / `.NET 8/9`, Avalonia UI + SkiaSharp** (MIT) | Use `Span<byte>`, `Memory<byte>`, `BinaryPrimitives`, and `AssemblyLoadContext`. |
| **L1 Mediator / Event Bus** | Generic `MediatR` | **Zero-NuGet custom `WalkmanEventBus` & `WalkmanMediator`** (in `L1.Orchestrator`), plus **`CommunityToolkit.Mvvm`** (`ObservableObject` / `RelayCommand`, MIT) for `L3` ViewModels | **Forbidden:** `MediatR` v13+ (commercial license). Keeping `Walkman.Contracts` and `L1.Orchestrator` zero-NuGet is enforced by automated architecture tests. |
| **USB Hot-Plug Detection** | Windows WMI (`System.Management`) | **`Microsoft.Windows.CsWin32` (MIT)** (`RegisterDeviceNotification` / `WM_DEVICECHANGE` + `SetupAPI`) + `DriveInfo.GetDrives()` | **Forbidden:** WMI polling in runtime code (500–2000 ms latency & STA UI thread deadlocks). |
| **Audio Metadata & Tags** | `mono/taglib-sharp` (LGPL-2.1) | **`ATL.NET` (`Zeugma440/atldotnet`, MIT)** + custom clean-room `Span<byte>` `ea3` header builder | **Forbidden:** Inline/modified `TagLibSharp`. |
| **Audio Input Decoding** | Bundled `ffmpeg.exe` + `FFMpegCore` | **Windows:** `NAudio` `MediaFoundationReader` (MIT)<br>**Cross-Platform:** `dr_libs` / `miniaudio` (Public Domain / MIT-0), `libFLAC` (BSD-3), `macosforge/alac` (Apache-2.0), `NLayer` (MIT), `NVorbis` (MIT), `Concentus` (BSD-3)<br>**`.OMA` Decode:** `vgmstream` (ISC) | **Forbidden:** Bundling GPLv3 `ffmpeg.exe`. Optional user-PATH `ffmpeg` fallback only via `FFMpegCore` (MIT). `omaenc.c` / `omadec.c` are `[SPEC-REFERENCE ONLY]`. |
| **Audio Resampling & DSP** | `FFmpeg` `libswresample` | `NAudio` `WdlResamplingSampleProvider` (MIT / Cockos WDL BSD-style), `NWaves` (MIT), `Concentus` `SpeexResampler` (BSD-3) | Strict target: **44,100 Hz / 16-bit Stereo PCM**. |
| **MP3 Encoding** | `libmp3lame.dll` | `NAudio.Lame` (MIT wrapper) + **external unmodified `libmp3lame.dll` (LGPL-2.0)** in `/codecs/` | Never statically link LGPL code into `.NET` assemblies. |
| **ATRAC3 / ATRAC3plus Encoding** | `atracdenc` | **ATRAC3 (MVP):** External unmodified `atracdenc.exe` (LGPL-2.1) in `/tools/atracdenc/` with `COPYING.LGPLv2.1` via stdin/stdout WAV pipe.<br>**ATRAC3plus:** Reverse-engineered native encoder planned post-MVP (optional temporary bridge or deferred in MVP). | `atracdenc` encodes ATRAC1 and ATRAC3 (66/105/132 kbps) only; it does **not** encode ATRAC3plus. |
| **Image & Cover Art Processing** | `SixLabors.ImageSharp` | **`SkiaSharp` (MIT)**, **`PhotoSauce.MagicScaler` (MIT)**, **`StbImageSharp` / `StbImageResizeSharp` (Public Domain)** | **Forbidden:** `ImageSharp` v3.0+ (commercial split license). Color Walkmans require strict Baseline JPEG (`SOF0`), `4:2:0` chroma subsampling, `200×200 px`, EXIF/ICC stripped. |
| **`.nwm` Pixel Editor & Dithering** | `PixiEditor` (LGPL-3.0 / GPL-3.0) | Clean-room Avalonia `WriteableBitmap` / `SKCanvas` + **`AsepriteDotNet` (MIT)** + **`Cyotek.Drawing.Imaging.ColorReduction` (MIT)** | `PixiEditor` is `[SPEC & UX REFERENCE ONLY]`. `.nwm` packer ported from `Pe4enikq/walkman-E400-500-custom-screensavers` (`[DIRECT-USE]`). |
| **Database Parsers (`OMGAUDIO`)** | `JSymphonic`, `NW-E00X MP3 File Manager`, `linux-minidisc` | **Clean-room C# `Span<byte>` / `BinaryPrimitives` parsers** (+ optional `KaitaiStruct.Runtime.CSharp`, MIT) | GPLv2/GPLv3 fan managers are `[SPEC-REFERENCE ONLY]` (verify byte offsets & `DvId.dat` math; never copy Java/C source). |
| **Legacy USB (Phase 4)** | `LibUsbDotNet` (LGPLv3/GPLv3), `webminidisc` (GPL-3.0) | **`WinUSBNet` (MIT)** or `winusb.dll` P/Invoke via `CsWin32` (MIT) + **`cybercase/netmd-js` (MIT)** protocol port | `webminidisc` UI is `[SPEC-REFERENCE ONLY]`; `netmd-js` is `[DIRECT-USE: MIT]`. |
| **Optical ISO9660 (Phase 4)** | Abandoned `DiscUtils/DiscUtils` | **`LTRData/DiscUtils` (MIT)** | Actively maintained `.NET 8/9` fork. |
| **Lyrics & Transliteration (Phase 3)** | — | `Whisper.net` (MIT), `AnyAscii` (ISC/MIT), `WanaKana.Net` (MIT) | Handles Cyrillic/Kanji transliteration for non-Unicode player ROMs. |

---

## 2. Three-Tier Modularity & Anti-Spaghetti Rules

1. **Dependency Direction (`Walkman.Contracts`):**
   - `L2.Engines` and `L3.Presentation` assemblies **must only reference `Walkman.Contracts`** (never each other's implementation assemblies).
2. **Mediator-Routed Commands (`L1.Orchestrator`):**
   - L3 modules dispatch commands/queries through the L1 Mediator/Messenger. L1 resolves the matching lazy-loaded L2 service and coordinates VFS writes via `L1.FileManager`.
3. **Bounded Streaming Pipelines:**
   - Audio transcode jobs run asynchronously via bounded `System.Threading.Channels` queues into a temporary staging buffer.
   - `L2.Database.OmgV1` commits `.DAT` index updates in a **single atomic batch** at transfer completion to prevent `NO DATA` corruption on mid-transfer disconnect.

---

## 3. Critical Binary & Hardware Invariants

1. **Dual `.OMA` Container Header (`ea3` + `EA3`) & `DvID` Key Acquisition:**
   - **Block 1 (`ea3`):** ID3v2.3-style tag header starting with ASCII `0x65 0x61 0x33` (`ea3`). Must honor the dynamic synchsafe size in bytes `6..9` (`10 + ea3BodySize`, e.g., `0x400` bytes observed on physical `NW-E403` vs. `0xC00` typical). Text frames (`TIT2`, `TPE1`, `TALB`) must use **UTF-16BE** (`0x02` encoding byte).
   - **Block 2 (`EA3`):** 96-byte audio/crypto descriptor starting with ASCII `0x45 0x41 0x33` (`EA3`), encoding Codec ID (`0x00` ATRAC3, `0x01` ATRAC3plus, `0x03` MP3) and protection mode: **`0xFFFE` 4-byte `DvID` XOR scrambling** (`((0x2465 + trackId * 0x5296E435) & 0xFFFFFFFF) ^ deviceKey`) for Gen 3 (`NW-E400/E500/E100/HD3/HD5`), **`0x0001` MagicGate LSI DRM** (`30GRCT/`, `05CIDLST.DAT`) for Gen 3 ATRAC3/3plus, and **`0xFFFF` keyless mode** for Gen 4+ (`NW-A600/A1000/A3000/E00x/S700/A800`).
   - **3-Tier Read-Only `DvID` Resolution:** (1) `MP3FM/DvID.DAT` / `OMGAUDIO/DvID.DAT` file, (2) zero-elevation known-plaintext recovery from existing `0xFFFE` MP3 `.OMA` Xing/LAME headers, (3) optional elevated `IOCTL_SCSI_PASS_THROUGH` (`0xA4`).
2. **FAT16/FAT32 & Disk Flush Discipline:**
   - All `OMGAUDIO` paths must be written in strict uppercase 8.3 ASCII (`OMGAUDIO/10F00/10000001.OMA`), while read enumeration permits authentic non-8.3 folders such as `NWWM-SCR/fading_wind.nwm`.
   - All file commits must call `FileStream.Flush(flushToDisk: true)` before reporting completion, validated against the mounted volume serial number (`GetVolumeInformationW`).
3. **Device Detection & `OmgV1`/`OmgV2` Disambiguation:**
   - Multi-factor match in `manifests/devices.json` combining `0x054C` `VID:PID`, USB product descriptor, FAT partition capacity, `DvID[6..7]` model segment, and filesystem markers (`OMGAUDIO`, `NWWM`, `NWWM-SCR`, `SCREEN`).
   - Because authentic Gen 3 `NW-E403` units carry empty playlist slot `0x22` tables (`01TREE22.DAT`, `03GINF22.DAT`), `OmgV2` exclusion uses `01TREE05.DAT` / `03GINF05.DAT` (Artist Link).
4. **MP3 Tag Hygiene:**
   - Native MP3 streams wrapped into `.OMA` must have pre-existing ID3v2/APIC artwork chunks stripped and MPEG frames aligned so vintage Sony hardware decoders do not freeze.
