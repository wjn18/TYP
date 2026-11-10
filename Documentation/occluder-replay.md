# Occluder replay

The first occluder-only state and image replay passed verification on 10 November 2026. Task D and milestone M2 remain in progress.

## Scene and motion

Open `Assets/Scenes/PCF_OccluderReplay.unity`, copied from the static baseline. Only `Geometry/Block` changes world X. Its fixed centre is `(-2, 1, -1)` and amplitude is 1.25 world units. Rotation and scale, the camera, directional light, receiving surface and all other geometry remain fixed.

For integer source frame `i` from 0 through 120 inclusive:

```text
x(i) = -2 + 1.25 * (1 - abs(i - 60) / 30)
y(i) = 1
z(i) = -1
logical_time(i) = i / 60 seconds
```

Indices 0, 60 and 120 therefore have X = -3.25, -0.75 and -3.25. The nominal 60 Hz defines logical time only. The trajectory contains 120 steps and 121 endpoint-inclusive states over two logical seconds. Export duration and actual Player frame rate are independent of this rate.

The renderer uses native High, the 16-comparison-fetch implementation reference, with the existing baseline controls. This reference is not physical ground truth or a custom forced-full implementation.

## Running

Build only the replay scene as a Windows Player. The checked configuration uses a non-development Windows x64 D3D11 Player, 1920 x 1080 Windowed, render scale 1, AA/post-processing/HDR disabled, VSync 0 and no frame-rate cap. Keep the Player visible: the earlier Task C hidden-window capture returned invalid black images.

```powershell
& '<build>/OccluderReplay.exe' -force-d3d11 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 `
  --pcf-preset=High --pcf-occluder-replay `
  --pcf-replay-output='<new empty directory>' --pcf-replay-order=forward --pcf-replay-quit
```

Use `forward`, `reverse` or `seek` for the order. Forward exports 0…120; reverse exports 120…0. Seek uses:

```text
120, 0, 60, 15, 105, 30, 90, 45, 75, 0, 60, 120
```

Screenshot baseline suites and timing smoke runs must execute separately. Existing output must be preserved; select a new empty directory for each run. Image names include the request ordinal and source index so repeated seek requests cannot overwrite earlier observations.

`OccluderReplayRunner.ApplyFrame(index)` reconstructs state directly without accumulating `Time.deltaTime`. `BeginCapture(outputDirectory, order)` starts export in Play mode. Manual application and capture suspend automatic preview. The scene's normal Play preview selects source indices using wall-clock time for visual inspection; it supplies no measurement evidence.

After each explicit state application, capture waits for two rendered end-of-frame boundaries. Configuration and static-scene invariants are checked, and the applied index is recorded with every image. PNG encoding and file I/O occur during export; this process cannot serve as a performance measurement.

## Validation

Two independent forward Player runs, one reverse run and one seek run all completed with exit code 0, totalling **375 captures** (121 + 121 + 121 + 12). The non-development build completed with zero errors and warnings. The analyser passed the trajectory, rotation/scale, fixed controls, 22-column CSV/JSON/manifest agreement, dimensions, hashes, request order and capture-frame alignment checks. All four runs restored the initial transform at completion.

All **254 cross-run corresponding-frame pairs** have identical PNG bytes and decoded RGBA pixels. Each run's frame 0 equals 120; 0 versus 60 has 108,641 changed pixels, a visible-motion diagnostic rather than a quality metric. At the original block centre, replay frame 30 also matches the earlier static native High PNG byte-for-byte. The inventory contains 758 raw files and passed a second hash verification.

Raw data and portable analysis outputs are under [`Captures/OccluderReplay/2026-11-10`](../Captures/OccluderReplay/2026-11-10). Recalculate from the repository root with Python 3 and Pillow:

```shell
python Tools/analysis/analyse_replay.py
```

The default writes to the capture root's `analysis` folder and verifies its existing raw hash inventory. An optional positional input root and `--output-dir` are supported. See [verification summary](../Captures/OccluderReplay/2026-11-10/analysis/replay_summary.json) and [source/build provenance](occluder-replay-provenance.json). The recorded build used uncommitted Task D code after parent commit `e8e03e1`; actual input hashes identify it. Build outputs and Player logs remain local and ignored. Post-processing is disabled; the Player logs include stripped/unsupported post-processing shader notices.

The static baseline scene and build scene list were preserved. Four build-generated settings files were archived and restored to the task starting bytes. The new replay scene had trailing spaces removed after the build; the provenance records both hashes and confirms equal content after whitespace normalisation. The final Editor check was Edit mode, a clean replay scene, native High and preview enabled. A temporary external-scene-change modal and MCP connection warnings were resolved; the warnings were archived before clearing the Console, whose next check returned no errors or warnings.

![Occluder-only inspection preview](../Captures/OccluderReplay/2026-11-10/preview.gif)

This derived preview uses every third source index. Its playback rate and GIF colours are inspection aids; original full-resolution PNGs remain the comparison evidence.

This step does not evaluate temporal quality, attach delayed GPU timings to moving source frames, establish performance benefits, validate full-evaluation equivalence or complete the camera/light motion and held-out cases required for M2.
