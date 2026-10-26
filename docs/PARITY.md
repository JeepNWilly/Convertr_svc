# Feature parity: Convertr (Windows) and Convertr_svc

Status at version 0.3.3. "Shared" = comes from `Convertr.Core`, so a fix there reaches both apps.

| Feature | Windows Convertr | Convertr_svc | Where | Notes |
|---|---|---|---|---|
| Media folder + Browse | yes | **yes (S2)** | UI | Server-side browser, limited to `AllowedRoots`; empty on a fresh install |
| Scan (ffprobe, cache, progress, cancel) | yes | **yes (S2)** | Shared `Scanner`, `ProbeCache` | Progress by SSE; links out of the root are skipped (Linux) |
| Plan / decision table | yes | **yes (S2)** | Shared `Planner` | Golden test keeps both identical |
| Savings estimate | yes | **yes (S2)** | Shared `SavingsModel` | Intel HEVC calibrated in 0.3.1 from the Arc benchmark (used only for VAAPI/QSV; NVENC and CPU unchanged); Intel AV1 not measured |
| Preview table, sort, filter, search | yes | **yes (S2)** | UI | Virtual scrolling; nothing preselected |
| CSV export | yes | **yes (S2)** | Shared `PlanCsv` | |
| Theme Dark / Light / System | yes | **yes (S1)** | UI + `Settings.Theme` | |
| Setup (rules) | yes | **partly (S2)** | UI | Thresholds, container, mode, options. Encoder, hardware, ffmpeg, scratch: S3 to S5 |
| Authentication | n/a | **yes (S1)** | Service only | |
| Confirmation dialog | yes | **yes (S3)** | UI | Adds scratch needed/free, dry run |
| Convert queue, progress, pause/resume, cancel | yes | **yes (S3)** | Shared engine | Pause = SIGSTOP/SIGCONT through `IProcessController` |
| Safe swap + `_Convertr_Originals` | yes | **yes (S3)** | Shared `Swapper` | |
| Local scratch disk workflow | n/a | **yes (S3)** | Service (`IJobStorage`) | DESIGN 7A |
| Run log viewer (Queue > Log, live) | yes (log window) | **yes (0.3.3)** | UI + `RunLog.Echo` | Shows the newest run log incl. dry-run ffmpeg commands; also to journald at Info |
| Undo | yes | **yes (S3)** | Shared `UndoService` | |
| Hardware detection / Auto mode | yes | **partly (S3)** | `IPlatformHardware`, `IntelProbe` | Setup > Hardware tests the Arc (VAAPI/QSV); no CPU-vs-GPU Auto recommendation yet. Service default: Intel VAAPI q23 |
| Intel quality settings (`IntelCq` 23, `IntelQsvCq` 21, `IntelAv1Cq` untested) | n/a (NVENC `GpuCq`) | **yes (0.3.1)** | Shared `Settings`, `FfmpegCommandBuilder` | Additive; ignored by the Windows program |
| Download older ffmpeg | yes | n/a | Windows only | Linux uses the system jellyfin-ffmpeg |
| Setup window size memory | yes | n/a | Windows only | |

Rule: a change to planning, savings, command building, probing, swapping or undo is made in `Convertr.Core` and must keep `Convertr.Tests` (incl. the golden test) green. A change to a screen is made in both UIs or recorded here as a difference.
