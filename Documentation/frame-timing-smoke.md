# Standalone frame timing availability

On 10 November 2026, a non-development Windows D3D11 Player was checked using `FrameTimingManager` with the existing Frame Timing Stats setting enabled. The native High scene and initial rendering controls were retained. The Player reported `LegacyJobified` rendering, whereas the Editor preflight reported `MultiThreaded`; comparisons should use the actual Player configuration.

`FrameTimingSmokeProbe` runs only when explicitly requested. It records warmup, drain and measurement observations in memory, then writes a CSV and JSON report after collection. It rejects duplicate/zero timestamps, non-increasing frame identity and non-positive/non-finite CPU or GPU durations. No screenshots or image GPU readbacks run during timing. The timing API and validation/buffering still add instrumentation overhead.

## Reproduction

Build `Assets/Scenes/PCF_Baseline.unity` for Windows x86-64 without Development Build. Frame Timing Stats must remain enabled. The checked local executable is `Builds/TaskC_Timing_2026-11-10/NativeTiming.exe`; build outputs are ignored by Git. Run it in a normal visible window with a new output directory:

```powershell
& .\Builds\TaskC_Timing_2026-11-10\NativeTiming.exe `
  -force-d3d11 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 `
  --pcf-preset=High --pcf-timing-smoke `
  --pcf-timing-output=F:/WorkingFiles/TYP/TYP/Measurements/FrameTiming/my_new_run `
  --pcf-timing-samples=1000 --pcf-timing-warmup=300 --pcf-timing-quit
```

The probe warms up for at least 300 frames and three seconds, records eight drain polls, then requests 1,000 unique valid samples with a 30-second/20,000-poll measurement limit. It checks the recorded rendering controls and native shadow path. Screenshot and timing suites must be run separately. `timing_smoke.json` and `observations.csv` retain configuration, frame identities, raw duration fields, phase and exclusion reasons. API duration fields are already milliseconds; `frameStartTimestamp` is retained as CPU-clock ticks, not treated as a current-frame index.

## Recorded results

**Later on 10 November:** the [longer-window pilot](frame-timing-pilot.md) exposed impossible positive finite GPU durations. The smoke below remains an availability check, while its original acceptance rule is insufficient to establish timing reliability. The pilot preserves and flags the anomalous raw values; it does not proceed to formal preset benchmarking.

| Run | Valid unique samples | GPU median/P95 ms | GPU=0 returns excluded | Measurement window s |
|---|---:|---|---:|---:|
| High_01 | 1000 | 0.104448 / 0.108544 | 109 of 1109 | 0.287265 |
| High_02 | 1000 | 0.103424 / 0.108544 | 129 of 1129 | 0.291432 |
| High_03 | 1000 | 0.103424 / 0.107520 | 120 of 1120 | 0.295137 |

Raw CSV/JSON files are under `Measurements/FrameTiming/2026-11-10/High_01` through `High_03`. Original Player logs remain locally alongside those folders and are excluded from Git by the existing `*.log` rule. Independent checks confirmed 23-column CSV consistency, accepted sample identity/validity, ready/final controls, exclusion counts and summary calculations. All three processes exited with code 0; the build and final Editor Console had no errors or warnings.

The pre-recorded protocol is `Measurements/FrameTiming/2026-11-10/protocol.json`; its planned status describes the original plan, while the run manifests record actual completion. To independently recompute the counts and descriptive statistics, use Python 3 with NumPy and run from the repository root:

```shell
python Tools/analysis/analyse_timing.py
```

The script defaults to the three recorded High runs and writes to `Measurements/FrameTiming/2026-11-10/analysis/`. `--output-dir` can select another output location. Median is the median of accepted observations; P95 uses NumPy's linear percentile. Raw CSV/JSON evidence has `-text` Git attributes to preserve its SHA-256 across Windows checkouts. Analysis reports contain the source file hashes and retain the missing-data limitations.

This validates availability of whole-frame CPU/GPU durations for this configuration. It does **not** validate PCF-only cost, a speedup, long-term stability or a formal preset comparison. Approximately 10% of measurement polls had GPU=0; these are missing/invalid results, not zero-cost rendering. Their missingness mechanism is not established. Accepted-frame statistics are conditional on valid returns, and adjacent frames are not independent trials. Measurement windows are short and other graphics applications were not isolated.

Before formal comparisons, investigate missing GPU timings, use longer measurement windows and control ambient graphics work. Task C remains in progress, and M2/dynamic replay/held-out evaluation/full-evaluation equivalence have not been completed. Extended source/build snapshots, launch telemetry and verification records are retained in the parent project workspace at `experiments/timing/2026-11-10/`; the repository includes raw observations, the protocol, analysis script and summary reports.
