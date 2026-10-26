#!/usr/bin/env bash
# intel-bench.sh - Intel Arc (VAAPI / QSV) versus CPU x265: size, speed and quality (VMAF) benchmark.
# Part of Convertr_svc. Copyright (C) 2026 JeepNWilly. Free software under the GNU GPL v3 or later; no warranty.
#
# Runs as a normal user (no root). Reads the test media, never writes into it, and only creates and deletes
# files inside its own output folder (default /srv/convertr-bench).
#
#   ./intel-bench.sh --quick          about 10 minutes: 30 s clips, CPU x265 + 3 GPU variants per source
#   ./intel-bench.sh --matched        quality-matching run: CPU CRF 21/22/24/26, VAAPI q20/23/26/29, QSV q21/24/27 (keeps the clips)
#   ./intel-bench.sh                  full run: 90 s clips, many quality values, AV1 (optional) - plan about 1.5 hours
#   ./intel-bench.sh --dry-run        show what would run, change nothing
#   ./intel-bench.sh --summary        only print the summary of an earlier run (results.csv)
#   ./intel-bench.sh --help           all options
#
# VMAF needs an ffmpeg with libvmaf. Point VMAF_FFMPEG at one (used only for scoring; encoding stays on jellyfin-ffmpeg),
# or unpack it to ~/ffmpeg-vmaf/ffmpeg where it is found automatically. Without one the script falls back to SSIM.
#
# Resumable: run it again and finished variants are skipped. A crash or failure of one variant never stops the run.

set -u
set -o pipefail
export LC_ALL=C

VERSION="1.1"
FFMPEG="${FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"
FFPROBE="${FFPROBE:-}"
VMAF_FFMPEG="${VMAF_FFMPEG:-}"   # optional ffmpeg WITH libvmaf, used only for the quality scoring
SRC_DIR="${BENCH_SRC:-/srv/testmedia/Movies}"
OUT_DIR="${BENCH_OUT:-/srv/convertr-bench}"
DEVICE="${BENCH_DEVICE:-}"
GPU_ENV_DEFAULT="LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri LIBVA_DRIVER_NAME=iHD"
GPU_ENV="$GPU_ENV_DEFAULT"
MODE="full"
CLIP_SECONDS=""
CLIP_START="middle"
TIMEOUT_S=3600
DRY=0
RETRY_FAILED=0
KEEP_OUTPUTS=-1     # -1 = mode default (matched: keep, others: delete), 1 = keep, 0 = delete
SKIP_VMAF=0
ONLY=""            # cpu | gpu
WANT_AV1=""        # "" = mode default, 1 = yes, 0 = no
FAKE_GPU=0
SUMMARY_ONLY=0
FILE_FILTER=""
CPU_THREADS=0
VMAF_THREADS="$(nproc 2>/dev/null || echo 4)"

usage() {
  sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
  cat <<'EOF'

Options
  --quick                 30 s clips, fewer variants (about 10 minutes)
  --full                  90 s clips, all variants (default)
  --matched               find the GPU quality values that match CPU x265 CRF 21: 30 s clips; CPU CRF 21/22/24/26,
                          VAAPI q20/23/26/29, QSV q21/24/27; keeps every encoded clip (see "Quality matching" in docs)
  --clip-seconds N        clip length per source (overrides the mode's default)
  --clip-start middle|N   where the clip starts: the middle of the file (default) or N seconds from the start
  --src DIR               folder with the test media (default /srv/testmedia/Movies)
  --out DIR               output folder (default /srv/convertr-bench)
  --files PATTERN         only sources whose file name contains PATTERN (for example "Aladdin")
  --ffmpeg PATH           ffmpeg to use for ENCODING (default /usr/lib/jellyfin-ffmpeg/ffmpeg); ffprobe is taken from the same folder
  --vmaf-ffmpeg PATH      ffmpeg with libvmaf, used only to SCORE the encodes (also: env VMAF_FFMPEG; auto-found at
                          ~/ffmpeg-vmaf/ffmpeg). Without libvmaf anywhere the script falls back to SSIM
  --device PATH           render node (default: the first /dev/dri/renderD*)
  --gpu-env "A=B C=D"     environment for GPU encodes (default: the system libva / iHD variables, see docs/INTEL_BENCH.md)
  --no-gpu-env            run GPU encodes with no special environment
  --cpu-only | --gpu-only run only the CPU or only the GPU variants
  --av1 | --no-av1        include / leave out the optional AV1 variants
  --skip-vmaf             do not measure quality (size and speed only)
  --cpu-threads N         limit libx265 threads (default: all)
  --timeout SEC           give up on one encode after SEC seconds (default 3600)
  --retry-failed          run failed / crashed variants again (finished ones are always skipped)
  --keep-outputs          keep the encoded clips in OUT/encoded/ (default: delete each one after it is measured;
                          --matched keeps them by default)
  --no-keep-outputs       delete each encoded clip after it is measured (also in --matched)
  --dry-run               print the plan and the commands, run nothing
  --summary               print the summary table of the existing results.csv and exit
  --fake-gpu              TEST ONLY: replace GPU encoders with software ones to test this script without Intel hardware
  -h, --help              this text
EOF
}

# ---------------------------------------------------------------- arguments
while [ $# -gt 0 ]; do
  case "$1" in
    --quick) MODE="quick"; shift ;;
    --full) MODE="full"; shift ;;
    --matched) MODE="matched"; shift ;;
    --clip-seconds) CLIP_SECONDS="${2:?--clip-seconds needs a number}"; shift 2 ;;
    --clip-start) CLIP_START="${2:?--clip-start needs middle or a number}"; shift 2 ;;
    --src) SRC_DIR="${2:?}"; shift 2 ;;
    --out) OUT_DIR="${2:?}"; shift 2 ;;
    --files) FILE_FILTER="${2:?}"; shift 2 ;;
    --ffmpeg) FFMPEG="${2:?}"; shift 2 ;;
    --vmaf-ffmpeg) VMAF_FFMPEG="${2:?}"; shift 2 ;;
    --device) DEVICE="${2:?}"; shift 2 ;;
    --gpu-env) GPU_ENV="${2-}"; shift 2 ;;
    --no-gpu-env) GPU_ENV=""; shift ;;
    --cpu-only) ONLY="cpu"; shift ;;
    --gpu-only) ONLY="gpu"; shift ;;
    --av1) WANT_AV1=1; shift ;;
    --no-av1) WANT_AV1=0; shift ;;
    --skip-vmaf) SKIP_VMAF=1; shift ;;
    --cpu-threads) CPU_THREADS="${2:?}"; shift 2 ;;
    --timeout) TIMEOUT_S="${2:?}"; shift 2 ;;
    --retry-failed) RETRY_FAILED=1; shift ;;
    --keep-outputs) KEEP_OUTPUTS=1; shift ;;
    --no-keep-outputs) KEEP_OUTPUTS=0; shift ;;
    --dry-run) DRY=1; shift ;;
    --summary) SUMMARY_ONLY=1; shift ;;
    --fake-gpu) FAKE_GPU=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
