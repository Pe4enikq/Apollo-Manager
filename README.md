# Apollo Manager

> **Apollo Manager** is a modern, modular desktop utility for Sony Network Walkman and ATRAC players (1999–2010), replacing Sony SonicStage, OpenMG Jukebox, CONNECT Player, MP3 File Manager, Simple Burner, and customization tools.

---

## Core Design Principles

- **Strict Three-Tier Modularity (L1 / L2 / L3):**
  - **L1 (`L1.Core`):** Always-resident spine (`Orchestrator`, `DevicePoller`, `FileManager`). Contains zero device-specific binary offsets or codec logic.
  - **L2 (`L2.Engines`):** Headless engines (`Transport.Msc`, `Database.OmgV1/V2`, `Crypto.OpenMG`, `Codec.Pipeline`, `Custom.NwmEngine`, `Telemetry.Dumper`) loaded dynamically on demand via `.NET AssemblyLoadContext`.
  - **L3 (`L3.Presentation`):** Avalonia UI + SkiaSharp views and optional feature studios (`AppShell`, `ScreensaverStudio`, `LyricsSubStudio`, `MetadataStudio`, `DumpWizard`) styled with hardware-matched 2007 "Frutiger Aero" aesthetics.
- **Contract-Driven Decoupling:** L2 and L3 plugins never reference each other directly. All shared DTOs, events, and interfaces live in `Walkman.Contracts`, routed through the L1 Mediator/Event Bus.
- **Permissive & Clean-Room Engineering:** Built on C# 12 / `.NET 8/9` using audited MIT / BSD / Apache-2.0 / Public Domain dependencies and clean-room `Span<byte>` binary parsers. Any LGPL tools (`atracdenc`, `libmp3lame.dll`) remain isolated external dynamic/CLI components.

---

## Planned Solution Structure

```text
WalkmanUtility.sln
├── manifests/
│   └── devices.json                       # Declarative database of Sony Walkman models & capabilities
├── src/
│   ├── Walkman.Contracts/                 # Shared interfaces, DTOs, Events, DeviceProfile models
│   ├── L1.Core/
│   │   ├── Walkman.L1.Orchestrator/       # DI Container, AssemblyLoadContext Plugin Loader, EventBus
│   │   ├── Walkman.L1.DevicePoller/       # USB VID/PID watcher, Volume scanner, Manifest matcher
│   │   └── Walkman.L1.FileManager/        # Local Library index, Device VFS abstraction, Sync Queue
│   ├── L2.Engines/                        # Headless plugins (loaded dynamically on demand)
│   │   ├── Walkman.L2.Transport.Msc/      # Mass Storage VFS driver
│   │   ├── Walkman.L2.Transport.NetUsb/   # Legacy WinUSB driver (Post-MVP Phase 4)
│   │   ├── Walkman.L2.Transport.Optical/  # ATRAC CD ISO9660 builder (Post-MVP Phase 4)
│   │   ├── Walkman.L2.Database.OmgV1/     # Binary parser/writer for NW-E400/E500/HD series
│   │   ├── Walkman.L2.Database.OmgV2/     # Extended parser for NW-A1000/A800/S700 (Post-MVP Phase 2)
│   │   ├── Walkman.L2.Database.Mp3Fm/     # Lightweight MP3FM table writer (Post-MVP)
│   │   ├── Walkman.L2.Crypto.OpenMG/      # EA3/ea3 header, DvId.dat / Mqkl.dat key obfuscation & XOR/DES
│   │   ├── Walkman.L2.Codec.Pipeline/     # Transcoding & wrapping pipeline (MP3, ATRAC3, PCM, etc.)
│   │   ├── Walkman.L2.ContentAware/       # LRC sync, subtitle alignment, Kanji/Cyrillic transliterator
│   │   ├── Walkman.L2.Custom.NwmEngine/   # .nwm 1-bit animation packer/unpacker
│   │   └── Walkman.L2.Telemetry.Dumper/   # Read-only ~50 KB diagnostic zip generator
│   └── L3.Presentation/                   # User-visible UI & feature studios
│       ├── Walkman.L3.AppShell/           # Avalonia main window, Frutiger Aero theme, Dual-Pane UI
│       ├── Walkman.L3.ScreensaverStudio/  # Visual .nwm editor & live OLED simulator
│       ├── Walkman.L3.LyricsSubStudio/    # Visual LRC & subtitle timeline editor (Post-MVP Phase 3)
│       ├── Walkman.L3.MetadataStudio/     # Tag editor, Cover Art sanitizer, Transliterator (Post-MVP Phase 3)
│       └── Walkman.L3.DumpWizard/         # Community device dump submission UI
└── docs/
    └── ARCHITECTURE_AND_AUDIT.md          # Stack reconciliation, license audit & binary invariants
```

---

## First MVP ("Project E403") Definition of Done

To qualify as the first MVP, **Apollo Manager** must reliably demonstrate:
1. **Device Communication & Model Identification:** Detect connected Sony Walkman hardware (`0x054C`), inspect its filesystem/descriptors, and accurately resolve the connected model profile (`NW-E400/E500` primary target + telemetry dump support for unknown models).
2. **Database Integrity (`OMGAUDIO` v1):** Read, update, and rebuild `OMGAUDIO` binary index tables (`00GTRLST.DAT`, `01TREE01..04.DAT`, `02TREINF.DAT`, `03GINF01..04.DAT`, `04CNTINF.DAT`, `05CIDLST.DAT`) cleanly without causing `NO DATA` or `FORMAT ERROR` on hardware.
3. **100% Bidirectional File & Codec Interchange:** Transfer music `PC <-> Player` and `Player <-> Player` with tag preservation across supported codecs (fast pass-through MP3-in-OMA wrapping, LAME MP3 transcoding, `atracdenc` ATRAC3 encoding, and full `.oma` decoding via `vgmstream`; see [`ROADMAP.md`](ROADMAP.md) for the MVP vs. Post-MVP ATRAC3plus strategy).
4. **Visual Animation Editor Prototype (`L3.ScreensaverStudio`):** Interactive `128×36` (and `120×36`) 1-bit pixel art & timeline prototype with GIF/PNG/`.aseprite` dithering import, simulated OLED glow preview, and `.nwm` export/deploy to the player's `SCREEN` folder.

---

## Documentation

- [Project Roadmap (`ROADMAP.md`)](ROADMAP.md)
- [Architecture Reconciliation & Dependency Audit (`docs/ARCHITECTURE_AND_AUDIT.md`)](docs/ARCHITECTURE_AND_AUDIT.md)
