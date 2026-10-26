# Convertr_svc — Design

**Status:** design only (no code yet) · **Author:** JeepNWilly · **Date:** 2026-10-18 · **Rev 4** (Intel benchmark results in §5B; Rev 3 = NAS-backed media + local scratch disk + real server results from `media-server`)
**Product name:** Convertr_svc (the Linux/web service edition of Convertr)
**Companion project:** Convertr (Windows desktop app, .NET 10 WinForms) — `C:\Projects\Convertr`

> Convertr_svc runs Convertr's conversion engine as a headless systemd service on an Ubuntu
> server, with a web page as its interface. It targets the server's **Intel Arc Pro B60
> (24 GB)** for hardware HEVC/AV1 encoding via VAAPI/QSV, with CPU x265 as the always-available
> fallback and the default quality choice.
>
> **Deployment assumption (Rev 2):** the media library lives on a **NAS** (for example a Synology), mounted on the
> server over **NFS** (recommended) or SMB; **Plex does not run on the server**. Each job is therefore staged
> through a **local scratch disk**: copy the source from the NAS to scratch, encode locally, verify, decide
> keep-only-if-smaller *before* any copy-back, then write the result back to the NAS and swap atomically (§7A).

---

## 0. Naming and conventions (one place, used everywhere)

| Thing | Value |
|---|---|
| Product / folder | `Convertr_svc` — `C:\Projects\Convertr_svc` (WINDOWS-PC), `/workspace/convertr-svc` (box) |
| Solution | `Convertr_svc.sln` |
| Host project / namespace | `Convertr.Svc` (ASP.NET Core) · tests `Convertr.Svc.Tests` |
| Published binary | `convertr-svc` (single self-contained `linux-x64` file) |
| systemd unit | `convertr-svc.service` |
| Linux service user / group | `convertr` / `convertr` (member of `render` and `video`) |
| Install dir | `/opt/convertr-svc` |
| Data dir (state) | `/var/lib/convertr-svc` (settings, queue, probe cache, undo journals, auth) |
| Scratch dir (local disk) | `/srv/convertr-scratch` recommended in general (on the owner's server: `/srv/convertr-scratch`, §5A.3) (config key `ScratchDir`; default `/var/lib/convertr-svc/scratch`) — never on the NAS (§7A) |
| Config dir (admin-edited) | `/etc/convertr-svc/appsettings.json` |
| Logs | journald, identifier `convertr-svc` (`journalctl -u convertr-svc`) |
| Default web port | `8484` (configurable) |
| Licence header on every `.cs` file | `Copyright (C) 2026 JeepNWilly` |

---

## 1. Goals, non-goals, and reuse of Convertr.Core

### 1.1 Goals
1. Do on a server what Convertr does on the desktop: **scan** a media tree, **preview** what would change and how much space it would save, **confirm**, **convert** in a queue with live progress, and **undo**.
2. Run unattended as a **systemd service**; survive reboots, resume the queue, never lose state.
3. Use the **Intel Arc Pro B60** for hardware encoding (HEVC default, AV1 optional) and fall back to **CPU x265** cleanly.
4. Keep the **same safety model** as Convertr (§6.5): never delete originals, verify, keep-only-if-smaller, originals moved to `_Convertr_Originals`, journaled undo — now **staged through a local scratch disk** because the media lives on a NAS (§7A).
5. Stay **in sync** with the Windows app: one engine, one set of planner rules, one test suite (§2).
6. Simple install: one self-contained binary + one unit file (+ optional `.deb`).

### 1.2 Non-goals (v1)
- No multi-user accounts/roles (single admin password).
- No cloud/remote-worker farm, no multi-server queue.
- No transcoding for streaming (this is a library-space-saving tool, not Plex's transcoder).
- No NVIDIA/AMD encode paths on Linux in v1 (the NVENC code already exists and is kept working where the hardware exists, but Intel is the tested target).
- Plain HTTP on the home LAN only (or localhost); never exposed to the internet. No HTTPS/TLS layer is planned.
- No Docker image in v1 (a possible later add-on; GPU passthrough adds moving parts).
- No automatic ffmpeg building from source.

### 1.3 What we reuse from Convertr.Core (unchanged logic)
`Convertr.Core` is already plain `net10.0` and **already builds and passes its whole test suite on Linux** (366 tests, run on the box). It is ~4.6 k lines with no UI dependency. Reused as-is:

| Area | Components |
|---|---|
| Scan & probe | `Scanner`, `ProbeCache`, ffprobe wrapper |
| Decision logic (pure) | the planner, `SavingsModel`, thresholds, "already efficient / skip" rules |
| Command building | `FfmpegCommandBuilder` (extended with VAAPI/QSV branches, §5) |
| Execution | `ConversionEngine`, `FfmpegRunner` (suspend/resume made portable, §2.4) |
| Safety | `Swapper` (temp part file → verify → keep-if-smaller → originals to `_Convertr_Originals` → undo journal) |
| State | `QueueStore`, `SettingsStore`/`Settings` (62 options), run log |
| Hardware | `HardwareDetector`, `HardwareDefaults`, `SpeedEstimator`, `BenchmarkRunner`, `IHardwareProbe` |
| ffmpeg mgmt | `FfmpegLocator`, `FfmpegDownloader`, `FfmpegVersions` (Windows part gets a Linux counterpart) |

What is **not** reusable: the WinForms UI (`MainForm`, `SetupForm`, `FfmpegVersionForm`, theming, grid helpers). The orchestration logic currently inside `MainForm` (scan → replan → summary → build run summary → offer existing queue → run → undo/purge/export) is not UI-agnostic and has to be extracted into a UI-neutral application layer (§2.5) so the server — and eventually the Windows form — share it.

---

## 2. KEY DECISION — keeping Convertr and Convertr_svc in sync

### 2.1 The options

| | **A. Copy + sync script** | **B. Shared Core, sibling projects** (recommended) | **C. Single monorepo** |
|---|---|---|---|
| Engine source copies | 2 (drift inevitable) | **1** | **1** |
| Where Windows app lives | `C:\Projects\Convertr` | unchanged — **nothing moves** | moves to `src/Convertr.Windows` in a new root |
| Where service lives | `C:\Projects\Convertr_svc` | `C:\Projects\Convertr_svc` (own solution, own releases) | `src/Convertr.Svc` in the same repo |
| A Core bug fix reaches both | only if the script is run and merges cleanly | **immediately** (same files) | **immediately** |
| Risk to the Windows app | low, but fixes get double-applied or forgotten | **very low** (only additive Core changes, same tests) | medium (project moves, script/path churn) |
| Tests guard both apps | no (two suites diverge) | **yes** — both builds run the same `Convertr.Tests` | yes |
| Release cadence independent | yes | **yes** | coupled unless tagged carefully |
| Fits "new directory" request | yes | **yes** | no (code would live in the old directory) |
| Tooling effort | script + merge conflicts forever | one MSBuild property | rewrite `build.ps1`, `deploy.ps1`, `sync.sh` paths |

**Recommendation: B — one shared `Convertr.Core`, consumed by both apps via a relative project reference. No copy of the engine exists anywhere.** C (monorepo) is a sound later step if the owner wants a single repo, and §2.6 spells out exactly what it would involve; it is not needed to get a single engine today, and it is the only option that would touch the Windows project layout. A is rejected: every Core fix (and Core is where nearly every bug and tuning lives — savings model, thresholds, swap safety) would have to be made twice.

### 2.2 How B works concretely
- `Convertr_svc.sln` contains `src/Convertr.Svc`, `tests/Convertr.Svc.Tests`, and **references** (does not copy) `Convertr.Core` and `Convertr.Tests` from the sibling Convertr repo.
- A single MSBuild property locates the engine, defined in `Convertr_svc/Directory.Build.props`:
  - default: `$(MSBuildThisFileDirectory)../Convertr/source/src/Convertr.Core` (matches the Windows layout `C:\Projects\Convertr\source`),
  - override with env var `CONVERTR_CORE_DIR` (used on the box, where the repo is `/workspace/convertr/source`, and in CI),
  - a build target **fails with a clear message** if the folder is missing (never a silent fallback).
- Both `build.ps1` (Windows) and the svc build/test script run `Convertr.Tests` (the Core suite), so a Core change that breaks either app is caught before shipping.
- **Parity rule:** any change to shared behaviour goes into `Convertr.Core` + `Convertr.Tests` first; UI-only changes go into the respective app. A short `docs/PARITY.md` (added in S0) lists each feature as `shared` / `Windows-only` / `Svc-only` so drift is visible. Both CHANGELOGs name the Core build they were built against (e.g. "Core 0.2.5 / build 10").
- For a git remote later: the svc repo pins the Convertr repo as a **git submodule** (exact commit), so a svc build is reproducible; day-to-day dev uses the sibling checkout.
- Version numbering: Convertr_svc has its own `VERSION`/`BUILD_NUMBER` (starting `0.1.0`); it embeds the Core version it was built with and shows it in the UI footer and `/api/version`.

### 2.3 What changing Core means for the Windows app (the promise)
- **The Windows exe stays functionally unchanged for the owner.** Settings file format changes are *additive only* (new optional fields with defaults that reproduce today's behaviour); `settings.json`, queue, undo journals and probe cache from existing installs keep loading unchanged.
- Every Core refactor lands behind the existing test suite; S0 acceptance is "all 366 tests green + a Windows build whose behaviour is identical" (§9).
- The Windows project, namespaces, exe name, icon, output path and `build.ps1` flow are not renamed or moved by option B.

### 2.4 Windows-only bits in Core to abstract (S0 work list)
Introduce a small **`Convertr.Core.Platform`** namespace in Core itself (no new assembly): `IPlatformServices` plus `WindowsPlatform` and `LinuxPlatform` implementations selected once at startup by `OperatingSystem`. Both implementations compile on every OS; P/Invoke is only invoked on its own OS. Existing seams (`IHardwareProbe`) are kept.

| Current code (Core) | Problem on Linux | Abstraction | Windows impl (unchanged behaviour) | Linux impl |
|---|---|---|---|---|
| `Hardware.cs` — CPU/GPU names via `powershell` + CIM, `nvidia-smi`, `OperatingSystem.IsWindows()` gates | no PowerShell/CIM | `IHardwareProbe` (exists) + `IPlatformHardware` (CPU name, cores, RAM, GPU list) | CIM + `nvidia-smi` as today | `/proc/cpuinfo`, `lscpu`, `/proc/meminfo`, `lspci -nn`, `/sys/class/drm/*`, `nvidia-smi` if present, `vainfo` |
| `Hardware.cs` — encoder test list covers NVENC / QSV / AMF | no VAAPI; QSV test assumes Windows | generic `TestEncoderAsync(encoderSpec)` driven by a per-platform list of candidate encoders | NVENC/QSV/AMF | `hevc_vaapi`, `av1_vaapi`, `hevc_qsv`, `av1_qsv`, `h264_vaapi`/`h264_qsv`, `hevc_nvenc` (if present) |
| `Environment.MachineName` (profile "from another machine" check) | works, but name may change on servers | keep; add a stable machine id (`/etc/machine-id`) on Linux | `MachineName` | `MachineName` + `/etc/machine-id` hash |
| `FfmpegLocator` — `.exe` names, `;` PATH split, `BaseDirectory`/`%LocalAppData%` folders | wrong names/folders | `IPlatformServices.ExeName()`, `PathSeparator`, `ToolSearchDirs` | as today | `PATH`, `/usr/lib/jellyfin-ffmpeg`, `/opt/convertr-svc/ffmpeg`, `/usr/local/bin`, `/usr/bin` |
| `FfmpegDownloader` / `FfmpegVersions` — gyan.dev Windows zips, NVENC driver table | Windows archives | `IFfmpegProvider` (list versions, install, verify) | existing gyan.dev flow (incl. "Download older") | **no auto-download in v1**: detect + explain how to install via apt / jellyfin-ffmpeg; optional "install static build into data dir" later (§6.5) |
| `FfmpegRunner` — `NtSuspendProcess`/`NtResumeProcess` (ntdll), `ProcessPriorityClass` | ntdll absent | `IProcessController` (`Suspend`, `Resume`, `SetPriority`) | ntdll P/Invoke | `kill(pid, SIGSTOP/SIGCONT)` via libc P/Invoke; `nice` via `PriorityClass` |
| `Swapper`/`ConversionEngine` — `File.Get/SetCreationTimeUtc` | birth time cannot be set on Linux | `IFileTimes.Copy(src, dst)` | creation + write + access (as today) | write + access only; creation time is best-effort no-op (documented; mtime — what Plex sorts on — is preserved) |
| `FfmpegRunner`/probe launches (`ProcessStartInfo` built in several places) | Linux GPU encodes need a custom environment (`LD_LIBRARY_PATH`, `LIBVA_*`) — see §5A.2 | `IFfmpegEnvironment.Apply(ProcessStartInfo)` hook called by every ffmpeg/ffprobe start; `Settings.FfmpegEnvironment` (key/value list) | no-op (nothing applied) | applies configured/auto-detected variables |
| `Swapper` + `ConversionEngine` do the whole job **in place** (temp part file next to the source, same volume) | a NAS-backed server must stage through local scratch (copy-in → encode → decide → copy-back → swap) | `IJobStorage` seam with stages `PrepareLocal`, `Commit`, `Cleanup` | `InPlaceStorage` = today's `Swapper` code **unchanged** (default on Windows) | `InPlaceStorage` (scratch off) and `ScratchStorage` (§7A; also usable later by the Windows app for SMB libraries) |
| Path comparisons with `StringComparison.OrdinalIgnoreCase` (Swapper, engine, scanner) | Linux file systems are case-sensitive | `PathCompare.Comparison` / `PathCompare.Comparer` from `IPlatformServices.PathsCaseSensitive` | `OrdinalIgnoreCase` (unchanged) | `Ordinal` |
| Long-path handling (`\\?\` prefix helpers, MAX_PATH guards) | n/a | `IPlatformServices.NormalizePath` | as today | identity |
| Path separators hard-coded in a few helpers/tests (`\`, `Z:\`) | mixed separators | use `Path.DirectorySeparatorChar` / `Path.Combine` consistently; tests build paths with `Path.Combine` | same | same |
| `Settings.DefaultPath`, `QueueStore.DefaultPath`, `ProbeCache` default — `AppContext.BaseDirectory` | wrong place for a service (`/opt` is read-only for the service user) | `IAppPaths` (`DataDir`, `LogsDir`, `QueuePath`, `SettingsPath`, `ProbeCachePath`, `UndoDir`); today's `AppPaths` lives in the WinForms project and moves into Core | next to the exe (as today) | `/var/lib/convertr-svc/...` (env `CONVERTR_DATA_DIR` overrides) |
| Logging to a file next to the exe | journald preferred | `ILogSink` | file | stdout/stderr → journald (plus per-job log files under the data dir) |
| `DialogGeometry` / `System.Drawing` types in Core | UI concern in Core | move to the Windows project (compiles on Linux today, but doesn't belong) | — | — |
| `EncoderMode { Gpu, Cpu }` with NVENC-only GPU branch in `FfmpegCommandBuilder` | no vendor/API choice | keep `EncoderMode` (compat) and add `GpuBackend { Auto, Nvenc, Qsv, Vaapi }` + `GpuCodec { Hevc, Av1 }` to `Settings` with defaults that reproduce today's behaviour | Nvenc/Hevc | Vaapi/Hevc (QSV optional) |
| `SavingsModel` — calibrated from NVENC GPU and x265 CPU measurements | Intel numbers unknown | calibration keyed by backend; Intel entry **initially marked "uncalibrated"**; **done in 0.3.1** from the §5B measurements (HEVC); AV1 stays unmeasured and is flagged as such | existing values | new Intel values |

### 2.5 UI-neutral application layer
New small library **`Convertr.App`** (in the Convertr repo, referenced by both apps; start in S0, grow as needed): `ScanService`, `PlanService` (replan + summary), `RunService` (build run summary, offer existing queue, run, pause/resume/cancel), `UndoService`, `PurgeService`, `ExportService`, and DTO/view-model types (a UI-neutral `PlanRow`). The server calls this layer directly. The Windows `MainForm` is migrated to it **gradually and optionally** (one extraction per release, behind the test suite) — it does not have to change for the server to ship, which keeps the Windows app risk-free. The cost of *not* migrating the form is a small amount of duplicated orchestration glue, tracked in `PARITY.md`.

### 2.6 What moving the Windows app (option C) would mean — for later, if wanted
Not required for B. If the owner later wants one repo:
1. Create root `Convertr/` with `src/Convertr.Core`, `src/Convertr.Windows` (the WinForms project, `git mv` from `src/Convertr`; **assembly name stays `Convertr`**, namespace unchanged, so the exe is still `Convertr.exe`), `src/Convertr.Svc`, `tests/…`.
2. Update `Convertr.sln`, `build.ps1` (publish path), `deploy.ps1`, `sync.sh` and the build-number bump to the new project path; keep `Directory.Build.props` at the root.
3. Fold `Convertr_svc` history in with `git subtree`; retire the sibling-path property.
4. Verify: byte-for-byte compare of the old and new published exe's behaviour (smoke run + harness `hardware`/`plan` outputs identical), full test suite, install over an existing settings.json.
Risks: broken scripts/paths (mitigated by doing it on a branch with the same tests), CI cache invalidation, and a coupled release cadence. Roll back = revert the branch.

### 2.7 Migration plan for S0 (shared-core refactor) — nothing breaks
1. Add `IPlatformServices`, `IAppPaths`, `IProcessController`, `IFileTimes`, `PathCompare`, `IPlatformHardware` in Core with **Windows implementations that call today's code verbatim** (pure moves, no behaviour change). Tests stay green after each step.
2. Replace direct calls site by site (one PR-sized commit each: paths → process → file times → path compare → hardware → ffmpeg locate), running the full suite each time.
3. Add `LinuxPlatform` + Linux unit tests that run on the box with fake probes/process runners.
4. Add the `IJobStorage` seam: extract the current `Swapper` flow behind `InPlaceStorage` as a pure move (golden test: same files, same journal, same command lines). `ScratchStorage` itself is built in S3, not S0, so the Windows hot path is not rewritten in the refactor milestone.
4b. Add the additive `Settings` fields (`GpuBackend`, `GpuCodec`) and `FfmpegCommandBuilder` VAAPI/QSV branches with command-string tests (no GPU needed to test strings).
5. Build the Windows app, run `harness hardware`, compare output to the previous build; diff the sample plan CSV on the test library: must be identical.
6. Version bump (Convertr 0.3.0) and CHANGELOG entry "internal: platform abstraction; no user-visible change".
Risks and mitigations: (a) accidental behaviour change on Windows → pure-move steps + golden-output comparison of plans and command lines; (b) `Settings` JSON compat → round-trip test with a saved real `settings.json`; (c) Linux case sensitivity exposes latent bugs → new tests with case-differing names; (d) P/Invoke on the wrong OS → guarded by `OperatingSystem` checks and covered by a "loads on Linux" test.

---

## 3. Architecture

```
 Browser (LAN)                      Ubuntu server
 ┌────────────────┐   HTTP/JSON     ┌────────────────────────────────────────────┐
 │ static web UI  │ ──────────────► │ convertr-svc (ASP.NET Core, Kestrel)       │
 │ HTML+JS (ES    │ ◄── SSE ─────── │  ├─ minimal API  (/api/*, cookie auth)      │
 │ modules, no    │   live progress │  ├─ static files (wwwroot, embedded)        │
 │ build step)    │                 │  ├─ JobHost: BackgroundService (queue)      │
 └────────────────┘                 │  ├─ Convertr.App  (scan/plan/run/undo)      │
                                    │  └─ Convertr.Core (engine, Swapper, …)      │
                                    │        └─ ffmpeg/ffprobe child processes    │
                                    │              └─ /dev/dri/renderD* (Arc B60) │
                                    └──────────── state: /var/lib/convertr-svc ───┘
```

### 3.1 Components
- **Host:** ASP.NET Core minimal API (`Microsoft.NET.Sdk.Web`), Kestrel, `Host.UseSystemd()` (Type=notify, clean stop). One process, one engine instance.
- **Worker:** a `BackgroundService` (`JobHost`) that owns the queue (`QueueStore`), runs `ConversionEngine` with N parallel jobs, supports pause/resume (SIGSTOP/SIGCONT on the ffmpeg children), cancel, and **resumes an interrupted queue after restart** (the existing queue-journal logic).
- **App layer:** `Convertr.App` services (§2.5) are the only thing the endpoints call.
- **Live progress:** **SSE** (`text/event-stream`, `GET /api/events`) — recommended over SignalR: one-way server→browser is all we need, no extra client library, automatic browser reconnect with `Last-Event-ID`. Commands stay ordinary `POST`s. Event types: `scan.progress`, `plan.changed`, `job.changed`, `run.progress`, `run.state`, `hardware.changed`, `log`. SignalR is the documented alternative if we ever need bidirectional streaming.
- **Static UI:** **plain HTML + CSS variables + small ES-module JavaScript, no framework, no Node toolchain** (recommended).
  - Why not Blazor: Blazor Server needs a per-tab circuit and re-renders large tables expensively (the preview table is ~3 000 rows); Blazor WASM adds a multi-MB runtime and a second compile target. Both couple UI iteration to the .NET build.
  - Why plain JS: tiny payload, no build step on the box or on the owner's server, works offline on the LAN, trivial to theme (`prefers-color-scheme` gives **System**; `data-theme` gives Dark/Light), and the table is handled with a virtualised list fed by server-side sort/filter/paging.
  - Escape hatch: if the UI grows beyond ~3 k lines of JS, move to a small framework (Preact/Lit) — API is unchanged.
- **Persistence (`/var/lib/convertr-svc`, owner `convertr`, mode 0750):**
  `settings.json` (same schema as Convertr's), `queue.json`, `probe-cache.json`, `undo/` (journals), `logs/` (per-run logs), `auth.json` (password hash, session key), `hardware.json` (cached detection/benchmark with machine-id), `scratch-ledger.json` (space reservations per job). The **scratch dir itself** is a separate local path (§7A). All writes are atomic (write temp + rename), like `QueueStore` today.
- **Config (`/etc/convertr-svc/appsettings.json`)** — admin-edited, read at start: listen address/port, `AllowedRoots`, `ScratchDir` default, ffmpeg path override, data dir override. The ffmpeg **environment variables** (`FfmpegEnvironment`, §5A.2) are a runtime setting (auto-detected, overridable in Setup → ffmpeg). Runtime-tunable options (everything in Convertr's Setup) live in `settings.json` and are edited from the web UI.
- **Logs:** structured console logging → journald (`SyslogIdentifier=convertr-svc`); per-job ffmpeg stderr kept in `logs/` and viewable in the UI.

### 3.2 API sketch (all under `/api`, JSON, cookie auth + CSRF header on non-GET)
| Method & path | Purpose |
|---|---|
| `POST /auth/setup` · `POST /auth/login` · `POST /auth/logout` · `POST /auth/password` | first-run setup (needs setup token), login, change password |
| `GET /fs/roots` · `GET /fs/list?path=` | allowed roots; directory listing (dirs only, never outside roots) |
| `GET /settings` · `PUT /settings` | read/update (validated, atomic, backed up) |
| `POST /scan` · `GET /scan` · `DELETE /scan` | start/status/cancel scan of the chosen folder |
| `GET /plan?sort=&filter=&skip=&take=` · `GET /plan/summary` · `PUT /plan/selection` | preview table (paged), totals, row selection (**starts empty**) |
| `POST /plan/export` | CSV |
| `POST /run` (confirm) · `GET /run` · `POST /run/pause` · `/resume` · `/cancel` | queue control |
| `GET /queue` · `DELETE /queue/{job}` | queue view / remove pending |
| `GET /undo` · `POST /undo/{journal}` · `POST /purge` | undo list/apply; purge originals (explicit confirm) |
| `GET /hardware` · `POST /hardware/detect` · `POST /hardware/benchmark` | detection results, re-detect, benchmark |
| `GET /ffmpeg` · `POST /ffmpeg/path` · `POST /ffmpeg/test` | locate/validate/test ffmpeg |
| `GET /events` | SSE stream |
| `GET /version` · `GET /healthz` | version (service + Core), liveness for monitoring |

### 3.3 systemd unit (sketch)
```ini
# /etc/systemd/system/convertr-svc.service
[Unit]
Description=Convertr_svc media converter
After=network-online.target remote-fs.target
Wants=network-online.target

[Service]
Type=notify
User=convertr
Group=convertr
SupplementaryGroups=render video
ExecStart=/opt/convertr-svc/convertr-svc
Environment=CONVERTR_DATA_DIR=/var/lib/convertr-svc
Environment=DOTNET_gcServer=0
StateDirectory=convertr-svc
ConfigurationDirectory=convertr-svc
SyslogIdentifier=convertr-svc
Restart=on-failure
RestartSec=5
Nice=10
IOSchedulingClass=best-effort
IOSchedulingPriority=6
# hardening (relaxed only where GPU / media paths require it)
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/convertr-svc /srv/convertr-scratch
# NAS media roots must be listed to be writable, e.g. via a drop-in:
#   [Service]
#   ReadWritePaths=/mnt/nas/media
# RequiresMountsFor=/mnt/nas/media   <- a [Unit] setting; the installer writes it into the [Unit] section of paths.conf (0.3.3), never under [Service]
DevicePolicy=closed
DeviceAllow=char-drm rw   # NOT /dev/dri: systemd needs a node path or a /proc/devices class (fixed in 0.3.2)
PrivateDevices=false
KillMode=control-group
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
```
`ReadWritePaths` for the NAS roots and the scratch dir is generated by the install script from `AllowedRoots` and `ScratchDir`. In scratch mode all large temp files (source copy, encoder output) live in the scratch dir — not in `PrivateTmp` and not on the OS disk unless that is what the owner chose; only the finished `.part` is ever written to the NAS (§7A).

### 3.4 Packaging
- `dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false` → one `convertr-svc` file (~70–90 MB, includes the web UI as embedded resources) plus nothing else. Trimming is off (reflection-based JSON/ASP.NET).
- `install.sh`: creates user/group, dirs, copies binary, installs unit, prints the first-run setup URL/token, and enables the service. `uninstall.sh` keeps `/var/lib/convertr-svc` unless `--purge`.
- **Optional `.deb`** (S5): `dpkg-deb` with `postinst` doing the same as `install.sh`; upgrade keeps state; conffile for `/etc/convertr-svc/appsettings.json`.
- Build/test happens on the box (Linux, .NET 10). The ffmpeg + Arc part can only be proven on the owner's server (§9).

---

## 4. Web UI — screens (mirror the Windows app)

General: single page app with a top bar (Convertr_svc, version, hardware badge "Arc Pro B60 · HEVC", queue state, theme, logout). Same wording and ordering as the Windows app wherever possible. **No row is preselected anywhere**; all buttons that need a selection are disabled until one exists. Dark / Light / System theme via CSS variables.

1. **Media folder** — text field + **Browse…** opens a *server-side* folder browser (modal tree). It lists only directories under the configured `AllowedRoots`, shows free space, and rejects anything else (§6). Field starts **empty** on a fresh install (no machine-specific defaults). Remembers the last folder in `settings.json` and re-validates it on start; if it no longer exists it shows empty with a note.
2. **Scan** — button + live progress (files probed / cached / skipped, current folder, elapsed), cancel. Uses the probe cache so rescans are fast.
3. **Preview table** — virtualised, sortable, filterable; columns as in the Windows grid (selected checkbox, file, codec, resolution, size, planned action, est. new size, est. saving, reason/skip). Header shows count / total size / estimated savings (uses `SavingsModel`; a note under the cards says what the estimate is based on; the Intel HEVC numbers are measured (§5B), AV1 is flagged as not measured). Filter chips: Convert / Skip / Already efficient / Needs attention. Select-all-visible and "Select none"; **nothing selected by default**. Export plan CSV.
4. **Confirmation** — modal showing the run summary (`BuildRunSummary`): files, input GB, estimated output GB and saving, encoder/backend, parallel jobs, estimated time, **scratch needed vs available, GB to copy in / expected GB to copy back** (§7A), and the safety statement (originals are moved to `_Convertr_Originals`, never deleted). Buttons: **Start** / Cancel. Dry-run checkbox (logs commands, changes nothing).
5. **Queue / progress** — per-job rows (**phase: Waiting for space → Copying in → Encoding → Verifying → Copying back → Swapping → Done / No gain / Waiting for NAS**, %, speed×, fps, ETA, size so far, encoder, transfer MB/s), overall progress bar, **Pause / Resume / Cancel**, log tail, failed-job reasons with "retry" and "open log". Survives page reloads and reconnects (state comes from `GET /run` + SSE).
6. **Setup** — grouped sections, identical option set to the Windows Setup (all 62 settings are surfaced; advanced ones under "Advanced"): output container **MP4 / MKV**, theme **Dark / Light / System**, junk-metadata cleanup, keep timestamps, savings thresholds (min saving %, min MB), skip rules, quality (CPU CRF / GPU quality), parallel jobs, priority, holding-area name, dry-run default, the **Scratch** section (below), plus the two sections after it. Save / Cancel; unsaved-changes guard; settings backed up before each save.
   - **Scratch section (new, §7A)** — **Use scratch (on/off)** (default **on**; off = in-place on the NAS like the Windows app), **Scratch folder** (path; validated: local filesystem not NFS/SMB, writable by the service, shows free space and the filesystem type), **Scratch budget (GB)** (default *Auto* = 80 % of free space at start), **Max scratch per job (GB)** (default 2 × the largest planned source, min 20), **Max concurrent transfers** (default 2), **Verify copy-back** (Size + probe *default* / Full SHA-256), **Stop encode early when output can't meet the keep threshold** (default on), **Keep scratch of failed jobs for diagnosis** (default off, auto-purged after 24 h), **NAS-offline retry window** (default 6 h). Buttons: **Test scratch** (write/read speed) and **Test NAS** (read/write MB/s, rename, timestamp round-trip in a user-chosen test folder).
   - **Hardware section** — shows detection result (CPU, GPU(s), driver, kernel, `renderD*` device, VAAPI/QSV capabilities, encoders that passed a **real test encode**), the active backend (Auto / CPU / VAAPI / QSV), codec (HEVC default / AV1), and buttons **Re-detect** and **Benchmark** (runs `BenchmarkRunner` on a built-in short clip; shows CPU vs GPU speed and estimated hours for the scanned library). The service default is **Intel VAAPI HEVC, quality 23** (§5B: about 20x faster than CPU); **CPU x265** stays selectable as the "smallest files, very slow" mode (about 20 % smaller at the same quality, 0.6x real time).
   - **ffmpeg section** — path in use, version, build flags (`--enable-vaapi`, `--enable-libvpl`, `--enable-libx265`), capability check, **Test** button, and guidance text for installing a suitable build (Linux has no one-click download in v1; see §6.5).
7. **Undo** — list of run journals (date, files, bytes), select one (none preselected) → shows contents → **Undo selected** restores originals from `_Convertr_Originals` and removes the converted files; plus **Purge originals…** with a double confirmation. Never auto-purges.

Accessibility/UX basics: keyboard navigation, focus rings, responsive layout (usable on a tablet), no auto-refresh that moves the selection.

---

## 5. Intel Arc Pro B60

### 5.1 What is known vs uncertain (as of 2026-10; verify on the server)
**Known / documented:**
- The Arc Pro B60 (PCI ID `E211`) is Xe2 "Battlemage" and uses the **Xe kernel driver** (`xe`). Intel's Xe-KMD table lists full support from **Ubuntu 24.04 with kernel ≥ 6.15** (Ubuntu 25.10 ships 6.17). Battlemage consumer cards (B580) are listed from kernel 6.12.
- **Resizable BAR must be enabled** in BIOS for Battlemage media acceleration (the media driver can crash otherwise). Above-4G decoding on; ASPM on for idle power.
- Required user-space: `intel-media-va-driver-non-free` (iHD, VAAPI, includes AV1/HEVC encode entrypoints), `libva`/`vainfo`, and for QSV `libvpl2` + the oneVPL GPU runtime (`libmfx-gen1.2` / `vpl-gpu-rt`). `level-zero`/OpenCL are **not needed** for encoding (only for OpenCL-based tone-mapping/filters, which we don't use).
- ffmpeg must be **recent** (7.x or newer; jellyfin-ffmpeg 7.1+ is a known-good Intel build with the Battlemage fixes). Old ffmpeg 6.x from distro or stale jellyfin-ffmpeg has produced "iHD init failed" on B60 in the field.
- Xe2 has hardware **HEVC and AV1 encode** (and AV1 decode); VAAPI encode via `hevc_vaapi`/`av1_vaapi` is the most widely reported working path on Arc.

> **Update (Rev 3):** most items below were settled on the real server — see **§5A Server status**. In particular the B60 works with kernel 7.0 + Intel PPA userspace, and jellyfin-ffmpeg's *bundled* libva/iHD crashes with `Bus error` unless the system libs are forced via environment (§5A.2).

**Uncertain (to be settled by the server checklist and test encodes):**
- Whether the **distro** packages on the owner's Ubuntu release (24.04 HWE vs 25.10) are new enough for the B60 media stack, or the Intel "client GPU" apt repo / jellyfin-ffmpeg is needed.
- Whether **QSV** (`hevc_qsv`, `av1_qsv`) is stable on B60 with the installed vpl-gpu-rt; users report `av1_qsv` failures on some stacks while `av1_vaapi` works. Plan: **VAAPI is the default backend, QSV is tested and offered if it passes.**
- Real-world **quality per bit** of Battlemage HEVC/AV1 vs CPU x265 on our content (§5.5), and the best quality knobs for the B60 (`look_ahead`, `async_depth`, B-frames, ICQ range).
- How many **concurrent encodes** the B60's media engines sustain without a per-stream slowdown.
- Idle/clock behaviour under a long queue, and whether the Pro card exposes the same media engines as B580 (expected, unverified).

### 5.2 Detection (implemented in `LinuxPlatform` + `HardwareDetector`)
Order, each step cheap and failure-tolerant, results cached in `hardware.json`:
1. **Kernel/OS:** `uname -r`, `/etc/os-release`.
2. **PCI:** `lspci -nn` → Intel (`8086`) display class devices; map device id (`e211` → "Arc Pro B60") with a table, unknown ids reported raw.
3. **Driver binding:** `/sys/class/drm/card*/device/driver` → `xe` or `i915`; flag `i915` on Battlemage as "unexpected".
4. **Render node:** enumerate `/dev/dri/renderD*`, check read/write access for the service user (explain the `render` group fix if denied), pick the Intel one (multi-GPU safe: match PCI address via `/sys/class/drm/renderD*/device`).
5. **ReBAR hint:** parse `lspci -vv -s <slot>` for `Resizable BAR`; warn if absent/disabled.
6. **VAAPI:** `vainfo --display drm --device /dev/dri/renderDNNN` — parse profiles/entrypoints: `VAProfileHEVCMain`/`Main10` with `EncSlice`/`EncSliceLP`, `VAProfileAV1Profile0` with `EncSliceLP`, H.264 as a fallback target; record driver version.
7. **ffmpeg capability:** `ffmpeg -hide_banner -hwaccels`, `-encoders` (grep `hevc_vaapi|av1_vaapi|hevc_qsv|av1_qsv|h264_vaapi`), build config flags.
8. **Real test encodes** (the only authoritative check, run per candidate with a 1–2 s synthetic `lavfi` source, 8-bit and 10-bit): `hevc_vaapi`, `av1_vaapi`, `hevc_qsv`, `av1_qsv`. A candidate is "usable" only if it exits 0 and the output probes correctly. Each result and any ffmpeg error text are shown in the Hardware section.
8b. **ffmpeg environment (required on Battlemage-class GPUs, §5A.2):** run each test encode first with ffmpeg's own bundled libs; if it dies with a signal (`Bus error`, exit 135) or `iHD init failed`, **retry with the system-library environment** (`LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu`, `LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri`, `LIBVA_DRIVER_NAME=iHD`). If that passes, the detector stores it as the effective **`FfmpegEnvironment`** (setting, §5A.2) and reports "bundled libs crash — system libs environment applied".
9. **Verdict:** `Auto` order = CPU x265 (size) > `hevc_vaapi` > `hevc_qsv` for speed mode; AV1 only when the user selects it. Detection is re-run on demand and automatically when `machine-id` or kernel/driver version changes.

### 5.3 ffmpeg command shapes (to be validated on the B60; strings are unit-tested without a GPU)
```
# VAAPI HEVC (default GPU path), hardware decode → hardware encode
ffmpeg -init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va \
  -hwaccel vaapi -hwaccel_device va -hwaccel_output_format vaapi -i IN \
  -map 0:v:0 -map 0:a? [-map 0:s?] \
  -vf "scale_vaapi=format=nv12" -c:v hevc_vaapi -rc_mode ICQ -global_quality 24 \
  -profile:v main -tag:v hvc1 -c:a copy OUT.part.mkv

# QSV HEVC
ffmpeg -init_hw_device qsv=hw:/dev/dri/renderD128 -hwaccel qsv -hwaccel_output_format qsv -i IN \
  -c:v hevc_qsv -preset slower -global_quality 24 -look_ahead 1 -tag:v hvc1 ...

# AV1 (optional): av1_vaapi / av1_qsv with the same structure
# Software-decode fallback (codec/profile not decodable on GPU): -vf "format=nv12,hwupload"
```
`FfmpegCommandBuilder` gets `Vaapi` and `Qsv` branches alongside NVENC/x265/x264, covering: container (MP4/MKV, `hvc1` tag), audio/subtitle mapping as today, junk-metadata cleanup, 10-bit sources (`p010`, Main10) , deinterlacing (`deinterlace_vaapi`/`vpp_qsv`), HDR/DV sources **skipped or CPU-only** (no GPU tone-mapping in v1), and automatic per-file fallback to CPU if the GPU attempt fails fast (exit before first progress) — logged and visible in the queue row.

### 5.4 Driver / kernel / package checklist for the server
Target: Ubuntu 24.04 LTS + HWE kernel ≥ 6.14 (6.15+ preferred) **or** Ubuntu 25.10 (6.17). Needed:
- BIOS: Resizable BAR **on**, Above-4G decoding **on**, CSM off.
- Kernel with `xe` (not `i915`) bound to the card; firmware (`linux-firmware` current: GuC/HuC) present — `dmesg | grep -i -E 'xe|guc|huc'`.
- `intel-media-va-driver-non-free`, `libva2`, `vainfo`, `libvpl2`, `libmfx-gen1.2` (QSV), possibly from Intel's apt repo if Ubuntu's are too old.
- A recent ffmpeg: **jellyfin-ffmpeg7** (recommended; self-contained under `/usr/lib/jellyfin-ffmpeg`, built with VAAPI+QSV+x265) or a current distro/PPA build. Convertr_svc's locator prefers it when present. **On Battlemage it must run with the system libva/iHD from the Intel PPA, not its bundled copy** (§5A.2).
- Service user in `render` (and `video`) groups.
Not needed: `level-zero`, `intel-opencl-icd`, Intel compute runtime (unless we later add OpenCL tone-mapping).

### 5.5 Quality tuning plan (measure, then set defaults)
Same method that produced the existing NVENC/x265 numbers (harness sweep + VMAF):
1. Pick ~10 representative clips from the owner's real library (live-action 1080p, animation, grainy film, 720p TV, 4K if any), 60–120 s each.
2. Encode each with: CPU x265 CRF 18/21/24 (baseline), `hevc_vaapi` ICQ `global_quality` 20/22/24/26/28/30, `hevc_qsv` (same, with/without `look_ahead`, presets `slow`/`slower`/`veryslow`), then `av1_vaapi`/`av1_qsv` over the same grid.
3. Measure **VMAF** (and size, wall-clock, fps, power if available) against the source; plot size vs VMAF per backend.
4. Choose per backend the quality value that matches the **CPU x265 CRF 21 VMAF** (the current default target) and report the size penalty. Prior knowledge from NVENC (≈2× larger than x265 at equal VMAF) is **not** assumed for Arc; Battlemage's encoder is newer and may do considerably better — to be measured. **Measured (§5B): about 20 % bigger than x265 at equal VMAF: closer than the ≈2× assumed from NVENC Pascal, but still not better than the CPU.**
5. Knobs to evaluate: `global_quality` (ICQ), `preset`/`quality` level, `look_ahead` + `look_ahead_depth` (QSV), `b_depth`/B-frames, `async_depth`, 10-bit encode of 8-bit sources (sometimes smaller at equal quality), `-low_power` (VDEnc vs full PAK) on/off.
6. Feed results into `SavingsModel` (new Intel calibration) and `HardwareDefaults` (default GPU quality value), and record them in `docs/RESULTS_INTEL.md`, as `RESULTS_SAVINGS.md` does for Windows.
7. Acceptance for "GPU mode is recommended": within an agreed size penalty of CPU (decided with the owner after seeing the data). **Decided (§5B):** the B60 (VAAPI HEVC q23) is the default because of its speed; CPU x265 stays the optional smallest-files mode.

### 5.6 AV1 vs HEVC (Plex direct play)
- Default **HEVC (`hevc_vaapi`)**. HEVC direct-plays on almost every Plex client in use (Fire Stick 4K, Roku, Apple TV, Shield, most TVs, Plex apps).
- **AV1** gives smaller files at equal quality, but **many TVs and Fire Sticks cannot decode AV1** (and the Plex server — which runs elsewhere, not on this server — then has to transcode, defeating the purpose and risking buffering/quality loss). Therefore AV1 is an opt-in setting with an explicit warning: *"Only choose AV1 if every device that plays this library supports AV1 hardware decoding."* Not applied to a library silently; a per-run override on the Confirm screen shows the chosen codec.
- Re-checked after §5.5 measurement: if AV1 saves e.g. >25 % over HEVC on Arc it may be worth a *separate* library or a "check my clients" note; this is a policy decision for the owner, not a technical default.
- Already-HEVC/AV1 sources keep the existing skip rules (no re-encode loops).

### 5.7 Concurrency and fallback
- **Parallel jobs:** default `GPU = 2`, `CPU = 1` (CPU x265 already uses all cores); the effective number is further capped by **scratch space and NAS throughput** (§7A.4), and tuned from the benchmark: the Benchmark button runs 1, 2, 3, 4 concurrent GPU encodes and picks the knee of fps-per-stream. Hard limit in Setup (1–6).
- Mixed mode (optional later): GPU worker(s) + CPU worker simultaneously.
- **Fallback to CPU x265:** (a) at detection — no usable GPU encoder → `Auto` = CPU with a clear note; (b) per job — GPU launch/first-frame failure, or `Device creation failed`, → retry once on CPU (setting "Fallback to CPU on GPU failure", default on); (c) at runtime — render node disappears (driver reset) → job fails safely (part file removed, original untouched), queue continues on CPU if allowed.
- GPU memory (24 GB) is not a constraint for 1080p/4K encodes at this concurrency; the media engine is.
- Temperature/power: nothing special; log GPU busy % when `intel_gpu_top` is installed (optional).

### 5.8 Server checklist — commands for the owner to run on the Ubuntu server and paste back
*(Rev 3: the Intel/GPU items below were **run on `media-server` and are resolved** — results in §5A. The NAS and scratch items remain open until the NAS is mounted.)*
```bash
uname -r
lsb_release -a
lspci -nn | grep -Ei 'vga|3d|display'
sudo lspci -vv -d ::0300 2>/dev/null | grep -iE 'Resizable BAR|Kernel driver in use|LnkSta:'
lspci -k | grep -A3 -Ei 'vga|display'
ls -l /dev/dri /dev/dri/by-path
id; getent group render video
sudo dmesg | grep -iE ' xe |xe 0000|guc|huc|i915' | tail -n 30
dpkg -l | grep -Ei 'intel-media|libva|vainfo|libvpl|libmfx|vpl-gpu|jellyfin-ffmpeg|^ii  ffmpeg'
vainfo --display drm --device /dev/dri/renderD128
ffmpeg -hide_banner -version | head -n 3
ffmpeg -hide_banner -hwaccels
ffmpeg -hide_banner -encoders | grep -E 'vaapi|qsv|x265|libsvtav1|libaom'
# real test encodes (each should finish without errors):
ffmpeg -hide_banner -loglevel error -init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va \
  -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 2 -vf format=nv12,hwupload -c:v hevc_vaapi -f null - ; echo hevc_vaapi=$?
ffmpeg -hide_banner -loglevel error -init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va \
  -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 2 -vf format=nv12,hwupload -c:v av1_vaapi -f null - ; echo av1_vaapi=$?
ffmpeg -hide_banner -loglevel error -init_hw_device qsv=hw:/dev/dri/renderD128 \
  -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 2 -vf format=nv12,hwupload=extra_hw_frames=64 -c:v hevc_qsv -f null - ; echo hevc_qsv=$?
ffmpeg -hide_banner -loglevel error -init_hw_device qsv=hw:/dev/dri/renderD128 \
  -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 2 -vf format=nv12,hwupload=extra_hw_frames=64 -c:v av1_qsv -f null - ; echo av1_qsv=$?
nproc; free -h; df -hT | grep -vE 'tmpfs|squashfs'
mount | grep -E 'nfs|cifs'          # how the media library is mounted
nfsstat -m 2>/dev/null              # NFS version and options actually in effect
findmnt -T /mnt/nas/media -o TARGET,SOURCE,FSTYPE,OPTIONS
df -hT / /srv 2>/dev/null           # local disks that could hold the scratch dir (lsblk -f also helps)
lsblk -d -o NAME,SIZE,ROTA,MODEL    # SSD/NVMe vs HDD for the scratch disk
id convertr 2>/dev/null; getent passwd | awk -F: '$3>=1000 {print $1":"$3":"$4}'   # local uids (to match NAS user ids)
# On a THROWAWAY test folder on the NAS share (create e.g. /mnt/nas/media/_convertr_test first):
cd /mnt/nas/media/_convertr_test && dd if=/dev/zero of=t.bin bs=1M count=2048 conv=fsync status=progress && \
  dd if=t.bin of=/dev/null bs=1M status=progress && touch -d '2020-01-02 03:04:05' t.bin && mv t.bin t2.bin && mkdir -p o && mv t2.bin o/ && stat -c '%U:%G %a %y' o/t2.bin; rm -rf /mnt/nas/media/_convertr_test/o /mnt/nas/media/_convertr_test/t.bin
dotnet --list-runtimes 2>/dev/null  # not required (self-contained), informational
```
(Add `sudo -u convertr …` variants later to prove the service user can open the render node.)

---

## 5A. Server status (measured on `media-server`, 2026-10-18)

### 5A.1 Results of the server checklist
| Item | Result | Status |
|---|---|---|
| Host | `media-server`, service/login user `youruser` | ✅ |
| OS / kernel | Ubuntu **24.04.5** LTS, kernel **7.0.0-38-generic** | ✅ (newer than the 6.15 floor) |
| GPU | Intel **Arc Pro B60**, driver **`xe`** | ✅ |
| Userspace | `intel-media-va-driver-non-free` **26.3.2**, libva **2.24**, from the Intel PPA `kobuk-team/intel-graphics` | ✅ (Ubuntu's own 24.04 packages not used) |
| `vainfo` | HEVC Main/Main10 **encode**, H.264 encode, AV1 Profile0 **encode** | ✅ |
| CPU / RAM | 8 cores, 31 GB | ✅ (CPU x265 runs 30 fps on the test clip) |
| GuC firmware | 70.44.1 (recommended 70.54.0) | ⚠ harmless; update `linux-firmware` later |
| Render node access | `render` group (service user must be in it; add `SupplementaryGroups=render` already in the unit) | ✅ |
| ffmpeg | none preinstalled; **jellyfin-ffmpeg7 7.1.4** installed at `/usr/lib/jellyfin-ffmpeg/ffmpeg` | ✅ (with the environment fix below) |
| Other services | other services share the second disk (`/srv`) | ℹ shared resource, see 5A.3 |
| NAS | **not mounted yet**; NFS planned | ⏳ open (NAS self-test §7A.7 pending) |

**Test encodes (1080p synthetic, 2 s):**
| Encoder | jellyfin-ffmpeg with its **bundled** libva/iHD | with **system-libs environment** (5A.2) |
|---|---|---|
| `hevc_vaapi` | ❌ `Bus error`, exit 135 | ✅ exit 0 |
| `av1_vaapi` | ❌ `Bus error`, exit 135 | ✅ exit 0 |
| `hevc_qsv` | ❌ `Bus error`, exit 135 | ✅ exit 0 |
| `av1_qsv` | ❌ `Bus error`, exit 135 | ✅ exit 0 |
| `libx265` (CPU) | ✅ | ✅ |

Conclusion: VAAPI **and** QSV both work; AV1 encodes work; HEVC stays the default (Plex client compatibility, §5.6). Quality/size measurements were done afterwards, see §5B.

### 5A.2 KEY FINDING — ffmpeg must be launched with the system libva/iHD environment
jellyfin-ffmpeg ships its own libva and an older `iHD_drv_video.so` under `/usr/lib/jellyfin-ffmpeg/lib`. On this B60 that bundled driver crashes (`Bus error`, exit 135) on every GPU encode. With the environment below ffmpeg loads the PPA's libva 2.24 + iHD 26.3.2 instead and all four encodes pass:
```
LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu
LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri
LIBVA_DRIVER_NAME=iHD
```
Design consequences:
1. **Setting `FfmpegEnvironment`** (new, additive, in `Settings`): an ordered list of `NAME=VALUE` pairs applied to **every** ffmpeg and ffprobe process the service starts (encode, probe, test encode, benchmark). Default empty on Windows; on Linux default **`Auto`**.
2. **Auto-detect:** the Intel test encode first runs with *no* extra environment; if it exits by signal / exit 135 / prints `iHD init failed` / `Failed to initialise VAAPI`, it is retried with the three variables above (paths resolved from the distro multiarch dir). If the retry passes, the environment is saved as detected and shown in Setup → ffmpeg ("System libva environment: **applied** (bundled libs crash on this GPU)"); if both fail, the GPU backend is reported unusable with both error texts and CPU x265 is used.
3. **Runtime safety net:** a GPU job that dies with a signal before its first progress line is retried **once with the system-libs environment** (if not already applied) and then with CPU (setting "Fall back to CPU on GPU failure"); the reason is logged in the queue row.
4. **Documented requirement:** on Battlemage-class GPUs the Intel PPA userspace (libva/iHD) must be installed and used; the installer and ADMIN_GUIDE say so, and the **Intel self-test** (`tools/intel-selftest.sh`) runs the four encodes both ways and prints which environment is required.
5. **Core hook:** a single `IFfmpegEnvironment.Apply(ProcessStartInfo)` call site used by every process start (S0 introduces it; Windows applies nothing).
6. Risk: the system libs and ffmpeg's bundled libs must be ABI-compatible for `LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu` to be safe (it is, here, for the tested build); the pinned jellyfin-ffmpeg version is recorded with the environment so an ffmpeg upgrade re-runs detection.

### 5A.3 Disks and proposed scratch
| Disk | State | Role |
|---|---|---|
| root volume | small, OS only | OS, `/opt/convertr-svc`, `/var/lib/convertr-svc` (small state only) |
| second disk (ext4) mounted at `/srv` | a few hundred GB free (shared with other services) | **proposed scratch: `/srv/convertr-scratch`** |
- Scratch sizing check (§7A.3): a typical 1080p job (≈ 8 GB source, ≈ 16 GB scratch per job) needs about 16 GB per parallel job plus one prefetch; Budget Auto = 80 % of free space.
- Caveats of sharing the disk with other services: their growth can shrink free space (the free-space check and ledger handle it, jobs wait instead of failing); heavy encode I/O may slow them; a dedicated disk would be cleaner. Decision: **use `/srv/convertr-scratch` for now** with a conservative explicit Scratch budget (e.g. 150 GB); revisit if contention is seen.
- The unit's `ReadWritePaths` must include `/srv/convertr-scratch` (and `ProtectSystem=strict` otherwise blocks it); the directory is created by the installer with owner `convertr`, mode 0700 and the `.convertr-scratch` marker. The service never touches anything else under `/srv`.

### 5A.4 Still open on the server
- NAS: not mounted. Needed: NFS export/squash decisions (§7.2, open questions 8, 8a), mount at `/mnt/nas/media`, then `tools/nas-selftest.sh`.
- `linux-firmware` update (GuC 70.54.0): optional, schedule later; re-run the Intel self-test afterwards.
- Quality/size sweep (§5.5): **done**, see §5B. The concurrency benchmark (§5.7) has not been run.
- Service user: decide `convertr` (as designed) vs reusing `media-server`; recommended: dedicated `convertr`, member of `render`.

---

## 5B. Intel Arc Pro B60 benchmark results (measured on `media-server`, 2026-10-18)

Measured with `tools/intel-bench.sh` 1.1 (`--matched`): **5 movies, 30 s clips cut from the middle, VMAF (`vmaf_v0.6.1`) against the source clip**, jellyfin-ffmpeg 7.1.4 with the system libva / iHD environment (§5A.2), VMAF scored by a separate BtbN ffmpeg 8.1. Size is the encoded clip as a percentage of the source clip; speed is times real time. Every number is an average over the 5 movies. These are small samples: treat sizes as indicative, not exact.

| Variant | Size % of source | Speed | VMAF | dVMAF vs CPU CRF 21 | dSize vs CPU CRF 21 |
|---|---|---|---|---|---|
| **CPU x265 slow CRF 21 (baseline)** | 36.4 | 0.60x | 96.97 | 0 | 0 |
| CPU x265 CRF 22 | 31.1 | 0.63x | 96.55 | -0.42 | -5.3 |
| CPU x265 CRF 24 | 23.2 | 0.69x | 95.45 | -1.52 | -13.2 |
| CPU x265 CRF 26 | 17.7 | 0.75x | 93.98 | -2.99 | -18.7 |
| VAAPI HEVC ICQ q20 | 48.2 | 13.1x | 97.16 | +0.18 | +11.8 |
| **VAAPI HEVC ICQ q23 (default)** | **29.6** | **13.1x** | **95.66** | -1.31 | -6.8 |
| VAAPI HEVC ICQ q26 | 28.9 | 13.3x | 95.60 | -1.37 | -7.5 |
| VAAPI HEVC ICQ q29 | 18.0 | 13.2x | 92.92 | -4.05 | -18.4 |
| QSV HEVC ICQ + look-ahead q21 | 30.3 | 11.8x | 95.69 | -1.28 | -6.2 |
| QSV HEVC ICQ + look-ahead q24 | 21.5 | 11.6x | 94.20 | -2.77 | -14.9 |
| QSV HEVC ICQ + look-ahead q27 | 18.2 | 11.8x | 93.24 | -3.73 | -18.3 |

### Conclusion: at equal quality the CPU is smaller, the Arc is about 20x faster
- **An earlier read of the quick run (SSIM, one CPU setting) said the Arc files were smaller than the CPU files. That was wrong.** The Arc settings in that run were simply of lower quality than CPU CRF 21 (a smaller file at a lower quality is not a smaller file at the same quality). With VMAF and the CPU also measured at several quality levels the picture is: **at the same VMAF the CPU x265 files are smaller.**
- Reading the CPU curve (CRF 22 to 24) at the quality the Arc reaches: VAAPI q23 (VMAF 95.66, 29.6 %) corresponds to a CPU file of about **24.7 %**, QSV q21 (95.69, 30.3 %) to about **24.9 %**. The Arc files are about **20 % bigger** at the same quality. At the full CPU CRF 21 quality (VMAF 96.97) VAAPI would need about 45.9 % of the source (straight-line estimate between q23 and q20), against 36.4 % for the CPU. QSV did not reach that quality in the tested range.
- Speed: the CPU runs at 0.6x real time (about 3 hours for a 2-hour movie, all cores busy); the Arc runs at 11.8x to 13.3x (about 10 minutes for a 2-hour movie, CPU free). For a library of hundreds of movies the Arc is the only practical choice; the 20 % size difference is what you pay for it.

### Decision (0.3.1)
- **Default: Intel VAAPI HEVC ICQ q23** (`IntelCq` 23, unchanged from the placeholder). VMAF 95.66 is "very close to the source", 29.6 % of the source size, 13.1x. q20 is too big (48.2 %, bigger than CPU CRF 21 for no gain a viewer can see); q29 falls to VMAF 92.9, where differences start to show.
- **QSV stays selectable** with its own value `IntelQsvCq` **21** (same quality and size as VAAPI q23, slightly slower, 11.8x). Both backends use ICQ; the settings are separate because the same number does not mean the same quality on the two APIs.
- **CPU x265 stays the optional "smallest files, very slow" mode.** Quality tweak to remember: CPU CRF 24 (VMAF 95.45, 23.2 %) is about as good as the Arc default but 6 points smaller.
- **Not offered as a "smaller" Arc setting yet:** VAAPI q26 came out almost the same as q23 (28.9 % against 29.6 %), so q24 to q28 should be measured (q27, q28) before suggesting one.
- **AV1 (`IntelAv1Cq` 27) is untested:** it was not benchmarked. The value is a placeholder, the Setup label says so, and the savings estimate for AV1 is shown as "not measured".
- **Savings estimate (`SavingsModel`):** for GPU mode with VAAPI/QSV the estimate is the CPU model (content dependent, already fitted to real library files) times the measured ratio Arc/CPU on the same clips (VAAPI q23: 29.6 / 36.4 = 0.813; QSV q21: 30.3 / 36.4 = 0.832), scaled by 0.74 per 3 quality steps (log fit over the VAAPI points q20 to q29; the q26 point is flat in the data, so this is approximate). The web UI shows what the estimate is based on under the summary cards. NVENC (Windows) and CPU estimates are unchanged.
- Not measured: 4K / HDR (skipped by the bench), 10-bit sources are covered only if present in the 5 movies, parallel encodes (§5.7), power use, AV1.

## 6. Security

### 6.1 Network exposure
- Default bind **`127.0.0.1:8484`** until the owner sets `Listen` to a LAN address in `/etc/convertr-svc/appsettings.json` (installer asks; recommended: LAN IP). Never `0.0.0.0` by default.
- HTTP only, LAN only: the service has no HTTPS/TLS layer and is not meant to be reachable from the internet (no port forwarding, no reverse proxy).

### 6.2 Authentication (mandatory)
- **No unauthenticated access to anything** except `/healthz` (no data) and the login/setup pages.
- **First-run setup:** on first start there is no password; the service generates a one-time **setup token**, prints it to the journal (`journalctl -u convertr-svc`) and the setup page requires it — so the first browser to hit a LAN service can't claim it. After setup the token is invalidated.
- Passwords hashed with ASP.NET `PasswordHasher` (PBKDF2-HMAC, per-user salt, high iteration count); minimum length enforced; hash stored in `auth.json` (0600). Login rate-limiting with exponential back-off; generic failure message.
- Sessions: HttpOnly, `SameSite=Strict`, 12 h sliding expiry; keys persisted in the data dir so a restart doesn't log everyone out; logout invalidates.
- Password reset: `convertr-svc --reset-password` run as root/service user on the server (physical/SSH access is the recovery factor).

### 6.3 CSRF and web hardening
- Anti-forgery token (header `X-CSRF`) required on every non-GET; plus `Origin`/`Host` verification; cookies `SameSite=Strict`.
- Security headers: strict CSP (no inline script, same-origin only), `X-Content-Type-Options`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`.
- All API input validated; request size limits; no shell string interpolation anywhere — ffmpeg is started with an **argument list** (as `FfmpegRunner` does today), never via `sh -c`.

### 6.4 File-system confinement
- Only paths under configured **`AllowedRoots`** (admin config, not changeable from the web) can be browsed, scanned, converted or undone.
- Every path from the client is canonicalised (`Path.GetFullPath` + realpath resolution of symlinks) and checked to be inside a root **after** resolution; `..`, symlink escapes, and non-regular files are rejected. Directory listing returns directories only plus counts.
- Undo/purge operate only on journal entries the service wrote itself (journal paths re-validated against the roots).
- The service user has OS-level access only to the roots (systemd `ReadWritePaths`), so confinement is enforced twice.

### 6.5 Safety model (same guarantees as Convertr, staged through local scratch)
**Invariants — hold in both scratch mode (default) and in-place mode:**
1. The original is **never overwritten and never deleted**. It is only ever *moved* (an atomic rename within the same NAS share) into `_Convertr_Originals`, and only after the finished, verified, smaller file is already fully present on the NAS as a temp `.part`.
2. **Verify** locally (ffprobe: duration within tolerance, streams present, decodable first/last seconds) before anything is sent back — as `Swapper` does now.
3. **Keep only if smaller** (≥ the configured saving %, and ≥ the minimum MB) is decided **locally, before copy-back**, so a no-gain result costs no upload; the scratch output is simply discarded.
4. Every commit step is **journaled** for crash recovery and for **Undo**.
5. **Never delete originals** — only the explicit, double-confirmed "Purge originals" action removes `_Convertr_Originals` content. The service deletes only files it created itself: scratch files (inside `ScratchDir`, in directories carrying its marker file) and its own `.convertr-part` files.
6. **Timestamps** (mtime/atime) are restored onto the finished file; creation time only where the platform allows (not on Linux/NAS).

**Scratch-mode commit sequence** (details and failure handling in §7A):
1. Copy-in: NAS source → `ScratchDir/<job>/source.*` (size + sampled/optional full checksum, recorded size/mtime of the NAS file).
2. Encode locally → `ScratchDir/<job>/out.part.*`; verify; **keep/discard decision**.
3. Pre-commit check: the NAS source is still there and **unchanged** (size + mtime match the copy-in record). If not → discard the result, mark "source changed", never swap.
4. Copy-back: scratch output → `<target folder>/<name>.convertr-part` on the NAS (same share, same folder), `fsync`, size check and ffprobe of the NAS copy (optional full SHA-256 read-back).
5. Set mtime/atime of the `.part` to the original's values.
6. Rename original → `_Convertr_Originals/<relative path>` (same share ⇒ atomic rename, not a copy).
7. Rename `.part` → final name (atomic). Re-stat and verify.
8. Mark job done, delete the scratch job dir.
Crash windows: before 6 — nothing visible changed; between 6 and 7 — the library file is momentarily absent but both the original (in `_Convertr_Originals`) and the verified `.part` exist; reconciliation completes step 7 (or renames the original back) on restart; after 7 — only scratch cleanup remains.
**In-place mode (Use scratch = off):** exactly the Windows app's flow — temp `.part` next to the source on the NAS, verify, keep-if-smaller, original moved to `_Convertr_Originals`, new file renamed into place — slower over NFS/SMB because ffmpeg reads and writes over the network.
- ffmpeg on Linux is installed by the admin (apt/jellyfin-ffmpeg), not downloaded by the web UI in v1 — a download button that executes a downloaded binary as a service user is deliberately out of scope until there is a hash-pinned, signed source and a clear threat model.

---

## 7. Linux paths, NAS mounts (Synology), permissions

### 7.1 Paths and layout
- **Media paths:** Linux paths under the NAS mount (`/mnt/nas/media/Movies/...`); `AllowedRoots` are the NAS mount points (one per Synology shared folder that is converted). The UI never shows Windows-style paths.
- **Same share, same mount:** the part file's target folder and `_Convertr_Originals` must be on the **same share/mount as the source** so the swap is an atomic rename. On Synology each shared folder is its own btrfs subvolume/NFS export, so a rename **across shared folders fails** (`EXDEV`). Therefore `_Convertr_Originals` lives *inside each media root* (e.g. `/mnt/nas/media/_Convertr_Originals/Movies/...`), never in a different share; this is validated at start and before each job.
- **Local state is on the server:** settings, queue, probe cache and journals are under `/var/lib/convertr-svc`; scratch is on a **local** disk (§7A). Nothing but finished `.part` files and the final renames is written to the NAS in scratch mode.

### 7.2 NFS (recommended) — Synology specifics
- **Why NFS:** POSIX semantics (atomic rename, setattr/utimensat for timestamps), low overhead, no credential file, good throughput for large sequential copies. NFSv4.1 (DSM 7) preferred; NFSv3 works.
- **DSM setup:** Control Panel → File Services → NFS (enable; max protocol NFSv4.1). Then Shared Folder → Edit → **NFS Permissions** → Create a rule for the server's IP: Privilege **Read/Write**, **Squash** per the table below, Security **sys**, enable "Allow connections from non-privileged ports" only if needed, "Allow users to access mounted subfolders" if the root is a sub-folder. Leave asynchronous **off (synchronous)** unless transfer speed proves inadequate — async can lose data if the NAS loses power (our verify + journal would catch it, but sync is the safe default).
- **Squash / UID mapping (ownership is decided here, not by the server):** with `sys` security the server sends numeric uid/gid. Options on Synology:
  | Squash setting | Effect | Use when |
  |---|---|---|
  | **No mapping** | server uid is used as-is; must equal a NAS user's uid for that user's ACL/permissions to apply | recommended with a dedicated NAS user: create NAS user `convertr`, give it Read/Write on the share, create the local `convertr` Linux user **with the same numeric uid/gid** (check `id convertr` on both) |
  | Map root to admin | root on the client acts as NAS `admin` | not needed; the service does not run as root |
  | **Map all users to admin** | every client uid is treated as NAS `admin`; files are owned by `admin` | simplest single-user setup; broadest privilege on that share, acceptable on a trusted LAN if the NFS rule is limited to the server's IP |
  | Map all users to guest | read-mostly | unsuitable (needs write) |
- **Ownership of new files = whatever the mapping resolves to** (the NAS user, or `admin` when "map all to admin"). The service **does not chown** and no other user/group on the server (including any media-player service account) matters; what matters is that the people/apps that read the library on the NAS or elsewhere have read access through **DSM share/ACL permissions** on the converted files. Synology ACLs can add/inherit permissions on new files; the NAS self-test (§5.8) prints owner, group and mode of a created file so the owner can confirm it matches existing files.
- **Timestamps:** `utimensat` over NFS sets mtime/atime with nanosecond precision on btrfs/ext4 volumes; the `.part` gets the original's mtime *before* the final rename, so the library never shows a new mtime. Birth/creation time cannot be set and is not used. The parent folder's mtime will change (renames) — unavoidable; an optional "restore folder timestamps" setting is a nice-to-have.
- **Recommended mount (fstab):** `nas:/volume1/media /mnt/nas/media nfs4 nfsvers=4.1,rw,hard,noatime,rsize=1048576,wsize=1048576,timeo=600,retrans=2,_netdev,nofail,x-systemd.automount,x-systemd.requires=network-online.target 0 0`. `hard` avoids silent partial writes on a NAS hiccup; the service's **stall watchdog** (§7A.6) handles the blocked-I/O case. `soft` is discouraged (can surface as truncated writes).
- **Synology housekeeping dirs:** skip `@eaDir`, `#recycle`, `#snapshot` and `@tmp`. Recycle Bin applies to deletes through SMB/File Station; deletes of our own `.convertr-part` over NFS are not expected to go to it (**verify on the owner's NAS**; if they do, empty `#recycle` for that share or exclude it).

### 7.3 SMB (supported, second choice)
- `mount.cifs` with `vers=3.1.1` (or 3.0), `credentials=/etc/convertr-svc/nas.cred` (0600, root-owned), `uid=`/`gid=` of the `convertr` user, `file_mode=0664,dir_mode=0775`, `nounix`, `nobrl`, `_netdev,nofail,x-systemd.automount`. Ownership on the NAS is the **SMB account's** identity (the mount's `uid=`/`gid=` only affect how Linux displays it).
- Rename within a share is atomic; mtime can be set over SMB; ACL semantics follow DSM permissions for that SMB user. Slightly lower throughput and more latency on many small files (the scan is slower than NFS); the probe cache hides this on rescans.
- The same share/rename constraints, scratch workflow and safety invariants apply unchanged.

### 7.4 Common rules
- **Mount guard:** the service checks each root at start and before each job (is a mount point or contains the marker file, writable, `df` sane) and pauses the queue with a clear message if a mount disappears — it never writes into an unmounted mountpoint (which would fill the local disk).
- **Permissions:** the `convertr` Linux user needs read+write on the NAS mount (via the mapping above), `render`/`video` for the GPU, and write on `ScratchDir`. The installer prints the exact fstab line and NAS-side settings to use; Setup shows a "service can write to this folder: yes/no" check (creates and deletes a tiny probe file inside a dedicated test/temp folder only).
- **Case sensitivity:** Linux is case-sensitive; `Movie.mkv` and `movie.mkv` can coexist. All path equality goes through `PathCompare`. Scanner extension matching stays case-insensitive (`.MKV`). Output names never differ from the source only by case. (Synology shares themselves are case-insensitive over SMB and case-sensitive over NFS by default — another reason NFS is preferred and names are compared as opaque strings.)
- **Special files:** skip symlinks to directories (loop guard), sockets, device files; follow symlinked files only if they resolve inside the root.
- **Unicode:** NFC/NFD normalisation differences from SMB shares are tolerated (paths are handled as opaque strings, never re-normalised).
- **Hidden/system names:** skip `.`-prefixed dirs, `@eaDir`, `#recycle`, `#snapshot`, `lost+found`, `_Convertr_Originals`, `.convertr-part` files (configurable skip list).
- **Real libraries are never touched by development or tests** — fixtures live in scratch directories on the box; real-library runs happen only through the service on the owner's server, after the NAS self-test passes.

---

## 7A. Local scratch workflow (NAS-backed media)

**Decision (owner):** work on a **local scratch disk**. Rationale: ffmpeg does heavy random reads and (for x265/QSV) many small writes; doing that over NFS/SMB is slow, ties up the NAS, and leaves a half-written file on the share if anything fails. A sequential copy-in, local encode and one sequential copy-back is faster, gentler on the NAS and makes failures harmless.

### 7A.1 Job pipeline (state machine, persisted in `queue.json` + `scratch/<job>/job.json`)
`Queued → WaitingForSpace → CopyingIn → Encoding → Verifying → Deciding → {NoGain | CopyingBack → Swapping → Done}`; side states `WaitingForNas`, `SourceChanged`, `Failed`, `Cancelled`.
1. **Admit:** reserve scratch space in the ledger (§7A.3); wait (`WaitingForSpace`) if the budget is exhausted.
2. **CopyingIn:** sequential copy NAS → `ScratchDir/<job>/source.<ext>`; record the NAS file's size, mtime (and inode where available); verify size (+ optional checksum, setting). Throttled by the transfer-slot limit.
3. **Encoding:** ffmpeg reads the local copy and writes `out.part.<container>` locally (CPU x265 or Arc VAAPI/QSV). If enabled, `-fs` caps the output at the largest size that could still be kept, so an encode that cannot meet the threshold stops early (wasted time and space bounded).
4. **Verifying / Deciding:** ffprobe checks the output; the **keep-only-if-smaller** rule is evaluated locally. If it fails → `NoGain`: delete scratch output **and the local source copy**, record the reason, **no upload**.
5. **Pre-commit check:** the NAS source must still exist and match the recorded size/mtime; otherwise `SourceChanged` (result discarded, source never touched).
6. **CopyingBack:** scratch → NAS temp `<target folder>/<name>.convertr-part` (same folder as the final file so the final step is a same-directory rename); `fsync`; size + ffprobe check (optionally SHA-256 read-back); set mtime/atime.
7. **Swapping:** rename original → `_Convertr_Originals` (same share), rename `.part` → final name; journal each step (Undo uses this journal exactly as today).
8. **Cleanup:** remove `ScratchDir/<job>`; release the reservation.
The local source copy is deleted as soon as it is no longer needed (after encode on success, immediately on NoGain/failure), shrinking the scratch footprint while the output waits for copy-back.

### 7A.2 Scratch directory rules
- Config: `ScratchDir` (appsettings default; overridable in Setup, §4), must be a **local** filesystem (checked against `/proc/self/mountinfo`; NFS/CIFS/FUSE-network rejected), on SSD/NVMe preferably, **not** the same share as the media, ideally not the OS disk. Created with a marker file `.convertr-scratch`; the service only ever deletes inside directories that carry the marker.
- Layout: `ScratchDir/<jobid>/{job.json, source.*, out.part.*, ffmpeg.log}`.
- Permissions 0700, owner `convertr`; included in `ReadWritePaths`.

### 7A.3 Sizing, free-space check, caps
- **Per-job reservation** `R = S_src + O_cap + headroom`, where `S_src` is the source size, `O_cap = S_src × (1 − min saving %)` (the largest output that could still be kept; ≤ `S_src`) and `headroom = max(1 GB, 2 % of S_src)`. Rule of thumb: **≈ 2 × the source size per job**.
- **Admission control:** a job starts only if `R` fits in `min(ScratchBudget, actual free space − safety margin (5 GB))` after counting reservations of running jobs and of outputs waiting for copy-back. The free-space check runs **before** each copy-in (the ledger plus a real `statvfs`), and again if `ENOSPC` occurs mid-job (job → `WaitingForSpace` and retried; never a crash).
- **Size cap per concurrent job:** `MaxScratchPerJob` (default 2 × the largest planned source, min 20 GB). A source whose `R` exceeds the cap — or can never fit the budget — is flagged **in the preview table** ("too large for scratch") and is not queued; optionally the user can run it **in place** on the NAS (explicit per-run choice).
- **Planning numbers (shown on the Confirmation screen):** largest file, total GB to copy in, expected GB to copy back (using the savings model), *recommended scratch size* = `2 × largest source × (parallel jobs + 1 prefetch)`, and available space. Example: largest 40 GB, 2 parallel + 1 prefetch ⇒ ≈ 240 GB recommended; typical 8 GB 1080p files need ≈ 16 GB per job.

### 7A.4 Concurrency: scratch space and network throughput
Effective parallelism is `min(Setup parallel jobs, jobs admissible by free scratch, jobs the NAS link can feed)`:
- **Scratch:** dynamic via the reservation ledger (several small files can run together while one huge file runs alone).
- **Network:** `MaxConcurrentTransfers` (default 2) limits simultaneous copy-in/out streams; a 1 GbE link moves ≈ 110 MB/s (≈ 6.6 GB/min), so an 8 GB file takes ≈ 75 s each way, a 40 GB remux ≈ 6 min. **Prefetch:** while one job encodes, the next job's source is copied in (bounded by scratch and transfer slots) so the encoder is not idle. With fast GPU encodes the transfer can be the bottleneck — extra parallel GPU jobs then add nothing, and the benchmark reports NAS read/write MB/s so the Hardware screen can recommend a sensible number.
- **Queue order** stays as planned; a job that does not fit yet blocks only itself and later smaller jobs may start (bounded skip-ahead of 3 jobs, so a large file is not starved).
- Copy-back of a kept file happens only for files that passed the keep rule, so total upload ≈ the (smaller) output sizes, total download = all sources processed.

### 7A.5 Restart and scratch leftovers
On service start (before the queue resumes), reconcile each `ScratchDir/<job>` against `queue.json` and the undo journal, **inside the scratch dir only**:
| Found | Action |
|---|---|
| Job state ≤ `CopyingIn`, partial `source.*` | delete the partial source, requeue from `CopyingIn` |
| `source.*` complete and size/mtime match the NAS file, no output | **reuse the copy** (saves the download), requeue at `Encoding` |
| Partial/unverified `out.part.*` | delete it, re-encode (ffmpeg is not resumable) |
| Complete, verified output with decision *keep* | re-verify, re-check the NAS source is unchanged, resume at `CopyingBack` (saves the whole encode) |
| Job in `CopyingBack`/`Swapping` | reconcile with the journal and the NAS `.part` (finish the rename, restart the copy-back, or rename the original back) — see §6.5 crash windows |
| Directory with the marker but **no matching job** and older than `StaleScratchHours` (default 6) | stale → delete the directory |
| Files without the marker, or anything outside `ScratchDir` | **never touched** |
- Also on start and after NAS reconnects: delete **our own** orphan `*.convertr-part` files on the NAS that no live job/journal references (name pattern + journal match; never anything else).
- The queue itself resumes exactly as today (paused/running state restored; Undo journals unaffected). A "Scratch" panel lists current usage per job with a *Clean up stale scratch now* button.
- Originals are untouched by every one of these actions; scratch copies are copies.

### 7A.6 NAS offline or stalled mid-job
Detection: mount guard (§7.4), I/O errors (`EIO`, `ESTALE`, `ETIMEDOUT`), and a **stall watchdog** — a copy with no byte progress for 120 s (configurable) marks the NAS *unavailable*. (With `hard` mounts the blocked syscall returns only when the NAS comes back; the job is shown as `WaitingForNas` meanwhile and finishes or fails when I/O resumes.)
| Phase when the NAS drops | Behaviour |
|---|---|
| Copy-in | abort job, delete the partial scratch source, state `WaitingForNas`; retry with back-off; original untouched |
| Encoding / Verifying (local only) | **keep encoding** — the NAS is not needed; the finished output waits in scratch (`AwaitingCopyBack`, counted in the ledger) |
| Copy-back | stop; the NAS `.part` may be partial → restart copy-back from the beginning after reconnect (partial `.part` deleted by us first) |
| Swapping | journal says which rename completed; reconcile on reconnect (§6.5 crash windows); the original is never lost |
- **Circuit breaker:** while the NAS is unavailable no *new* copy-ins start (so scratch does not fill with sources), the queue shows "NAS unavailable — waiting", and the UI banner/SSE `run.state` says so. When the mount returns and passes the guard, the queue resumes automatically; after `NAS-offline retry window` (default 6 h) jobs are marked `Failed — NAS unavailable` (scratch kept until the stale window, resumable via *Retry*).
- If scratch fills while waiting, encoding jobs that cannot reserve more space pause; nothing is deleted except stale scratch (§7A.5).

### 7A.7 Core impact and testing
- New shared Core component `ScratchStorage : IJobStorage` (+ `ScratchLedger`, `NasGuard`, `TransferService`) so the logic is unit-testable on the box and reusable by the Windows app later; Windows behaviour is unchanged (scratch off by default, in-place code untouched, §2.4).
- Tests (box, no NAS/GPU): a fault-injecting `IFileOps` simulating the NAS in a temp directory — fail/stall/disconnect at **every phase and every commit step**, ENOSPC on scratch, source changed during encode, output larger than input (no upload), kill -9 at each step then restart (reconciliation matrix in §7A.5), stale-scratch cleanup, "marker-less directory is never deleted", ledger admission with mixed sizes. Real NAS tests: `tools/nas-selftest.sh` (S3) on a throwaway share folder (copy, rename, mtime round-trip, ownership report, throughput).

### 7A.8 Uncertainties to confirm on the owner's NAS/server
- Exact Synology squash/ACL behaviour for newly created files (owner/mode) — verified by the self-test.
- Whether NFS deletes of our `.part` files bypass `#recycle` (expected) and whether `@eaDir`/Synology Drive/snapshots interact with renames.
- Real NAS throughput and whether `hard` mounts hang the .NET copy thread acceptably (watchdog design above is a mitigation, not a proof).
- Which local disk will hold scratch (size/speed); sizing in §7A.3 assumes a dedicated SSD/NVMe of a few hundred GB.

---

## 7B. Network storage (NAS) on the Setup page (0.3.4)

> **Note:** the IP addresses in this document (172.16.0.x, for example 172.16.0.12 for the server and 172.16.0.35 for the NAS) are placeholder examples for documentation only, not real addresses. Use your own server and NAS addresses, and replace `media-server` with your server's host name.

Goal: the one-time mount of a Synology share is done from the Setup page without the service ever getting root. The page **explains, generates and checks**; the administrator **runs** the root commands once in a terminal. A typical setup (example values only): NFS v3 from `172.16.0.35:/volume1/library`, mounted at `/mnt/nas-media` through `/etc/fstab`, `nfs-common` installed, an NFS rule on the NAS for the server's address with a suitable Squash setting (§7.2), the mount point in `AllowedRoots`, and `install.sh --media-root` for `ReadWritePaths`/`RequiresMountsFor` (`paths.conf`).

### 7B.1 Decisions
- **Show-only for everything that needs root.** `NoNewPrivileges=true`, `ProtectSystem=strict` and the dedicated unprivileged user stay as they are. The service cannot and must not edit `/etc/fstab`, mount, install packages, or edit `appsettings.json`. It generates the exact text (server side, one source of truth, unit-tested) and the UI renders it with **Copy** buttons (clipboard API where the page is a secure context, a hidden-textarea `execCommand('copy')` fallback on plain `http://` LAN addresses, which is the normal case here).
- **Allowed folders are never writable from the web** (section 6.4). The page tells whether the chosen folder is inside `AllowedRoots` (`inside`, `configured-not-loaded` = listed but missing when the service started, so restart it, or `outside`) and, when needed, prints the exact `sudo sed` command (one variant for an empty list, one for a filled one), a `grep` to verify, and the `install.sh --media-root ...` line. The installer line **repeats every folder already in AllowedRoots** because `install.sh` rewrites `paths.conf` from its `--media-root` options. `--scratch` is not suggested (it would reset the scratch marker); the unit already allows the default scratch folder.
- **Fixed template, no free text in commands.** Only these inputs reach a command or the fstab line: host (hostname labels or canonical IPv4, never a numeric/hex-looking last label, never starting with `-`), share (`^/[A-Za-z0-9._@+-]+(/...)*$`, no `..`, no spaces), mount point (same charset, and a new folder below `/mnt`, `/media` or `/srv`; configurable with `Convertr:NasMountParents`), NFS version (`3` or `4.1`), read-only flag. The media folder is only used for checks, and only appears in the write-test command when it contains no quote or space. Options are fixed: `vers=<v>,rw|ro,hard,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=0`.
- **The only program the service starts is `showmount -e <host>`**, with `ProcessStartInfo.ArgumentList` (no shell), a 12 s timeout, output capped at 64 KB, `LC_ALL=C`. The host was validated first. It only talks to the NAS's rpcbind/mountd and is behind the login and CSRF protection like every other endpoint.

### 7B.2 API (all need login; POST needs the CSRF token)
| Endpoint | Purpose |
|---|---|
| `GET /api/nas` | saved form (`nas.json` in the data dir), whether `mount.nfs`/`showmount` exist (else `installHint`: `sudo apt install -y nfs-common`), this server's IPv4 addresses (for the DSM rule), current AllowedRoots, version |
| `POST /api/nas/exports {host}` | `showmount -e`; list of `{path, clients, allowsThisServer (true/false/null), usable}`; plain-language reasons for RPC/DNS/timeout errors; `available:false` + hint when nfs-common is missing |
| `POST /api/nas/commands {host, share, mountPoint, vers, readOnly, mediaFolder}` | validates, **saves the form**, returns `fstabLine`, `steps[]` (client, export, mkdir, fstab, reload, mount, write test), `script` (all steps) and the AllowedRoots advice. 400 with a plain message on bad input; nothing is saved then |
| `POST /api/nas/check {mountPoint, mediaFolder}` | runs the checks below |

The one-time commands, in order: `sudo apt install -y nfs-common`; `showmount -e HOST`; `sudo mkdir -p MP`; an idempotent fstab append (`awk '$2=="MP"{f=1} END{exit !f}' /etc/fstab && echo 'already in /etc/fstab' || echo '<line>' | sudo tee -a /etc/fstab`); `sudo systemctl daemon-reload`; `sudo mount MP && ls MP`; `touch DIR/.convertr-write-test && echo WRITE-OK && rm ...` (not for a read-only mount).

### 7B.3 "Check mount" (runs inside the service, so it sees what the service sees)
Steps, each `ok` / `warn` / `fail` / `skip` with a sentence a non-expert can act on: (1) mount point exists and can be opened (this also wakes the automount); (2) it is **really a mount point** per `/proc/self/mountinfo` (newest entry at that path wins, so autofs-then-NFS works) and not a plain folder; **if not, nothing is written** (never fill the local disk, section 7.4); an autofs entry that did not turn into NFS is reported; (3) type, NFS version, rw/ro, hard/soft (`soft` = warning, other filesystem than NFS = warning); (4) size and free space (warning under 20 GB); (5) the media folder exists and is **on this mount** (real path, same mount entry); (6) **write**: a 4 KB random hidden `.convertr-check-<id>.tmp` in the media folder (or the share's top folder when none is given), read back and compared, removed; (7) **rename** in the same folder; (8) **originals**: uses `HoldingArea.Root` like the real run, finds the nearest existing parent, requires it to be on the same mount, creates a hidden temporary folder there and moves the test file into it (cross-folder rename on the same share, so no `EXDEV`), then removes everything. The real `_Convertr_Originals` is never created by the check; (9) AllowedRoots state with the commands; (10) systemd write access: reads the world-readable drop-in (`paths.conf`) and tells whether `ReadWritePaths=` covers the folder (`skip` when the file does not exist).
Error mapping: `Read-only file system` on a read/write mount means the systemd protection (run `install.sh --media-root`); permission denied means the DSM rule (Squash "Map all users to admin", Read/Write). A NAS that does not answer cannot hang the page: the work runs in the background, the page gets a "no answer in 45 s" result, a second check is refused (409) until the first one ends, and a `hard` mount is never turned into `soft`.

### 7B.4 Not done / limits
IPv6 hosts, SMB mounts, share names with spaces, mounting or editing fstab from the web (deliberately), editing AllowedRoots from the web (deliberately), reading `paths.conf` when the service runs from a hand-made unit. `showmount` needs the NAS to allow rpcbind from this server; NFSv4-only NAS setups may list nothing even though a mount works (then type the share by hand). Tests use a fake mount table and fake `showmount`; the real Synology was not touched by development.

---

## 8. Parity and shared behaviour
Single source of truth (Core): planner rules, thresholds, `SavingsModel`, Swapper safety, queue/undo formats, defaults. Differences (tracked in `PARITY.md`): platform hardware detection, ffmpeg install flow, creation-time preservation, UI, auth. A setting added to Core `Settings` automatically appears in the Windows Setup only if added there, and in the web Setup via a **schema endpoint** (`GET /settings/schema`, generated from `Settings` attributes: label, group, type, range, default, help text) — the web Setup is data-driven so new Core options show up with minimal UI work. (Optional S0 item: add the same attributes so the Windows Setup could be generated too; not required.)

---

## 9. Testing strategy
1. **Unit tests (box, Linux, no GPU):** the existing 366 Core tests run on every build of either app. New: `LinuxPlatform` (parsers for `lscpu`, `lspci`, `vainfo`, `ffmpeg -encoders` fed from captured sample outputs, including a B60 sample once the owner pastes it), `PathCompare`, `IFileTimes`, `IProcessController` (SIGSTOP/SIGCONT on a `sleep` process), command-string tests for VAAPI/QSV/AV1 builders (golden strings), settings round-trip/compat, path confinement (traversal, symlink escape, encoded `..`), auth (setup token, lockout, CSRF rejection).
2. **API tests:** `WebApplicationFactory` against the real endpoints with a fake ffmpeg executable (shell script that emulates progress output and failure modes) — scan → plan → confirm → run → undo end to end in a temp directory.
3. **Safety tests:** kill the process mid-conversion (part file orphan, journal half-written) and verify reconciliation; mount-disappears simulation; disk-full simulation (scratch **and** NAS); output larger than input (no copy-back); verify-failure; NAS offline at every pipeline phase; source changed during encode; the full scratch-leftover reconciliation matrix (§7A.5, §7A.7).
4. **Integration on the box (CPU only):** real ffmpeg x265 on synthetic clips under `/workspace/...` scratch dirs (never a real media library), with a temp directory standing in for the NAS.
5. **Real Intel tests on the owner's server (manual, scripted):** `tools/intel-selftest.sh` (S3) runs the §5.8 checklist plus a 1-clip encode per backend and writes a report the owner pastes back; `tools/nas-selftest.sh` (S3) checks the NAS mount options, uid/gid mapping and ownership of a created file, atomic rename, `utimensat` round-trip and read/write throughput in a throwaway share folder; later the §5.5 quality sweep as a script with a results CSV. Pass criteria are in §10 (S3).
6. **UI checks:** headless-browser smoke on the box (screenshots per screen in Dark/Light), plus a manual pass on the owner's browser. No GUI left running.
7. **Release gate:** both apps build; `Convertr.Tests` green; svc tests green; Windows exe smoke-run (`harness hardware`) unchanged.

---

## 10. Milestones (effort = focused working sessions of ~half a day; ±50 %)

| # | Milestone | Effort | Acceptance criteria |
|---|---|---|---|
| **S0** | Refactor to shared Core: `Convertr.Core.Platform` abstractions (§2.4), `IJobStorage` seam with `InPlaceStorage` = today's code, `IFfmpegEnvironment` hook (§5A.2), `IAppPaths` in Core, `Convertr.App` skeleton (§2.5), additive `Settings` fields, VAAPI/QSV command-builder branches (strings only), `PARITY.md`, `Convertr_svc` solution skeleton referencing Core by relative path | 4–6 | All existing 366 tests green on Linux **and** Windows build; new platform tests green; Windows app build behaves identically (plan CSV + `harness hardware` output identical to previous build; existing `settings.json`/queue load); `CONVERTR_CORE_DIR` build works on the box; Convertr 0.3.0 changelog entry |
| **S1** | Server skeleton + auth: host, `UseSystemd`, config, data dir/`IAppPaths`, first-run setup token, login/logout, CSRF, security headers, `/healthz`, `/api/version`, static shell with theme | 3–4 | Service starts under systemd on the box (test unit), no endpoint reachable without login, setup-token flow works, CSRF/lockout tests green, journald shows structured logs |
| **S2** | Scan + preview UI: folder browser with `AllowedRoots`, scan with SSE progress, probe cache, virtualised preview table (no preselection), summary, CSV export, settings read-only | 5–6 | Scan of a ≥3 000-file synthetic tree streams progress; table scrolls smoothly and sorts/filters server-side; traversal/symlink escape tests green; nothing preselected; Dark/Light/System work |
| **S3** | Conversion + queue + Intel GPU + **scratch pipeline (§7A)**: `JobHost`, confirm dialog (scratch/transfer estimates), queue/progress with phases, pause/resume/cancel, resume after restart incl. scratch reconciliation, undo/purge, `ScratchStorage` (copy-in, local encode, keep-decision before copy-back, `.part` + atomic rename + timestamp restore), scratch ledger/admission, NAS guard and offline handling, `tools/nas-selftest.sh`, CPU x265 path complete; VAAPI (and QSV) backends; Linux hardware detection; CPU fallback; Intel self-test script (runs each GPU encode with and without the system-libs `FfmpegEnvironment`, §5A.2) | 12–15 (+ 3–4 loops with the owner's server/NAS) | On the box: full CPU conversion + undo through the scratch pipeline with the fault-injection suite green (kill at every step, NAS offline per phase, ENOSPC, source changed, bigger output ⇒ no upload, stale-scratch cleanup never touches marker-less dirs). On the owner's NAS: self-test report OK (ownership, rename, mtime, throughput); a real file converts via scratch, swaps atomically with original mtime and an `_Convertr_Originals` copy, and undoes. On the owner's server: self-test report shows `hevc_vaapi` OK; a real 1080p file converts via GPU, verifies, swaps, and undoes; fallback to CPU proven by forcing a GPU failure; B60 sample outputs added to parser tests |
| **S4** | Settings/hardware/ffmpeg screens: data-driven Setup (all options), Hardware (detect/benchmark/backends/codec), ffmpeg (locate/test/guidance), quality calibration run and `RESULTS_INTEL.md`, Intel `SavingsModel` calibration | 6–8 | Every Windows Setup option present and persisted, plus the **Scratch section** (Use scratch on/off, Scratch folder with validation, budget, per-job cap, transfers, verify mode, retry window, Test scratch / Test NAS); settings backup on save; benchmark gives per-concurrency numbers on the B60; savings estimate within ±15 % of actuals on a test batch; Auto recommendation documented and matching data |
| **S5** | Packaging/install/docs: self-contained publish, `install.sh`/`uninstall.sh`, optional `.deb`, upgrade path, USER_GUIDE, ADMIN_GUIDE (Synology NFS/SMB setup, UID mapping, scratch disk sizing, GPU), CHANGELOG, v0.1.0 | 3–4 | Clean Ubuntu VM: install from one file, first-run setup, convert a clip (CPU) end to end; upgrade keeps state; uninstall leaves data unless `--purge`; the owner's server install verified; docs complete |

Estimated total: **33–43 sessions** (~7–9 weeks part-time). S0 is the only milestone that touches the Windows app's source, and only additively.

---

## 11. Open questions (with recommended defaults)

| # | Question | Recommended default |
|---|---|---|
| 1 | Sync approach: shared Core via sibling reference (B), or monorepo (C) now? | **B**; revisit C after S5 |
| 2 | Ubuntu release on the server | ✅ **Resolved:** Ubuntu 24.04.5 + kernel 7.0.0-38 works (B60 on `xe`, ReBAR on); GuC firmware update optional later |
| 3 | Which ffmpeg: jellyfin-ffmpeg7, distro, or a static build? | ✅ **Resolved:** jellyfin-ffmpeg7 7.1.4 (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) **run with the system-libs environment** (`FfmpegEnvironment`, §5A.2) because its bundled libva/iHD crashes on the B60 |
| 4 | Default GPU API: VAAPI or QSV? | ✅ **Resolved for availability:** both pass (with the environment fix). Default **VAAPI** (q23). ✅ §5B: QSV at q21 gives the same quality and size, slightly slower (11.8x against 13.1x), so QSV stays a selectable alternative with its own quality value (`IntelQsvCq` 21) |
| 5 | Default codec for GPU mode: HEVC or AV1? | **HEVC**; AV1 opt-in with warning (many TVs/Fire Sticks and Plex clients can't direct-play AV1). ✅ AV1 encodes work on the B60 (vaapi and qsv) |
| 6 | Auto recommendation: CPU x265 vs GPU? | ✅ **Decided after §5B:** Intel GPU (VAAPI HEVC q23) is the default (about 20x faster, files about 20 % bigger than CPU at equal quality); CPU x265 is the optional "smallest files, very slow" mode |
| 7 | Parallel GPU jobs default | 2 (benchmark picks the knee) |
| 8 | Which NAS shared folders become `AllowedRoots`, and NFS or SMB? | **NFS (v4.1)**; one root per Synology shared folder; SMB only if NFS is unavailable |
| 8a | NAS user / squash: dedicated NAS user `convertr` with matching uid ("No mapping"), or "Map all users to admin"? | Dedicated NAS user + matching uid + No mapping; fall back to map-all-to-admin on a trusted LAN limited to the server's IP |
| 8b | Local scratch disk: which device, how large, which path? | ✅ **Proposed (the owner's server):** `/srv/convertr-scratch` on a second local disk (shared with other services) with an explicit budget (e.g. 150 GB); a dedicated disk would be cleaner — revisit if contention appears (§5A.3) |
| 8c | Scratch on by default? | **Yes** (Use scratch = on); off = in-place mode for tiny files/tests |
| 8d | Scratch budget / per-job cap defaults | Budget Auto = 80 % of free; per-job cap = 2 × largest planned source (min 20 GB); files that don't fit are flagged, optional per-run in-place |
| 8e | Copy-back verification level | Size + ffprobe (default); full SHA-256 read-back as an option |
| 8f | NAS-offline policy | Keep encoding locally, pause new copy-ins, retry up to 6 h, then fail (scratch kept for retry) |
| 8g | Synology NFS sync vs async export, `hard` vs `soft` mount | Sync export, `hard` mount + stall watchdog |
| 8h | Network speed to the NAS (1 GbE / 2.5 / 10)? | Measure with the NAS self-test; sets `MaxConcurrentTransfers` and the GPU-parallelism advice |
| 8i | Stale scratch window | 6 h (marker-dirs with no matching job); never outside `ScratchDir` |
| 8j | Does the service need the system-libva environment? | ✅ **Yes on this B60** — `FfmpegEnvironment` auto-detect + manual override + self-test (§5A.2) |
| 8k | Service user: `convertr` or reuse `media-server`? | dedicated `convertr` (member of `render`) |
| 9 | Network exposure: LAN only, or also remote? | LAN only, plain HTTP; never exposed to the internet |
| 10 | Web port | 8484 |
| 11 | Auth: single admin password, or SSO later? | Single password in v1 |
| 12 | Hand over `.deb` or script install? | Script in S5, `.deb` as a stretch |
| 13 | Should the Windows `MainForm` be migrated onto `Convertr.App` over time? | Yes, gradually, no deadline |
| 14 | Keep creation-time preservation as "Windows-only" in docs? | Yes |
| 15 | HDR/Dolby Vision sources on GPU | Skip (or CPU-only) in v1 |
| 16 | Subtitle/audio handling for MP4 vs MKV on the server | Same rules as Windows (MKV recommended default on server) |
| 17 | Where to keep both repos on the server for building (or only build on the box and ship a tarball)? | Build on the box, ship tarball + hash to the owner |
| 18 | Schedule windows (e.g. only convert at night)? | Not in v1; "pause/resume" only; schedule is a nice S5+ idea |

---

## 12. Risks
- **B60 stack maturity** (new Battlemage Pro driver/ffmpeg paths) — mitigated by real test encodes, VAAPI default, CPU fallback, and S3 loops with the owner's server.
- **Hardware encoder size penalty** may make GPU unattractive for savings — §5B measured it: at equal VMAF the Arc files are about 20 % bigger than CPU x265 (not smaller), in exchange for about 20x the speed; CPU stays available as the smallest-files mode.
- **Linux file-system semantics** (case, NFS permissions/squash, atomic moves) — mitigated by same-share design, `PathCompare`, the NAS self-test and safety tests.
- **NAS availability and throughput** (offline mid-job, link as bottleneck, scratch exhaustion) — mitigated by the scratch pipeline, circuit breaker, reservation ledger, transfer slots and the fault-injection test matrix (§7A).
- **Added complexity of staging** (more states, more crash windows) — mitigated by journaling every step, small shared state machine, in-place mode as a fallback, and reconciliation tests.
- **Drift between apps** — mitigated by single Core, shared tests, `PARITY.md`.
- **LAN attack surface** of a service that can rewrite media — mitigated by mandatory auth, setup token, CSRF, confinement, localhost default.
- **Scope creep** (UI polish, scheduling, Docker) — deferred beyond S5.

---

## 13. Out of this document
No code is written by this design. Next step on approval: S0 (in the Convertr repo, additive + tested), then S1.

---

## 14. Implementation status

### S0 — done (2026-10-18, Convertr 0.3.0 build 10)
- **Convertr repo (shared Core):** `Platform.cs` / `PlatformLinux.cs` (`IPlatformServices`, `IAppPaths`, `IProcessController`, `IFileTimes`, `IPlatformHardware`, `PathCompare`, `IFfmpegEnvironment`), `JobStorage.cs` (`IJobStorage`, `InPlaceStorage` = the unchanged `Swapper`), additive `Settings` fields (`GpuBackend`, `GpuCodec`, `FfmpegEnvironment`, `FfmpegEnvironmentAuto`, `UseScratch`, `ScratchFolder`, `ScratchBudgetGb`, `MaxScratchPerJobGb`, `MaxConcurrentTransfers`, `CopyBackVerify`, `StopEncodeEarly`, `KeepScratchOnFailure`, `NasOfflineRetryHours`, `StaleScratchHours`). Windows implementations are moves of the old inline code. Tests: 377 (366 existing + 11 new, incl. the golden plan dump); the golden dump (3 140 files: plan, savings estimates, CPU and GPU command lines) is **byte-identical before and after**.
- **This repo:** `Convertr_svc.sln`, `Directory.Build.props` (sibling Core by relative path, `CONVERTR_CORE_DIR` override, **the build fails loudly** if Core is missing), `src/Convertr.Svc` (placeholder entry point, binary `convertr-svc`), `tests/Convertr.Svc.Tests` (3 smoke tests), `scripts/test-core.sh` (runs the shared Core suite + the svc tests on Linux). No web host yet (S1).
- **Not done in S0 (by design):** Linux hardware *encoder* detection and the VAAPI/QSV command-builder branches (S3), the ffmpeg-environment *auto-detection* (S3; only the hook and the setting exist), `ScratchStorage` (S3), any Setup screen for the new settings (S4), `Convertr.App` (grows with S1/S2).

### S1 + S2 — done (2026-10-18, version 0.2.0)
- **S1:** `Server/` (`ServerOptions`, `ServerHost`, `Pipeline`, `Api`, `AuthService`, `RootGuard`, `EventHub`, `StaticUi`), `UseSystemd`, config file + env + CLI, first-run setup token (hashed in `auth.json`, 24 h), PBKDF2-SHA256 password, signed session cookie + per-session CSRF token + same-origin check, login/setup rate limiting, security headers/CSP, `/healthz`, `/api/version`, `--reset-password`.
- **S2:** `ScanService` (Core `Scanner`, `ProbeCache`, `Planner`, `PlanSummary`, `PlanCsv`), SSE progress, plan paging/sort/filter/search/selection/export API, settings API (allow-listed fields only), embedded static UI (`wwwroot`: login/setup, Media folder + Browse, Scan, virtualised preview table, Setup page, theme).
- **Only change to the shared Core:** `Scanner` does not enter symlinked folders on Linux (Windows unchanged); golden output identical; Core tests 378.
- **Differences from this design, on purpose:** (a) the password hash is `Rfc2898DeriveBytes.Pbkdf2` with its own versioned format instead of ASP.NET `PasswordHasher` (avoids the Identity package; same algorithm class); (b) sessions are stateless signed cookies plus an epoch counter in `auth.json` (password change/reset ends every session; logout is remembered until restart); (c) the ffmpeg folder, ffmpeg environment, scratch and GPU settings are read-only in the web UI until S3 to S5 (prevents arbitrary-executable / `LD_PRELOAD`-style settings from a web form); (d) rows with nothing to convert cannot be selected; (e) the S2 UI has no "Already efficient" chip (it is part of Skip) and no hardware badge yet.
- **Known gaps / risks recorded for S3:** the service is plain HTTP on the LAN, so the session cookie is not `Secure`-flagged (by design: LAN only, not internet-facing); `_Convertr_Originals` is created as a *sibling of the chosen Media folder* (see the correction below), so S3 must refuse a Media folder that is an `AllowedRoot` itself unless its parent is also writable and on the same mount.

### Correction to section 7.1 found while implementing S0
The original-holding folder is **not** inside the media folder: `HoldingArea.Root` puts `_Convertr_Originals` as a *sibling of the chosen Media folder* (`<parent>/_Convertr_Originals/<media leaf>/...`). On the NAS this is on the same share only when the Media folder is a **sub-folder** of the share; if the Media folder *is* the share root the code falls back to a folder inside it. Rule for the service: choose a Media folder below the share root (for example `/mnt/nas/media/Movies`), and the S3 mount guard must verify that the holding folder and the media folder are on the same mount (`stat` device id) before any rename.



### 0.3.1 — Intel defaults from the benchmark (2026-10-18)
- Intel defaults set from §5B: VAAPI HEVC q23 (`IntelCq`), new `IntelQsvCq` 21, AV1 27 marked untested. `SavingsModel` has an Intel estimate (shared Core, additive; Windows estimates identical). Web UI: encoder help text, labels, an estimate note under the summary cards, web icon. The service treats a stored NVENC/Auto backend as VAAPI (the service has no NVIDIA path) until the hardware test picks VAAPI or QSV. 65 service tests + 416 Core tests green.

### S3 — done (2026-10-18, version 0.3.0)
- **Built:** `ScratchStorage` + `IJobStorage` hooks, `MountGuard`, `IntelProbe`, Intel/VAAPI/QSV/AV1 branches in `FfmpegCommandBuilder`, `JobService` (queue persistence, run control, undo), `HardwareService`, API (`/api/convert/*`, `/api/queue/*`, `/api/undo`, `/api/hardware*`), SSE events (`queue`, `job`, `progress`, `undo`, `hardware`), Queue tab, confirmation dialog, Setup (encoder/scratch, hardware).
- **Differences from this design, on purpose / not yet:** (a) restart reconciliation is simple: unfinished jobs go back to Pending and are copied in again (scratch copies are deleted at the next run), the swap itself is recovered by the existing journal; (b) NAS-offline handling stops the run and keeps jobs waiting, with no timed auto-retry yet; (c) `tools/nas-selftest.sh` and `tools/intel-selftest.sh` are not written: `IntelProbe` (Setup > Hardware) does the Intel test encodes and `tools/intel-bench.sh` does the quality/speed sweep; (d) fault injection is by real temp directories (offline library, no space, cap, unmarked folder, changed source, recovery), not an `IFileOps` shim; (e) Intel quality defaults were placeholders in 0.3.0; 0.3.1 sets them from the bench results (`IntelCq` 23 confirmed, `IntelQsvCq` 21 added; `IntelAv1Cq` 27 is still an untested placeholder).

### 0.3.4 — Network storage (NAS) on the Setup page (2026-10-18)
- **Built:** section 7B: `NasService` (+ `INasEnvironment`), `/api/nas*`, `wwwroot/js/nas.js`, Setup panel "Network storage (NAS)". Show-only root commands with Copy buttons, `showmount` listing, "Check mount", AllowedRoots status with the exact commands. Service tests 215 (78 before).