done

if [ "$MODE" = quick ] || [ "$MODE" = matched ]; then : "${CLIP_SECONDS:=30}"; [ -z "$WANT_AV1" ] && WANT_AV1=0
else : "${CLIP_SECONDS:=90}"; [ -z "$WANT_AV1" ] && WANT_AV1=1; fi
case "$CLIP_SECONDS" in ''|*[!0-9]*) echo "--clip-seconds must be a whole number" >&2; exit 2 ;; esac
[ "$CLIP_SECONDS" -ge 3 ] || { echo "--clip-seconds must be at least 3" >&2; exit 2; }
case "$CLIP_START" in middle|''|*[!0-9]*) [ "$CLIP_START" = middle ] || { echo "--clip-start must be middle or a number" >&2; exit 2; } ;; esac

if [ "$KEEP_OUTPUTS" = -1 ]; then if [ "$MODE" = matched ]; then KEEP_OUTPUTS=1; else KEEP_OUTPUTS=0; fi; fi
if [ -z "$FFPROBE" ]; then FFPROBE="$(dirname "$FFMPEG")/ffprobe"; fi
RESULTS="$OUT_DIR/results.csv"
WORK="$OUT_DIR/work"
CLIPS="$OUT_DIR/clips"
ENCODED="$OUT_DIR/encoded"
LOGS="$OUT_DIR/logs"
SUMMARY_TXT="$OUT_DIR/summary.txt"
HOST="$(hostname 2>/dev/null || echo host)"
CSV_HEADER="timestamp,host,source,variant,backend,codec,mode,quality,clip_req_s,status,clip_s,enc_s,speed_x,fps,src_kbps,out_kbps,src_bytes,out_bytes,size_pct,metric,vmaf_mean,vmaf_min,ssim,psnr,note"

