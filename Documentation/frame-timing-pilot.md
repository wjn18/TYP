# Frame-timing reliability pilot

**10 November 2026: collection and data-consistency checks passed; reliable GPU timing has not been established.** Longer windows exposed positive, finite GPU durations around `1.665 × 10^13 ms`. The original availability rule accepts those values. Do not use this collector's `accepted` flag alone as evidence of benchmark-quality timings or proceed to formal preset comparisons from these results.

## Changes and protocol

`FrameTimingSmokeProbe` retains the original 1,000-sample smoke mode and its 23-column CSV. The optional `--pcf-timing-duration-seconds` selects a fixed wall-clock window, checked independently of the accepted count. Supported durations are positive finite values up to 60 seconds. A separate 200,000-poll cap must not terminate a successful duration run early; the requested sample count is a minimum checked at the end.

The pilot used three independent non-development Windows x64 D3D11 Players, each with a static native High scene and a **20-second measurement window**. Each first warmed up for at least 300 frames and three seconds, then recorded eight drain polls. Duration mode preallocates observation/identity storage before warmup and records exact measurement boundaries plus GC collection counts. No screenshots, image readback or per-frame file I/O occur during collection. Configuration checks, timing polling and memory instrumentation still have overhead.

The existing baseline controls remain: 1920 × 1080 Windowed, render scale 1, 2048 main shadow map, one cascade, distance 50, pipeline bias inputs 0.1/0.5, AA/post/HDR/dynamic resolution disabled, VSync 0 and target frame rate -1. Other graphics applications were retained. One-hertz `nvidia-smi` telemetry describes the whole GPU over each Player process, including startup/warmup/export, rather than an isolated Player or precisely aligned measurement interval.

Build `Assets/Scenes/PCF_Baseline.unity` as a non-development Windows Player and run visibly:

```powershell
& '<build>/TimingPilot.exe' -force-d3d11 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 `
  --pcf-preset=High --pcf-timing-smoke --pcf-timing-output='<new empty directory>' `
  --pcf-timing-duration-seconds=20 --pcf-timing-samples=1000 --pcf-timing-warmup=300 --pcf-timing-quit
```

Omit the duration flag for the original sample-count smoke. Capture/replay workflows run separately. Use a new empty directory for every run; all returned observations and excluded values are retained.

## Observed results

| Run | Actual window s | Preliminary accepted / polls | GPU=0 polls | GPU median / P95 ms | Implausible positive GPU values |
| --- | ---: | ---: | ---: | --- | ---: |
| High_20s_01 | 20.0002694 | 57,696 / 61,664 | 3,968 (6.435%) | 0.106496 / 0.346112 | 6 |
| High_20s_02 | 20.0003297 | 56,056 / 60,617 | 4,561 (7.524%) | 0.106496 / 0.350208 | 4 |
| High_20s_03 | 20.0004875 | 56,906 / 61,007 | 4,101 (6.722%) | 0.106496 / 0.348160 | 2 |

“Preliminary accepted” means nonzero, unique increasing source timestamp with positive finite CPU/GPU durations under the original runtime rule. It includes the impossible positive GPU values. The analyser flags durations exceeding the entire elapsed application time at their observation, a conservative diagnostic added after this unexpected finding. Raw values and runtime acceptance flags remain unchanged; no outliers were silently trimmed. Means and standard deviations of those preliminary GPU values cannot be interpreted as rendering cost. Equal medians do not establish reliability.

One-second GPU=0 fractions ranged from 5.192–8.331%, 5.478–11.291% and 5.329–9.492% across the three runs. Measurement GC deltas were `[0,0,0]` in all three. Whole-process telemetry included changes in temperature and clock rate; this does not establish thermal equilibrium or isolate ambient graphics activity. These are descriptive blocks and process repeats, not independent frame-level experiments.

## Separate history diagnostic and compatibility check

`--pcf-timing-recovery-history=10`, used only with duration mode, adds a history query after the unchanged latest-one poll. It tracks first zero values by source timestamp and first later positive CPU/GPU returns without changing primary acceptance. Primary zero values are recorded before the extra query, allowing same-poll recovery. The additional API/memory work means this run is not pooled with ordinary pilots.

The separate `High_recovery_5s` run measured 5.000437 seconds and requested ten history entries. Actual returns ranged from 0 to 10. Across warmup/drain/measurement it tracked 1,577 zero-GPU identities; **no recovery was observed** within the available history and observation period. There were 1,002 first-zero identities in measurement. This does not prove the data can never become available. The run also contained one impossible positive GPU value.

The separate `High_smoke_compat` run correctly stopped at 1,000 preliminary accepted samples in 0.3528325 seconds with `completionCriterion=sample_count`. It had no flagged impossible positive GPU value. All five Players exited with code 0; the build and final Console check had zero errors/warnings.

## Reproduction and evidence

Original CSV/JSON plus whole-device telemetry are under [`Measurements/FrameTimingPilot/2026-11-10`](../Measurements/FrameTimingPilot/2026-11-10). The planning protocol remains an unchanged pre-execution record; manifests and this document describe execution. The post-observation sanity check is recorded separately in `analysis_policy.json`.

From the repository root, using Python 3 and NumPy:

```shell
python Tools/analysis/analyse_timing_pilot.py
```

The analyser checks the 23-column policy and identities, timing boundaries, controls, counts, history diagnostic and old-mode compatibility. It writes 1-second/5-second blocks and verifies an existing raw SHA inventory before reuse. A passed `validation` refers to capture/data consistency. `reliability_assessment=not_established_implausible_positive_gpu_timings` is a separate result.

See the [summary](../Measurements/FrameTimingPilot/2026-11-10/analysis/pilot_summary.json) and [source/build provenance](frame-timing-pilot-provenance.json). The build used uncommitted pilot changes after pushed commit `dcb4397`; exact input hashes identify the source. Local build outputs, Player logs, helper UTC bounds and extended snapshots remain in the parent workspace experiment directory and are not part of a clone. Build-generated settings were archived and restored to starting bytes; the original scenes and build scene list were preserved.

Unity's version-matched API documents [GPU durations in milliseconds](https://docs.unity.com/en-us/engine/6000.6/script-reference/unityengine/frametiming/gpuframetime) and warns that [delayed results are not guaranteed accurate](https://docs.unity.com/en-us/engine/6000.6/manual/analysis/graphics-performance-profiling/profile-rendering/frame-timing-manager/manager). These sources do not identify the cause of this run's impossible positive values. No engine/driver defect or fix has been established.

Before formal four-preset measurements, investigate the implausible values with a separately validated timing path, agree a traceable validity policy and control background load. Reading a different counter name is not automatically an independent timing source. This pilot does not validate PCF-only cost, a speedup, temporal quality, dynamic frame-to-timing alignment, full-evaluation equivalence or M2.
