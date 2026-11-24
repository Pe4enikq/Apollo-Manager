# Architecture Reconciliation & Dependency Governance

This document establishes the binding technical, architectural, and licensing rules for Apollo Manager contributors.

---

## 1. Approved Permissive Tech Stack

| Layer / Responsibility | Superseded Draft Reference | Approved Production Stack (License) | Notes & Enforcement |
| :--- | :--- | :--- | :--- |
| **Runtime & UI** | C# 12 / `.NET 8/9`, Avalonia UI + SkiaSharp | **C# 12 / `.NET 8.0`, `Avalonia 11.3.23` + `SkiaSharp`** (MIT) | Use `Span<byte>`, `Memory<byte>`, `BinaryPrimitives`, and `AssemblyLoadContext`. `Apollo Manager.exe` (`Walkman.L3.AppShell`) packages as `<OutputType>WinExe</OutputType>` with parent-console `AttachConsole`, persisted JSON settings (`%LocalAppData%\ApolloManager\settings.json`), and isolated `DemoModeOverlay`. |
| **L1 Mediator / Event Bus** | Generic `MediatR` | **Zero-NuGet custom `WalkmanEventBus` & `WalkmanMediator`** (in `L1.Orchestrator`), plus **`CommunityToolkit.Mvvm` (`8.4.0`)** (`ObservableObject` / `RelayCommand`, MIT) for `L3` ViewModels | **Forbidden:** `MediatR` v13+ (commercial license). Keeping `Walkman.Contracts` and `L1.Orchestrator` zero-NuGet is enforced by automated architecture tests. |
| **USB Hot-Plug Detection** | Windows WMI (`System.Management`) | **`Microsoft.Windows.CsWin32` (MIT)** (`RegisterDeviceNotification` / `WM_DEVICECHANGE` + `SetupAPI`) + `DriveInfo.GetDrives()` | **Forbidden:** WMI polling in runtime code (500–2000 ms latency & STA UI thread deadlocks). |
| **Audio Metadata & Tags** | `mono/taglib-sharp` (LGPL-2.1) | **`ATL.NET` (`Zeugma440/atldotnet`, MIT)** + from-scratch `Span<byte>` `ea3` header builder | **Forbidden:** Inline/modified `TagLibSharp`. Transitive `Ude.NetStandard` (`1.2.0`) is used unmodified under `MPL-1.1 / LGPL-2.1` (see `THIRD_PARTY_NOTICES.md`). |
| **Audio Input Decoding** | Bundled `ffmpeg.exe` + `FFMpegCore` | **Windows:** `NAudio` `MediaFoundationReader` (MIT) + from-scratch managed RFC 9639 `FlacWaveStream` fallback<br>**Cross-Platform:** `NLayer` (MIT), `NVorbis` (MIT), `Concentus` (BSD-3), `Concentus.Oggfile` (MS-PL / BSD-3)<br>**`.OMA` Decode:** External `vgmstream-cli.exe` (ISC + bundled LGPL-2.1 FFmpeg/mpg123 & BSD-3 Xiph DLLs) | **Forbidden:** Bundling GPLv3 `ffmpeg.exe` or statically linking LGPL decoders into `.NET` assemblies. `FlacWaveStream` handles large `PADDING` FLAC files rejected by Windows MF (`0xC00D36C4`) without native dependencies. |
| **Audio Resampling & DSP** | `FFmpeg` `libswresample` | `NAudio` `WdlResamplingSampleProvider` (MIT / Cockos WDL BSD-style), `NWaves` (MIT), `Concentus` `SpeexResampler` (BSD-3) | Strict target: **44,100 Hz / 16-bit Stereo PCM**. |
| **MP3 Encoding** | `libmp3lame.dll` | **Collectible-ALC-safe `LameNativeEncoder`** (`NativeLibrary.Load`/`Free` + `delegate* unmanaged[Cdecl]`) + **external unmodified `libmp3lame.dll` (LAME 3.100, LGPL-2.0+)** in `/codecs/` | **Forbidden:** `NAudio.Lame` (its `AppDomain.CurrentDomain.AssemblyResolve` hook permanently pins collectible `PluginLoadContext` instances). Never statically link LGPL code into `.NET` assemblies. |
| **ATRAC3 / ATRAC3plus Encoding** | `atracdenc` | **ATRAC3:** External unmodified `atracdenc.exe` (`v0.2.3`, LGPL-2.1) in `/tools/atracdenc/` via stdin/stdout pipe.<br>**ATRAC3plus:** **Standalone pure managed C# encoder** in `Walkman.L2.Codec.Pipeline` (`48–352 kbps`, `44.1 kHz` Stereo, zero external DLLs/executables, zero GPL/LGPL code). | `atracdenc` encodes ATRAC1 and ATRAC3 (`66/105/132 kbps`) only; `CodecPipelineService` wraps emitted frames with `ea3`/`EA3` headers and UTF-16BE tags. |
| **Image & Cover Art Processing** | `SixLabors.ImageSharp` | **`SkiaSharp` (MIT)**, **`PhotoSauce.MagicScaler` (MIT)**, **`StbImageSharp` / `StbImageResizeSharp` (Public Domain)** | **Forbidden:** `ImageSharp` v3.0+ (commercial split license). Color Walkmans require strict Baseline JPEG (`SOF0`), `4:2:0` chroma subsampling, `200×200 px`, EXIF/ICC stripped. |
| **`.nwm` Pixel Editor & Dithering** | `PixiEditor` (LGPL-3.0 / GPL-3.0) | Pure C# **`L2.Custom.NwmEngine` (`NwmScreensaverPacker.cs`)** + standalone Avalonia `WriteableBitmap` / `SKCanvas` (+ planned **`AsepriteDotNet` (MIT)** + **`Cyotek.Drawing.Imaging.ColorReduction` (MIT)**) | `PixiEditor` and `nwm-manager` GPL/LGPL code is never copied. `NwmScreensaverPacker` supports `0xEC01`, `0xEC02`, and `0xD301` natively in C#. |
| **Database Parsers (`OMGAUDIO`)** | `JSymphonic`, `NW-E00X MP3 File Manager`, `linux-minidisc` | **From-scratch C# `Span<byte>` / `BinaryPrimitives` parsers** | GPLv2/GPLv3 fan managers are referenced only as format documentation; zero Java/C source is copied. |
| **Legacy USB (Phase 4)** | `LibUsbDotNet` (LGPLv3/GPLv3), `webminidisc` (GPL-3.0) | **`WinUSBNet` (MIT)** or `winusb.dll` P/Invoke via `CsWin32` (MIT) + **`cybercase/netmd-js` (MIT)** protocol port | Never copy GPL UI code. |
| **Optical ISO9660 (Phase 4)** | Abandoned `DiscUtils/DiscUtils` | **`LTRData/DiscUtils` (MIT)** | Actively maintained `.NET 8/9` fork. |
| **Lyrics & Transliteration (Phase 3)** | — | `Whisper.net` (MIT), `AnyAscii` (ISC/MIT), `WanaKana.Net` (MIT) | Handles Cyrillic/Kanji transliteration for non-Unicode player ROMs. |