say() { printf '%s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
now() { date +%s.%N; }
awkf() { awk "BEGIN{printf \"%.$1f\", $2}"; }   # awkf DECIMALS EXPRESSION

# ---------------------------------------------------------------- summary (also used at the end of a run)
summarize() {
  [ -s "$RESULTS" ] || { say "No results yet ($RESULTS)."; return 0; }
  awk -F, -v hdr="$CSV_HEADER" '
    function fmt(v, d) { return (v == "" ? "-" : sprintf("%." d "f", v)) }
    NR == 1 { next }
    {
      src = $3; var = $4; st = $10
      key = src SUBSEP var
      if (!(src in sseen)) { sseen[src] = 1; sorder[++ns] = src }
      if (!(var in vseen)) { vseen[var] = 1; vorder[++nv] = var; vback[var] = $5 "/" $6 "/" $7 "/q" $8 }
      status[key] = st; size[key] = $19; speed[key] = $13; vmaf[key] = $21; vmin[key] = $22; ssim[key] = $23; psnr[key] = $24; metric[key] = $20; fps[key] = $14
      note[key] = $25
    }
    END {
      cpu = ""
      if ("cpu_x265_crf21_slow" in vseen) cpu = "cpu_x265_crf21_slow"
      else for (i = 1; i <= nv; i++) if (vorder[i] ~ /^cpu_/) { cpu = vorder[i]; break }
      printf "\n=== Per source (size = output as %% of the source clip, speed = times real time) ===\n"
      printf "%-26s %-24s %-9s %8s %8s %8s %9s %8s\n", "source", "variant", "status", "size%", "speed_x", "fps", "quality", "VMAFmin"
      for (a = 1; a <= ns; a++) for (i = 1; i <= nv; i++) {
        s = sorder[a]; v = vorder[i]; k = s SUBSEP v
        if (!(k in status)) continue
        q = (metric[k] == "ssim" ? "ssim" : "vmaf")
        printf "%-26s %-24s %-9s %8s %8s %8s %9s %8s\n", substr(s, 1, 26), v, status[k], fmt(size[k], 1), fmt(speed[k], 2), fmt(fps[k], 1), (metric[k] == "ssim" ? (ssim[k] == "" ? "-" : sprintf("%.4f", ssim[k]) "s") : fmt(vmaf[k], 2)), fmt(vmin[k], 1)
      }
      printf "\n=== Averages per variant over the sources where it finished (quality = VMAF, or SSIM marked s when libvmaf is missing) ===\n"
      printf "%-24s %-26s %3s %8s %8s %9s %8s %10s %11s\n", "variant", "backend/codec/mode/quality", "n", "size%", "speed_x", "quality", "VMAFmin", "dQualVsCPU", "dSize%vsCPU"
      for (i = 1; i <= nv; i++) {
        v = vorder[i]; n = 0; ss = 0; sp = 0; vm = 0; vn = 0; nvm = 0; nmin = 0; dv = 0; ndv = 0; dsz = 0; isssim = 0
        for (a = 1; a <= ns; a++) {
          s = sorder[a]; k = s SUBSEP v
          if (status[k] != "ok") continue
          n++; ss += size[k]; sp += speed[k]
          qv = (metric[k] == "ssim" ? ssim[k] : vmaf[k])
          if (qv != "") { vm += qv; nvm++; if (metric[k] == "ssim") isssim = 1 }
          if (vmin[k] != "") { vn += vmin[k]; nmin++ }
          kc = s SUBSEP cpu
          if (cpu != "" && v != cpu && status[kc] == "ok" && qv != "") {
            qc = (metric[kc] == "ssim" ? ssim[kc] : vmaf[kc]); dv += qv - qc; dsz += size[k] - size[kc]; ndv++
          }
        }
        if (n == 0) { printf "%-24s %-26s %3d %8s %8s %9s %8s %10s %11s\n", v, vback[v], 0, "-", "-", "-", "-", "-", "-"; continue }
        printf "%-24s %-26s %3d %8.1f %8.2f %9s %8s %10s %11s\n", v, vback[v], n, ss / n, sp / n, (nvm ? sprintf(isssim ? "%.4fs" : "%.2f", vm / nvm) : "-"), (nmin ? sprintf("%.1f", vn / nmin) : "-"), (ndv ? sprintf(isssim ? "%+.4f" : "%+.2f", dv / ndv) : "-"), (ndv ? sprintf("%+.1f", dsz / ndv) : "-")
      }
      printf "\nHow to read it: smaller size%% = more saving; higher speed_x = faster than real time; VMAF 93+ is normally hard to tell from the source, 95+ is very close.\n"
      printf "A good GPU variant has dQualVsCPU (quality minus the CPU quality) close to 0 (or better than -1) with a size%% not much above the CPU one, and a much higher speed_x.\n"
      nf = 0
      for (k in status) if (status[k] != "ok" && status[k] !~ /^skipped/) nf++
      if (nf) printf "%d variant run(s) did not finish ok (see the status column and the logs folder).\n", nf
    }' "$RESULTS" > "$SUMMARY_TXT"
  matched_report >> "$SUMMARY_TXT"
  cat "$SUMMARY_TXT"
}

# Quality matching: for every GPU backend/codec (and for the CPU CRF ladder) estimate the size at the quality of the CPU CRF 21 baseline.
# Uses only sources where the baseline and the variant both finished with the same metric (the newest row of each wins).
matched_report() {
  awk -F, '
    NR == 1 { next }
    $10 == "ok" {
      key = $3 SUBSEP $4
      st[key] = 1; sz[key] = $19; me[key] = $20; q[key] = ($20 == "ssim" ? $23 : $21)
      if (!($3 in sseen)) { sseen[$3] = 1; srcs[++ns] = $3 }
      if (!($4 in vseen)) { vseen[$4] = 1; vord[++nv] = $4; vgrp[$4] = $5 "/" $6; vq[$4] = $8 }
    }
    END {
      base = "cpu_x265_crf21_slow"
      if (!(base in vseen)) exit
      n = 0
      printf "\n=== Quality matching against %s (the sources where both finished with the same metric) ===\n", base
      printf "%-24s %-12s %3s %8s %10s %10s %9s\n", "variant", "backend", "n", "size%", "quality", "dQual", "dSize%"
      for (i = 1; i <= nv; i++) {
        v = vord[i]; c = 0; sS = 0; sQ = 0; bQ = 0; bS = 0
        for (a = 1; a <= ns; a++) {
          s = srcs[a]; k = s SUBSEP v; kb = s SUBSEP base
          if (!(k in st) || !(kb in st) || me[k] != me[kb] || q[k] == "" || q[kb] == "") continue
          c++; sS += sz[k]; sQ += q[k]; bQ += q[kb]; bS += sz[kb]; metric = me[k]
        }
        if (!c) continue
        n++; cnt[v] = c; as[v] = sS / c; aq[v] = sQ / c; abq[v] = bQ / c; abs_[v] = bS / c; mt[v] = metric
        printf "%-24s %-12s %3d %8.1f %10s %10s %9s\n", v, vgrp[v], c, as[v], sprintf(metric == "ssim" ? "%.4f" : "%.2f", aq[v]), sprintf(metric == "ssim" ? "%+.4f" : "%+.2f", aq[v] - abq[v]), sprintf("%+.1f", as[v] - abs_[v])
      }
      printf "\nSize at the baseline quality, by straight-line interpolation between the two variants of one backend that bracket it:\n"
      groups = "vaapi/hevc qsv/hevc qsv/av1 vaapi/av1"
      ng = split(groups, G, " ")
      found = 0
      for (g = 1; g <= ng; g++) {
        m = 0
        for (i = 1; i <= nv; i++) { v = vord[i]; if ((v in cnt) && vgrp[v] == G[g]) { m++; L[m] = v } }
        if (m < 2) continue
        for (x = 1; x <= m; x++) for (y = x + 1; y <= m; y++) if (aq[L[y]] < aq[L[x]]) { t = L[x]; L[x] = L[y]; L[y] = t }   # sort by quality, low to high
        best = ""; tgt = abq[L[1]]; mt0 = mt[L[1]]
        for (x = 1; x < m; x++) {
          lo = L[x]; hi = L[x + 1]
          if (aq[lo] <= tgt && aq[hi] >= tgt && aq[hi] > aq[lo]) {
            f = (tgt - aq[lo]) / (aq[hi] - aq[lo]); est = as[lo] + f * (as[hi] - as[lo])
            printf "  %-11s ~%.1f%% of the source at baseline quality %s (between %s and %s); baseline itself is %.1f%%  ->  %+.1f points\n", G[g], est, sprintf(mt0 == "ssim" ? "%.4f" : "%.2f", tgt), lo, hi, abs_[L[1]], est - abs_[L[1]]
            best = "x"; found = 1; break
          }
        }
        if (best == "") {
          lo = L[1]; hi = L[m]
          printf "  %-11s the baseline quality (%s) is outside the tested range %s .. %s - add values on that side and run again\n", G[g], sprintf(mt0 == "ssim" ? "%.4f" : "%.2f", tgt), sprintf(mt0 == "ssim" ? "%.4f" : "%.2f", aq[lo]), sprintf(mt0 == "ssim" ? "%.4f" : "%.2f", aq[hi])
          found = 1
        }
      }
      printf "\nPick the cheapest-in-size variant whose dQual is about 0 (VMAF: better than -0.5; SSIM: better than -0.001). The CPU baseline sets the target quality.\n"
    }' "$RESULTS"
}

if [ "$SUMMARY_ONLY" = 1 ]; then summarize; exit 0; fi

# ---------------------------------------------------------------- preflight
[ -x "$FFMPEG" ] || { echo "ffmpeg not found or not executable: $FFMPEG (use --ffmpeg PATH)" >&2; exit 1; }
[ -x "$FFPROBE" ] || { echo "ffprobe not found: $FFPROBE (use FFPROBE=PATH or put it next to ffmpeg)" >&2; exit 1; }
[ -d "$SRC_DIR" ] || { echo "test media folder not found: $SRC_DIR (use --src DIR)" >&2; exit 1; }
SRC_REAL="$(cd "$SRC_DIR" && pwd -P)"
mkdir -p "$OUT_DIR" 2>/dev/null || { echo "cannot create $OUT_DIR" >&2; exit 1; }
OUT_REAL="$(cd "$OUT_DIR" && pwd -P)"
case "$OUT_REAL/" in "$SRC_REAL/"*) echo "refusing to write inside the test media folder ($SRC_REAL)" >&2; exit 1 ;; esac
case "$SRC_REAL/" in "$OUT_REAL/"*) echo "the output folder must not contain the test media ($OUT_REAL)" >&2; exit 1 ;; esac
if [ "$DRY" = 0 ]; then mkdir -p "$WORK" "$CLIPS" "$LOGS" || exit 1; [ "$KEEP_OUTPUTS" = 1 ] && { mkdir -p "$ENCODED" || exit 1; }; fi

