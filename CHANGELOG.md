# Changelog

All notable changes to Convertr_svc. Dates are America/New_York. The shared engine (`Convertr.Core`) is versioned with the Windows app (see its CHANGELOG).

## Unreleased

### Docs
- Docs: new "Built and tested on" section in the README and the user guide (Ubuntu 24.04.5 LTS, kernel 7.0.0-38-generic, Intel Arc Pro B60 with `xe`, Intel PPA userspace with intel-media-va-driver-non-free 26.3.2 and libva 2.24, jellyfin-ffmpeg 7.1.4, .NET 10); other distributions untested.
- `scripts/install.sh`: header comment no longer calls the installer an untested draft.
- Docs: user guide. New `docs/USER_GUIDE.md` (opening the page, first run, Setup, NAS, Hardware, Scan, Convert, Queue and Log, Undo, logs, updating, troubleshooting); linked from the README.

## 0.3.4 - 2026-10-18 - Network storage (NAS) setup on the Setup page

### Added
- **Setup > Network storage (NAS).** Mount the Synology share without giving the service root rights (DESIGN.md section 7B):
  - Enter the NAS host (name or IPv4) and **List shares on the NAS** (`showmount -e`, no shell, 12 s timeout). Each export shows its allowed clients and whether this server is in the list. Without `nfs-common` the page says so and shows `sudo apt install -y nfs-common`.
  - **Show the commands** validates the input, saves the form (`nas.json` in the data dir) and shows the exact `/etc/fstab` line (`vers=3,rw,hard,_netdev,nofail,x-systemd.automount,x-systemd.idle-timeout=0`, or 4.1, or read-only) and the one-time `sudo` commands in order (install client, check export, mkdir, idempotent fstab append, daemon-reload, mount, write test) with **Copy** buttons and **Copy all commands**, plus the DSM steps (with this server's address). The service never runs them.
  - **Check mount** (from inside the service): is the mount point a real mounted share (never writes into a plain folder), NFS type/version/rw/hard-soft, size and free space, write with read-back, rename, and whether the originals folder can be created next to the media folder on the same share (cross-folder rename), each result in plain words with the fix. The test files are hidden and removed again; `_Convertr_Originals` itself is not created.
  - **Allowed folders:** shows whether the media folder is inside AllowedRoots (`inside`, listed-but-not-loaded, or outside). AllowedRoots stays **not editable from the web** (a safety rule); when needed the page prints the exact `sudo sed` command, a `grep` to verify, and the `install.sh --media-root` line (it repeats every folder already allowed, because the installer rewrites `paths.conf`). It also reads `paths.conf` to say whether the systemd `ReadWritePaths` cover the folder.
- API: `GET /api/nas`, `POST /api/nas/exports`, `POST /api/nas/commands`, `POST /api/nas/check` (login and CSRF as everywhere). New optional settings in `appsettings.json`: `NasMountParents` (default `/mnt`, `/media`, `/srv`) and `PathsDropInPath` (default the installer's `paths.conf`).

### Security
- Host, share and mount point are validated (host name or canonical IPv4; share and mount point `[A-Za-z0-9._@+-]` segments only, no `..`, no spaces; mount point only below `/mnt`, `/media`, `/srv`); NFS version is 3 or 4.1. Nothing else reaches a command or the fstab line. The only program the service starts is `showmount`, with arguments passed one by one.

### Tests
- Service 215 (+137): validation (injection attempts for host, share, mount point, version), exact fstab line and command list, the fstab command run twice against a temp file (one line), the `sed` command run against empty and filled `appsettings.json` samples (valid JSON), `bash -n` of the script, showmount parsing from the real Synology output and client matching (CIDR, `*`), mount table parsing (automount under NFS, ro, soft, escaped spaces), no-shell argument passing, every endpoint needs login and CSRF, the check against a fake NFS mount (all steps ok, plain folder, autofs, ro, soft, cifs, media folder problems, top-of-share originals, permission denied, NAS that does not answer, no leftovers), AllowedRoots states, page and script wiring.

## 0.3.3 - 2026-10-18 - Log panel on the Queue page, installer drop-in fix

### Added
- **Queue > Log panel (web UI).** Under the queue table, a collapsible **Log** shows the newest run log of the server: the exact ffmpeg command of every file in a **dry run** (`[dry run] <file>: <action>: ffmpeg ...`), and for a real run the per-file lines (`start`, `command`, `scratch: copying in / back`, `original moved`, `done ... MB -> ... MB (n% saved)`, `FAILED`, `not worth it`, `run finished`). New lines arrive live through the existing event stream (new SSE event `log`; the page keeps the last 1000 lines); it reloads when opened and when a run ends. Before this, the dry-run commands were only in the file on the server and not visible anywhere in the UI.
- `GET /api/queue/log?lines=N` (login required; default 300, at most 2000) returns `{file, path, logsDir, lines[]}` for the newest `Convertr_*.log`.
- The same lines are now written to the journal at **Info** level (category `Convertr.Svc.Server.JobService`), so `journalctl -u convertr-svc` shows the dry-run commands and per-job results (the live encode percentage is only in the progress bar, it is not logged).
- Logs stay in `/var/lib/convertr-svc/logs/` (`Convertr_<date>_<time>.log`, plus the undo journals `undo_*.csv`); nothing changed there, the panel only reads them. Shared engine (`Convertr.Core`) unchanged: the service hooks the existing `RunLog.Echo`.

### Fixed
- **`Unknown key name 'RequiresMountsFor' in section 'Service', ignoring` in the journal.** `scripts/install.sh` wrote `RequiresMountsFor=` under `[Service]` of `/etc/systemd/system/convertr-svc.service.d/paths.conf`. It is a `[Unit]` setting. The drop-in is now `[Unit] RequiresMountsFor=...` plus `[Service] ReadWritePaths=...`. Running the installer again rewrites the file; an old wrong file is also repaired when the installer is run without `--media-root` (the folders already in it are kept). `docs/DESIGN.md` unit sketch corrected.

### Tests
- Service 78 (+7): log endpoint (login needed, empty before the first run, tail of the newest file and clamping), dry run end to end with real ffmpeg (file under `DataDir/logs`, endpoint, SSE `log` events, journal Info), page and script wiring, installer drop-in layout, old-file detection, installer uses the helper.

## 0.3.2 - 2026-10-18 - GPU access inside the systemd unit, readable Intel test errors

### Fixed
- **The Intel GPU test failed inside the service although the same user could use the GPU outside it.** The unit had `DeviceAllow=/dev/dri rw`; systemd does not apply that to the nodes inside the folder, so `/dev/dri/renderD128` stayed blocked. It is now `DeviceAllow=char-drm rw` (the DRM device class, covers `card*` and `renderD*`), with `DevicePolicy=closed` made explicit (the pseudo devices `/dev/null`, `zero`, `full`, `random`, `urandom`, `tty` and the pty devices remain allowed; `PrivateDevices=false` is unchanged). `scripts/install.sh` already copies the unit on every run and reloads systemd, so installing this package upgrades the unit; a test now checks both.
- **Web UI: the scan progress bar and "Looking for video files..." stayed on screen after a scan finished.** A very fast scan (for example 5 files in 0.0 s) completed before the answer to "Scan" arrived; that answer was an older "running" snapshot and was drawn after the live "done" event, which brought the bar back with nothing left to hide it. Scan snapshots now carry an increasing `seq` and the page ignores any snapshot older than the one it already showed, so done, cancelled and error states always hide the bar and the text.

### Changed
- **The Intel encoder test now says why it failed.** When no Intel hardware HEVC encoder works, the Setup > Hardware "Problem" text and the test log contain the first 5 lines of ffmpeg's stderr for each failed encoder (before, only one line per attempt was kept and often nothing was visible), and the service writes the same text to the journal at **Warning** level ("Intel GPU encoder test failed ..."). The shared engine (`Convertr.Core`) is unchanged; the capture is done in the service.
- README: new Troubleshooting section (GPU access, how to check the running unit with `systemctl cat`, and a local `systemctl edit` drop-in for device overrides that survives upgrades).
- Tests: service 71 (+4: stderr head shown in the problem text and log, line cut, unit allows `char-drm` and not the folder, installer refreshes the unit); 67 before. `systemd-analyze` is not available on the build box, so the unit was checked by reading it against `systemd.resource-control(5)`.

## 0.3.1 - 2026-10-18 - Intel defaults from the Arc Pro B60 benchmark, web icon
Measured with `tools/intel-bench.sh` 1.1 `--matched` on `media-server` (5 movies, 30 s clips, VMAF). Full table in `docs/DESIGN.md` section 5B.

### Benchmark result (and a correction)
- **At the same VMAF the CPU x265 files are about 20 % smaller than the Arc files; the Arc is about 20x faster** (VAAPI q23: 29.6 % of the source, VMAF 95.66, 13.1x; CPU CRF 21: 36.4 %, VMAF 96.97, 0.6x; CPU at the Arc's quality would be about 24.7 %).
- Earlier notes (the quick SSIM run) suggested the Arc files were smaller than the CPU files. That was wrong: those Arc settings were of lower quality than CPU CRF 21. Corrected in DESIGN.md and INTEL_BENCH.md.

### Changed
- **Intel defaults:** default encoder is **VAAPI HEVC ICQ q23** (`IntelCq` 23, confirmed). **QSV** stays selectable and now has its own quality `IntelQsvCq` **21** (same quality and size as VAAPI q23; before, QSV would have used 23, which is a visibly lower quality). **CPU x265** stays the optional "smallest files, very slow" mode. **AV1** (`IntelAv1Cq` 27) is still an untested placeholder and the Setup label says so.
- **Savings estimate:** the estimate for VAAPI/QSV is now an Intel model (the CPU model times the measured Arc/CPU size ratio, VAAPI 0.813 and QSV 0.832, scaled 0.74 per 3 quality steps) instead of the NVENC numbers. A note under the summary cards says what the estimate is based on ("measured on 5 movies, 30 s clips"; AV1: "NOT measured"). Windows (NVENC) and CPU estimates are unchanged.
- The service treats a stored NVENC or Auto GPU backend as VAAPI (it has no NVIDIA path), so a fresh or upgraded install estimates and converts with the Arc even before the hardware test has run.
- Setup: encoder help text (what each choice costs), clearer labels for Encoder, GPU backend, codec and the three quality values; `IntelQsvCq` is editable (range 10 to 45).
- Shared engine (additive, Windows unchanged, golden output identical): `Settings.IntelQsvCq`, `PlannerOptions` carries `GpuBackend`, `GpuCodec`, `IntelCq`, `IntelQsvCq`, `SavingsModel.IntelOutputKbps` / `UsesIntelModel`.
- Tests: Core 416 (+5: defaults, QSV own quality, Intel model ratios, Intel model only for VAAPI/QSV, quality follows the setting), service 65 (+2: defaults and Nvenc shown as Vaapi, estimate note).

### Web UI icon
- The web UI uses the same icon as the Windows Convertr: browser tab (`/favicon.ico`, `/img/favicon-32.png`), home-screen (`/img/apple-touch-icon.png`, 180 px) and the logo beside the title in the header and on the sign-in / first-run pages (`/img/logo-128.png`). The images are made from the Windows app's `Convertr.ico`, embedded in the binary and served from the same origin (no external requests; the strict CSP is unchanged).

## 0.3.1 (tool) - tools/intel-bench.sh 1.1
- `intel-bench.sh` finds sources up to 3 folder levels deep; scores with VMAF when an ffmpeg with libvmaf is found (`VMAF_FFMPEG` / `--vmaf-ffmpeg` / `~/ffmpeg-vmaf/ffmpeg`), SSIM otherwise (VMAF rows also record SSIM); new `--matched` mode (CPU CRF 21/22/24/26, VAAPI q20/23/26/29, QSV q21/24/27, keeps clips in `encoded/`) with a quality-matching summary block; SSIM-scored rows are redone when VMAF is available. See `docs/INTEL_BENCH.md`.

## 0.3.0 - 2026-10-18 - S3 conversion queue, Intel GPU backends, scratch pipeline
The service can now convert. Nothing is ever deleted: a verified, smaller result replaces the original, which moves to `_Convertr_Originals` (undoable). A result that is not smaller is discarded and the original is not touched.

### Added
- **Convert selected...** with a confirmation dialog: files, source and expected size, encoder, storage, **scratch needed vs free / budget**, originals handling, warnings and blockers (nothing selected, ffmpeg missing, scratch folder unusable, scratch disk too small, originals folder not writable, Intel GPU not working), optional **dry run** (logs the exact ffmpeg commands, changes nothing).
- **Queue tab** (Server-Sent Events): per-file state (waiting, copying to scratch, encoding, verifying, copying back, swapping in, done, kept original, failed, skipped), copy MB/s, encode speed and ETA, overall progress, **Pause / Resume** (SIGSTOP/SIGCONT), **Stop after this file**, **Stop now** (kill ffmpeg, delete temporaries, originals untouched), remove a waiting file, **Resume queue** after a restart (the queue is persisted in `queue.json` after every step; nothing starts by itself), Discard queue.
- **Undo list** (shared `UndoService`): put the original back (old timestamps), the converted file is kept in `_Convertr_Undone`.
- **Scratch pipeline (`ScratchStorage`, DESIGN 7A):** copy the source to the local scratch disk, verify size and that the library file did not change meanwhile, encode only on scratch, verify (probe + decode test), **decide keep-only-if-smaller BEFORE anything goes back**, copy the result to the library as `<name>.convertr-part` (size, optional SHA-256 and probe check), then the existing journaled swap: original to `_Convertr_Originals`, atomic rename, timestamps. Scratch budget / per-file cap / admission wait, transfer slots, marker file (`.convertr-scratch`; a non-empty unmarked folder is refused), stale `job-*` cleanup at the start of a run, `KeepScratchOnFailure` (`kept-*` folders, removed after `StaleScratchHours`), library-offline detection (job stays waiting, run stops, nothing changed), **same-mount guard** (originals folder must be on the library's mount, from `/proc/self/mountinfo`), originals-folder writability check.
- **Intel backends** in the shared command builder: `hevc_vaapi` (ICQ, default), `hevc_qsv` (ICQ + look-ahead, preset veryslow), AV1 (`av1_vaapi` / `av1_qsv`, opt-in via GPU codec); hardware upload chain with CPU deinterlace first, 10-bit as `p010le` + `main10`, SD sources one quality step lower, legacy H.264 targets stay on the CPU, rate-control options replaceable by what the encoder test found (`GpuEncoderArgs`).
- **Hardware detection** (Setup > Hardware, `POST /api/hardware/detect`): CPU, GPUs, ffmpeg, then Intel test encodes per backend/codec with candidate option sets; **ffmpeg environment auto-detect**: bundled libraries first, on a crash (Bus error / exit 135 / 139 ...) the system libva/iHD environment is used and remembered; the same fallback runs if ffmpeg crashes during a real encode (the file is encoded again once).
- Settings now editable in the UI: scratch (on/off, folder, budget, per-file cap, transfers, verify mode, keep on failure), GPU backend / codec / device, Intel and NVENC quality, CPU CRF. Scratch folder must be absolute, outside the media folders; GPU device must be `/dev/dri/renderDNNN`.
- Tests: Core (+33): Intel command lines, Intel probe (crash fallback, candidates, permission), scratch pipeline (happy path, not smaller = nothing copied back, failure cleanup, keep-on-failure, no space, per-file cap, library offline, unmarked folder refused, stale cleanup, checksum, source changed, recovery, mount guard); Service (+6): settings validation, auth/CSRF on the new endpoints, preview blockers, **end-to-end with real ffmpeg** (scan, select, dry run, convert on scratch, original held, timestamps, undo), stop/pause/discard, hardware detection without a GPU.

### Changed in the shared engine (`Convertr.Core`)
- `FfmpegCommandBuilder` (Intel branch only when `GpuBackend` is Qsv/Vaapi), `IJobStorage` (default-implemented `StageAsync` / `CopyBackAsync` / `Release` / `CleanStale`; `InPlaceStorage` unchanged), `ConversionEngine` (scratch hooks, `CopyingIn`/`CopyingBack`, library-offline check only with scratch), new `ScratchStorage`, `IntelProbe`, settings `GpuDevice`, `IntelCq`, `IntelAv1Cq`, `GpuEncoderArgs`, `HardwareProfile.Intel`. **Windows output is unchanged**: golden plan hash `A58372B7...A455` identical, all Windows tests green.

### Not done / needs the real server
- Real Intel Arc encodes, real NAS behaviour (ownership, rename, mtime) and `tools/nas-selftest.sh` are untested on the build box (no GPU, no NAS). Use `tools/intel-bench.sh` first to calibrate `IntelCq` / `IntelAv1Cq`.
- Resume after a restart re-copies the file (partial scratch copies are not reused). The fault-injection suite covers the main phases, not a kill at every single step.
- Offline NAS: the run stops and keeps the files waiting; the automatic retry window (`NasOfflineRetryHours`) is not wired yet.

## 0.2.0 - 2026-10-18 - S1 server skeleton + auth, S2 scan / preview web UI
First runnable service. **It only reads**: it can browse folders, scan and show a plan. Converting arrives in S3.

### Added
- ASP.NET Core minimal API host with systemd integration (`Type=notify`, journald logging); listens on `127.0.0.1:8484` by default.
- Configuration: `/etc/convertr-svc/appsettings.json` (section `Convertr`: `Listen`, `Port`, `AllowedRoots`, `DataDir`), overridden by environment (`CONVERTR_CONFIG_DIR`, `CONVERTR_DATA_DIR`, `CONVERTR_LISTEN`, `CONVERTR_PORT`, `CONVERTR_ALLOWED_ROOTS`) and the command line (`--config-dir`, `--data-dir`, `--listen`, `--port`, `--allowed-root`). Data in `/var/lib/convertr-svc`.
- Authentication: one admin password (PBKDF2-SHA256, 600,000 iterations, per-password salt, minimum 10 characters), first-run one-time setup token printed to the log (24 h, new one at every start while no password exists), signed `HttpOnly; SameSite=Strict` session cookie (12 h, sliding), per-session CSRF token (`X-CSRF-Token`) on every state-changing request, same-origin (`Origin` / `Sec-Fetch-Site`) check, login and setup rate limiting with exponential back-off (5 free tries, then 2, 4, 8 ... up to 15 min; plus a global limit), logout, change password, `convertr-svc --reset-password` (works while the service runs; ends all sessions).
- Security headers on every response: CSP (`default-src 'self'`, no inline script or style, `frame-ancestors 'none'`), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Cache-Control: no-store` on `/api`.
- Folder confinement (`AllowedRoots`): absolute paths only, `..` refused, every path resolved through all symlinks (loops refused) and required to lie inside a root; sibling-prefix names (`/media2` vs `/media`) refused; server-side folder browser lists directories only and hides links that lead out of the roots, dot-folders, `@eaDir`, `#recycle`, `_Convertr_Originals`.
- Scan: reuses the Core scanner, probe cache and planner; progress by Server-Sent Events (`/api/events`); cancel; symlinks that leave the root are dropped from results.
- Plan API: virtual-scroll paging, sort, filter (all / convert / skip / attention), path search, row selection (starts empty; only rows that would write a file can be selected), summary, CSV export. A planning setting change re-plans the scan result.
- Settings API for the rules (thresholds, container, mode, keep-if-smaller, options); the ffmpeg folder, ffmpeg environment, scratch and GPU fields are deliberately read-only until S3 to S5.
- Static web UI (plain HTML + ES modules, embedded in the binary): sign-in / first-run setup, Media folder + Browse dialog, Scan with progress and cancel, summary cards, virtualised preview table (nothing preselected), filter chips, search, select / export, Setup page (rules, server info, change password), Dark / Light / System theme.
- `/healthz` (public, returns only `{"status":"ok"}`).
- Deployment: `deploy/convertr-svc.service`, `deploy/appsettings.example.json`, `scripts/install.sh` (draft), `scripts/uninstall.sh`, `scripts/publish.sh` (linux-x64 self-contained single file + tarball).
- Tests: 52 service tests (auth, CSRF, origin, rate limit, session expiry / tampering, symlink and `..` confinement, scan on an ffmpeg-generated tree, selection, SSE, config precedence, CLI).

### Changed in the shared engine (`Convertr.Core`)
- `Scanner` no longer enters symbolic links to folders on Linux/macOS (they could leave the media folder or loop). Windows behaviour is unchanged. New Core test `LinuxScannerTests`; the golden plan output is byte-identical.

## 0.1.0 - 2026-10-18 - S0 skeleton
Solution skeleton, `Directory.Build.props` (shared Core by relative path or `CONVERTR_CORE_DIR`, fails loudly when missing), smoke tests, Core platform abstraction (in the Windows repository).
