# Convertr_svc

Linux / web edition of **Convertr** — a media converter that runs as a systemd service
(`convertr-svc.service`) on an Ubuntu server and is operated from a web page.
It shrinks a video library with HEVC (or optionally AV1), using the server's Intel Arc Pro B60
(VAAPI/QSV) or CPU x265, with the same safety model as Convertr: temp part file, verify,
keep only if smaller, originals moved to `_Convertr_Originals`, journaled undo, never deletes originals.

**Status: 0.3.4 — S0 to S3 done, Intel defaults from the benchmark, NAS mount helper on the Setup page.** The service signs you in, browses the allowed media folders, scans them, shows the plan, and **converts the files you select** (CPU x265, or Intel Arc via VAAPI/QSV) through a local scratch disk, with queue/progress, pause/stop, and undo. Real Arc and NAS behaviour still needs testing on the server (see the CHANGELOG). **How to use it: [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md).** See [`docs/DESIGN.md`](docs/DESIGN.md), [`docs/PARITY.md`](docs/PARITY.md), [`CHANGELOG.md`](CHANGELOG.md).

The web UI carries the same icon as the Windows app (tab icon, home-screen icon, header and sign-in logo).

**Encoder choice (measured on the Intel Arc Pro B60, 5 movies, 30 s clips, VMAF):** the default is **Intel VAAPI HEVC, quality 23**: about 13x faster than real time and files about 30 % of the source size (VMAF 95.7, very close to the source). **QSV quality 21** gives the same quality and size, slightly slower. **CPU x265** gives files about 20 % smaller at the same quality but runs at about 0.6x real time (roughly 3 hours per movie), so it is the optional "smallest files, very slow" mode. AV1 is untested. Details: [`docs/DESIGN.md`](docs/DESIGN.md) section 5B and [`docs/INTEL_BENCH.md`](docs/INTEL_BENCH.md).

