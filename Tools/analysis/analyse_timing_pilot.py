"""Validate fixed-duration High timing pilots without treating them as benchmarks.

Python 3 and NumPy are required. Inputs are read only. Existing raw SHA inventories
are verified before output is updated. GPU telemetry describes the whole device.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import sys

import numpy as np

REPO = Path(__file__).resolve().parents[2]
DEFAULT_ROOT = REPO / "Measurements/FrameTimingPilot/2026-11-10"
RUNS = ("High_20s_01", "High_20s_02", "High_20s_03")
AUXILIARY = ("High_recovery_5s", "High_smoke_compat")
ALL_RUNS = RUNS + AUXILIARY
FIELDS = ("phase,poll_index,unity_frame,realtime_seconds,focused,screen_width,screen_height,"
          "returned_count,accepted,reasons,controls_error,frame_start_timestamp,cpu_frame_time_ms,"
          "gpu_frame_time_ms,cpu_main_thread_frame_time_ms,cpu_render_thread_frame_time_ms,"
          "cpu_main_thread_present_wait_time_ms,cpu_time_frame_complete,cpu_time_present_called,"
          "first_submit_timestamp,width_scale,height_scale,sync_interval").split(",")
CONTROL = {
    "platform": "WindowsPlayer", "graphicsAPI": "Direct3D11", "pipeline": "PCF_Baseline_URP",
    "expectedPreset": "High", "perLightQuality": "High", "lightShadows": "Soft",
    "screenWidth": 1920, "screenHeight": 1080, "cameraPixelWidth": 1920,
    "cameraPixelHeight": 1080, "vSyncCount": 0, "targetFrameRate": -1,
    "renderScale": 1, "msaaSamples": 1, "mainShadowResolution": 2048,
    "cascades": 1, "shadowDistance": 50, "cameraHDR": False, "cameraMSAA": False,
    "cameraDynamicResolution": False, "cameraPostProcessing": False,
    "cameraAntialiasing": "None", "timingFeatureEnabled": True,
    "mainLightShadowKeyword": True, "softShadowKeyword": True,
    "runInBackground": True, "activeColorSpace": "Linear", "lightStrength": 1,
    "usesPipelineBias": True,
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def positive(value):
    return math.isfinite(value) and value > 0


def stats(values):
    if not values:
        return {"count": 0, "min": None, "median": None, "p95": None, "max": None}
    a = np.asarray(values, dtype=float)
    require(np.isfinite(a).all(), "Non-finite value in descriptive statistics")
    return {"count": len(a), "min": float(a.min()), "median": float(np.median(a)),
            "p95": float(np.percentile(a, 95, method="linear")), "max": float(a.max()),
            "mean": float(a.mean()), "sample_sd": float(a.std(ddof=1)) if len(a) > 1 else None}


def check_snapshot(snapshot):
    for field, expected in CONTROL.items():
        require(snapshot[field] == expected, f"Control changed: {field}={snapshot[field]!r}")
    for key, expected in (("pipelineDepthBiasInput", .1), ("pipelineNormalBiasInput", .5),
                          ("cameraFOV", 45)):
        require(abs(snapshot[key] - expected) < 1e-6, f"Unexpected {key}")
    params = snapshot["mainLightShadowParams"]
    require(params["x"] == 1 and params["y"] == 3, "Rendered native High parameters missing")


def classify_rows(rows):
    """Independently reconstruct the runtime identity/exclusion policy in row order."""
    seen, maximum = set(), 0
    for sequence, row in enumerate(rows):
        require(int(row["poll_index"]) == sequence, "Poll indices are not contiguous")
        require(row["phase"] in ("warmup", "drain", "measurement"), "Unknown phase")
        require(row["focused"] in ("true", "false"), "Invalid focus flag")
        require(row["accepted"] in ("true", "false"), "Invalid acceptance flag")
        require((int(row["screen_width"]), int(row["screen_height"])) == (1920, 1080),
                "Resolution changed during polling")
        reasons = []
        returned = int(row["returned_count"])
        require(returned in (0, 1), "Latest-one collector returned an unexpected count")
        if returned:
            timestamp = int(row["frame_start_timestamp"])
            previous, nonmonotonic = timestamp in seen, timestamp <= maximum
            if timestamp:
                seen.add(timestamp)
                maximum = max(maximum, timestamp)
            if not timestamp:
                reasons.append("timestamp_zero")
            elif previous:
                reasons.append("duplicate_timestamp")
            elif nonmonotonic:
                reasons.append("non_monotonic_timestamp")
            if not positive(float(row["cpu_frame_time_ms"])):
                reasons.append("cpu_not_positive_finite")
            if not positive(float(row["gpu_frame_time_ms"])):
                reasons.append("gpu_not_positive_finite")
        else:
            require(all(not row[field] for field in FIELDS[11:]), "Empty return has timing values")
            reasons.append("no_timing_returned")
        if row["controls_error"]:
            reasons.append("controls_invalid")
        if row["phase"] != "measurement":
            reasons.append(row["phase"] + "_excluded")
        require(row["reasons"] == ";".join(reasons), "Raw reason disagrees with independent policy")
        require((row["accepted"] == "true") == (row["phase"] == "measurement" and not reasons),
                "Acceptance flag disagrees with independent policy")


def describe(rows, cpu_hz):
    accepted = [r for r in rows if r["accepted"] == "true"]
    returned = [r for r in rows if int(r["returned_count"]) > 0]
    by_id = defaultdict(list)
    for row in returned:
        timestamp = int(row["frame_start_timestamp"])
        if timestamp:
            by_id[timestamp].append(row)
    zero_ids, unavailable_ids, recovered = [], [], []
    for timestamp, observed in by_id.items():
        zero_seen = False
        has_positive = False
        for row in observed:
            value = float(row["gpu_frame_time_ms"])
            if value == 0:
                zero_seen = True
            elif positive(value):
                has_positive = True
                if zero_seen:
                    recovered.append(timestamp)
                    break
        if zero_seen:
            zero_ids.append(timestamp)
        if not has_positive:
            unavailable_ids.append(timestamp)
    zero_polls = sum(float(r["gpu_frame_time_ms"]) == 0 for r in returned)
    impossible = [{"poll_index": int(r["poll_index"]), "phase": r["phase"],
                   "frame_start_timestamp": r["frame_start_timestamp"],
                   "gpu_frame_time_ms": float(r["gpu_frame_time_ms"]),
                   "application_realtime_ms_at_poll": float(r["realtime_seconds"]) * 1000,
                   "runtime_accepted": r["accepted"] == "true"}
                  for r in returned if math.isfinite(float(r["gpu_frame_time_ms"]))
                  and float(r["gpu_frame_time_ms"]) > float(r["realtime_seconds"]) * 1000]
    ticks = [int(r["frame_start_timestamp"]) for r in accepted]
    cpu = [float(r["cpu_frame_time_ms"]) for r in accepted]
    gpu = [float(r["gpu_frame_time_ms"]) for r in accepted]
    return {
        "polls": len(rows), "returned_polls": len(returned), "accepted_unique_samples": len(accepted),
        "excluded_polls": len(rows) - len(accepted), "gpu_zero_polls": zero_polls,
        "excluded_poll_fraction": (len(rows) - len(accepted)) / len(rows) if rows else None,
        "gpu_zero_poll_fraction": zero_polls / len(rows) if rows else None,
        "nonzero_timestamp_ids_observed": len(by_id), "ids_ever_gpu_zero": len(zero_ids),
        "ids_without_any_positive_gpu_return": len(unavailable_ids),
        "unique_id_gpu_unavailable_fraction": len(unavailable_ids) / len(by_id) if by_id else None,
        "zero_then_positive_ids": sorted(set(recovered)),
        "focus_true_polls": sum(r["focused"] == "true" for r in rows),
        "focus_true_accepted": sum(r["focused"] == "true" for r in accepted),
        "rejection_counts": dict(Counter(r["reasons"] for r in rows if r["accepted"] != "true")),
        "gpu_whole_frame_ms": stats(gpu), "cpu_whole_frame_ms": stats(cpu),
        "gpu_duration_exceeds_application_lifetime": impossible,
        "gpu_mean_sd_interpretation": "Uninterpretable as rendering cost: raw policy-accepted returns include impossible positive durations" if impossible else "Descriptive, conditional on runtime-valid returns only",
        "accepted_cpu_clock_span_seconds": (max(ticks) - min(ticks)) / cpu_hz if ticks else None,
        "timestamp_id_scope": "Nonzero IDs actually returned by latest-one API, not all rendered frames",
    }


def time_blocks(rows, origin, seconds, block_seconds, cpu_hz):
    count = math.ceil(seconds / block_seconds)
    buckets = [[] for _ in range(count)]
    outside = 0
    for row in rows:
        relative = float(row["realtime_seconds"]) - origin
        index = math.floor(relative / block_seconds)
        if index < 0 or index >= count:
            outside += 1
        buckets[min(max(index, 0), count - 1)].append(row)
    return {"clock": "poll realtime; not delayed timing source-frame time",
            "outside_interval_rows_assigned_to_end_blocks": outside,
            "blocks": [{"start_seconds": i * block_seconds,
                        "end_seconds": min((i + 1) * block_seconds, seconds),
                        **describe(bucket, cpu_hz)} for i, bucket in enumerate(buckets)]}


def telemetry(path):
    if not path.exists():
        return {"status": "not_provided", "scope": "Whole-device telemetry, not Player-only"}
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        rows = list(reader)
        fields = reader.fieldnames or []
    metrics = {}
    for field in fields:
        if not any(word in field.lower() for word in ("utilization", "temperature", "clock", "power")):
            continue
        values = []
        for row in rows:
            match = re.fullmatch(r"\s*([-+]?\d+(?:\.\d+)?)\s*(?:%|W|MHz)?\s*", row.get(field) or "")
            if match:
                values.append(float(match.group(1)))
        metrics[field] = {**stats(values), "unparsed_or_missing_rows": len(rows) - len(values)}
    return {"status": "recorded", "rows": len(rows), "fields": fields, "metrics": metrics,
            "scope": "Whole GPU with ambient applications over the process, including startup/warmup/measurement. Not an exact measurement-window trace or Player-only attribution; timestamp timezone is unconfirmed and not aligned to source frames."}


def recovery(folder, report, rows):
    data = read_json(folder / "timing_recovery.json")
    require(not data.get("error") and data["historyRequested"] == 10, "Recovery run failed or wrong history request")
    require(data["historyPolls"] == len(rows), "Recovery history polls disagree with raw row count")
    low, high, total = data["actualReturnedMin"], data["actualReturnedMax"], int(data["actualReturnedTotal"])
    require(0 <= low <= high <= 10 and low * len(rows) <= total <= high * len(rows), "Invalid history return counts")
    zeros = data["zeroRecords"]
    ids = [int(z["frameStartTimestamp"]) for z in zeros]
    require(all(i > 0 for i in ids) and len(ids) == len(set(ids)) == data["zeroTimestampCount"], "Invalid recovery IDs/count")
    primary_zero_ids = {int(r["frame_start_timestamp"]) for r in rows if r["returned_count"] == "1"
                        and int(r["frame_start_timestamp"]) > 0 and float(r["gpu_frame_time_ms"]) == 0}
    require(primary_zero_ids <= set(ids), "A primary zero timestamp was not seeded into recovery tracking")
    recovered = []
    for z in zeros:
        first = rows[z["firstPoll"]]
        require(z["firstPhase"] == first["phase"] and -1 <= z["firstSlot"] < 10, "Recovery first-event identity mismatch")
        require(z["firstSource"] in ("latest_poll", "history_probe"), "Unknown recovery source")
        if z["latestWasZero"]:
            require(first["frame_start_timestamp"] == z["frameStartTimestamp"]
                    and float(first["gpu_frame_time_ms"]) == 0, "Primary seed disagrees with raw CSV")
        require(bool(z["primaryObservedZero"]) == (int(z["frameStartTimestamp"]) in primary_zero_ids),
                "Primary zero observation flag disagrees with raw CSV")
        if z["recovered"]:
            last = rows[z["firstRecoveredPoll"]]
            require(z["firstRecoveredPoll"] >= z["firstPoll"] and z["firstRecoveredPhase"] == last["phase"]
                    and 0 <= z["firstRecoveredSlot"] < high, "Recovery endpoint identity mismatch")
            delay = z["firstRecoveredRealtime"] - z["firstRealtime"]
            require(delay >= 0 and abs(z["recoveryDelaySeconds"] - delay) < 1e-8
                    and positive(z["recoveredGpuFrameTimeMs"]) and positive(z["recoveredCpuFrameTimeMs"]), "Invalid recovered values")
            recovered.append(z)
        else:
            require(z["firstRecoveredPoll"] == -1 and z["recoveryDelaySeconds"] == -1, "Unrecovered entry has recovery identity")
    require(len(recovered) == data["recoveredCount"] and len(zeros) - len(recovered) == data["unrecoveredCount"], "Recovery totals disagree")
    return {"validation": "passed", "history_requested": 10, "actual_returned_min": low,
            "actual_returned_max": high, "actual_returned_total": total,
            "zero_ids": len(zeros), "recovered_ids": len(recovered), "unrecovered_ids": len(zeros) - len(recovered),
            "primary_observed_zero_ids": len(primary_zero_ids),
            "primary_zero_ids_recovered": sum(z["primaryObservedZero"] for z in recovered),
            "first_zero_phase_counts": dict(Counter(z["firstPhase"] for z in zeros)),
            "recovered_by_first_zero_phase": dict(Counter(z["firstPhase"] for z in recovered)),
            "recovery_delay_seconds": stats([z["recoveryDelaySeconds"] for z in recovered]),
            "meaning": "Reported timestamp-linked history recovery, including same-poll recovery. Unrecovered IDs are right-censored, not proof that recovery is impossible. Extra API overhead; never pool with ordinary timing pilots."}


def inspect(root, name):
    folder = root / name
    report = read_json(folder / "timing_smoke.json")
    with (folder / "observations.csv").open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        require(reader.fieldnames == FIELDS, "Unexpected 23-column CSV schema")
        rows = list(reader)
    require(all(None not in r and all(v is not None for v in r.values()) for r in rows), "Malformed CSV row")
    require(report["state"] == "completed" and not report.get("error"), "Run did not complete")
    smoke = name == "High_smoke_compat"
    criterion = "sample_count" if smoke else "fixed_duration"
    require(report["completionCriterion"] == criterion, "Unexpected completion criterion")
    duration = report["requestedMeasurementSeconds"]
    expected_duration = 0 if smoke else (5 if name == "High_recovery_5s" else 20)
    require(duration == expected_duration and report["measurementSeconds"] >= duration, "Full requested window missing")
    require(report["minimumValidSamples"] == report["requestedSamples"] == 1000, "Minimum valid-sample configuration changed")
    require(report["maximumMeasurementPolls"] == (20000 if smoke else 200000)
            and report["maximumMeasurementSeconds"] == (30 if smoke else duration), "Safety limits disagree with protocol")
    require(report["recoveryHistoryRequested"] == (10 if name == "High_recovery_5s" else 0), "Unexpected history instrumentation")
    require(report["measurementPolls"] < report["maximumMeasurementPolls"], "Poll cap reached")
    require(report["warmupFrames"] >= 300 and report["warmupSeconds"] >= 3 and report["drainFrames"] == 8,
            "Warmup or drain did not satisfy the protocol")
    require(len(rows) == report["totalRecordedPolls"], "CSV/manifest row count mismatch")
    check_snapshot(report["ready"])
    check_snapshot(report["final"])
    for field in ("cameraPosition", "cameraRotation", "lightRotation", "renderingThreadingMode"):
        require(report["ready"][field] == report["final"][field], "Static control changed: " + field)
    classify_rows(rows)
    measure = [r for r in rows if r["phase"] == "measurement"]
    require([r["phase"] for r in rows] == ["warmup"] * report["warmupFrames"] + ["drain"] * 8
            + ["measurement"] * report["measurementPolls"], "Polling phases are out of order")
    require(measure and len(measure) == report["measurementPolls"], "Measurement count mismatch")
    require(sum(r["phase"] == "warmup" for r in rows) == report["warmupFrames"], "Warmup count mismatch")
    require(sum(r["phase"] == "drain" for r in rows) == 8, "Raw drain count mismatch")
    require(all(float(b["realtime_seconds"]) > float(a["realtime_seconds"]) for a, b in zip(rows, rows[1:])),
            "Polling clock is not increasing")
    accepted = [r for r in measure if r["accepted"] == "true"]
    require(len(accepted) == report["acceptedSamples"] and len(accepted) >= report["minimumValidSamples"],
            "Valid sample count does not meet the duration protocol")
    if smoke:
        require(len(accepted) == report["requestedSamples"] == 1000, "Legacy smoke compatibility sample count changed")
    require(int(accepted[0]["frame_start_timestamp"]) == int(report["firstAcceptedTimestamp"])
            and int(accepted[-1]["frame_start_timestamp"]) == int(report["lastAcceptedTimestamp"]),
            "Accepted endpoint IDs disagree")
    require(dict(Counter(r["reasons"] for r in measure if r["accepted"] != "true"))
            == {r["reason"]: r["count"] for r in report["rejections"]}, "Rejections disagree")
    cpu_hz = int(report["ready"]["cpuTimerFrequencyDiagnostic"])
    require(cpu_hz > 0, "CPU timestamp frequency missing")
    origin, end = report["measurementStartedRealtimeSeconds"], report["measurementEndedRealtimeSeconds"]
    require(origin <= float(measure[0]["realtime_seconds"]) <= float(measure[-1]["realtime_seconds"]) <= end
            and abs(end - origin - report["measurementSeconds"]) < 1e-6, "Measurement clock bounds disagree")
    before, after = report["measurementStartGcCollections"], report["measurementEndGcCollections"]
    require(len(before) == len(after) == 3 and all(b >= a for a, b in zip(before, after)), "Invalid GC collection counters")
    result = {"run": name, "validation": "passed", "requested_seconds": duration,
            "measurement_seconds": report["measurementSeconds"], "phase_counts": dict(Counter(r["phase"] for r in rows)),
            "block_origin_realtime_seconds": origin,
            "block_origin_source": "manifest measurement start",
            "gc_collections_during_measurement": [b - a for a, b in zip(before, after)],
            "measurement": describe(measure, cpu_hz),
            "one_second_blocks": time_blocks(measure, origin, duration or report["measurementSeconds"], 1, cpu_hz),
            "five_second_blocks": time_blocks(measure, origin, duration or report["measurementSeconds"], 5, cpu_hz),
            "telemetry": telemetry(folder / "gpu_telemetry.csv"),
            "ready": report["ready"], "final": report["final"]}
    if name == "High_recovery_5s":
        result["history_recovery_diagnostic"] = recovery(folder, report, rows)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", nargs="?", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--verify-hashes", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    output = (args.output_dir or root / "analysis").resolve()
    output.mkdir(parents=True, exist_ok=True)
    inventory = {str(p.relative_to(root)).replace("\\", "/"): digest(p)
                 for name in ALL_RUNS for p in sorted((root / name).glob("*")) if p.is_file()}
    hashes = {"algorithm": "sha256", "paths_relative_to": "pilot input root", "files": inventory}
    hash_path = output / "raw_sha256.json"
    for old in (hash_path, args.verify_hashes):
        if old is not None:
            if old.exists():
                require(read_json(old)["files"] == inventory, "Raw bytes/identities differ from saved hashes: " + str(old))
            elif old == args.verify_hashes:
                raise ValueError("Requested SHA inventory is missing")
    results = {}
    for name in ALL_RUNS:
        try:
            results[name] = inspect(root, name)
        except Exception as error:
            results[name] = {"run": name, "validation": "failed", "error": str(error)}
        write_json(output / (name + "_analysis.json"), results[name])
        print(json.dumps({k: v for k, v in results[name].items() if k in ("run", "validation", "error")}))
    passed = all(r["validation"] == "passed" for r in results.values())
    medians = [results[name]["measurement"]["gpu_whole_frame_ms"]["median"] for name in RUNS] if passed else []
    summary = {"validation": "passed" if passed else "failed", "runs": results,
               "pooled_pilot_run_names": list(RUNS), "separate_auxiliary_run_names": list(AUXILIARY),
               "gpu_run_median_ms": medians,
               "gpu_median_range_over_mean": (max(medians) - min(medians)) / float(np.mean(medians)) if medians else None,
               "raw_file_count": len(inventory), "raw_hash_inventory": hash_path.name,
               "classification": "Fixed-window reliability pilot; no formal benchmark, PCF-only cost, speedup, temporal-quality or M2 claim",
               "reliability_assessment": "not_established_implausible_positive_gpu_timings" if passed and any(results[n]["measurement"]["gpu_duration_exceeds_application_lifetime"] for n in RUNS) else "not_established_missingness_and_ambient_activity_remain",
               "limitations": ["Adjacent frames/time blocks are not independent experiments; no significance test.",
                               "Technical capture validation passing does not establish reliable GPU values; impossible positive durations are retained and flagged, not silently filtered.",
                               "Statistics condition on valid API returns; missingness may be biased.",
                               "FrameTimingManager capture, validation and in-memory logging overhead remain.",
                               "Telemetry describes the whole GPU with ambient applications, not an isolated Player.",
                               "Poll time/Unity frame does not establish the delayed timing source-frame index."]}
    if not hash_path.exists():
        write_json(hash_path, hashes)
    write_json(output / "pilot_summary.json", summary)
    print(json.dumps({"overall": summary["validation"], "raw_files": len(inventory), "output_directory": str(output)}))
    return 0 if passed else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("Pilot analysis failed: " + str(error), file=sys.stderr)
        sys.exit(1)
