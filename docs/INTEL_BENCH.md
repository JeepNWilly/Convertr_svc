# Intel Arc B60 vs CPU x265 benchmark (`tools/intel-bench.sh`)

Answers: **for the same picture quality, how much smaller is CPU x265 than the Arc encoders, and how much faster is the Arc?**
It cuts a short clip from each test movie, encodes it with CPU x265 and with the Arc (HEVC through VAAPI and QSV, optionally AV1), and measures output size, encode speed and quality (VMAF) against the original clip.

It needs no root. It only **reads** the test media and only creates / deletes files inside its own output folder.

**Version 1.1:** finds test movies up to 3 folder levels deep (Plex layout `Movies/Title (Year)/Title.mkv`); scores with **VMAF** when it can find an ffmpeg with libvmaf (`VMAF_FFMPEG`, section 2), otherwise falls back to SSIM; new `--matched` mode that finds which Arc quality value gives the same quality as CPU x265 CRF 21, and keeps the encoded clips so you can look at them.

## 1. Copy it to the server (Windows, WinSCP)
1. On WINDOWS-PC take `C:\Projects\Convertr_svc\intel-bench.tar.gz` (version 1.1; unpacking over an older copy replaces it) (or `C:\Projects\Convertr_svc\tools\intel-bench.sh`).
2. WinSCP: connect to `media-server` as user `youruser`, drag the file into `/home/youruser/`. (Or: `scp C:\Projects\Convertr_svc\intel-bench.tar.gz youruser@media-server:~/`.)
3. In an SSH session:
   ```bash
   cd ~
   tar xzf intel-bench.tar.gz          # gives ~/intel-bench/intel-bench.sh  (skip this step if you copied the .sh alone)
   cd intel-bench
   chmod +x intel-bench.sh
   ./intel-bench.sh --help
   ```
   If WinSCP saved the file with Windows line endings (`bad interpreter: ^M`), run `sed -i 's/\r$//' intel-bench.sh` once.