if [ -z "$DEVICE" ]; then
  for d in /dev/dri/renderD*; do [ -e "$d" ] && { DEVICE="$d"; break; }; done
fi

FILTERS="$("$FFMPEG" -hide_banner -filters 2>/dev/null)"
ENCODERS="$("$FFMPEG" -hide_banner -encoders 2>/dev/null)"
have_filter() { grep -qE "[[:space:]]$1[[:space:]]" <<<"$FILTERS"; }
have_encoder() { grep -qE "[[:space:]]$1[[:space:]]" <<<"$ENCODERS"; }
# Scoring ffmpeg: VMAF_FFMPEG / --vmaf-ffmpeg, else ~/ffmpeg-vmaf/ffmpeg, else the encoding ffmpeg; the first one that has the libvmaf filter wins.
has_libvmaf() {   # capture first: grep -q closing the pipe early would make ffmpeg die of SIGPIPE and fail the pipeline (pipefail)
  local out; [ -x "$1" ] || return 1
  out="$("$1" -hide_banner -filters 2>/dev/null)"
  grep -qE '[[:space:]]libvmaf[[:space:]]' <<<"$out"
}
SCORE_FFMPEG="$FFMPEG"; HAVE_VMAF=0
if [ -n "$VMAF_FFMPEG" ] && ! has_libvmaf "$VMAF_FFMPEG"; then
  warn "VMAF_FFMPEG '$VMAF_FFMPEG' is not executable or has no libvmaf filter - ignoring it"
fi
for cand in "$VMAF_FFMPEG" "${HOME:-/nonexistent}/ffmpeg-vmaf/ffmpeg" "$FFMPEG"; do
  [ -n "$cand" ] && has_libvmaf "$cand" && { SCORE_FFMPEG="$cand"; HAVE_VMAF=1; break; }
done
if [ "$SKIP_VMAF" = 1 ]; then METRIC="none"; elif [ "$HAVE_VMAF" = 1 ]; then METRIC="vmaf"; else METRIC="ssim"; fi

env_info() {
  say "intel-bench $VERSION  mode=$MODE  clip=${CLIP_SECONDS}s  $(date '+%Y-%m-%d %H:%M:%S')"
  say "host: $HOST   user: $(id -un)   kernel: $(uname -r)"
  say "cpu: $(awk -F: '/model name/{gsub(/^ +/,"",$2); print $2; exit}' /proc/cpuinfo 2>/dev/null)  threads: $(nproc 2>/dev/null)"
  say "ffmpeg: $FFMPEG  ($("$FFMPEG" -version 2>/dev/null | head -1))"
  say "encoders: libx265=$(have_encoder libx265 && echo yes || echo NO) hevc_vaapi=$(have_encoder hevc_vaapi && echo yes || echo NO) hevc_qsv=$(have_encoder hevc_qsv && echo yes || echo NO) av1_qsv=$(have_encoder av1_qsv && echo yes || echo NO) av1_vaapi=$(have_encoder av1_vaapi && echo yes || echo NO)"
  say "quality metric: $( [ "$METRIC" = vmaf ] && echo "VMAF (model vmaf_v0.6.1; scored with $SCORE_FFMPEG, $("$SCORE_FFMPEG" -version 2>/dev/null | head -1 | cut -c1-40))" || { [ "$METRIC" = ssim ] && echo 'SSIM + PSNR (no ffmpeg with libvmaf found - results are not VMAF; see docs/INTEL_BENCH.md section 2)' || echo 'none (--skip-vmaf)'; } )"
  say "render device: ${DEVICE:-none found}   gpu env: ${GPU_ENV:-<none>}"
  if command -v lspci >/dev/null 2>&1; then lspci 2>/dev/null | grep -iE 'vga|display|3d' | sed 's/^/gpu: /'; fi
  if command -v vainfo >/dev/null 2>&1 && [ -n "$DEVICE" ]; then
    # shellcheck disable=SC2086
    env $GPU_ENV vainfo --display drm --device "$DEVICE" 2>&1 | grep -E "Driver version|VAProfileHEVCMain|VAProfileAV1" | head -6 | sed 's/^/vainfo: /'
  fi
  say "source folder: $SRC_REAL"
  say "output folder: $OUT_REAL   free: $(df -h "$OUT_REAL" 2>/dev/null | awk 'NR==2{print $4}')"
}

# ---------------------------------------------------------------- variants
# Quality values per mode. Format of a plan line:  id|backend|codec|quality|kind   (kind: cpu | gpu | opt = optional AV1)
plan_variants() {
  local q
  if [ "$ONLY" != gpu ]; then
    echo "cpu_x265_crf21_slow|cpu|hevc|21|cpu"
    [ "$MODE" = full ] && echo "cpu_x265_crf24_slow|cpu|hevc|24|cpu"
    if [ "$MODE" = matched ]; then for q in 22 24 26; do echo "cpu_x265_crf${q}_slow|cpu|hevc|$q|cpu"; done; fi
  fi
  if [ "$ONLY" != cpu ]; then
    if [ "$MODE" = quick ]; then
      for q in 23 26; do echo "vaapi_hevc_q$q|vaapi|hevc|$q|gpu"; done
      echo "qsv_hevc_q24|qsv|hevc|24|gpu"
    else   # full and matched use the same GPU grid
      for q in 20 23 26 29; do echo "vaapi_hevc_q$q|vaapi|hevc|$q|gpu"; done
      for q in 21 24 27; do echo "qsv_hevc_q$q|qsv|hevc|$q|gpu"; done
    fi
    if [ "$WANT_AV1" = 1 ]; then
      echo "qsv_av1_q27|qsv|av1|27|opt"
      echo "vaapi_av1_q110|vaapi|av1|110|opt"
    fi
  fi
}

