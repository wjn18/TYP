# Native shadow baseline reproduction

Task C now has a static diagnostic capture workflow for native Hard, Low, Medium and High shadows. Native High is the selected **fixed-budget 16-comparison-fetch reference** for later work. No custom fixed-PCF implementation, forced-full path or adaptive shader has been added.

## Recorded environment

The checked environment is Unity 6000.6.5f1, URP/Core 17.6.0, Windows/D3D11 and an RTX 4070 with driver 617.42. The six native source hashes still match the initial environment record. The existing Forward scene, camera, geometry and rendering settings are retained: 1920 x 1080, 2048 x 2048 main shadow map, one cascade, shadow distance 50, Linear colour, no AA/post-processing/HDR/dynamic resolution, VSync 0 and no frame-rate cap.

`NativeBaselineRunner` is attached to the existing Experiment Settings object. It sets the main light's shadow mode and explicit per-light soft-shadow quality at runtime. It does not edit the URP asset or package source. The default remains High. Setting the component's Default Preset in Edit mode selects the preset used on the next Play session.

## Capture suite

The suite captures `High`, `Hard`, `Low`, `Medium`, then `High_repeat`. Each preset settles for 30 frames before an end-of-frame screen capture. Every image is accompanied by JSON describing the scene, camera, light, hardware, public rendering settings, shadow globals and keyword readbacks. The original light settings are restored at the end or on interruption.

Use a new or empty output directory. Existing non-empty directories are rejected.

In Play mode, an Editor automation tool can call:

```csharp
UnityEngine.Object.FindFirstObjectByType<TYP.Experiments.NativeBaselineRunner>()
    .BeginCaptureSuite("F:/WorkingFiles/TYP/TYP/Captures/NativeBaselines/my_new_run");
```

For a standalone build, build `Assets/Scenes/PCF_Baseline.unity` as a Windows x86-64 Player. The checked local build is a non-development D3D11 build at `Builds/TaskC_2026-11-10_v2/NativeBaselines.exe`. Build outputs are ignored by Git. From the project root, run the Player in a normal visible window:

```powershell
& .\Builds\TaskC_2026-11-10_v2\NativeBaselines.exe `
  -force-d3d11 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 `
  --pcf-capture-suite --pcf-output=F:/WorkingFiles/TYP/TYP/Captures/NativeBaselines/my_new_player_run `
  --pcf-quit
```

`--pcf-preset=Hard|Low|Medium|High` can select a starting preset without running a suite. `--pcf-quit` exits after a requested suite, with exit code 0 for file-capture completion and 1 for a reported capture error. A completed file-capture manifest must still pass image/configuration validation; process exit alone does not prove that rendering occurred.

## Actual evidence from 10 November 2026

- `Captures/NativeBaselines/2026-11-10/editor_static_01/`: five valid 1920 x 1080 images, metadata and suite manifest.
- `Captures/NativeBaselines/2026-11-10/player_static_02/`: the final valid standalone suite, with the same five image SHA-256 values as the Editor suite.
- `High` and `High_repeat` are pixel-identical within both valid suites.
- `_MainLightShadowParams.y` read back as 0/1/2/3 for Hard/Low/Medium/High. The desktop path uses `_SHADOWS_SOFT` plus this per-light parameter; the static quality keywords remain false.
- Frame Debugger showed `Draw Main Light Shadowmap/MainLightShadow` and `DrawOpaqueObjects` for Main Camera. The former generates the map; it is not a measurement of receiver-side PCF filtering cost.
- Both Windows builds succeeded with zero build errors or warnings. The final Player exited with code 0, and image/configuration checks passed. The Editor was returned to Edit mode with High selected.
- `player_static_01/` is an **invalid run** retained as evidence: a hidden-window launch produced black images and zero shadow globals. It was rejected by validation. Do not include it in image comparisons or timing results. Its original Player log is retained locally alongside the folder and is excluded from Git by the existing `*.log` rule.

## Filters and caster bias

| Native preset | Source comparison-sampling calls per selected filtering evaluation | Filter | Quality bias multiplier |
|---|---:|---|---:|
| Hard | 1 | One comparison-sampler fetch | 1 |
| Low | 4 | Four half-texel offsets, each weighted 0.25 | 1.5 |
| Medium | 9 | 5 x 5 tent helper | 2.5 |
| High | 16 | 7 x 7 tent helper | 3.5 |

These are source-level fetch counts, not the number of covered texels, internal hardware depth comparisons or measured GPU instructions. Tent positions and weights depend on the receiving coordinate's fractional alignment to the shadow texels. Fixed 16 means a fixed fetch budget and filter definition, not identical absolute UVs across the image.

The bias inputs are taken from the pipeline: depth 0.1 and normal 0.5. For this single directional cascade, the recorded world-to-shadow matrix gives a light-space frustum width of approximately 85.8352 world units and a texel width of 0.04191172 world units. Using the local `GetShadowBias` formula gives:

| Preset | Derived depth coefficient | Derived normal coefficient |
|---|---:|---:|
| Hard | -0.00419117 | -0.02095586 |
| Low | -0.00628676 | -0.03143379 |
| Medium | -0.01047793 | -0.05238965 |
| High | -0.01466910 | -0.07334551 |

The post-render `_ShadowBias` diagnostic readbacks agree with these derived coefficients within 1e-6. This is not a per-draw GPU capture. The normal coefficient is further modulated by `1 - saturate(dot(L, N))` in the caster shader; the table is not a final displacement for every vertex. The derivation assumes this recorded full-size, single-cascade atlas and must be revisited if its layout changes.

Native presets change the filter and effective bias together. They are a whole-preset comparison, not a controlled sampling-count-only experiment. Native High is an implementation reference, not physical ground truth. Later forced-full/adaptive comparisons must keep High's map, kernel, sample positions/weights and effective bias consistent.

## Current limits and next work

This completes a first static baseline reproduction step. The screenshot suites contain no timing data. A subsequent native High standalone timing-availability check on the same date collected actual valid whole-frame CPU/GPU samples; see [frame timing availability](frame-timing-smoke.md) for exclusions and limitations. Formal timing reliability, performance gains, dynamic replay, held-out evaluation and forced-full equivalence remain unestablished. Task C remains in progress; continue C/D platform work toward the planned 20 November M2 checkpoint.

The top-level project workspace retains full analysis, raw-source verification, build input snapshots, generated-file restoration records and the invalid-run explanation under `experiments/baselines/2026-11-10/`. These extended archives and original Player logs are local workspace records; the repository contains the raw PNG/JSON capture evidence and this reproduction guide.
