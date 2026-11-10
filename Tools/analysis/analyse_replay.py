"""Validate recorded occluder replay state and images; this is not a benchmark.

Requires Python 3 and Pillow. The optional positional argument is the directory
containing forward_01, forward_02, reverse_01 and seek_01. No input is modified.
An existing raw_sha256.json in the output directory is verified before reuse.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import sys

from PIL import Image, ImageChops


REPOSITORY = Path(__file__).resolve().parents[2]
DEFAULT_ROOT = REPOSITORY / "Captures/OccluderReplay/2026-11-10"
RUNS = ("forward_01", "forward_02", "reverse_01", "seek_01")
SEEK = [120, 0, 60, 15, 105, 30, 90, 45, 75, 0, 60, 120]
ORDERS = {
    "forward_01": list(range(121)),
    "forward_02": list(range(121)),
    "reverse_01": list(range(120, -1, -1)),
    "seek_01": SEEK,
}
TOLERANCE = 1e-5
SIZE = (1920, 1080)
SHA_PATTERN = re.compile(r"[0-9a-f]{64}")
CSV_FIELDS = [
    "sequence", "frame_index", "simulation_time_seconds", "applied_unity_frame",
    "capture_frame", "applied_index_before", "applied_index_after",
    "expected_x", "expected_y", "expected_z", "actual_x", "actual_y", "actual_z",
    "static_controls_hash", "post_capture_controls_hash", "png_sha256",
    "width", "height", "success", "png_file", "metadata_file", "error",
]
CONTROL_VALUES = {
    "scenePath": "Assets/Scenes/PCF_OccluderReplay.unity",
    "cameraPath": "Main Camera", "lightPath": "Main Directional Light",
    "pipelineName": "PCF_Baseline_URP", "lightMode": "Soft",
    "perLightQuality": "High", "lightType": "Directional",
    "renderScale": 1, "msaaSamples": 1, "pipelineHDR": False,
    "shadowResolution": 2048, "cascades": 1, "shadowDistance": 50,
    "depthBiasInput": 0.1, "normalBiasInput": 0.5,
    "lightUsesPipelineBias": True, "lightStrength": 1, "lightIntensity": 1,
    "cameraFOV": 45, "cameraNear": 0.1, "cameraFar": 100,
    "cameraHDR": False, "cameraMSAA": False, "cameraDynamicResolution": False,
    "cameraPostProcessing": False, "cameraAntialiasing": "None",
    "screenWidth": 1920, "screenHeight": 1080,
    "cameraPixelWidth": 1920, "cameraPixelHeight": 1080,
    "vSyncCount": 0, "targetFrameRate": -1, "activeColorSpace": "Linear",
    "fog": False,
}
BASELINE_TRANSFORMS = {
    "Main Camera": ((8, 7, -11), (1, 1, 1),
                    (0.19834514, -0.2889616, 0.061327495, 0.9345584)),
    "Main Directional Light": ((0, 0, 0), (1, 1, 1),
                               (0.11507514, 0.88111955, -0.3649717, 0.27781594)),
    "Experiment Settings": ((0, 0, 0), (1, 1, 1), (0, 0, 0, 1)),
    "Geometry": ((0, 0, 0), (1, 1, 1), (0, 0, 0, 1)),
    "Geometry/Receiver_Plane": ((0, 0, 0), (2, 1, 2), (0, 0, 0, 1)),
    "Geometry/Thin_Wall": ((2.5, 1, 0.5), (0.2, 2, 3), (0, 0, 0, 1)),
    "Geometry/Thin_Post_008": ((-0.7, 1, 2), (0.08, 2, 0.08), (0, 0, 0, 1)),
    "Geometry/Thin_Post_015": ((0.1, 1, 2), (0.15, 2, 0.15), (0, 0, 0, 1)),
    "Geometry/Thin_Post_030": ((1, 1, 2), (0.3, 2, 0.3), (0, 0, 0, 1)),
}


class ValidationError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ValidationError(message)


def reject_constant(value):
    raise ValidationError("Non-finite JSON value: " + value)


def unique_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key: " + key)
        result[key] = value
    return result


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"),
                      object_pairs_hook=unique_keys, parse_constant=reject_constant)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def semantic_bytes(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False,
                      separators=(",", ":")).encode("utf-8")


def finite_tree(value, label):
    if isinstance(value, float):
        require(math.isfinite(value), label + " contains a non-finite number")
    elif isinstance(value, dict):
        for key, child in value.items():
            finite_tree(child, label + "." + key)
    elif isinstance(value, list):
        for child in value:
            finite_tree(child, label)


def close(actual, expected, label, tolerance=TOLERANCE):
    require(isinstance(actual, (int, float)) and not isinstance(actual, bool)
            and math.isfinite(actual), label + " is not a finite number")
    require(abs(actual - expected) <= tolerance,
            f"{label}: {actual!r} differs from {expected!r}")


def vector(value, keys, label):
    require(isinstance(value, dict) and set(value) == set(keys),
            label + " has an unexpected vector schema")
    result = tuple(value[key] for key in keys)
    for number in result:
        require(isinstance(number, (int, float)) and not isinstance(number, bool)
                and math.isfinite(number), label + " has invalid components")
    return result


def check_vector(value, expected, label):
    actual = vector(value, "xyz", label)
    require(math.dist(actual, expected) <= TOLERANCE,
            f"{label}: {actual!r} differs from {expected!r}")


def check_rotation(value, expected, label):
    actual = vector(value, "xyzw", label)
    require(abs(sum(v * v for v in actual) - 1) <= 1e-4,
            label + " is not a unit quaternion")
    # q and -q describe the same rotation.
    require(min(math.dist(actual, expected),
                math.dist(actual, tuple(-v for v in expected))) <= TOLERANCE,
            label + " differs from the fixed rotation")


def trajectory(index):
    progress = index / 60 if index <= 60 else (120 - index) / 60
    return (-3.25 + 2.5 * progress, 1, -1)


def check_diagnostics(data, label):
    finite_tree(data, label)
    require(data["mainLightShadows"] is True, label + ": main shadows disabled")
    require(data["softShadows"] is True or data["softHigh"] is True,
            label + ": no native High soft-shadow path")
    close(data["mainLightShadowParams"]["x"], 1, label + ".strength")
    close(data["mainLightShadowParams"]["y"], 3, label + ".quality")
    size = data["mainLightShadowmapSize"]
    for key, expected in (("x", 1 / 2048), ("y", 1 / 2048),
                          ("z", 2048), ("w", 2048)):
        close(size[key], expected, label + ".map." + key, 1e-8)


def check_controls(controls):
    finite_tree(controls, "fixedControls")
    for key, expected in CONTROL_VALUES.items():
        actual = controls[key]
        if isinstance(expected, bool) or isinstance(expected, str):
            require(actual == expected, f"fixedControls.{key}: {actual!r}")
        else:
            close(actual, expected, "fixedControls." + key)
    transforms = controls["staticTransforms"]
    paths = [entry["path"] for entry in transforms]
    require(len(paths) == len(set(paths)), "Duplicate fixed-transform path")
    require(set(paths) == set(BASELINE_TRANSFORMS),
            "Fixed-transform set differs from the recorded baseline")
    for item in transforms:
        position, scale, rotation = BASELINE_TRANSFORMS[item["path"]]
        check_vector(item["position"], position, item["path"] + ".position")
        check_vector(item["localScale"], scale, item["path"] + ".scale")
        check_rotation(item["rotation"], rotation, item["path"] + ".rotation")
        require(item["activeSelf"] is True and item["activeInHierarchy"] is True,
                item["path"] + " is inactive")
    renderers = controls["renderers"]
    renderer_paths = [entry["path"] for entry in renderers]
    expected_renderers = {path for path in BASELINE_TRANSFORMS
                          if path.startswith("Geometry/")} | {"Geometry/Block"}
    require(len(renderer_paths) == len(set(renderer_paths))
            and set(renderer_paths) == expected_renderers,
            "Renderer set differs from the baseline geometry")
    for renderer in renderers:
        require(renderer["type"] == "MeshRenderer" and renderer["enabled"] is True,
                renderer["path"] + " is not an enabled baseline MeshRenderer")
        require(renderer["materials"], renderer["path"] + " has no materials")
        for material in renderer["materials"]:
            require(material["shader"] == "Universal Render Pipeline/Lit"
                    and material["hasBaseColor"] is True,
                    renderer["path"] + " has an unexpected material")
    projection = controls["cameraProjection"]
    require(set(projection) == {f"e{r}{c}" for r in range(4) for c in range(4)},
            "Unexpected projection-matrix schema")
    vertical = 1 / math.tan(math.radians(45) / 2)
    near, far = 0.1, 100
    expected = {"e00": vertical / (1920 / 1080), "e11": vertical,
                "e22": -(far + near) / (far - near),
                "e23": -2 * far * near / (far - near), "e32": -1}
    for key in projection:
        close(projection[key], expected.get(key, 0), "cameraProjection." + key)


def png_details(path):
    with Image.open(path) as source:
        require(source.format == "PNG", str(path) + " is not a PNG")
        source.load()
        require(source.size == SIZE, str(path) + " has incorrect decoded dimensions")
        pixels = source.convert("RGBA").tobytes()
    return hashlib.sha256(pixels).hexdigest()


def same_image(left, right):
    # Direct bytes comparison supplements the separately verified SHA values.
    png_equal = left.read_bytes() == right.read_bytes()
    with Image.open(left) as a, Image.open(right) as b:
        rgba_equal = a.convert("RGBA").tobytes() == b.convert("RGBA").tobytes()
    require(png_equal and rgba_equal,
            f"Image differs: {left.name} versus {right}")
    return {"png_bytes_exact": png_equal, "decoded_rgba_exact": rgba_equal}


def changed_pixels(left, right):
    with Image.open(left) as a, Image.open(right) as b:
        channels = ImageChops.difference(a.convert("RGBA"),
                                        b.convert("RGBA")).split()
        mask = channels[0]
        for channel in channels[1:]:
            mask = ImageChops.lighter(mask, channel)
        count = SIZE[0] * SIZE[1] - mask.histogram()[0]
        bounds = mask.getbbox()
    require(count > 0, "Frames 0 and 60 show no changed RGBA pixels")
    return {"changed_pixel_count": count, "bounding_box": bounds,
            "meaning": "Visible-change diagnostic, not a temporal quality metric"}


def inspect_run(root, name, inventory):
    directory = root / name
    require(directory.is_dir(), f"Run directory is absent: {directory}")
    manifest_path = directory / "replay_manifest.json"
    csv_path = directory / "frames.csv"
    manifest = load_json(manifest_path)
    require(manifest["state"] == "completed" and not manifest.get("error"),
            "Manifest does not report error-free completion")
    require(manifest["initialTransformRestored"] is True
            and manifest["previewAfterCapture"] is False,
            "Initial transform was not restored or preview remains active")
    expected_order = ORDERS[name]
    expected_name = "seek" if name == "seek_01" else name.split("_")[0]
    require(manifest["requestedOrderName"].lower() == expected_name
            and manifest["requestedOrder"] == expected_order,
            "Requested order differs from the protocol")
    require(all(type(index) is int for index in manifest["requestedOrder"]),
            "Requested source indices must be integers")
    for key, expected in (("logicalHz", 60), ("firstFrame", 0), ("lastFrame", 120),
                          ("endOfFrameWaitsPerIndex", 2),
                          ("startupSettlingFrames", 30)):
        require(manifest[key] == expected, "Unexpected manifest." + key)
    require(manifest["movingObjectPath"] == "Geometry/Block",
            "Unexpected moving-object path")
    check_vector(manifest["pathCentre"], (-2, 1, -1), "pathCentre")
    close(manifest["amplitude"], 1.25, "amplitude")
    check_vector(manifest["initialPosition"], (-2, 1, -1), "initialPosition")
    check_vector(manifest["initialScale"], (1.5, 2, 1.5), "initialScale")
    check_rotation(manifest["initialRotation"], (0, 0, 0, 1), "initialRotation")
    device = manifest["device"]
    require(device["platform"] == "WindowsPlayer"
            and device["graphicsAPI"] == "Direct3D11"
            and device["unityVersion"] == "6000.6.5f1"
            and device["runInBackground"] is True, "Unexpected device configuration")
    controls = manifest["fixedControls"]
    check_controls(controls)
    controls_hash = manifest["fixedControlsHash"]
    require(isinstance(controls_hash, str) and SHA_PATTERN.fullmatch(controls_hash),
            "Invalid fixedControlsHash")
    check_diagnostics(manifest["startupShaderDiagnostics"], "startupShaderDiagnostics")
    check_diagnostics(manifest["finalShaderDiagnostics"], "finalShaderDiagnostics")
    with csv_path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        require(reader.fieldnames == CSV_FIELDS, "CSV does not have the 22-column schema")
        rows = list(reader)
    require(all(None not in row and all(value is not None for value in row.values())
                for row in rows), "CSV row width differs from the header")
    frames = manifest["frames"]
    require(len(frames) == len(rows) == len(expected_order), "Capture counts disagree")
    expected_files = {"replay_manifest.json", "frames.csv"}
    checked_frames = []
    previous_capture = -1
    for sequence, (index, frame, row) in enumerate(zip(expected_order, frames, rows)):
        label = f"{name}: request {sequence}, source {index}"
        stem = f"s{sequence:03d}_f{index:03d}"
        png_name, json_name = stem + ".png", stem + ".json"
        expected_files.update((png_name, json_name))
        require(frame["pngFile"] == png_name and frame["metadataFile"] == json_name,
                label + ": filename does not preserve request identity")
        require(load_json(directory / json_name) == frame,
                label + ": individual JSON differs from manifest.frames")
        finite_tree(frame, label)
        require(frame["success"] is True and not frame.get("error"),
                label + ": frame reports a failure")
        require(frame["sequence"] == sequence and frame["frameIndex"] == index,
                label + ": request/source index mismatch")
        require(frame["appliedIndexBeforeCapture"] == index
                and frame["appliedIndexAfterCapture"] == index,
                label + ": applied index changed during capture")
        require(type(frame["appliedUnityFrame"]) is int
                and type(frame["captureFrame"]) is int
                and frame["captureFrame"] > frame["appliedUnityFrame"] >= 0
                and frame["captureFrameAfterReadback"] == frame["captureFrame"]
                and frame["captureFrame"] > previous_capture,
                label + ": render/capture frame identity is inconsistent")
        previous_capture = frame["captureFrame"]
        close(frame["simulationTimeSeconds"], index / 60, label + ".logicalTime", 1e-12)
        check_vector(frame["expectedPosition"], trajectory(index), label + ".expected")
        check_vector(frame["actualPosition"], trajectory(index), label + ".actual")
        for prefix in ("expected", "actual"):
            check_rotation(frame[prefix + "Rotation"], (0, 0, 0, 1),
                           label + "." + prefix + "Rotation")
            check_vector(frame[prefix + "Scale"], (1.5, 2, 1.5),
                         label + "." + prefix + "Scale")
        require(frame["staticControlsUnchanged"] is True
                and frame["staticControlsHash"] == controls_hash
                and frame["postCaptureControlsHash"] == controls_hash,
                label + ": fixed controls hash changed")
        check_diagnostics(frame["shaderDiagnostics"], label + ".shaderDiagnostics")
        require((frame["width"], frame["height"]) == SIZE,
                label + ": metadata dimensions differ")
        for column, field in (
            ("sequence", "sequence"), ("frame_index", "frameIndex"),
            ("applied_unity_frame", "appliedUnityFrame"),
            ("capture_frame", "captureFrame"),
            ("applied_index_before", "appliedIndexBeforeCapture"),
            ("applied_index_after", "appliedIndexAfterCapture"),
            ("width", "width"), ("height", "height"),
        ):
            require(int(row[column]) == frame[field], label + ": CSV " + column)
        close(float(row["simulation_time_seconds"]), frame["simulationTimeSeconds"],
              label + ": CSV logical time", 1e-12)
        for prefix in ("expected", "actual"):
            for axis in "xyz":
                close(float(row[prefix + "_" + axis]), frame[prefix + "Position"][axis],
                      label + ": CSV " + prefix + "_" + axis)
        for column, field in (
            ("static_controls_hash", "staticControlsHash"),
            ("post_capture_controls_hash", "postCaptureControlsHash"),
            ("png_sha256", "pngSha256"), ("png_file", "pngFile"),
            ("metadata_file", "metadataFile"),
        ):
            require(row[column] == frame[field], label + ": CSV " + column)
        require(row["success"] == "true" and not row["error"],
                label + ": CSV does not report success")
        png_path = directory / png_name
        require(digest(png_path) == frame["pngSha256"],
                label + ": PNG SHA-256 differs from metadata")
        rgba_sha = png_details(png_path)
        checked_frames.append({"sequence": sequence, "frame_index": index,
                               "png_file": png_name, "metadata_file": json_name,
                               "png_sha256": frame["pngSha256"],
                               "decoded_rgba_sha256": rgba_sha})
    present_files = {path.name for path in directory.iterdir()
                     if path.is_file() and path.suffix.lower() in (".json", ".csv", ".png")}
    require(present_files == expected_files, "Unexpected or missing raw protocol files")
    for filename in sorted(expected_files):
        path = directory / filename
        inventory[path.relative_to(root).as_posix()] = digest(path)
    first_by_index = {}
    for frame in checked_frames:
        first_by_index.setdefault(frame["frame_index"], directory / frame["png_file"])
    endpoint = same_image(first_by_index[0], first_by_index[120])
    change = changed_pixels(first_by_index[0], first_by_index[60])
    result = {
        "run": name, "status": "passed", "order": expected_name,
        "requested_and_validated_captures": len(frames), "csv_columns": len(CSV_FIELDS),
        "fixed_controls_hash": controls_hash,
        "fixed_controls_semantic_sha256": hashlib.sha256(semantic_bytes(controls)).hexdigest(),
        "first_last_endpoint_comparison": endpoint, "frame_0_vs_60": change,
        "initial_transform_restored": True, "preview_after_capture": False,
        "frames": checked_frames, "device": device,
    }
    return result, manifest


def cross_checks(root, results, manifests):
    baseline = results["forward_01"]
    controls = manifests["forward_01"]["fixedControls"]
    reference = {entry["frame_index"]: entry for entry in baseline["frames"]}
    pairs = []
    for name in RUNS:
        require(manifests[name]["fixedControls"] == controls,
                name + ": fixedControls content differs across processes")
        require(manifests[name]["fixedControlsHash"] == manifests["forward_01"]["fixedControlsHash"],
                name + ": fixedControlsHash differs across processes")
        for field in ("pathCentre", "amplitude", "initialPosition", "initialRotation",
                      "initialScale", "movingObjectPath"):
            require(manifests[name][field] == manifests["forward_01"][field],
                    name + ": source movement baseline differs: " + field)
        if name == "forward_01":
            continue
        for frame in results[name]["frames"]:
            ref = reference[frame["frame_index"]]
            require(frame["png_sha256"] == ref["png_sha256"]
                    and frame["decoded_rgba_sha256"] == ref["decoded_rgba_sha256"],
                    name + ": source-index image hash differs")
            equality = same_image(root / "forward_01" / ref["png_file"],
                                  root / name / frame["png_file"])
            pairs.append({"run": name, "sequence": frame["sequence"],
                          "frame_index": frame["frame_index"], **equality})
    return {"status": "passed", "pairs_checked": len(pairs),
            "fixed_controls_semantic_exact": True,
            "png_bytes_and_decoded_rgba_exact": True,
            "seek_repeated_indices_preserved": len(results["seek_01"]["frames"]) == 12,
            "pairs": pairs}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False)
                    + "\n", encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", nargs="?", type=Path, default=DEFAULT_ROOT,
                        help="Directory containing the four protocol run directories.")
    parser.add_argument("--output-dir", type=Path,
                        help="Analysis output directory; defaults to ROOT/analysis.")
    parser.add_argument("--verify-hashes", type=Path,
                        help="Also verify an earlier raw SHA-256 inventory.")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    output = (args.output_dir or root / "analysis").resolve()
    # Do not allow analysis products to overwrite a protocol run.
    require(all(output != root / name and root / name not in output.parents for name in RUNS),
            "Output directory must be outside the four raw run directories")
    output.mkdir(parents=True, exist_ok=True)
    results, manifests, inventory = {}, {}, {}
    for name in RUNS:
        try:
            result, manifest = inspect_run(root, name, inventory)
            results[name], manifests[name] = result, manifest
        except Exception as error:
            results[name] = {"run": name, "status": "failed", "error": str(error)}
        write_json(output / (name + "_analysis.json"), results[name])
        print(json.dumps({key: value for key, value in results[name].items()
                          if key not in ("frames", "device")}))
    all_runs_passed = all(result["status"] == "passed" for result in results.values())
    comparisons = {"status": "not_checked", "reason": "One or more raw runs failed"}
    if all_runs_passed:
        try:
            comparisons = cross_checks(root, results, manifests)
        except Exception as error:
            comparisons = {"status": "failed", "error": str(error)}
    hashes = {"algorithm": "sha256", "paths_relative_to": "replay input root",
              "scope": "Raw protocol CSV, JSON and PNG files; excludes Player logs",
              "files": dict(sorted(inventory.items()))}
    hash_errors = []
    hash_path = output / "raw_sha256.json"
    verification_paths = [path for path in (hash_path, args.verify_hashes)
                          if path is not None and path.exists()]
    if args.verify_hashes and not args.verify_hashes.exists():
        hash_errors.append("Requested hash inventory is absent: " + str(args.verify_hashes))
    for previous_path in verification_paths:
        try:
            previous = load_json(previous_path)
            require(previous["algorithm"] == "sha256"
                    and previous["files"] == hashes["files"],
                    "Raw file identities/bytes differ from the earlier inventory")
        except Exception as error:
            hash_errors.append(str(previous_path) + ": " + str(error))
    # Preserve an earlier baseline inventory when verification fails.
    if not hash_path.exists():
        write_json(hash_path, hashes)
    elif hash_errors:
        write_json(output / "raw_sha256_observed.json", hashes)
    success = all_runs_passed and comparisons["status"] == "passed" and not hash_errors
    summary = {
        "status": "passed" if success else "failed",
        "classification": "Replay state/image reproducibility only; no performance, temporal-quality or M2 claim",
        "requested_runs": list(RUNS), "expected_capture_count": 375,
        "validated_capture_count": sum(result.get("requested_and_validated_captures", 0)
                                       for result in results.values()),
        "position_and_scale_tolerance": TOLERANCE,
        "rotation_tolerance": "1e-5 quaternion component distance, allowing q/-q equivalence",
        "individual_runs": {name: {key: value for key, value in result.items()
                                  if key not in ("frames", "device")}
                            for name, result in results.items()},
        "cross_run_checks": comparisons,
        "raw_hash_inventory_file": hash_path.name,
        "raw_hash_files": len(inventory), "raw_hash_verification_errors": hash_errors,
        "limitations": [
            "Fixed-control hashes bind reported frame states to the runtime manifest; no per-draw GPU-state proof.",
            "Unity JsonUtility hash bytes are not reconstructed with Python's different JSON serializer.",
            "Logical 60 Hz and source index are not measured FPS or a GPU timing frame association.",
            "Only the occluder-motion case is covered; camera/light motion, held-out evaluation and M2 remain open.",
        ],
    }
    write_json(output / "replay_summary.json", summary)
    print(json.dumps({"overall": summary["status"],
                      "validated_captures": summary["validated_capture_count"],
                      "cross_run_pairs": comparisons.get("pairs_checked", 0),
                      "raw_hash_files": len(inventory),
                      "output_directory": str(output)}))
    return 0 if success else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("Replay analysis failed: " + str(error), file=sys.stderr)
        sys.exit(1)