## 2. Get VMAF (a second ffmpeg, used only for scoring)
The jellyfin-ffmpeg 7.1.4 on the server has **no libvmaf**, and neither has Ubuntu 24.04's own `ffmpeg` package (6.1.1: checked - `libavfilter9` does not depend on any vmaf library and no libvmaf package exists in noble). The practical way is a static GPL build that has it. **BtbN's FFmpeg-Builds** `linux64-gpl` build has libvmaf with the standard model built in, so no model files are needed (johnvansickle's static builds also have libvmaf, but its release needs separate model files and its git build on the site was last built in June 2024, so it is not recommended).

Nothing is installed system-wide: it goes into `~/ffmpeg-vmaf` and the benchmark finds it there by itself. Encoding stays on jellyfin-ffmpeg; this binary only measures.
```bash
mkdir -p ~/ffmpeg-vmaf && cd ~/ffmpeg-vmaf
wget -O ffmpeg.tar.xz https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz
wget -O checksums.sha256 https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256
grep ' ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz$' checksums.sha256 | awk '{print $1"  ffmpeg.tar.xz"}' | sha256sum -c -     # must print: ffmpeg.tar.xz: OK
tar xf ffmpeg.tar.xz --strip-components=2 --wildcards '*/bin/ffmpeg' '*/bin/ffprobe'
./ffmpeg -hide_banner -filters | grep libvmaf        # must print a line with "libvmaf"
```
(The `latest` release is rebuilt every day, so the checksum is taken from the file published next to it, not typed in. About 150 MB download, 345 MB unpacked.)

The benchmark looks for libvmaf in this order: `--vmaf-ffmpeg PATH` / environment `VMAF_FFMPEG`, then `~/ffmpeg-vmaf/ffmpeg`, then the encoding ffmpeg. The first one that has the `libvmaf` filter is used and the run header says which. If none has it the run falls back to SSIM + PSNR and says so (rows are marked `ssim`).
```bash
VMAF_FFMPEG=/some/other/ffmpeg ./intel-bench.sh --quick      # point it somewhere else
```
Every VMAF row also stores SSIM (column `ssim` of `results.csv`), so the numbers of the earlier SSIM run stay comparable.

The VMAF model is `vmaf_v0.6.1` (the standard one, tuned for a 1080p screen). Clips are compared at their own resolution; for 4K sources the number is a little harsh, which does not matter when comparing encoders against each other.

**Rows scored with SSIM earlier are not reused when you now score with VMAF:** those variants are encoded and scored again (the old rows stay in `results.csv`, the newest row wins in the tables).

## 3. Run it
Run inside `tmux` or `screen` (or with `nohup`) so a dropped SSH session does not matter; the script is **resumable** anyway - start it again and finished variants are skipped.
```bash
./intel-bench.sh --dry-run          # optional: shows the plan and exact commands, runs nothing
./intel-bench.sh --quick            # about 10 minutes: 30 s clips, CPU x265 + 3 GPU variants per movie
./intel-bench.sh --matched          # quality matching, see below: 30 s clips, 4 CPU + 7 GPU variants, keeps the clips; about 30-40 minutes
./intel-bench.sh                    # full run: 90 s clips, 2 CPU + 7 HEVC GPU variants + 2 optional AV1; plan about 1.5 hours
```
Defaults (all overridable, see `--help`): ffmpeg `/usr/lib/jellyfin-ffmpeg/ffmpeg`, media `/srv/testmedia/Movies`, output `/srv/convertr-bench`, render device = first `/dev/dri/renderD*`.

The GPU encodes automatically run with the environment the server needs (without it the bundled libraries crash with `Bus error`):
`LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri LIBVA_DRIVER_NAME=iHD`. CPU encodes and the quality measurement run without it. Change it with `--gpu-env "A=B ..."`, switch it off with `--no-gpu-env`.

Useful options: `--files Aladdin` (one movie only), `--clip-seconds 60`, `--gpu-only`, `--cpu-only`, `--no-av1`, `--skip-vmaf` (size and speed only), `--vmaf-ffmpeg PATH`, `--retry-failed`, `--keep-outputs` / `--no-keep-outputs` (keep or delete the encoded clips), `--timeout SEC`.

### Quality matching (`--matched`)
Question: **which Arc quality value gives the same picture quality as CPU x265 CRF 21?** The run encodes every test clip with
- CPU x265 `slow`: CRF **21** (the baseline), 22, 24, 26
- VAAPI HEVC ICQ: q **20, 23, 26, 29**
- QSV HEVC ICQ (look-ahead): q **21, 24, 27**

and scores each against the original clip. AV1 is left out unless you add `--av1`. After the usual tables the summary has a **"Quality matching"** block: per variant the average quality and size, `dQual` (quality minus the CPU CRF 21 quality) and `dSize%`, then for VAAPI and QSV a straight-line **estimate of the size at CPU CRF 21 quality** (when two of the tested values bracket it). If the CRF 21 quality lies outside what the Arc values reached it says so, with the range tested, so you know which side to add values on. Take the Arc setting with `dQual` about 0 (VMAF better than -0.5) and the smallest size, then compare `speed_x` with the CPU line.

`--matched` **keeps the encoded clips** in `/srv/convertr-bench/encoded/` (`<movie>_<variant>_30s.mkv`, next to the untouched reference cuts in `clips/`) so you can open one CPU and one Arc clip of the same movie side by side (VLC, or `ffplay`). They are small (30 s each); plan 2-4 GB free. Use `--no-keep-outputs` to delete them as before.

Finished rows from the earlier quick run are reused only if they were VMAF scored (see section 2); the 30 s reference clips themselves are reused.

What it does, in order
1. Prints the environment (ffmpeg version, which encoders exist, GPU, whether VMAF is available) and saves it to `environment.txt`.
2. **Capability probe:** 1-second test encodes find which option set really works for each Arc encoder (for example ICQ vs CQP, low-power on/off, QSV look-ahead on/off). Whatever does not work is recorded as `skipped_unsupported` instead of failing.
3. Per movie: skips HDR (PQ / HLG) and anything above 10-bit; cuts a clip from the middle by stream copy (so the reference is bit-identical to the source video; falls back to a near-lossless cut if copy is not possible); encodes every variant (8-bit sources stay 8-bit, 10-bit stay 10-bit); measures; deletes the encoded clip (`--matched` keeps it).
4. Any variant that crashes (exit 135 / 139), fails or times out is logged and the run continues.
5. Prints the summary tables (also saved as `summary.txt`).

Output folder `/srv/convertr-bench`: `results.csv` (one row per movie x variant), `summary.txt`, `environment.txt`, `clips/` (the reference clips, reused on re-runs), `encoded/` (the kept encoded clips, only with `--matched` / `--keep-outputs`), `logs/` (the exact ffmpeg command and messages of every encode). It needs roughly 1-2 GB (quick) or 3-6 GB (full) of free space. To start over delete that folder yourself; the script never deletes anything outside `work/` and the encodes it just made.

## 4. Reading the results
`./intel-bench.sh --summary` prints the tables again at any time.

**Per source table** - one line per movie and variant:
| column | meaning |
|---|---|
| `size%` | output size as a percentage of the source clip (smaller = more disk saved; 50 means half the size) |
| `speed_x` | encode speed in times real time (10 = a 90 s clip took 9 s) |
| `fps` | frames encoded per second |
| `quality` | **VMAF** (0-100, higher is better). If this ffmpeg has no libvmaf the script uses **SSIM** instead and marks it with an `s` (for example `0.9812s`) - then the numbers are *not* VMAF and are only comparable with each other |
| `VMAFmin` | the worst frame (a high average with a very low minimum means visible glitches) |

**Averages table** - one line per variant over all movies, with the two columns that matter:
- `dQualVsCPU`: quality of that variant minus the quality of the CPU x265 CRF 21 variant, same movie (0 = equal, -1 = one VMAF point worse).
- `dSize%vsCPU`: size% minus the CPU size%, in percentage points (+10 = ten points bigger than CPU x265).

Rules of thumb: VMAF 95+ is very close to the source, 93 is normally hard to tell apart, below 90 you will see it. **The best Arc setting is the one with `dQualVsCPU` around 0 to -1 and the smallest `size%`; compare its `speed_x` with the CPU line to see what the speed is worth.** Hardware encoders are typically a few points bigger than x265 at the same quality; the question is whether the speed makes up for it. AV1 variants are optional and may be marked unsupported on your driver.

Status values in `results.csv`: `ok`, `failed_<exit code>`, `crash_135` (Bus error: wrong GPU environment), `timeout`, `skipped_hdr`, `skipped_unsupported`, `failed_clip`, `failed_probe`.

## 5. When something goes wrong
- `crash_135` / `Bus error` on every GPU variant: the GPU environment is wrong. Check `LIBVA_DRIVER_NAME=iHD`, that `/usr/lib/x86_64-linux-gnu/dri/iHD_drv_video.so` exists, and read `logs/probe_*.log`.
- `no render device`: pass `--device /dev/dri/renderD129`, check `ls -l /dev/dri` and that user `youruser` is in group `render` (`id`).
- `skipped_unsupported` for QSV only: normal when the QSV runtime (`libvpl`) is not installed; VAAPI results are still valid.
- `quality metric: SSIM ... no ffmpeg with libvmaf found`: do section 2, or check that `~/ffmpeg-vmaf/ffmpeg -hide_banner -filters | grep libvmaf` prints a line. A `WARNING: VMAF_FFMPEG ... ignoring it` means the path you gave is wrong or has no libvmaf.
- Slow run: use `--quick`, `--files NAME`, or `--clip-seconds 45`.
- Please send back: `results.csv`, `summary.txt`, `environment.txt` and the `logs/` folder if anything crashed (`tar czf bench-results.tar.gz -C /srv convertr-bench --exclude=clips`).

## 6. Tested how
The script is checked with shellcheck (only info-level notes remain) and was run end to end on a machine **without** an Intel GPU using `--fake-gpu` (software encoders stand in for the Arc): nested folders, clip cutting, 8-bit and 10-bit handling, HDR skipping, CSV, resume, `--retry-failed`, crash handling (exit 135 injected), the SSIM fallback, **real VMAF scoring with the BtbN static ffmpeg (n8.1)**, SSIM rows being redone for VMAF, `--matched` with kept clips, and the summary tables including the quality-matching block. With `--fake-gpu` the "Arc" numbers are software numbers, so only the mechanics were tested. The real Arc commands were already proven on `media-server` by the quick run (v1.0); VMAF scoring and `--matched` run for real there for the first time.

## 7. Results on `media-server` (2026-10-18, `--matched`, version 1.1)
5 movies, 30 s clips, VMAF. Full table, reading and decision in [`DESIGN.md` section 5B](DESIGN.md). In short:

| Variant | Size % of source | Speed | VMAF |
|---|---|---|---|
| CPU x265 CRF 21 (baseline) | 36.4 | 0.60x | 96.97 |
| CPU x265 CRF 24 | 23.2 | 0.69x | 95.45 |
| VAAPI HEVC q20 / **q23** / q26 / q29 | 48.2 / **29.6** / 28.9 / 18.0 | 13.1x | 97.16 / **95.66** / 95.60 / 92.92 |
| QSV HEVC q21 / q24 / q27 | 30.3 / 21.5 / 18.2 | 11.8x | 95.69 / 94.20 / 93.24 |

- **At the same VMAF the CPU files are smaller, not the Arc files** (CPU about 24.7 % where VAAPI q23 gives 29.6 %: the Arc files are about 20 % bigger). An earlier reading of the quick SSIM run suggested the opposite; it compared Arc settings of lower quality with CPU CRF 21 and is withdrawn.
- The Arc encodes about 20 times faster (13x real time against 0.6x).
- Defaults chosen from this: VAAPI HEVC q23 (`IntelCq` 23), QSV q21 (`IntelQsvCq` 21, same quality and size as VAAPI q23), CPU x265 kept as the optional smallest-files mode, AV1 not tested (`IntelAv1Cq` 27 is a placeholder).
- Still open if you want more data: VAAPI q24 to q28 (q26 was almost the same as q23, so the curve is not smooth in this sample), a full 90 s run (`./intel-bench.sh --full`), AV1 (`--av1`), and more or different movies. Averages over 5 movies and 30 s clips are indicative only.
