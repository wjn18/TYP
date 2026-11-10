# Unity Shadow Map Filtering Experiments

**Project:** Investigating the Performance–Quality Trade-off of Shadow Map Filtering in Unity

A Unity 6.6 URP project for investigating PCF shadow filtering. The current working tree contains controlled static and occluder-motion scenes, native preset capture and a frame-timing availability workflow.

## Current status

The initial environment was configured and checked in the Unity Editor on **2 November 2026**. The scene entered and exited Play mode successfully, rendered at 1920 × 1080, and the final Console check returned no errors or warnings.

On **10 November 2026**, the first static native-preset suites were reproduced in the Editor and a standalone Windows Player. Both valid suites contain five captures, with High repeated; corresponding Editor/Player images have identical SHA-256 values. See [native baseline reproduction](Documentation/native-baselines.md) for commands, filter/bias records and the retained invalid hidden-window run.

Also on **10 November 2026**, a standalone native High timing smoke collected three sets of 1,000 valid unique CPU/GPU frame timings. Approximately 10% of measurement polls returned GPU=0 and were retained but excluded. See [frame timing availability](Documentation/frame-timing-smoke.md) for raw-data locations, commands and limitations.

The first occluder-only replay was also implemented on **10 November 2026**. Two forward runs, a reverse run and a seek run exported 375 frame-indexed images and state records. See [occluder replay](Documentation/occluder-replay.md) for the trajectory, commands, verification and inspection preview.

A subsequent [fixed-window timing pilot](Documentation/frame-timing-pilot.md) completed three 20-second High runs, a separate history diagnostic and a sample-count compatibility check. Longer windows exposed impossible positive GPU timings as well as missing returns. Collection/data consistency passed, but timing reliability has **not** been established; the original positive-finite `accepted` rule is insufficient for benchmark-quality data.

The settings remain initial development values. Reliable formal timing, camera/light motion, dynamic timing association, full-evaluation equivalence and adaptive sampling remain future work. There are no performance improvement results yet. Tasks C and D are in progress; M2 has not been passed.

## Requirements and opening the project

- Unity Editor **6000.6.5f1** (revision `3ff58d469c8a`).
- URP and Core **17.6.0**, recorded in `Packages/manifest.json` and `Packages/packages-lock.json`.
- Git and Git LFS. Images are stored using the existing LFS rules.
- The checked environment is Windows with Direct3D 11; other platforms have not been checked.

With Git LFS installed:

```shell
git clone https://github.com/wjn18/TYP.git
cd TYP
git lfs pull
```

1. Add this repository folder to Unity Hub and open it with the recorded Editor version.
2. Allow package resolution and shader compilation to finish.
3. Open `Assets/Scenes/PCF_Baseline.unity`.
4. Select the **PC** quality level and **Full HD (1920x1080)** in the Game view.
5. Enter Play mode. The scene's runtime settings component disables VSync and the frame-rate cap. A standalone Player requests 1920 × 1080 Windowed.

The installed MCP automation package is a Git dependency. Its manifest references `main`; the committed package lock records resolved commit `81542a7e86292b5c4c21b1782c5af23b567f45ce`. Initial package restoration therefore needs Git and network access.

## Initial rendering configuration

| Setting | Value |
| --- | --- |
| Renderer | Forward; no Renderer Features |
| Main light | Realtime directional light; High soft shadows; strength 1 |
| Main shadow map | 2048; one cascade; shadow distance 50 |
| Bias inputs | Depth 0.1; normal 0.5, taken from the pipeline asset |
| Output | 1920 × 1080; render scale 1; Linear colour space |
| AA, post-processing, HDR and dynamic resolution | Disabled |
| SSAO, Screen Space Shadows and additional lights | Disabled |
| Timing controls | VSync 0; target frame rate -1; Frame Timing Stats enabled |
| Validated hardware | RTX 4070; NVIDIA driver 617.42; D3D11 |

Bias values above are configuration inputs, not the final GPU bias offsets. Shadow coverage, bias and the other initial parameters still require pilot checks before experiment settings are frozen. GPU Recorder support and enabled frame timing statistics do not establish that valid benchmark samples have been collected.

The scene contains a receiving plane, a block, a thin wall and three posts of widths 0.08, 0.15 and 0.30 Unity units. These provide initial cases for inspecting shadow boundaries and thin shadows.

![Initial native High scene](Captures/EnvironmentSetup/baseline_high_play.png)

## Repository layout

- `Assets/Experiments/Settings/`: separate experiment URP asset and Renderer.
- `Assets/Experiments/Materials/`: URP Lit materials.
- `Assets/Experiments/Runtime/`: display and frame-rate controls.
- `Assets/Experiments/Runtime/NativeBaselineRunner.cs`: native preset selection and diagnostic capture suite.
- `Assets/Scenes/PCF_Baseline.unity`: active initial scene.
- `Documentation/initial-environment.json`: recorded environment and Editor Play checks.
- `Documentation/native-source-hashes.json`: hashes of the unmodified native URP/Core source inspected during setup.
- `Captures/EnvironmentSetup/`: self-generated setup screenshots.
- `Captures/NativeBaselines/`: native baseline images, configuration metadata and retained invalid captures.
- `Documentation/native-baselines.md`: reproduction steps, current results and limitations.
- `Assets/Experiments/Runtime/FrameTimingSmokeProbe.cs`: explicitly requested timing availability collector.
- `Measurements/FrameTiming/`: raw polling observations, configuration and retained invalid timing returns.
- `Documentation/frame-timing-smoke.md`: timing commands and availability results with limitations.
- `Tools/analysis/analyse_timing.py`: independent validation and summary calculation from the recorded raw timing observations.
- `Assets/Scenes/PCF_OccluderReplay.unity`: separate replay scene; enter Play to preview the moving block.
- `Assets/Experiments/Runtime/OccluderReplayRunner.cs`: frame-indexed motion, state checks and diagnostic export.
- `Captures/OccluderReplay/`: original replay PNG/JSON/CSV records and derived inspection previews.
- `Documentation/occluder-replay.md`: replay protocol and current validation results.
- `Documentation/occluder-replay-provenance.json`: exact source/build input hashes and raw-data linkage.
- `Tools/analysis/analyse_replay.py`: independent replay-state and image verification.
- `Measurements/FrameTimingPilot/`: longer-window raw timings, recovery diagnostic, GPU telemetry, hashes and analysis.
- `Documentation/frame-timing-pilot.md`: reliability findings and commands; includes the unresolved impossible-positive-value issue.
- `Documentation/frame-timing-pilot-provenance.json`: source/build/raw identities for the pilot.
- `Tools/analysis/analyse_timing_pilot.py`: fixed-window, block, recovery, compatibility and plausibility verification.

Unity package source remains in the installed packages. The repository preserves package references, lock data and source hashes; it does not vendor or modify URP. Cache, logs, user settings, generated IDE projects and build outputs are excluded by the Unity ignore rules.