# Candidate option sets, tried in order by the capability probe; first one that really encodes wins. "@Q@" = quality value.
# format: label|options
candidates_for() {   # backend codec
  case "$1/$2" in
    vaapi/hevc) printf '%s\n' "icq|-rc_mode ICQ -global_quality @Q@" "icq_lp|-rc_mode ICQ -global_quality @Q@ -low_power 1" "cqp|-rc_mode CQP -qp @Q@" "cqp_lp|-rc_mode CQP -qp @Q@ -low_power 1" ;;
    qsv/hevc)   printf '%s\n' "icq_la|-preset veryslow -global_quality @Q@ -look_ahead 1 -look_ahead_depth 40" "icq|-preset veryslow -global_quality @Q@" "icq_basic|-global_quality @Q@" ;;
    qsv/av1)    printf '%s\n' "icq|-preset veryslow -global_quality @Q@" "icq_basic|-global_quality @Q@" ;;
    vaapi/av1)  printf '%s\n' "cqp|-rc_mode CQP -global_quality @Q@" "cqp_lp|-rc_mode CQP -global_quality @Q@ -low_power 1" "qp|-rc_mode CQP -qp @Q@" ;;
  esac
}

declare -A CAP_LABEL CAP_OPTS CAP_DONE

encoder_name() {   # backend codec
  case "$1/$2" in
    cpu/hevc) echo libx265 ;; vaapi/hevc) echo hevc_vaapi ;; qsv/hevc) echo hevc_qsv ;;
    qsv/av1) echo av1_qsv ;; vaapi/av1) echo av1_vaapi ;;
  esac
}

# build_cmd INPUT OUTPUT BACKEND CODEC OPTIONS BITS   -> fills the array CMD
build_cmd() {
  local in="$1" out="$2" backend="$3" codec="$4" opts="$5" bits="$6" enc vf pixfmt prof=""
  CMD=()
  enc="$(encoder_name "$backend" "$codec")"
  local base=("$FFMPEG" -hide_banner -nostdin -y -loglevel error -progress "$WORK/progress.txt" -nostats)
  if [ "$bits" = 10 ]; then pixfmt="yuv420p10le"; else pixfmt="yuv420p"; fi
  if [ "$backend" = cpu ]; then
    local th=(); [ "$CPU_THREADS" -gt 0 ] && th=(-threads "$CPU_THREADS")
    # shellcheck disable=SC2206
    CMD=("${base[@]}" "${th[@]}" -i "$in" -map 0:v:0 -an -sn -c:v libx265 $opts -pix_fmt "$pixfmt" -x265-params log-level=error "$out")
    return
  fi
  if [ "$FAKE_GPU" = 1 ]; then      # test mode: software encoder, so the whole pipeline can be exercised on a machine without Intel hardware
    local q; q="$(printf '%s' "$opts" | sed -n 's/.*@Q=\([0-9]*\).*/\1/p')"
    # shellcheck disable=SC2206
    CMD=("${base[@]}" -i "$in" -map 0:v:0 -an -sn -c:v libx265 -preset ultrafast -crf "${q:-28}" -pix_fmt "$pixfmt" -x265-params log-level=error "$out")
    return
  fi
  local hwfmt="nv12"; [ "$bits" = 10 ] && hwfmt="p010"
  [ "$bits" = 10 ] && [ "$codec" = hevc ] && prof="-profile:v main10"
  local init
  if [ "$backend" = vaapi ]; then
    init="-init_hw_device vaapi=va:${DEVICE:-/dev/dri/renderD128} -filter_hw_device va"; vf="format=$hwfmt,hwupload"
  else
    init="-init_hw_device vaapi=va:${DEVICE:-/dev/dri/renderD128} -init_hw_device qsv=qs@va -filter_hw_device qs"; vf="format=$hwfmt,hwupload=extra_hw_frames=64"
  fi
  # shellcheck disable=SC2206
  CMD=("${base[@]}" $init -i "$in" -map 0:v:0 -an -sn -vf "$vf" -c:v "$enc" $opts $prof "$out")
  if [ -n "$GPU_ENV" ]; then
    # shellcheck disable=SC2206
    CMD=(env $GPU_ENV "${CMD[@]}")
  fi
}

# Run a command with a timeout; returns the exit code (124 = timeout).
run_cmd() { timeout --foreground "$TIMEOUT_S" "$@"; }

# capability probe: finds, per backend/codec, the option set that works on this machine (1 second test clip)
probe_capability() {   # backend codec quality
  local backend="$1" codec="$2" q="$3" key="$1/$2" enc
  [ -n "${CAP_DONE[$key]:-}" ] && return 0
  CAP_DONE[$key]=1; CAP_LABEL[$key]=""; CAP_OPTS[$key]=""
  enc="$(encoder_name "$backend" "$codec")"
  if [ "$FAKE_GPU" = 1 ]; then CAP_LABEL[$key]="fake"; CAP_OPTS[$key]="@Q=$q"; return 0; fi
  if ! have_encoder "$enc"; then say "  probe $key: encoder $enc is not in this ffmpeg - skipped"; return 1; fi
  if [ -z "$DEVICE" ] || [ ! -e "$DEVICE" ]; then say "  probe $key: no render device (/dev/dri/renderD*) - skipped"; return 1; fi
  local test="$WORK/probe_src.mkv" label opts o rc
  if [ ! -s "$test" ]; then
    "$FFMPEG" -hide_banner -nostdin -y -loglevel error -f lavfi -i "testsrc2=size=1280x720:rate=24:duration=1" -pix_fmt yuv420p -c:v libx264 -crf 18 "$test" 2>/dev/null
  fi
  while IFS='|' read -r label opts; do
    [ -z "$label" ] && continue
    o="${opts//@Q@/$q}"
    build_cmd "$test" "$WORK/probe_out.mkv" "$backend" "$codec" "$o" 8
    rm -f "$WORK/probe_out.mkv"
    run_cmd "${CMD[@]}" >"$LOGS/probe_${backend}_${codec}_${label}.log" 2>&1; rc=$?
    if [ "$rc" = 0 ] && [ -s "$WORK/probe_out.mkv" ]; then
      CAP_LABEL[$key]="$label"; CAP_OPTS[$key]="$opts"; say "  probe $key: works with '$label'"; rm -f "$WORK/probe_out.mkv"; return 0
    fi
    say "  probe $key: '$label' failed (exit $rc)$( [ "$rc" = 135 ] && echo ' - Bus error: the GPU environment is probably wrong')"
  done < <(candidates_for "$backend" "$codec")
  rm -f "$WORK/probe_out.mkv"
  return 1
}

# ---------------------------------------------------------------- media helpers
slugify() { printf '%s' "$1" | sed 's/\.[^.]*$//' | tr -c 'A-Za-z0-9\n' '_' | sed 's/__*/_/g; s/^_//; s/_$//' | cut -c1-28; }

