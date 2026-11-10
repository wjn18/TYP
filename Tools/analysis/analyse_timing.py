"""Independently inspect exported timing observations; availability is not a benchmark."""
from pathlib import Path
from collections import Counter
import csv
import hashlib
import json
import math
import sys
import argparse
import numpy as np

REPOSITORY = Path(__file__).resolve().parents[2]
ROOT = REPOSITORY / 'Measurements/FrameTiming/2026-11-10/analysis'
EXPECTED = {
    'platform': 'WindowsPlayer', 'graphicsAPI': 'Direct3D11', 'pipeline': 'PCF_Baseline_URP',
    'expectedPreset': 'High', 'perLightQuality': 'High', 'lightShadows': 'Soft',
    'screenWidth': 1920, 'screenHeight': 1080, 'cameraPixelWidth': 1920, 'cameraPixelHeight': 1080,
    'vSyncCount': 0, 'targetFrameRate': -1, 'renderScale': 1, 'msaaSamples': 1,
    'mainShadowResolution': 2048, 'cascades': 1, 'shadowDistance': 50,
    'cameraHDR': False, 'cameraMSAA': False, 'cameraDynamicResolution': False,
    'cameraPostProcessing': False, 'cameraAntialiasing': 'None',
    'timingFeatureEnabled': True, 'mainLightShadowKeyword': True, 'softShadowKeyword': True,
    'runInBackground': True, 'activeColorSpace': 'Linear', 'lightStrength': 1, 'usesPipelineBias': True
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def stats(values):
    a = np.asarray(values, dtype=float)
    return {'count':len(a), 'min':float(a.min()), 'median':float(np.median(a)),
            'p95':float(np.percentile(a, 95, method='linear')), 'max':float(a.max()),
            'mean':float(a.mean()), 'sd':float(a.std(ddof=1)) if len(a)>1 else None}


def inspect(folder):
    report_path, csv_path = folder/'timing_smoke.json', folder/'observations.csv'
    report = json.loads(report_path.read_text(encoding='utf-8-sig'))
    with csv_path.open(encoding='utf-8-sig', newline='') as f:
        reader = csv.DictReader(f)
        if len(reader.fieldnames) != 23:
            raise ValueError(f'Unexpected CSV schema: {folder}')
        rows = list(reader)
    if any(None in row or any(v is None for v in row.values()) for row in rows):
        raise ValueError('CSV rows do not match the header.')
    if report['state'] != 'completed' or report['acceptedSamples'] != report['requestedSamples']:
        raise ValueError(f'Incomplete run: {folder}, state={report["state"]}')
    if len(rows) != report['totalRecordedPolls']:
        raise ValueError('Manifest/raw observation count disagreement.')
    accepted = [r for r in rows if r['accepted']=='true']
    measure = [r for r in rows if r['phase']=='measurement']
    if len(accepted) != 1000 or len(accepted) != report['acceptedSamples']:
        raise ValueError('This protocol requires 1000 valid samples.')
    if report['warmupFrames'] < 300 or report['warmupSeconds'] < 3 or report['drainFrames'] != 8:
        raise ValueError('Warmup/drain protocol did not complete.')
    for phase in ['ready','final']:
        snapshot = report[phase]
        for field, value in EXPECTED.items():
            if snapshot[field] != value:
                raise ValueError(f'{phase}.{field}={snapshot[field]!r}, expected {value!r}')
        for field, value in [('pipelineDepthBiasInput',0.1),('pipelineNormalBiasInput',0.5)]:
            if abs(snapshot[field]-value) > 1e-6:
                raise ValueError('Bias input changed.')
        if snapshot['mainLightShadowParams']['y'] != 3 or snapshot['mainLightShadowParams']['x'] != 1:
            raise ValueError('Rendered native High path was not selected.')
    ids = [int(r['frame_start_timestamp']) for r in accepted]
    prior_ids = {int(r['frame_start_timestamp']) for r in rows if r['phase']!='measurement' and r['frame_start_timestamp']}
    if len(set(ids)) != len(ids) or any(b<=a for a,b in zip(ids, ids[1:])) or any(t==0 or t in prior_ids for t in ids):
        raise ValueError('Accepted frame identities are not unique, increasing and separate from warmup/drain observations.')
    if any(r['phase']!='measurement' or r['reasons'] or r['controls_error'] or r['returned_count']!='1' for r in accepted):
        raise ValueError('An excluded observation was accepted.')
    gpu, cpu = [], []
    for r in accepted:
        g, c = float(r['gpu_frame_time_ms']), float(r['cpu_frame_time_ms'])
        if not (math.isfinite(g) and math.isfinite(c) and g>0 and c>0):
            raise ValueError('A non-positive/non-finite duration was accepted.')
        if (int(r['screen_width']),int(r['screen_height'])) != (1920,1080):
            raise ValueError('Resolution changed during timing.')
        gpu.append(g); cpu.append(c)
    empty = [r for r in rows if r['returned_count']=='0']
    if any(any(r[k] for k in ['frame_start_timestamp','gpu_frame_time_ms','cpu_frame_time_ms']) for r in empty):
        raise ValueError('Empty API returns contain fabricated timing fields.')
    exclusions = Counter(r['reasons'] for r in measure if r['accepted']!='true')
    manifest_exclusions = {r['reason']:r['count'] for r in report['rejections']}
    if dict(exclusions) != manifest_exclusions or len(measure) != report['measurementPolls']:
        raise ValueError('Exclusion counts disagree with raw rows.')
    cpu_hz = int(report['ready']['cpuTimerFrequencyDiagnostic'])
    span_ms = 1000*(ids[-1]-ids[0])/cpu_hz if cpu_hz else None
    result = {
        'run':folder.name,'directory':str(folder),'validation':'passed',
        'raw_observations':len(rows),'phase_counts':dict(Counter(r['phase'] for r in rows)),
        'measurement_polls':len(measure),'accepted_unique_samples':len(accepted),
        'measurement_exclusions':dict(exclusions),
        'all_phases_empty_returns':len(empty),
        'accepted_focused_samples':sum(r['focused']=='true' for r in accepted),
        'gpu_whole_frame_ms':stats(gpu),'cpu_whole_frame_ms':stats(cpu),
        'measurement_elapsed_seconds':report['measurementSeconds'],
        'accepted_cpu_clock_span_ms':span_ms,
        'source_tick_identity':'frameStartTimestamp, raw CPU clock ticks; duration fields already milliseconds',
        'ready':report['ready'],'final':report['final'],
        'sha256':{'observations.csv':digest(csv_path),'timing_smoke.json':digest(report_path)},
        'classification':'Availability smoke with ambient GPU activity and instrumentation overhead. No PCF-only cost or performance gain claim.'
    }
    (ROOT/f'{folder.name}_analysis.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directories',nargs='*',help='Run directories; defaults to the three recorded High runs.')
    parser.add_argument('--output-dir',default=str(ROOT),help='Directory for analysis JSON outputs.')
    options=parser.parse_args()
    ROOT=Path(options.output_dir).resolve()
    ROOT.mkdir(parents=True,exist_ok=True)
    dirs=[Path(x) for x in options.directories] or [REPOSITORY/'Measurements/FrameTiming/2026-11-10'/name for name in ['High_01','High_02','High_03']]
    results=[]
    for folder in dirs:
        try:
            result=inspect(folder)
        except Exception as error:
            (ROOT/f'{folder.name}_validation_failure.json').write_text(json.dumps({'run':folder.name,'validation':'failed','reason':str(error)},indent=2),encoding='utf-8')
            raise
        results.append(result)
        print(json.dumps({k:result[k] for k in ['run','validation','accepted_unique_samples','measurement_exclusions','gpu_whole_frame_ms','cpu_whole_frame_ms','measurement_elapsed_seconds']}))
    if len(results)>1:
        medians=[r['gpu_whole_frame_ms']['median'] for r in results]
        summary={'classification':'Descriptive timing-availability repeat check, not formal benchmark results',
                 'runs':[{k:r[k] for k in ['run','accepted_unique_samples','gpu_whole_frame_ms','cpu_whole_frame_ms']} for r in results],
                 'gpu_median_min_ms':min(medians),'gpu_median_max_ms':max(medians),
                 'gpu_median_range_percent_of_mean':100*(max(medians)-min(medians))/float(np.mean(medians)),
                 'gpu_median_descriptive_cv_percent':100*float(np.std(medians,ddof=1))/float(np.mean(medians)),
                 'caution':'Other graphics applications were not closed. Repeats are not isolated trials; no significance or PCF-cost inference.'}
        (ROOT/'repeat_summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
        print(json.dumps(summary))
