# Unity Shadow Map Filtering Experiments

**Project:** Investigating the Performance–Quality Trade-off of Shadow Map Filtering in Unity

A Unity 6.6 URP project for investigating PCF shadow filtering. The current repository contains an initial controlled scene and native High rendering configuration.

## Current status

The initial environment was configured and checked in the Unity Editor on **2 November 2026**. The scene entered and exited Play mode successfully, rendered at 1920 × 1080, and the final Console check returned no errors or warnings.

The settings are initial development values. Standalone Player validation, repeatable GPU timing, baseline comparisons, motion replay, full-evaluation equivalence and adaptive sampling remain future work. There are no performance improvement results yet.

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
- `Assets/Scenes/PCF_Baseline.unity`: active initial scene.
- `Documentation/initial-environment.json`: recorded environment and Editor Play checks.
- `Documentation/native-source-hashes.json`: hashes of the unmodified native URP/Core source inspected during setup.
- `Captures/EnvironmentSetup/`: self-generated setup screenshots.

Unity package source remains in the installed packages. The repository preserves package references, lock data and source hashes; it does not vendor or modify URP. Cache, logs, user settings, generated IDE projects and build outputs are excluded by the Unity ignore rules.