## Getting the source
Convertr_svc builds against the shared engine `Convertr.Core` from the [Convertr](https://github.com/JeepNWilly/Convertr) repository, included here as a git submodule in `external/Convertr`:
```bash
git clone --recursive https://github.com/JeepNWilly/Convertr_svc.git
# already cloned without --recursive:  git submodule update --init
```
The build looks for `Convertr.Core` in this order: the `CONVERTR_CORE_DIR` environment variable, the `external/Convertr` submodule, then a sibling checkout (`../Convertr/source`, the Windows layout, or `../Convertr`).

## Try it (development, any Linux with the .NET 10 SDK and ffmpeg)
```bash
dotnet build Convertr_svc.sln
scripts/test-core.sh                          # shared Convertr.Core tests + Convertr_svc tests
dotnet run --project src/Convertr.Svc -- --data-dir /tmp/convertr-data --allowed-root /path/to/media --port 8484
# the log prints:  FIRST-RUN SETUP: ... enter this one-time setup token ...: <token>
# open http://127.0.0.1:8484/ , enter the token, choose a password (10+ characters), Browse, Scan.
dotnet test tests/Convertr.Svc.Tests          # Convertr_svc tests (auth, CSRF, confinement, scan, convert, NAS, config)
```
`CONVERTR_WWWROOT=src/Convertr.Svc/wwwroot` serves the web UI from disk while editing it. Forgot the password: `convertr-svc --data-dir ... --reset-password` (prints a new setup token; works while the service runs).

## Built and tested on
- Ubuntu 24.04.5 LTS, kernel 7.0.0-38-generic
- Intel Arc Pro B60 GPU with the `xe` kernel driver
- Intel graphics userspace from the `kobuk-team/intel-graphics` PPA (intel-media-va-driver-non-free 26.3.2, libva 2.24)
- jellyfin-ffmpeg 7.1.4
- .NET 10 (SDK 10.0.402, runtime 10.0.13; the release is published self-contained, so the server needs no .NET install)

Other distributions and GPUs may work but are untested.

## Install on the server (Ubuntu, systemd)
Build the release with `scripts/publish.sh` (writes `dist/convertr-svc-<version>-linux-x64.tar.gz` and its SHA-256), copy it to the server, then:
```bash
tar xzf convertr-svc-<version>-linux-x64.tar.gz && cd convertr-svc-<version>-linux-x64
sudo ./scripts/install.sh --media-root /mnt/nas/media   # add --dry-run first to see what it would do
journalctl -u convertr-svc | grep "FIRST-RUN SETUP"      # the one-time setup token
```
It listens on `127.0.0.1:8484` only. The service is plain HTTP and is meant for the home LAN only: to reach it from other computers on the LAN, set `Listen` in `/etc/convertr-svc/appsettings.json` to the server's LAN IP address (or pass `--listen <LAN IP>` to the installer) and restart the service. **Do not expose it to the internet** (no port forwarding). `scripts/uninstall.sh [--purge]` removes it again (keeps data unless `--purge`).

- Media lives on a NAS, for example a Synology (NFS recommended, SMB supported; **Setup > Network storage (NAS)** generates the exact fstab line and one-time `sudo` commands, lists the NAS shares and checks the mount, see DESIGN.md 7B); each job is staged through a **local scratch disk** (copy in → encode → verify → keep-only-if-smaller → copy back → atomic swap).
- Shares one engine (`Convertr.Core`) with the Windows app Convertr (through the `external/Convertr` submodule, see Getting the source).
- Layout: `src/Convertr.Svc` (host, `Server/` services, `wwwroot/` web UI), `tests/Convertr.Svc.Tests`, `deploy/` (unit file, example config), `scripts/` (`install.sh`, `uninstall.sh`, `publish.sh`, `test-core.sh`), `docs/`.
- Build/test: check out the `external/Convertr` submodule (or set `CONVERTR_CORE_DIR` to the folder with `Convertr.Core.csproj`), then `dotnet build Convertr_svc.sln` and `scripts/test-core.sh`. The build fails with a clear message if Core is missing.
- Service user `convertr`, install `/opt/convertr-svc`, state `/var/lib/convertr-svc`, config `/etc/convertr-svc`.

## Troubleshooting

**"Intel GPU encoder test failed" in the service, but ffmpeg works for the same user on the command line.** The service runs under systemd's device policy, which only allows the devices the unit lists. Version 0.3.1 and earlier had `DeviceAllow=/dev/dri rw`, which systemd does not apply to the nodes inside the folder (`/dev/dri/renderD128`); 0.3.2 uses `DeviceAllow=char-drm rw` (the whole DRM device class) with `DevicePolicy=closed` (`/dev/null`, `/dev/zero`, `/dev/urandom`, ttys stay available). `scripts/install.sh` copies the unit file again on every run and calls `systemctl daemon-reload`, so upgrading the package is the fix; restart is done by the installer. Check what the running unit really contains with `systemctl cat convertr-svc` (look for `char-drm`).

The Setup page (Hardware) and `journalctl -u convertr-svc | grep -i "Intel GPU encoder test"` now show ffmpeg's own error text (first 5 lines of its stderr, per failed encoder). Typical causes: the service user is not in the `render` group (`id convertr`), the unit still has the old `DeviceAllow`, or the driver is missing (`vainfo`).

**Where is the log of a conversion or dry run?** In the web page: **Queue** tab > **Log** (click the word "Log" under the queue table to open it). It shows the newest run log live, including the exact ffmpeg command of every file in a dry run, and the file's path. The same text is a plain file `/var/lib/convertr-svc/logs/Convertr_<date>_<time>.log` (kept across restarts and upgrades) and is written to the journal at Info level: `sudo journalctl -u convertr-svc -n 100 --no-pager` shows it (follow live with `-f`).

**`Unknown key name 'RequiresMountsFor' in section 'Service'` in the journal.** Versions 0.3.0 to 0.3.2 wrote that line into the `[Service]` section of `/etc/systemd/system/convertr-svc.service.d/paths.conf`; it belongs in `[Unit]`. Harmless (systemd ignored it) but noisy. 0.3.3 writes it correctly and repairs an old file on upgrade, also when you run the installer without `--media-root`.

**Changing the unit without editing the packaged file.** A local drop-in survives upgrades (the installer only rewrites `convertr-svc.service` and `paths.conf` in that folder):
```bash
sudo systemctl edit convertr-svc      # creates /etc/systemd/system/convertr-svc.service.d/override.conf
```
```ini
[Service]
# a second GPU or an exact node instead of the whole class:
DeviceAllow=/dev/dri/renderD129 rw
# as a last resort, remove the device restrictions (empty value resets the list first):
#DevicePolicy=auto
#DeviceAllow=
```
then `sudo systemctl daemon-reload && sudo systemctl restart convertr-svc`.

## License

Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU General Public License, version 3 or (at your option) any later version. It comes with no warranty. See [LICENSE](LICENSE).