# probe_media FILE -> sets P_* variables
probe_media() {
  local f="$1" k v
  P_DUR=""; P_CODEC=""; P_PIX=""; P_TRC=""; P_W=""; P_H=""; P_FPS=""; P_FIELD=""; P_BITS=8
  while IFS='=' read -r k v; do
    case "$k" in
      codec_name) P_CODEC="$v" ;; pix_fmt) P_PIX="$v" ;; color_transfer) P_TRC="$v" ;;
      width) P_W="$v" ;; height) P_H="$v" ;; r_frame_rate) P_FPS="$v" ;; field_order) P_FIELD="$v" ;;
      bits_per_raw_sample) [ "$v" != "N/A" ] && [ -n "$v" ] && P_BITS="$v" ;;
    esac
  done < <("$FFPROBE" -v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,color_transfer,width,height,r_frame_rate,field_order,bits_per_raw_sample -of default=nw=1 "$f" 2>/dev/null)
  P_DUR="$("$FFPROBE" -v error -show_entries format=duration -of default=nw=1:nk=1 "$f" 2>/dev/null | head -1)"
  case "$P_PIX" in *10le|*10be|p010*) P_BITS=10 ;; *12le|*12be|*16le|*16be) P_BITS=12 ;; esac
  case "$P_BITS" in 8|10|12) ;; *) P_BITS=8 ;; esac
  [ -z "$P_FPS" ] && P_FPS="24/1"
}

is_number() { case "$1" in ''|*[!0-9.]*|*.*.*) return 1 ;; *) return 0 ;; esac; }

# make_clip SRC CLIP_FILE -> stream-copy cut (the reference is then bit-identical to the source video); falls back to a near-lossless re-encode
make_clip() {
  local src="$1" clip="$2" start dur="$CLIP_SECONDS" tmp="$2.tmp.mkv"
  [ -s "$clip" ] && { local d; d="$("$FFPROBE" -v error -show_entries format=duration -of default=nw=1:nk=1 "$clip" 2>/dev/null | head -1)"; is_number "$d" && return 0; }
  if [ "$CLIP_START" = middle ]; then start="$(awkf 1 "($P_DUR - $dur) / 2")"; else start="$CLIP_START"; fi
  awk "BEGIN{exit !($start < 0)}" && start=0
  rm -f "$tmp"
  run_cmd "$FFMPEG" -hide_banner -nostdin -y -loglevel error -ss "$start" -i "$src" -t "$dur" -map 0:v:0 -an -sn -c:v copy -fflags +genpts "$tmp" >"$LOGS/clip_$(slugify "$(basename "$src")").log" 2>&1
  if [ -s "$tmp" ]; then
    local d; d="$("$FFPROBE" -v error -show_entries format=duration -of default=nw=1:nk=1 "$tmp" 2>/dev/null | head -1)"
    if is_number "$d" && awk "BEGIN{exit !($d >= $dur * 0.5)}"; then mv -f "$tmp" "$clip"; return 0; fi
  fi
  rm -f "$tmp"
  say "    stream-copy cut failed for this file, using a near-lossless x264 cut instead"
  run_cmd "$FFMPEG" -hide_banner -nostdin -y -loglevel error -ss "$start" -i "$src" -t "$dur" -map 0:v:0 -an -sn -c:v libx264 -crf 8 -preset veryfast -pix_fmt "$( [ "$P_BITS" = 10 ] && echo yuv420p10le || echo yuv420p )" "$tmp" >>"$LOGS/clip_$(slugify "$(basename "$src")").log" 2>&1
  [ -s "$tmp" ] && mv -f "$tmp" "$clip" && return 0
  rm -f "$tmp"; return 1
}

# measure REF DIST -> sets M_METRIC M_VMAF M_VMIN M_SSIM M_PSNR  (distorted first, reference second; scaled and fps-normalised to the reference)
measure() {
  local ref="$1" dist="$2" w="$3" h="$4" fps="$5" bits="$6" log="$7" pf norm
  M_METRIC=""; M_VMAF=""; M_VMIN=""; M_SSIM=""; M_PSNR=""
  [ "$METRIC" = none ] && return 0
  pf="yuv420p"; [ "$bits" = 10 ] && pf="yuv420p10le"
  norm="settb=AVTB,setpts=PTS-STARTPTS,fps=$fps,scale=$w:$h:flags=bicubic,format=$pf"
  if [ "$METRIC" = vmaf ]; then
    local csv="$WORK/vmaf.csv"; rm -f "$csv"
    # one pass: VMAF (default model vmaf_v0.6.1) plus SSIM, so the SSIM numbers of the earlier quick run stay comparable
    run_cmd "$SCORE_FFMPEG" -hide_banner -nostdin -y -loglevel info -i "$dist" -i "$ref" \
      -lavfi "[0:v]${norm},split[d1][d2];[1:v]${norm},split[r1][r2];[d1][r1]libvmaf=n_threads=${VMAF_THREADS}:log_fmt=csv:log_path=${csv}[ov];[d2][r2]ssim[os]" -map "[ov]" -map "[os]" -f null - >"$log" 2>&1
    local rc=$?
    if [ "$rc" = 0 ] && [ -s "$csv" ]; then
      read -r M_VMAF M_VMIN < <(awk -F, 'NR==1{for(i=1;i<=NF;i++) if($i=="vmaf") c=i; next} c&&$c!=""{s+=$c;n++; if(n==1||$c<m)m=$c} END{if(n) printf "%.3f %.3f\n", s/n, m}' "$csv")
    fi
    if [ -z "$M_VMAF" ]; then M_VMAF="$(sed -n 's/.*VMAF score: *\([0-9.]*\).*/\1/p' "$log" | tail -1)"; fi
    if [ -n "$M_VMAF" ]; then
      M_SSIM="$(sed -n 's/.*SSIM.* All:\([0-9.]*\).*/\1/p' "$log" | tail -1)"
      M_METRIC="vmaf"; return 0
    fi
    # libvmaf failed on this clip: fall back to SSIM + PSNR below (the row says which metric was used)
  fi
  run_cmd "$SCORE_FFMPEG" -hide_banner -nostdin -y -loglevel info -i "$dist" -i "$ref" \
    -lavfi "[0:v]${norm},split[d1][d2];[1:v]${norm},split[r1][r2];[d1][r1]ssim;[d2][r2]psnr" -f null - >"$log" 2>&1 || return 1
  M_SSIM="$(sed -n 's/.*SSIM.* All:\([0-9.]*\).*/\1/p' "$log" | tail -1)"
  M_PSNR="$(sed -n 's/.*PSNR.* average:\([0-9.]*\).*/\1/p' "$log" | tail -1)"
  [ -n "$M_SSIM" ] && { M_METRIC="ssim"; return 0; }
  return 1
}

