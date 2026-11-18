# License Isolation Boundary: `/tools/`

This directory isolates external, unmodified CLI tools invoked out-of-process by `L2.Engines` (such as `Walkman.L2.Codec.Pipeline`) via standard process pipes / CLI invocations.

- **Policy:** Never statically link or embed LGPL/GPL source code into `.NET` assemblies inside `src/`.
- **Expected Runtime Tools:**
  - `tools/atracdenc/atracdenc.exe` (LGPL-2.1, v0.2.3) + accompanying `COPYING` / `LICENSE` notice.
  - `tools/vgmstream/vgmstream-cli.exe` (ISC, r2168) + accompanying `COPYING` notice.
- Note: Actual third-party binaries are excluded from Git via `.gitignore` while license markers (`COPYING*`, `LICENSE*`) may be preserved alongside staged redistributables.
