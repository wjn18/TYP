using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TYP.Experiments
{
    /// <summary>Explicitly requested availability check, not a performance benchmark.</summary>
    [DisallowMultipleComponent]
    public sealed class FrameTimingSmokeProbe : MonoBehaviour
    {
        private const int MaximumMeasurementPolls = 20000;
        private const double MaximumMeasurementSeconds = 30;
        private const int MaximumDurationMeasurementPolls = 200000;
        private const double MaximumRequestedDurationSeconds = 60;
        private readonly FrameTiming[] timingBuffer = new FrameTiming[1];
        private readonly List<Observation> observations = new List<Observation>();
        private readonly HashSet<ulong> observedTimestamps = new HashSet<ulong>();
        private readonly Dictionary<string, int> measurementRejections = new Dictionary<string, int>();
        private readonly Dictionary<ulong, ZeroRecord> recoveryByTimestamp = new Dictionary<ulong, ZeroRecord>();
        private readonly List<ZeroRecord> recoveryRecords = new List<ZeroRecord>();
        private FrameTiming[] recoveryBuffer;
        private int recoveryHistoryRequested, historyPolls;
        private uint historyReturnedMin = uint.MaxValue, historyReturnedMax;
        private ulong historyReturnedTotal;
        private bool recoveryArgumentSpecified;
        private double recoveryStartedRealtimeSeconds = -1, recoveryEndedRealtimeSeconds = -1;
        private string recoveryError;
        private NativeBaselineRunner runner;
        private Camera mainCamera;
        private Light mainLight;
        private UniversalAdditionalLightData lightData;
        private UniversalAdditionalCameraData cameraData;
        private UniversalRenderPipelineAsset pipeline;
        private Coroutine runningCoroutine;
        private Report report;
        private string outputDirectory;
        private bool outputValidated, quitRequested, finished;
        private bool fixedDurationMode;
        private double requestedMeasurementSeconds;
        private int requestedSamples = 1000, requestedWarmupFrames = 300, acceptedSamples;
        private int pollIndex, measurementPolls;
        private ulong maximumObservedTimestamp, firstAcceptedTimestamp, lastAcceptedTimestamp;
        private NativeBaselinePreset expectedPreset = NativeBaselinePreset.High;
        private string pollFailure;

        public bool IsBusy { get; private set; }
        public string Status { get; private set; } = "Not requested";

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool requested = Array.IndexOf(args, "--pcf-timing-smoke") >= 0;
            if (!requested) return;
            quitRequested = Array.IndexOf(args, "--pcf-timing-quit") >= 0;
            report = new Report { startedUtc = DateTime.UtcNow.ToString("o"), applicationStartupRealtimeSeconds = Time.realtimeSinceStartupAsDouble };
            try
            {
                if (Array.IndexOf(args, "--pcf-occluder-replay") >= 0)
                    throw new InvalidOperationException("Timing smoke and occluder replay must run separately.");
                foreach (string arg in args)
                {
                    if (arg.StartsWith("--pcf-timing-output=", StringComparison.Ordinal))
                        outputDirectory = Path.GetFullPath(arg.Substring("--pcf-timing-output=".Length));
                    else if (arg.StartsWith("--pcf-timing-samples=", StringComparison.Ordinal))
                        requestedSamples = int.Parse(arg.Substring("--pcf-timing-samples=".Length), CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("--pcf-timing-warmup=", StringComparison.Ordinal))
                        requestedWarmupFrames = int.Parse(arg.Substring("--pcf-timing-warmup=".Length), CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("--pcf-timing-duration-seconds=", StringComparison.Ordinal))
                    {
                        fixedDurationMode = true;
                        requestedMeasurementSeconds = double.Parse(arg.Substring("--pcf-timing-duration-seconds=".Length), CultureInfo.InvariantCulture);
                    }
                    else if (arg.StartsWith("--pcf-timing-recovery-history=", StringComparison.Ordinal))
                    {
                        recoveryArgumentSpecified = true;
                        recoveryHistoryRequested = int.Parse(arg.Substring("--pcf-timing-recovery-history=".Length), CultureInfo.InvariantCulture);
                    }
                    else if (arg.StartsWith("--pcf-preset=", StringComparison.Ordinal))
                    {
                        if (!Enum.TryParse(arg.Substring("--pcf-preset=".Length), true, out expectedPreset) ||
                            !Enum.IsDefined(typeof(NativeBaselinePreset), expectedPreset))
                            throw new ArgumentException("Unknown --pcf-preset value.");
                    }
                }
                if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Provide --pcf-timing-output=<new empty directory>.");
                if (Directory.Exists(outputDirectory) && Directory.GetFileSystemEntries(outputDirectory).Length != 0)
                    throw new IOException("Output directory is not empty; earlier observations will not be overwritten.");
                Directory.CreateDirectory(outputDirectory);
                outputValidated = true;
                if (Array.IndexOf(args, "--pcf-capture-suite") >= 0)
                    throw new InvalidOperationException("Timing smoke and screenshot capture suites must run separately.");
                if (requestedSamples < 1 || requestedSamples > MaximumMeasurementPolls)
                    throw new ArgumentOutOfRangeException(nameof(requestedSamples), "Samples must be between 1 and 20000.");
                if (requestedWarmupFrames < 300)
                    throw new ArgumentOutOfRangeException(nameof(requestedWarmupFrames), "Use at least 300 warmup frames.");
                if (fixedDurationMode && (!PositiveFinite(requestedMeasurementSeconds) || requestedMeasurementSeconds > MaximumRequestedDurationSeconds))
                    throw new ArgumentOutOfRangeException(nameof(requestedMeasurementSeconds), "Duration must be finite, positive and at most 60 seconds; 1 to 60 seconds is recommended.");
                if (recoveryArgumentSpecified && (!fixedDurationMode || recoveryHistoryRequested < 1 || recoveryHistoryRequested > 10))
                    throw new ArgumentException("Recovery history requires duration mode and a value from 1 to 10.");
                if (fixedDurationMode)
                {
                    observations.Capacity = Math.Max(observations.Capacity, 220000);
                    observedTimestamps.EnsureCapacity(220000);
                }
                if (recoveryHistoryRequested > 0) recoveryBuffer = new FrameTiming[recoveryHistoryRequested];
                runner = FindAnyObjectByType<NativeBaselineRunner>();
                mainCamera = Camera.main;
                mainLight = RenderSettings.sun;
                pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                lightData = mainLight == null ? null : mainLight.GetComponent<UniversalAdditionalLightData>();
                cameraData = mainCamera == null ? null : mainCamera.GetComponent<UniversalAdditionalCameraData>();
                if (runner == null || mainCamera == null || mainLight == null || pipeline == null || lightData == null || cameraData == null)
                    throw new InvalidOperationException("The native runner, main camera, main light and URP data are required.");
                report.initial = Snapshot();
                if (!FrameTimingManager.IsFeatureEnabled())
                    throw new InvalidOperationException("FrameTimingManager feature is disabled in this Player.");
                IsBusy = true;
                Status = "Warming up timing availability check";
                runningCoroutine = StartCoroutine(RunSmoke());
            }
            catch (Exception ex) { Finish(false, "startup_failed", ex.Message); }
        }

        private IEnumerator RunSmoke()
        {
            double warmupStarted = Time.realtimeSinceStartupAsDouble;
            int warmupFrames = 0;
            while (warmupFrames < requestedWarmupFrames || Time.realtimeSinceStartupAsDouble - warmupStarted < 3.0)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Probe disabled during warmup."); yield break; }
                if (Time.realtimeSinceStartupAsDouble - warmupStarted > 30 || warmupFrames >= 20000)
                { Finish(false, "warmup_timeout", "Warmup could not complete within 30 seconds or 20000 polls."); yield break; }
                Poll("warmup", false);
                if (pollFailure != null) { Finish(false, "poll_failed", pollFailure); yield break; }
                warmupFrames++;
                yield return null;
            }
            report.warmupFrames = warmupFrames;
            report.warmupSeconds = Time.realtimeSinceStartupAsDouble - warmupStarted;
            Status = "Draining delayed frame timings";
            for (int i = 0; i < 8; i++)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Probe disabled during drain."); yield break; }
                Poll("drain", false);
                if (pollFailure != null) { Finish(false, "poll_failed", pollFailure); yield break; }
                report.drainFrames++;
                yield return null;
            }
            string conditions = ValidateConditions();
            if (conditions != null) { Finish(false, "controls_invalid", conditions); yield break; }
            report.ready = Snapshot();
            Status = "Collecting unique valid frame timings";
            report.measurementStartGcCollections = CollectionCounts();
            double measurementStarted = Time.realtimeSinceStartupAsDouble;
            report.measurementStartedRealtimeSeconds = measurementStarted;
            int pollLimit = fixedDurationMode ? MaximumDurationMeasurementPolls : MaximumMeasurementPolls;
            double durationLimit = fixedDurationMode ? requestedMeasurementSeconds : MaximumMeasurementSeconds;
            while ((fixedDurationMode || acceptedSamples < requestedSamples) && measurementPolls < pollLimit &&
                   Time.realtimeSinceStartupAsDouble - measurementStarted < durationLimit)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Probe disabled during measurement."); yield break; }
                conditions = ValidateConditions();
                Poll("measurement", true, conditions);
                measurementPolls++;
                if (pollFailure != null) { Finish(false, "poll_failed", pollFailure); yield break; }
                if (conditions != null) { Finish(false, "controls_changed", conditions); yield break; }
                if (fixedDurationMode && measurementPolls >= pollLimit && Time.realtimeSinceStartupAsDouble - measurementStarted < durationLimit)
                {
                    MarkMeasurementEnd();
                    Finish(false, "measurement_poll_limit", "Safety poll limit reached before the requested duration elapsed.");
                    yield break;
                }
                yield return null;
            }
            MarkMeasurementEnd();
            bool durationElapsed = !fixedDurationMode || report.measurementSeconds >= requestedMeasurementSeconds;
            bool complete = fixedDurationMode ? durationElapsed && acceptedSamples >= requestedSamples : acceptedSamples == requestedSamples;
            Finish(complete, complete ? "completed" : !durationElapsed ? "measurement_poll_limit" : "insufficient_valid_samples",
                complete ? null : !durationElapsed ? "Safety poll limit reached before the requested duration elapsed." :
                "Required unique positive CPU/GPU timing samples were not obtained within the measurement limits.");
        }

        private void Poll(string phase, bool measurement, string controlsError = null)
        {
            var observation = new Observation
            {
                phase = phase, pollIndex = pollIndex++, unityFrame = Time.frameCount,
                realtimeSeconds = Time.realtimeSinceStartupAsDouble, focused = Application.isFocused,
                screenWidth = Screen.width, screenHeight = Screen.height, controlsError = controlsError
            };
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                observation.returnedCount = FrameTimingManager.GetLatestTimings(1, timingBuffer);
                if (observation.returnedCount > 0)
                {
                    // Only read the buffer when GetLatestTimings reports a valid entry.
                    FrameTiming value = timingBuffer[0];
                    observation.frameStartTimestamp = value.frameStartTimestamp;
                    observation.cpuFrameTimeMs = value.cpuFrameTime;
                    observation.gpuFrameTimeMs = value.gpuFrameTime;
                    observation.cpuMainThreadFrameTimeMs = value.cpuMainThreadFrameTime;
                    observation.cpuRenderThreadFrameTimeMs = value.cpuRenderThreadFrameTime;
                    observation.cpuMainThreadPresentWaitTimeMs = value.cpuMainThreadPresentWaitTime;
                    observation.cpuTimeFrameComplete = value.cpuTimeFrameComplete;
                    observation.cpuTimePresentCalled = value.cpuTimePresentCalled;
                    observation.firstSubmitTimestamp = value.firstSubmitTimestamp;
                    observation.widthScale = value.widthScale;
                    observation.heightScale = value.heightScale;
                    observation.syncInterval = value.syncInterval;
                    bool previouslySeen = value.frameStartTimestamp != 0 && !observedTimestamps.Add(value.frameStartTimestamp);
                    bool notIncreasing = value.frameStartTimestamp != 0 && value.frameStartTimestamp <= maximumObservedTimestamp;
                    observation.reasons = value.frameStartTimestamp == 0 ? "timestamp_zero" :
                        previouslySeen ? "duplicate_timestamp" : notIncreasing ? "non_monotonic_timestamp" : "";
                    if (value.frameStartTimestamp > maximumObservedTimestamp) maximumObservedTimestamp = value.frameStartTimestamp;
                    if (!PositiveFinite(value.cpuFrameTime)) observation.reasons = AddReason(observation.reasons, "cpu_not_positive_finite");
                    if (!PositiveFinite(value.gpuFrameTime)) observation.reasons = AddReason(observation.reasons, "gpu_not_positive_finite");
                }
                else observation.reasons = "no_timing_returned";
                if (controlsError != null) observation.reasons = AddReason(observation.reasons, "controls_invalid");
                if (!measurement) observation.reasons = AddReason(observation.reasons, phase + "_excluded");
                observation.accepted = measurement && string.IsNullOrEmpty(observation.reasons);
                if (observation.accepted)
                {
                    if (acceptedSamples == 0) firstAcceptedTimestamp = observation.frameStartTimestamp;
                    lastAcceptedTimestamp = observation.frameStartTimestamp;
                    acceptedSamples++;
                }
                else if (measurement)
                {
                    string reason = observation.reasons ?? "unspecified";
                    measurementRejections[reason] = measurementRejections.TryGetValue(reason, out int count) ? count + 1 : 1;
                }
            }
            catch (Exception ex)
            {
                observation.reasons = AddReason(observation.reasons, "poll_exception");
                observation.controlsError = ex.Message;
                pollFailure = ex.Message;
            }
            observations.Add(observation);
            if (recoveryBuffer != null)
            {
                try { ProbeRecovery(observation); }
                catch (Exception ex) { recoveryError = ex.Message; pollFailure = "Recovery diagnostic failed: " + ex.Message; }
            }
        }

        private void ProbeRecovery(Observation latest)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (recoveryStartedRealtimeSeconds < 0) recoveryStartedRealtimeSeconds = now;
            // Seed zero values from the unchanged primary poll too: the history
            // call may already see that same timestamp's GPU result recovered.
            if (latest.returnedCount > 0 && latest.frameStartTimestamp != 0 && latest.gpuFrameTimeMs == 0 &&
                !recoveryByTimestamp.ContainsKey(latest.frameStartTimestamp))
            {
                var zero = new ZeroRecord
                {
                    frameStartTimestamp = latest.frameStartTimestamp.ToString(CultureInfo.InvariantCulture),
                    firstPoll = latest.pollIndex, firstPhase = latest.phase, firstRealtime = now,
                    firstSlot = -1, firstSource = "latest_poll", latestWasZero = true, primaryObservedZero = true
                };
                recoveryByTimestamp.Add(latest.frameStartTimestamp, zero); recoveryRecords.Add(zero);
            }
            else if (latest.returnedCount > 0 && latest.frameStartTimestamp != 0 && latest.gpuFrameTimeMs == 0 &&
                     recoveryByTimestamp.TryGetValue(latest.frameStartTimestamp, out ZeroRecord primaryZero))
                primaryZero.primaryObservedZero = true;
            uint count = FrameTimingManager.GetLatestTimings((uint)recoveryBuffer.Length, recoveryBuffer);
            now = Time.realtimeSinceStartupAsDouble;
            historyPolls++;
            historyReturnedMin = Math.Min(historyReturnedMin, count);
            historyReturnedMax = Math.Max(historyReturnedMax, count);
            historyReturnedTotal += count;
            for (int slot = 0; slot < Math.Min(count, (uint)recoveryBuffer.Length); slot++)
            {
                FrameTiming value = recoveryBuffer[slot];
                if (value.frameStartTimestamp == 0) continue;
                if (value.gpuFrameTime == 0 && !recoveryByTimestamp.ContainsKey(value.frameStartTimestamp))
                {
                    var zero = new ZeroRecord
                    {
                        frameStartTimestamp = value.frameStartTimestamp.ToString(CultureInfo.InvariantCulture),
                        firstPoll = latest.pollIndex, firstPhase = latest.phase, firstRealtime = now, firstSlot = slot,
                        firstSource = "history_probe",
                        latestWasZero = false
                    };
                    recoveryByTimestamp.Add(value.frameStartTimestamp, zero);
                    recoveryRecords.Add(zero);
                }
                if (PositiveFinite(value.gpuFrameTime) && PositiveFinite(value.cpuFrameTime) &&
                    recoveryByTimestamp.TryGetValue(value.frameStartTimestamp, out ZeroRecord record) && !record.recovered)
                {
                    record.recovered = true; record.firstRecoveredPoll = latest.pollIndex;
                    record.firstRecoveredPhase = latest.phase; record.firstRecoveredRealtime = now; record.firstRecoveredSlot = slot;
                    record.recoveryDelaySeconds = now - record.firstRealtime;
                    record.recoveredGpuFrameTimeMs = value.gpuFrameTime; record.recoveredCpuFrameTimeMs = value.cpuFrameTime;
                }
            }
            recoveryEndedRealtimeSeconds = Time.realtimeSinceStartupAsDouble;
        }

        private static int[] CollectionCounts() { return new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) }; }
        private void MarkMeasurementEnd()
        {
            if (report == null || report.measurementStartedRealtimeSeconds < 0 || report.measurementEndedRealtimeSeconds >= 0) return;
            report.measurementEndedRealtimeSeconds = Time.realtimeSinceStartupAsDouble;
            report.measurementSeconds = report.measurementEndedRealtimeSeconds - report.measurementStartedRealtimeSeconds;
            report.measurementEndGcCollections = CollectionCounts();
        }

        private string ValidateConditions()
        {
            if (runner == null || !runner.isActiveAndEnabled || runner.IsBusy) return "Native runner is absent, disabled or capturing screenshots.";
            if (mainCamera == null || mainLight == null || pipeline == null || lightData == null) return "Required rendering references were lost.";
            if (GraphicsSettings.currentRenderPipeline != pipeline || pipeline.name != "PCF_Baseline_URP") return "Unexpected active pipeline.";
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) return "D3D11 is required for this smoke configuration.";
            if (Screen.width != 1920 || Screen.height != 1080 || mainCamera.pixelWidth != 1920 || mainCamera.pixelHeight != 1080)
                return "Screen and camera must be 1920 x 1080.";
            if (!mainCamera.enabled || !mainCamera.gameObject.activeInHierarchy || mainCamera.targetTexture != null)
                return "Main camera must render to the screen.";
            if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1) return "VSync and frame cap must be disabled.";
            if (mainCamera.allowHDR || mainCamera.allowMSAA || mainCamera.allowDynamicResolution ||
                cameraData.antialiasing != AntialiasingMode.None || cameraData.renderPostProcessing ||
                pipeline.supportsHDR || pipeline.msaaSampleCount != 1 || Mathf.Abs(pipeline.renderScale - 1f) > 0.00001f)
                return "AA, post-processing, HDR, MSAA and dynamic resolution must be disabled, with render scale 1.";
            if (pipeline.shadowCascadeCount != 1 || pipeline.mainLightShadowmapResolution != 2048 ||
                Mathf.Abs(pipeline.shadowDistance - 50f) > 0.00001f || !lightData.usePipelineSettings ||
                Mathf.Abs(pipeline.shadowDepthBias - 0.1f) > 0.00001f || Mathf.Abs(pipeline.shadowNormalBias - 0.5f) > 0.00001f)
                return "Recorded single-cascade shadow map, distance and pipeline bias inputs changed.";
            if (!FrameTimingManager.IsFeatureEnabled()) return "FrameTimingManager became disabled.";
            if (runner.CurrentPreset != expectedPreset) return "Native runner preset does not match --pcf-preset.";
            bool hard = expectedPreset == NativeBaselinePreset.Hard;
            if (mainLight.shadows != (hard ? LightShadows.Hard : LightShadows.Soft)) return "Unexpected main-light shadow mode.";
            SoftShadowQuality quality = expectedPreset == NativeBaselinePreset.Low ? SoftShadowQuality.Low :
                expectedPreset == NativeBaselinePreset.Medium ? SoftShadowQuality.Medium : SoftShadowQuality.High;
            if (lightData.softShadowQuality != quality) return "Per-light quality does not match selected preset.";
            Vector4 parameters = Shader.GetGlobalVector("_MainLightShadowParams");
            int expectedQuality = hard ? 0 : (int)expectedPreset;
            if (!Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS")) return "Rendered _MAIN_LIGHT_SHADOWS keyword is not enabled.";
            if (Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_CASCADE") || Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_SCREEN") ||
                Shader.IsKeywordEnabled("_SCREEN_SPACE_OCCLUSION")) return "Unexpected cascade, screen-space-shadow or SSAO path.";
            bool staticLow = Shader.IsKeywordEnabled("_SHADOWS_SOFT_LOW");
            bool staticMedium = Shader.IsKeywordEnabled("_SHADOWS_SOFT_MEDIUM");
            bool staticHigh = Shader.IsKeywordEnabled("_SHADOWS_SOFT_HIGH");
            if ((staticLow && expectedPreset != NativeBaselinePreset.Low) ||
                (staticMedium && expectedPreset != NativeBaselinePreset.Medium) ||
                (staticHigh && expectedPreset != NativeBaselinePreset.High) ||
                (hard && Shader.IsKeywordEnabled("_SHADOWS_SOFT"))) return "Conflicting static or soft-shadow keyword.";
            if (Mathf.Abs(parameters.y - expectedQuality) > 0.001f || Mathf.Abs(parameters.x - 1f) > 0.001f)
                return "Rendered main-light shadow parameters do not match the selected quality/strength.";
            if (!hard && !Shader.IsKeywordEnabled("_SHADOWS_SOFT") &&
                !Shader.IsKeywordEnabled("_SHADOWS_SOFT_" + expectedPreset.ToString().ToUpperInvariant()))
                return "No rendered soft-shadow keyword matches the selected preset.";
            return null;
        }

        private SnapshotData Snapshot()
        {
            var data = new SnapshotData
            {
                utc = DateTime.UtcNow.ToString("o"), realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                unityVersion = Application.unityVersion, platform = Application.platform.ToString(),
                focused = Application.isFocused, runInBackground = Application.runInBackground,
                operatingSystem = SystemInfo.operatingSystem, cpu = SystemInfo.processorType,
                gpu = SystemInfo.graphicsDeviceName, graphicsVersion = SystemInfo.graphicsDeviceVersion,
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(), renderingThreadingMode = SystemInfo.renderingThreadingMode.ToString(), supportsGpuRecorder = SystemInfo.supportsGpuRecorder,
                timingFeatureEnabled = FrameTimingManager.IsFeatureEnabled(),
                cpuTimerFrequencyDiagnostic = FrameTimingManager.GetCpuTimerFrequency().ToString(CultureInfo.InvariantCulture),
                gpuTimerFrequencyDiagnostic = FrameTimingManager.GetGpuTimerFrequency().ToString(CultureInfo.InvariantCulture),
                screenWidth = Screen.width, screenHeight = Screen.height,
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                activeColorSpace = QualitySettings.activeColorSpace.ToString(), expectedPreset = expectedPreset.ToString(),
                mainLightShadowParams = Shader.GetGlobalVector("_MainLightShadowParams"),
                mainLightShadowmapSize = Shader.GetGlobalVector("_MainLightShadowmapSize"),
                shadowBiasGlobalDiagnostic = Shader.GetGlobalVector("_ShadowBias"),
                mainLightShadowKeyword = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS"),
                softShadowKeyword = Shader.IsKeywordEnabled("_SHADOWS_SOFT")
            };
            if (mainCamera != null)
            {
                data.cameraPixelWidth = mainCamera.pixelWidth; data.cameraPixelHeight = mainCamera.pixelHeight;
                data.cameraPosition = mainCamera.transform.position; data.cameraRotation = mainCamera.transform.eulerAngles;
                data.cameraFOV = mainCamera.fieldOfView; data.cameraHDR = mainCamera.allowHDR;
                data.cameraMSAA = mainCamera.allowMSAA; data.cameraDynamicResolution = mainCamera.allowDynamicResolution;
                var cameraData = mainCamera.GetComponent<UniversalAdditionalCameraData>();
                if (cameraData != null) { data.cameraAntialiasing = cameraData.antialiasing.ToString(); data.cameraPostProcessing = cameraData.renderPostProcessing; }
            }
            if (pipeline != null)
            {
                data.pipeline = pipeline.name; data.renderScale = pipeline.renderScale; data.msaaSamples = pipeline.msaaSampleCount;
                data.mainShadowResolution = pipeline.mainLightShadowmapResolution; data.cascades = pipeline.shadowCascadeCount;
                data.shadowDistance = pipeline.shadowDistance; data.pipelineDepthBiasInput = pipeline.shadowDepthBias;
                data.pipelineNormalBiasInput = pipeline.shadowNormalBias;
            }
            if (mainLight != null)
            {
                data.lightRotation = mainLight.transform.eulerAngles; data.lightShadows = mainLight.shadows.ToString();
                data.lightStrength = mainLight.shadowStrength;
            }
            if (lightData != null) { data.perLightQuality = lightData.softShadowQuality.ToString(); data.usesPipelineBias = lightData.usePipelineSettings; }
            return data;
        }

        private void Finish(bool success, string state, string error)
        {
            if (finished) return;
            MarkMeasurementEnd();
            finished = true;
            IsBusy = false;
            if (report == null) report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            report.state = state; report.error = error; report.endedUtc = DateTime.UtcNow.ToString("o");
            report.requestedSamples = requestedSamples; report.acceptedSamples = acceptedSamples;
            report.completionCriterion = fixedDurationMode ? "fixed_duration" : "sample_count";
            report.requestedMeasurementSeconds = requestedMeasurementSeconds;
            report.minimumValidSamples = requestedSamples;
            report.maximumMeasurementPolls = fixedDurationMode ? MaximumDurationMeasurementPolls : MaximumMeasurementPolls;
            report.maximumMeasurementSeconds = fixedDurationMode ? requestedMeasurementSeconds : MaximumMeasurementSeconds;
            if (fixedDurationMode)
                report.classification = "Fixed-duration whole-frame timing reliability pilot, not a formal benchmark. Recorded ambient GPU/CPU activity and FrameTimingManager capture/polling instrumentation remain. CPU/GPU frame times are already milliseconds; GPU time is whole-frame, not PCF-only or a render pass. Screenshot capture and image readback are excluded. Timer frequencies are diagnostic only. Observations are buffered in memory and written after measurement; minimum valid samples are checked only after the duration ends.";
            report.recoveryHistoryRequested = recoveryHistoryRequested;
            if (recoveryHistoryRequested > 0)
                report.classification += " Additional GetLatestTimings history-recovery probing is enabled; this extra API overhead prevents pooling this run with ordinary timing pilots.";
            report.requestedWarmupFrames = requestedWarmupFrames; report.measurementPolls = measurementPolls;
            report.totalRecordedPolls = observations.Count; report.expectedPreset = expectedPreset.ToString();
            report.firstAcceptedTimestamp = firstAcceptedTimestamp.ToString(CultureInfo.InvariantCulture);
            report.lastAcceptedTimestamp = lastAcceptedTimestamp.ToString(CultureInfo.InvariantCulture);
            report.rejections = new List<RejectionCount>();
            foreach (var pair in measurementRejections) report.rejections.Add(new RejectionCount { reason = pair.Key, count = pair.Value });
            try { report.final = Snapshot(); } catch (Exception ex) { report.snapshotError = ex.Message; }
            try
            {
                if (!outputValidated)
                {
                    // A rejection sidecar preserves the existing non-empty directory.
                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        string sidecar = outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            ".rejected_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + ".json";
                        using (var stream = new FileStream(sidecar, FileMode.CreateNew, FileAccess.Write))
                        using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(report, true));
                    }
                }
                else
                {
                    File.WriteAllText(Path.Combine(outputDirectory, "observations.csv"), BuildCsv());
                    File.WriteAllText(Path.Combine(outputDirectory, "timing_smoke.json"), JsonUtility.ToJson(report, true));
                    if (recoveryHistoryRequested > 0)
                    {
                        int recoveredCount = 0;
                        foreach (ZeroRecord zero in recoveryRecords) if (zero.recovered) recoveredCount++;
                        var recovery = new RecoveryReport
                        {
                            historyRequested = recoveryHistoryRequested, historyPolls = historyPolls,
                            actualReturnedMin = historyPolls == 0 ? 0 : historyReturnedMin, actualReturnedMax = historyReturnedMax,
                            actualReturnedTotal = historyReturnedTotal.ToString(CultureInfo.InvariantCulture),
                            startedRealtimeSeconds = recoveryStartedRealtimeSeconds, endedRealtimeSeconds = recoveryEndedRealtimeSeconds,
                            zeroTimestampCount = recoveryRecords.Count, recoveredCount = recoveredCount,
                            unrecoveredCount = recoveryRecords.Count - recoveredCount, zeroRecords = recoveryRecords, error = recoveryError
                        };
                        File.WriteAllText(Path.Combine(outputDirectory, "timing_recovery.json"), JsonUtility.ToJson(recovery, true));
                    }
                }
            }
            catch (Exception ex) { success = false; error = (error == null ? "" : error + " ") + "Output write failed: " + ex.Message; }
            Status = (success ? "Completed" : "Failed") + ": " + (error ?? outputDirectory);
            if (!success) Debug.LogError("FrameTimingSmokeProbe: " + Status, this);
            if (quitRequested)
            {
#if !UNITY_EDITOR
                Application.Quit(success ? 0 : 1);
#endif
            }
        }

        private string BuildCsv()
        {
            var text = new StringBuilder("phase,poll_index,unity_frame,realtime_seconds,focused,screen_width,screen_height,returned_count,accepted,reasons,controls_error,frame_start_timestamp,cpu_frame_time_ms,gpu_frame_time_ms,cpu_main_thread_frame_time_ms,cpu_render_thread_frame_time_ms,cpu_main_thread_present_wait_time_ms,cpu_time_frame_complete,cpu_time_present_called,first_submit_timestamp,width_scale,height_scale,sync_interval\n");
            foreach (Observation o in observations)
            {
                text.Append(Csv(o.phase)).Append(',').Append(o.pollIndex).Append(',').Append(o.unityFrame).Append(',')
                    .Append(Number(o.realtimeSeconds)).Append(',').Append(o.focused ? "true" : "false").Append(',')
                    .Append(o.screenWidth).Append(',').Append(o.screenHeight).Append(',').Append(o.returnedCount).Append(',')
                    .Append(o.accepted ? "true" : "false").Append(',').Append(Csv(o.reasons)).Append(',').Append(Csv(o.controlsError));
                if (o.returnedCount > 0)
                    text.Append(',').Append(o.frameStartTimestamp.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Number(o.cpuFrameTimeMs)).Append(',').Append(Number(o.gpuFrameTimeMs)).Append(',')
                        .Append(Number(o.cpuMainThreadFrameTimeMs)).Append(',').Append(Number(o.cpuRenderThreadFrameTimeMs)).Append(',')
                        .Append(Number(o.cpuMainThreadPresentWaitTimeMs)).Append(',').Append(o.cpuTimeFrameComplete.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(o.cpuTimePresentCalled.ToString(CultureInfo.InvariantCulture)).Append(',').Append(o.firstSubmitTimestamp.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Number(o.widthScale)).Append(',').Append(Number(o.heightScale)).Append(',').Append(o.syncInterval);
                else text.Append(",,,,,,,,,,,,");
                text.Append('\n');
            }
            return text.ToString();
        }

        private static bool PositiveFinite(double value) { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string AddReason(string current, string reason) { return string.IsNullOrEmpty(current) ? reason : current + ";" + reason; }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }

        private void OnDisable()
        {
            if (!IsBusy || finished) return;
            if (runningCoroutine != null) StopCoroutine(runningCoroutine);
            runningCoroutine = null;
            Finish(false, "interrupted", "Probe disabled before the requested samples were collected.");
        }

        private struct Observation
        {
            public string phase, reasons, controlsError;
            public int pollIndex, unityFrame, screenWidth, screenHeight;
            public double realtimeSeconds, cpuFrameTimeMs, gpuFrameTimeMs, cpuMainThreadFrameTimeMs;
            public double cpuRenderThreadFrameTimeMs, cpuMainThreadPresentWaitTimeMs;
            public uint returnedCount, syncInterval;
            public ulong frameStartTimestamp, cpuTimeFrameComplete, cpuTimePresentCalled, firstSubmitTimestamp;
            public double widthScale, heightScale;
            public bool focused, accepted;
        }

        [Serializable] private sealed class RejectionCount { public string reason; public int count; }
        [Serializable] private sealed class Report
        {
            public string startedUtc, endedUtc, state, error, snapshotError, expectedPreset, completionCriterion;
            public double applicationStartupRealtimeSeconds, warmupSeconds, measurementSeconds, requestedMeasurementSeconds;
            public int minimumValidSamples;
            public int recoveryHistoryRequested;
            public double measurementStartedRealtimeSeconds = -1, measurementEndedRealtimeSeconds = -1;
            public int[] measurementStartGcCollections, measurementEndGcCollections;
            public int requestedSamples, acceptedSamples, requestedWarmupFrames, warmupFrames, drainFrames, measurementPolls, totalRecordedPolls;
            public string firstAcceptedTimestamp, lastAcceptedTimestamp;
            public List<RejectionCount> rejections;
            public SnapshotData initial, ready, final;
            public string classification = "FrameTimingManager availability smoke only, not a formal benchmark. CPU and GPU frame times are already milliseconds. GPU time is whole-frame timing, not PCF-only or a particular render pass. Screenshot capture and image GPU readback are excluded; FrameTimingManager capture/polling overhead remains. Timer frequencies are diagnostics only; no unit conversion uses them. Raw observations are buffered in memory and written after measurement.";
            public string identityRule = "Timestamp zero, duplicates, timestamps not increasing, and non-positive/non-finite CPU or GPU times are excluded. Warmup and eight drain frames are recorded but never accepted. Timestamps observed during warmup/drain cannot be reused for measurement.";
            public int maximumMeasurementPolls = MaximumMeasurementPolls;
            public double maximumMeasurementSeconds = MaximumMeasurementSeconds;
        }
        [Serializable] private sealed class ZeroRecord
        {
            public string frameStartTimestamp, firstPhase, firstRecoveredPhase, firstSource;
            public int firstPoll, firstSlot, firstRecoveredPoll = -1, firstRecoveredSlot = -1;
            public double firstRealtime, firstRecoveredRealtime = -1, recoveryDelaySeconds = -1, recoveredGpuFrameTimeMs, recoveredCpuFrameTimeMs;
            public bool latestWasZero, primaryObservedZero, recovered;
        }
        [Serializable] private sealed class RecoveryReport
        {
            public int historyRequested, historyPolls, zeroTimestampCount, recoveredCount, unrecoveredCount;
            public uint actualReturnedMin, actualReturnedMax;
            public string actualReturnedTotal, error;
            public double startedRealtimeSeconds, endedRealtimeSeconds;
            public List<ZeroRecord> zeroRecords;
            public string classification = "Additional GetLatestTimings history availability/recovery diagnostic across warmup, drain and measurement. First zero-GPU observations and later positive CPU/GPU observations are linked by nonzero timestamp, including unrecovered entries. Primary acceptance and seen-timestamp rules are unchanged. Additional API work means this run must not be pooled with ordinary pilots. Filter firstPhase/firstRecoveredPhase for measurement-specific recovery analysis.";
        }
        [Serializable] private sealed class SnapshotData
        {
            public string utc, unityVersion, platform, operatingSystem, cpu, gpu, graphicsVersion, graphicsAPI, renderingThreadingMode, activeColorSpace;
            public string cpuTimerFrequencyDiagnostic, gpuTimerFrequencyDiagnostic, pipeline, expectedPreset, lightShadows, perLightQuality, cameraAntialiasing;
            public bool focused, runInBackground, timingFeatureEnabled, supportsGpuRecorder, mainLightShadowKeyword, softShadowKeyword;
            public bool cameraHDR, cameraMSAA, cameraDynamicResolution, cameraPostProcessing, usesPipelineBias;
            public double realtimeSeconds;
            public int screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight, vSyncCount, targetFrameRate, msaaSamples, mainShadowResolution, cascades;
            public float renderScale, shadowDistance, pipelineDepthBiasInput, pipelineNormalBiasInput, cameraFOV, lightStrength;
            public Vector3 cameraPosition, cameraRotation, lightRotation;
            public Vector4 mainLightShadowParams, mainLightShadowmapSize, shadowBiasGlobalDiagnostic;
        }
    }
}