already_done() {   # source variant -> 0 when a finished (or, without --retry-failed, any final) row exists
  # An "ok" row scored with SSIM does not count when this run scores with VMAF: that variant is encoded and scored again.
  [ -s "$RESULTS" ] || return 1
  awk -F, -v s="$1" -v v="$2" -v c="$CLIP_SECONDS" -v retry="$RETRY_FAILED" -v metric="$METRIC" '
    $3==s && $4==v && $9==c { if ($10=="ok") { if (metric != "vmaf" || $20 == "vmaf") f=1 } else if ($10 ~ /^skipped/) f=1; else if (!retry) f=1 }
    END { exit !f }' "$RESULTS"
}

add_row() {   # all 25 columns as arguments
  local IFS=,; printf '%s\n' "$*" >> "$RESULTS"
}

# ---------------------------------------------------------------- the run
CURRENT_OUT=""
cleanup() { [ -n "$CURRENT_OUT" ] && rm -f "$CURRENT_OUT"; rm -f "$WORK/progress.txt" "$WORK/vmaf.csv" 2>/dev/null; }
trap cleanup EXIT
trap 'echo; echo "Interrupted. Run the script again to continue where it stopped."; exit 130' INT TERM

env_info | tee "$( [ "$DRY" = 1 ] && echo /dev/null || echo "$OUT_DIR/environment.txt")"
say ""

# sources
mapfile -t SOURCES < <(find "$SRC_REAL" -maxdepth 3 -type f \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.avi' -o -iname '*.m4v' -o -iname '*.mov' -o -iname '*.ts' -o -iname '*.wmv' \) | sort)
if [ -n "$FILE_FILTER" ]; then
  FILTERED=(); for f in "${SOURCES[@]}"; do case "$(basename "$f")" in *"$FILE_FILTER"*) FILTERED+=("$f") ;; esac; done
  SOURCES=("${FILTERED[@]+"${FILTERED[@]}"}")
fi
[ "${#SOURCES[@]}" -gt 0 ] || { echo "no video files found in $SRC_REAL${FILE_FILTER:+ matching '$FILE_FILTER'}" >&2; exit 1; }
# one unique short name per source (two files whose names shorten to the same text get _2, _3 ...)
declare -A SLUG_OF SLUG_USED
for f in "${SOURCES[@]}"; do
  sl="$(slugify "$(basename "$f")")"; n=1; base_sl="$sl"
  while [ -n "${SLUG_USED[$sl]:-}" ]; do n=$((n+1)); sl="${base_sl}_$n"; done
  SLUG_USED[$sl]=1; SLUG_OF[$f]="$sl"
done
mapfile -t PLAN < <(plan_variants)
say "Plan: ${#SOURCES[@]} source(s) x ${#PLAN[@]} variant(s) = $(( ${#SOURCES[@]} * ${#PLAN[@]} )) encodes of ${CLIP_SECONDS}s each, quality metric: $METRIC"
for f in "${SOURCES[@]}"; do say "  source: $(basename "$f")"; done
for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; say "  variant: $id  ($be $cd quality $q$( [ "$kind" = opt ] && echo ', optional AV1'))"; done
say ""

if [ "$DRY" = 1 ]; then
  say "DRY RUN - commands that would run (GPU option sets are chosen by the capability probe on the real run; the first candidate is shown):"
  for f in "${SOURCES[@]}"; do
    probe_media "$f"; slug="${SLUG_OF[$f]}"; clip="$CLIPS/${slug}_${CLIP_SECONDS}s.mkv"
    say ""; say "# $(basename "$f")  codec=$P_CODEC pix_fmt=$P_PIX ${P_W}x${P_H} ${P_FPS}fps bits=$P_BITS duration=${P_DUR}s transfer=${P_TRC:-unknown}"
    case "$P_TRC" in smpte2084|arib-std-b67) say "#   HDR source: would be skipped"; continue ;; esac
    [ "$P_BITS" -gt 10 ] && { say "#   ${P_BITS}-bit source: would be skipped"; continue; }
    start="middle"; [ "$CLIP_START" != middle ] && start="$CLIP_START"
    say "#   clip: $FFMPEG -ss <$start> -i <source> -t $CLIP_SECONDS -map 0:v:0 -an -sn -c:v copy $clip"
    for p in "${PLAN[@]}"; do
      IFS='|' read -r id be cd q kind <<<"$p"
      if [ "$be" = cpu ]; then opts="-preset slow -crf $q"; else first="$(candidates_for "$be" "$cd" | head -1)"; opts="${first#*|}"; opts="${opts//@Q@/$q}"; fi
      build_cmd "$clip" "$WORK/$id.mkv" "$be" "$cd" "$opts" "$P_BITS"
      already_done "$slug" "$id" && tag="[already done, skipped]" || tag=""
      say "#   $id $tag"; say "      ${CMD[*]}"
    done
  done
  say ""; say "Dry run finished; nothing was written."
  exit 0
fi

[ -s "$RESULTS" ] || echo "$CSV_HEADER" > "$RESULTS"

# capability probes (GPU only)
if [ "$ONLY" != cpu ]; then
  say "Checking which GPU encoders work here (1 second test encodes)..."
  for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; [ "$be" = cpu ] && continue; probe_capability "$be" "$cd" "$q" || true; done
  say ""
fi