---

## 2. Three-Tier Modularity & Anti-Spaghetti Rules

1. **Dependency Direction (`Walkman.Contracts`):**
   - `L2.Engines` and dynamic `L3` studios (`DumpWizard`, `ScreensaverStudio`) **must only reference `Walkman.Contracts`** (never each other's implementation assemblies), enforced by `ArchitectureDependencyTests`.
   - All 6 `L2.Engines` and 2 dynamic `L3` plugin `.csproj` files must set `<EnableDynamicLoading>true</EnableDynamicLoading>` so `.deps.json` dependency resolution works cleanly inside collectible `PluginLoadContext` instances.
   - When an L2 engine requires another contract service (e.g., `L2.Database.OmgV1` resolving `ITransportDriver`), it uses `IPluginRegistrationContext.TryResolveEngine<TService>()` via `ModuleOrchestrator` rather than direct project references.
2. **Mediator-Routed Commands (`L1.Orchestrator`):**
   - L3 modules dispatch commands/queries through the L1 Mediator/Messenger. L1 resolves the matching lazy-loaded L2 service and coordinates VFS writes via `L1.FileManager`.
3. **Bounded Streaming Pipelines:**
   - Audio transcode jobs run asynchronously via bounded `System.Threading.Channels` queues into a temporary staging buffer.
   - `L2.Database.OmgV1` commits `.DAT` index updates in a **single atomic batch** at transfer completion to prevent `NO DATA` corruption on mid-transfer disconnect.

---

## 3. Critical Binary & Hardware Invariants

1. **Dual `.OMA` Container Header (`ea3` + `EA3`), OpenMG `0x0001` Crypto & `DvID` Acquisition:**
   - **Block 1 (`ea3`):** ID3v2.3-style tag header starting with ASCII `0x65 0x61 0x33` (`ea3`). Must honor the dynamic synchsafe size in bytes `6..9` (`10 + ea3BodySize` — observed as `0x400` / `1024` B on `NW-E403` `0xFFFE`, `0x800` / `2048` B on `0x0001`, and `0xC00` / `3072` B). Text frames (`TIT2`, `TPE1`, `TALB`) must use **UTF-16BE** (`0x02` encoding byte).
   - **Block 2 (`EA3`):** 96-byte audio/crypto descriptor starting with ASCII `0x45 0x41 0x33` (`EA3`), encoding Codec ID (`0x00` ATRAC3, `0x01` ATRAC3plus, `0x03` MP3) and protection mode:
     - **`0xFFFE` (Gen 3 MP3):** 4-byte `DvID` XOR scrambling (`((0x2465 + trackId * 0x5296E435) & 0xFFFFFFFF) ^ deviceKey`) for `NW-E400/E500/E100/HD3/HD5`.
     - **`0x0001` (Gen 3 ATRAC3/3plus MagicGate OpenMG):** Requires zero `30GRCT/` or `SRCIDLST.DAT` files on `NW-E403`; relies on `00010021.DAT` (`172 B`), `LEAF_TABLE[3]`, continuous DES-CBC audio (`EA3[0x58..0x5F] = IV`), `3` `GEOB` ID3v2 frames (`OMG_ULINF`, `OMG_BKLSI`, `OMG_OLINF`), `05CIDLST.DAT` (`00000000 || CID20 || 24 zeros`), `MACLIST0.DAT` (`32,768 B`), and a **non-FAT `168`-byte Hardware `ICV` register** (`SCSI 0xA4/0x33` read, `0xA3/0x34` write with `0x30..0x32` CM-AAD AKE). **Approved Hybrid Strategy:** **Option 2 Primary Cross-Platform Default** reuses pre-allocated/freed signed `MACLIST0.DAT` slots (`OMG_ULINF` + recovered `Kc` intact) via pure MSC alongside keyless `0xFFFE` MP3 / `0xFFFF` ATRAC3plus; **Option 1 Optional Windows Extension** delegates minting/deletion of new `MACLIST0` slots to an optional out-of-process 32-bit COM ICV bridge (`CLSID_OmgHdwmAuth`/`CLSID_OmgHdwmSac`, zero Sony binaries bundled in repo) paired with Win32 `IOCTL_SCSI_PASS_THROUGH_DIRECT`.
     - **`0xFFFF` (Gen 4+ Keyless):** For `NW-A600/A1000/A3000/E00x/S700/A800`.
   - **3-Tier Read-Only `DvID` Resolution:** (1) `MP3FM/DvID.DAT` / `OMGAUDIO/DvID.DAT` file, (2) zero-elevation known-plaintext recovery from existing `0xFFFE` MP3 `.OMA` Xing/LAME headers, (3) optional elevated `IOCTL_SCSI_PASS_THROUGH` (`0xA4`).
2. **ATRAC3plus Bitstream & `NW-E403` Hardware Invariants (`RE WIP 02–04`):**
   - `44.1 kHz` Stereo, `2,048` samples/frame, `frameBytes = ((p24 & 0x3FF) + 1) * 8` across all `9` Sony Walkman bitrates (`48..352 kbps`, `01 00 28 22` .. `01 00 28 FF`). Physical `NW-E403` ASIC decodes and plays **`352 kbps` (`2,048 B/frame`) natively**.
   - Sony encoder invariants verified across `11,619 / 11,619` golden frames: `use_full_table = 0`, `noise_present = 0`, `negate_coeffs = 0`, `pow_levs = 15`, valid `num_qu in {1..28, 32}`, `~15.16–15.3 kHz` psychoacoustic cutoff, and `3f 00 ...` silence framing.
3. **`.NWM` `0xEC01` Coordinate & Deduplication Invariants (`M4` Audit):**
   - Physical `NW-E400/E500` OLED is `120 × 36` inside packed `48 × 120` frames (`x = 0..11` is an unlit 12-px hardware dead strip); upright `120 × 36` maps bijectively via `px = 47 - row, py = 119 - col`. Deduplicate unique `720`-byte bitmaps cleanly within `<= 56` bitmaps / `45,056 B`.
4. **FAT16/FAT32 & Disk Flush Discipline:**
   - All `OMGAUDIO` paths must be written in strict uppercase 8.3 ASCII (`OMGAUDIO/10F00/10000001.OMA`), while read enumeration permits authentic non-8.3 folders such as `NWWM-SCR/fading_wind.nwm`. Player firmware updates `NWWM/BRWLIST.DAT` and `FNOTABLE.DAT` on playback/boot.
   - All file commits must call `FileStream.Flush(flushToDisk: true)` before reporting completion, validated against the mounted volume serial number (`GetVolumeInformationW`).
5. **Device Detection & `OmgV1`/`OmgV2` Disambiguation:**
   - Multi-factor match in `manifests/devices.json` combining `0x054C` `VID:PID`, USB product descriptor, FAT partition capacity, `DvID[6..7]` model segment, and filesystem markers (`OMGAUDIO`, `NWWM`, `NWWM-SCR`, `SCREEN`).
   - Because authentic Gen 3 `NW-E403` units carry empty playlist slot `0x22` tables (`01TREE22.DAT`, `03GINF22.DAT`), `OmgV2` exclusion uses `01TREE05.DAT` / `03GINF05.DAT` (Artist Link).
   - **Verified `NW-E507 (FW 1.00)` Hardware Nuances (`RE_WIP_06` & `M3.5` 5-Layer Adaptive Architecture):** `FW 1.00` omits `NWWM\`/`NWWM-SCR\` until tables are staged, uses `45`-slot `00GTRLST.DAT`, `01TREE2D.DAT`/`03GINF2D.DAT`, and `RESERVED.DAT` (`0x43`), requires `0xFFFE` HW XOR for MP3 (`0xFFFF` -> `"MG ERROR"`), requires Sony factory FAT16 BPB (`Rsvd=1`, `OEM='        '`; Windows `Rsvd=8` causes `"FORMAT ERROR"`), and returns all-zero `0xA4/0x3F` `DvID` until CM-AAD `Auth 1 -> Auth 2` (`0xA3/0x30 -> 0xA4/0x31`). Handled by `TryReadFat16Bpb`, `Tier3b_ScsiAuth1Auth2`, `OmgDatabaseSchemaIntrospector` (Adopt/Bootstrap), and `OmgDatabaseValidator`.
6. **MP3 Tag Hygiene:**
   - Native MP3 streams wrapped into `.OMA` must have pre-existing ID3v2/APIC artwork chunks stripped and MPEG frames aligned so vintage Sony hardware decoders do not freeze.
7. **UI State Isolation, Persistence & Placeholder Governance:**
   - Desktop UI rendering uses thread-safe reference-counted Skia caches (`RefCountedSkImage`, `RefCountedSectorBatch`), cached `SKMaskFilter` shadows, deterministic triangular-PDF dithering (`ApplyTriangularDither`), and a `<= 60 FPS` frame-pacing cap that pauses when idle, minimized, or in Low-Spec mode. Preferences persist locally in `%LocalAppData%\ApolloManager\settings.json` (`AppSettingsStore.cs`), defaulting `IsDemoModeEnabled = false` on first run.
   - Synthetic Walkman profiles and sample catalog tracks are strictly isolated behind `DemoModeOverlay` (`DEMO` badges), while live hardware profiles carry explicit `SupportStatus` (`Verified`, `Experimental`, `Unsupported`) badges and session-deduplicated warnings.
   - Planned post-beta features (interactive Screen & OLED pixel editor, casing-color Chameleon accents, Firmware flashing & maintenance tabs, `AAL` encode, `Artist Link`) are clearly badged with lightweight Aero glass `"WIP"` pills.