TOTAL=$(( ${#SOURCES[@]} * ${#PLAN[@]} )); N=0
for f in "${SOURCES[@]}"; do
  name="$(basename "$f")"; slug="${SLUG_OF[$f]}"; clip="$CLIPS/${slug}_${CLIP_SECONDS}s.mkv"
  say "=== $name"
  probe_media "$f"
  if ! is_number "${P_DUR:-}"; then
    say "    cannot read this file (ffprobe) - skipped"
    for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; N=$((N+1)); already_done "$slug" "$id" || add_row "$(date +%FT%T)" "$HOST" "$slug" "$id" "$be" "$cd" "" "$q" "$CLIP_SECONDS" "failed_probe" "" "" "" "" "" "" "" "" "" "" "" "" "" "" "ffprobe could not read the file"; done
    continue
  fi
  say "    ${P_CODEC} ${P_W}x${P_H} ${P_PIX} ${P_FPS}fps, ${P_BITS}-bit, ${P_DUR%.*}s long"
  skipreason=""
  case "$P_TRC" in smpte2084|arib-std-b67) skipreason="HDR ($P_TRC)" ;; esac
  [ -z "$skipreason" ] && [ "$P_BITS" -gt 10 ] && skipreason="${P_BITS}-bit"
  if [ -n "$skipreason" ]; then
    say "    skipped: $skipreason sources are not part of this benchmark"
    for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; N=$((N+1)); already_done "$slug" "$id" || add_row "$(date +%FT%T)" "$HOST" "$slug" "$id" "$be" "$cd" "" "$q" "$CLIP_SECONDS" "skipped_hdr" "" "" "" "" "" "" "" "" "" "" "" "" "" "" "$skipreason"; done
    continue
  fi
  # does anything for this source remain to be done?
  pending=0; for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; already_done "$slug" "$id" || pending=1; done
  if [ "$pending" = 0 ]; then say "    all variants already done"; N=$((N + ${#PLAN[@]})); continue; fi
  if ! make_clip "$f" "$clip"; then
    say "    could not cut a clip - source skipped (see $LOGS)"
    for p in "${PLAN[@]}"; do IFS='|' read -r id be cd q kind <<<"$p"; N=$((N+1)); already_done "$slug" "$id" || add_row "$(date +%FT%T)" "$HOST" "$slug" "$id" "$be" "$cd" "" "$q" "$CLIP_SECONDS" "failed_clip" "" "" "" "" "" "" "" "" "" "" "" "" "" "" "clip cut failed"; done
    continue
  fi
  CLIP_DUR="$("$FFPROBE" -v error -show_entries format=duration -of default=nw=1:nk=1 "$clip" 2>/dev/null | head -1)"
  CLIP_BYTES="$(stat -c %s "$clip")"
  SRC_KBPS="$(awkf 0 "$CLIP_BYTES * 8 / $CLIP_DUR / 1000")"
  FPS_EXPR="$P_FPS"

  for p in "${PLAN[@]}"; do
    IFS='|' read -r id be cd q kind <<<"$p"; N=$((N+1))
    if already_done "$slug" "$id"; then say "  [$N/$TOTAL] $id: already done"; continue; fi
    mode_label=""; opts=""
    if [ "$be" = cpu ]; then
      mode_label="crf"; opts="-preset slow -crf $q"
    else
      key="$be/$cd"
      if [ -z "${CAP_LABEL[$key]:-}" ]; then
        say "  [$N/$TOTAL] $id: not available on this machine - skipped"
        add_row "$(date +%FT%T)" "$HOST" "$slug" "$id" "$be" "$cd" "" "$q" "$CLIP_SECONDS" "skipped_unsupported" "" "" "" "" "" "" "" "" "" "" "" "" "" "" "capability probe found no working option set"
        continue
      fi
      mode_label="${CAP_LABEL[$key]}"; opts="${CAP_OPTS[$key]//@Q@/$q}"
      [ "$FAKE_GPU" = 1 ] && opts="@Q=$q"
    fi
    out="$WORK/${slug}_${id}.mkv"; log="$LOGS/${slug}_${id}.log"; mlog="$LOGS/${slug}_${id}.metric.log"
    CURRENT_OUT="$out"; rm -f "$out" "$WORK/progress.txt"
    build_cmd "$clip" "$out" "$be" "$cd" "$opts" "$P_BITS"
    printf '  [%s/%s] %-22s encoding ... ' "$N" "$TOTAL" "$id"
    { echo "# $(date)"; echo "# ${CMD[*]}"; } > "$log"
    t0="$(now)"; run_cmd "${CMD[@]}" >>"$log" 2>&1; rc=$?; t1="$(now)"
    enc_s="$(awkf 2 "$t1 - $t0")"
    ts="$(date +%FT%T)"
    if [ "$rc" != 0 ] || [ ! -s "$out" ]; then
      case "$rc" in 124) status="timeout" ;; 135|139|134) status="crash_$rc" ;; *) status="failed_$rc" ;; esac
      note="$(grep -v '^#' "$log" | tail -2 | tr '\n,' '  ' | cut -c1-120)"
      say "$status ($enc_s s)"
      add_row "$ts" "$HOST" "$slug" "$id" "$be" "$cd" "$mode_label" "$q" "$CLIP_SECONDS" "$status" "$CLIP_DUR" "$enc_s" "" "" "$SRC_KBPS" "" "$CLIP_BYTES" "" "" "" "" "" "" "" "$note"
      rm -f "$out"; CURRENT_OUT=""; continue
    fi
    out_bytes="$(stat -c %s "$out")"
    frames="$(awk -F= '$1=="frame"{f=$2} END{print f}' "$WORK/progress.txt" 2>/dev/null)"
    is_number "${frames:-}" || frames=""
    speed="$(awkf 2 "$CLIP_DUR / $enc_s")"
    fpsv=""; [ -n "$frames" ] && fpsv="$(awkf 1 "$frames / $enc_s")"
    pct="$(awkf 1 "$out_bytes * 100 / $CLIP_BYTES")"
    out_kbps="$(awkf 0 "$out_bytes * 8 / $CLIP_DUR / 1000")"
    printf 'done in %s s (%sx real time), %s%% of source; measuring ... ' "$enc_s" "$speed" "$pct"
    measure "$clip" "$out" "$P_W" "$P_H" "$FPS_EXPR" "$P_BITS" "$mlog"; mrc=$?
    note=""; [ "$mrc" != 0 ] && [ "$METRIC" != none ] && note="metric failed (see $(basename "$mlog"))"
    [ "$FAKE_GPU" = 1 ] && [ "$be" != cpu ] && note="FAKE-GPU test run (software encoder)"
    if [ "$M_METRIC" = vmaf ]; then say "VMAF $M_VMAF"; elif [ "$M_METRIC" = ssim ]; then say "SSIM $M_SSIM (no libvmaf)"; else say "no quality metric"; fi
    add_row "$ts" "$HOST" "$slug" "$id" "$be" "$cd" "$mode_label" "$q" "$CLIP_SECONDS" "ok" "$CLIP_DUR" "$enc_s" "$speed" "$fpsv" "$SRC_KBPS" "$out_kbps" "$CLIP_BYTES" "$out_bytes" "$pct" "$M_METRIC" "$M_VMAF" "$M_VMIN" "$M_SSIM" "$M_PSNR" "$note"
    if [ "$KEEP_OUTPUTS" = 1 ]; then mv -f "$out" "$ENCODED/${slug}_${id}_${CLIP_SECONDS}s.mkv"; else rm -f "$out"; fi
    CURRENT_OUT=""
  done
done

say ""
say "Finished. Results: $RESULTS"
summarize
say ""
say "Files: $RESULTS  $SUMMARY_TXT  $OUT_DIR/environment.txt  (logs of every encode in $LOGS)"
