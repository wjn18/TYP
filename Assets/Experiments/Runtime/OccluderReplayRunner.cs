using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TYP.Experiments
{
    /// <summary>Frame-indexed occluder replay and diagnostic capture, not a benchmark.</summary>
    [DisallowMultipleComponent]
    public sealed class OccluderReplayRunner : MonoBehaviour
    {
        private const int LastFrame = 120;
        private const int LogicalHz = 60;
        private const float PositionTolerance = 0.00001f;
        [SerializeField] private Transform movingOccluder;
        [SerializeField] private Camera captureCamera;
        [SerializeField] private Light mainLight;
        [SerializeField] private Vector3 pathCentre = new Vector3(-2, 1, -1);
        [SerializeField] private float amplitude = 1.25f;
        [SerializeField] private bool previewMotion;
        private Vector3 initialPosition, initialScale;
        private Quaternion initialRotation;
        private bool initialRemembered, finished, quitRequested;
        private double previewStarted;
        private string outputDirectory;
        private Coroutine captureCoroutine;
        private NativeBaselineRunner baselineRunner;
        private UniversalRenderPipelineAsset pipeline;
        private UniversalAdditionalLightData lightData;
        private string baselineControlsHash;
        private int appliedUnityFrame = -1;
        private RunManifest manifest;
        private readonly List<FrameRecord> frameRecords = new List<FrameRecord>();
        public bool IsBusy { get; private set; }
        public int AppliedFrame { get; private set; } = -1;
        public string Status { get; private set; } = "Idle";

        private void Awake()
        {
            previewStarted = Time.realtimeSinceStartupAsDouble;
            if (movingOccluder != null) RememberInitialTransform();
        }

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--pcf-occluder-replay") < 0) return;
            quitRequested = Array.IndexOf(args, "--pcf-replay-quit") >= 0;
            string output = null, order = "forward";
            foreach (string arg in args)
            {
                if (arg.StartsWith("--pcf-replay-output=", StringComparison.Ordinal)) output = arg.Substring("--pcf-replay-output=".Length);
                if (arg.StartsWith("--pcf-replay-order=", StringComparison.Ordinal)) order = arg.Substring("--pcf-replay-order=".Length);
            }
            try { BeginCapture(output, order); }
            catch (Exception ex)
            {
                Status = "Replay startup failed: " + ex.Message;
                Debug.LogError("OccluderReplayRunner: " + Status, this);
                Quit(1);
            }
        }

        private void Update()
        {
            if (!previewMotion || IsBusy || movingOccluder == null) return;
            int index = (int)(Math.Floor((Time.realtimeSinceStartupAsDouble - previewStarted) * LogicalHz) % LastFrame);
            ApplyFrameInternal(index);
        }

        private void RememberInitialTransform()
        {
            if (initialRemembered) return;
            initialPosition = movingOccluder.position;
            initialRotation = movingOccluder.rotation;
            initialScale = movingOccluder.localScale;
            initialRemembered = true;
        }

        private void ResolveReferences()
        {
            if (captureCamera == null) captureCamera = Camera.main;
            if (mainLight == null) mainLight = RenderSettings.sun;
            baselineRunner = FindAnyObjectByType<NativeBaselineRunner>();
            pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            lightData = mainLight == null ? null : mainLight.GetComponent<UniversalAdditionalLightData>();
            if (movingOccluder == null || captureCamera == null || mainLight == null || baselineRunner == null || pipeline == null || lightData == null)
                throw new InvalidOperationException("Assign the moving occluder, camera and directional light; the scene also requires the native baseline runner and URP data.");
            RememberInitialTransform();
        }

        /// <summary>Set a state from the requested index alone and pause preview.</summary>
        public void ApplyFrame(int frameIndex)
        {
            previewMotion = false;
            ApplyFrameInternal(frameIndex);
        }

        private void ApplyFrameInternal(int frameIndex)
        {
            if (movingOccluder == null) throw new InvalidOperationException("Assign movingOccluder before applying a replay frame.");
            if (frameIndex < 0 || frameIndex > LastFrame) throw new ArgumentOutOfRangeException(nameof(frameIndex), "Replay frames are 0 through 120 inclusive.");
            RememberInitialTransform();
            movingOccluder.position = ExpectedPosition(frameIndex);
            movingOccluder.rotation = initialRotation;
            movingOccluder.localScale = initialScale;
            AppliedFrame = frameIndex;
            appliedUnityFrame = Time.frameCount;
        }

        private Vector3 ExpectedPosition(int frameIndex)
        {
            float progress = frameIndex <= 60 ? frameIndex / 60f : (120 - frameIndex) / 60f;
            return new Vector3(pathCentre.x - amplitude + 2f * amplitude * progress, pathCentre.y, pathCentre.z);
        }

        public void BeginCapture(string output, string order)
        {
            if (IsBusy) throw new InvalidOperationException("An occluder capture is already running.");
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode before capturing.");
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Provide a new --pcf-replay-output directory.");
            string directory = Path.GetFullPath(output);
            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length != 0)
                throw new IOException("Output directory is not empty; previous captures will not be overwritten.");
            Directory.CreateDirectory(directory);
            outputDirectory = directory;
            finished = false;
            frameRecords.Clear();
            manifest = new RunManifest { startedUtc = DateTime.UtcNow.ToString("o"), requestedOrderName = order, state = "starting" };
            try
            {
                manifest.requestedOrder = BuildOrder(order);
                string[] args = Environment.GetCommandLineArgs();
                if (Array.IndexOf(args, "--pcf-capture-suite") >= 0 || Array.IndexOf(args, "--pcf-timing-smoke") >= 0)
                    throw new InvalidOperationException("Occluder capture, native screenshot suite and timing smoke must run separately.");
                foreach (string arg in args)
                    if (arg.StartsWith("--pcf-preset=", StringComparison.Ordinal) &&
                        !string.Equals(arg.Substring("--pcf-preset=".Length), "High", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The occluder replay capture requires native High.");
                ResolveReferences();
                if (baselineRunner.IsBusy) throw new InvalidOperationException("The native screenshot suite is busy.");
                if (pathCentre != new Vector3(-2, 1, -1) || Mathf.Abs(amplitude - 1.25f) > PositionTolerance)
                    throw new InvalidOperationException("This recorded replay requires centre (-2,1,-1) and amplitude 1.25.");
                previewMotion = false;
                RestoreInitialTransform();
                baselineRunner.ApplyPreset(NativeBaselinePreset.High);
                manifest.pathCentre = pathCentre; manifest.amplitude = amplitude;
                manifest.initialPosition = initialPosition; manifest.initialRotation = initialRotation; manifest.initialScale = initialScale;
                manifest.movingObjectPath = HierarchyPath(movingOccluder);
                manifest.device = DeviceData.Read();
                IsBusy = true;
                Status = "Waiting for replay capture display";
                captureCoroutine = StartCoroutine(CaptureRun());
            }
            catch (Exception ex) { Finish(false, "startup_failed", ex.Message); }
        }

        private static int[] BuildOrder(string order)
        {
            if (string.Equals(order, "seek", StringComparison.OrdinalIgnoreCase))
                return new[] { 120, 0, 60, 15, 105, 30, 90, 45, 75, 0, 60, 120 };
            bool reverse = string.Equals(order, "reverse", StringComparison.OrdinalIgnoreCase);
            if (!reverse && !string.Equals(order, "forward", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Replay order must be forward, reverse or seek.");
            int[] values = new int[LastFrame + 1];
            for (int i = 0; i <= LastFrame; i++) values[i] = reverse ? LastFrame - i : i;
            return values;
        }

        private IEnumerator CaptureRun()
        {
            for (int i = 0; i < 120 && (Screen.width != 1920 || Screen.height != 1080); i++)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Runner disabled during display startup."); yield break; }
                yield return null;
            }
            for (int i = 0; i < 30; i++)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Runner disabled during High startup settling."); yield break; }
                yield return null;
            }
            string error = ValidateRendering();
            if (error != null) { Finish(false, "controls_invalid", error); yield break; }
            try
            {
                manifest.fixedControls = CaptureControls();
                baselineControlsHash = HashText(JsonUtility.ToJson(manifest.fixedControls));
                manifest.fixedControlsHash = baselineControlsHash;
                manifest.startupShaderDiagnostics = ShaderDiagnostics.Read();
                manifest.state = "capturing";
            }
            catch (Exception ex) { Finish(false, "snapshot_failed", ex.Message); }
            if (finished) yield break;
            for (int sequence = 0; sequence < manifest.requestedOrder.Length; sequence++)
            {
                if (!isActiveAndEnabled) { Finish(false, "interrupted", "Runner disabled before replay frame."); yield break; }
                int index = manifest.requestedOrder[sequence];
                try { ApplyFrame(index); }
                catch (Exception ex) { Finish(false, "frame_apply_failed", ex.Message); }
                if (finished) yield break;
                Status = "Capturing sequence " + sequence + ", frame " + index;
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();
                if (!TryCapture(sequence, index)) { Finish(false, "frame_failed", frameRecords[frameRecords.Count - 1].error); yield break; }
            }
            Finish(true, "completed", null);
        }

        private bool TryCapture(int sequence, int index)
        {
            string stem = "s" + sequence.ToString("D3", CultureInfo.InvariantCulture) + "_f" + index.ToString("D3", CultureInfo.InvariantCulture);
            var record = new FrameRecord
            {
                sequence = sequence, frameIndex = index, simulationTimeSeconds = index / (double)LogicalHz,
                expectedPosition = ExpectedPosition(index), expectedRotation = initialRotation, expectedScale = initialScale,
                pngFile = stem + ".png", metadataFile = stem + ".json", captureUtc = DateTime.UtcNow.ToString("o")
            };
            Texture2D image = null;
            try
            {
                record.appliedUnityFrame = appliedUnityFrame;
                record.captureFrame = Time.frameCount;
                record.appliedIndexBeforeCapture = AppliedFrame;
                record.actualPosition = movingOccluder.position; record.actualRotation = movingOccluder.rotation; record.actualScale = movingOccluder.localScale;
                string error = ValidateRendering() ?? ValidateMovingState(index);
                record.staticControlsHash = HashText(JsonUtility.ToJson(CaptureControls()));
                record.staticControlsUnchanged = record.staticControlsHash == baselineControlsHash;
                record.shaderDiagnostics = ShaderDiagnostics.Read();
                if (error != null) throw new InvalidOperationException(error);
                if (record.captureFrame <= record.appliedUnityFrame)
                    throw new InvalidOperationException("Capture must follow the applied Unity frame after rendering settles.");
                if (!record.staticControlsUnchanged) throw new InvalidOperationException("Static transforms, camera, light, materials or URP controls changed.");
                image = ScreenCapture.CaptureScreenshotAsTexture();
                if (image == null) throw new InvalidOperationException("Screen capture returned no image.");
                record.width = image.width; record.height = image.height;
                if (image.width != 1920 || image.height != 1080) throw new InvalidOperationException("Captured dimensions must be 1920 x 1080.");
                byte[] png = image.EncodeToPNG();
                record.pngSha256 = HashBytes(png);
                record.appliedIndexAfterCapture = AppliedFrame;
                record.captureFrameAfterReadback = Time.frameCount;
                error = ValidateMovingState(index);
                record.postCaptureControlsHash = HashText(JsonUtility.ToJson(CaptureControls()));
                if (error != null || record.postCaptureControlsHash != baselineControlsHash || record.captureFrame != record.captureFrameAfterReadback)
                    throw new InvalidOperationException(error ?? "Frame state or fixed controls changed during image readback.");
                File.WriteAllBytes(Path.Combine(outputDirectory, record.pngFile), png);
                record.success = true;
            }
            catch (Exception ex) { record.error = ex.Message; }
            finally { if (image != null) Destroy(image); }
            frameRecords.Add(record);
            try { File.WriteAllText(Path.Combine(outputDirectory, record.metadataFile), JsonUtility.ToJson(record, true)); }
            catch (Exception ex) { record.success = false; record.error = "Frame metadata write failed: " + ex.Message; }
            return record.success;
        }

        private string ValidateMovingState(int index)
        {
            if (AppliedFrame != index) return "Applied index changed before or during capture.";
            if ((movingOccluder.position - ExpectedPosition(index)).sqrMagnitude > PositionTolerance * PositionTolerance) return "Moving occluder position differs from the frame function.";
            if (Quaternion.Angle(movingOccluder.rotation, initialRotation) > 0.001f) return "Moving occluder rotation changed.";
            if ((movingOccluder.localScale - initialScale).sqrMagnitude > PositionTolerance * PositionTolerance) return "Moving occluder scale changed.";
            return null;
        }

        private string ValidateRendering()
        {
            if (movingOccluder == null || captureCamera == null || mainLight == null || lightData == null || pipeline == null || baselineRunner == null)
                return "Required scene references are missing.";
            if (baselineRunner.IsBusy) return "Native screenshot capture is active.";
            if (GraphicsSettings.currentRenderPipeline != pipeline || pipeline.name != "PCF_Baseline_URP") return "Unexpected active URP asset.";
            if (Screen.width != 1920 || Screen.height != 1080 || captureCamera.pixelWidth != 1920 || captureCamera.pixelHeight != 1080)
                return "Screen and camera must both be 1920 x 1080.";
            if (!captureCamera.enabled || !captureCamera.gameObject.activeInHierarchy || captureCamera.targetTexture != null) return "Camera must render to the screen.";
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) return "This replay run requires D3D11.";
            if (baselineRunner.CurrentPreset != NativeBaselinePreset.High || mainLight.shadows != LightShadows.Soft || lightData.softShadowQuality != SoftShadowQuality.High)
                return "Native High must remain selected explicitly on the main light.";
            Vector4 parameters = Shader.GetGlobalVector("_MainLightShadowParams");
            if (Mathf.Abs(parameters.x - 1f) > 0.001f || Mathf.Abs(parameters.y - 3f) > 0.001f || !Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS"))
                return "Rendered main-light strength or High quality parameters are missing.";
            if (!Shader.IsKeywordEnabled("_SHADOWS_SOFT") && !Shader.IsKeywordEnabled("_SHADOWS_SOFT_HIGH")) return "Rendered soft-shadow keyword is missing.";
            if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1) return "VSync and frame cap must remain disabled.";
            return null;
        }

        private Controls CaptureControls()
        {
            var controls = new Controls
            {
                scenePath = movingOccluder.gameObject.scene.path, cameraPath = HierarchyPath(captureCamera.transform),
                lightPath = HierarchyPath(mainLight.transform), pipelineName = pipeline.name,
                renderScale = pipeline.renderScale, msaaSamples = pipeline.msaaSampleCount, pipelineHDR = pipeline.supportsHDR,
                shadowResolution = pipeline.mainLightShadowmapResolution, cascades = pipeline.shadowCascadeCount,
                shadowDistance = pipeline.shadowDistance, depthBiasInput = pipeline.shadowDepthBias, normalBiasInput = pipeline.shadowNormalBias,
                lightUsesPipelineBias = lightData.usePipelineSettings, lightDepthBiasInput = mainLight.shadowBias, lightNormalBiasInput = mainLight.shadowNormalBias,
                lightMode = mainLight.shadows.ToString(), perLightQuality = lightData.softShadowQuality.ToString(), lightStrength = mainLight.shadowStrength,
                lightType = mainLight.type.ToString(), lightColor = mainLight.color, lightIntensity = mainLight.intensity,
                cameraProjection = captureCamera.projectionMatrix, cameraFOV = captureCamera.fieldOfView,
                cameraNear = captureCamera.nearClipPlane, cameraFar = captureCamera.farClipPlane,
                cameraHDR = captureCamera.allowHDR, cameraMSAA = captureCamera.allowMSAA, cameraDynamicResolution = captureCamera.allowDynamicResolution,
                cameraClearFlags = captureCamera.clearFlags.ToString(), cameraBackground = captureCamera.backgroundColor,
                cameraCullingMask = captureCamera.cullingMask, cameraOcclusionCulling = captureCamera.useOcclusionCulling,
                screenWidth = Screen.width, screenHeight = Screen.height, cameraPixelWidth = captureCamera.pixelWidth, cameraPixelHeight = captureCamera.pixelHeight,
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                activeColorSpace = QualitySettings.activeColorSpace.ToString(), fog = RenderSettings.fog,
                ambientMode = RenderSettings.ambientMode.ToString(), ambientLight = RenderSettings.ambientLight
            };
            var cameraData = captureCamera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData != null) { controls.cameraAntialiasing = cameraData.antialiasing.ToString(); controls.cameraPostProcessing = cameraData.renderPostProcessing; }
            foreach (Transform item in FindObjectsByType<Transform>())
                if (item.gameObject.scene == movingOccluder.gameObject.scene && item != movingOccluder && !item.IsChildOf(movingOccluder))
                    controls.staticTransforms.Add(new TransformState
                    {
                        path = HierarchyPath(item), position = item.position, rotation = item.rotation, localScale = item.localScale,
                        activeSelf = item.gameObject.activeSelf, activeInHierarchy = item.gameObject.activeInHierarchy, isStatic = item.gameObject.isStatic
                    });
            controls.staticTransforms.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            foreach (Renderer renderer in FindObjectsByType<Renderer>())
                if (renderer.gameObject.scene == movingOccluder.gameObject.scene)
                {
                    var state = new RendererState
                    {
                        path = HierarchyPath(renderer.transform), type = renderer.GetType().Name, enabled = renderer.enabled,
                        receiveShadows = renderer.receiveShadows, shadowCastingMode = renderer.shadowCastingMode.ToString(),
                        renderingLayerMask = renderer.renderingLayerMask
                    };
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        var m = new MaterialState();
                        if (material != null)
                        {
                            m.name = material.name; m.shader = material.shader == null ? "" : material.shader.name;
                            m.hasBaseColor = material.HasProperty("_BaseColor");
                            if (m.hasBaseColor) m.baseColor = material.GetColor("_BaseColor");
                            m.renderQueue = material.renderQueue;
                        }
                        state.materials.Add(m);
                    }
                    controls.renderers.Add(state);
                }
            controls.renderers.Sort((a, b) => string.CompareOrdinal(a.path + "/" + a.type, b.path + "/" + b.type));
            return controls;
        }

        private static string HierarchyPath(Transform target)
        {
            string path = target.name;
            while (target.parent != null) { target = target.parent; path = target.name + "/" + path; }
            return path;
        }

        private void RestoreInitialTransform()
        {
            if (!initialRemembered || movingOccluder == null) return;
            movingOccluder.position = initialPosition; movingOccluder.rotation = initialRotation; movingOccluder.localScale = initialScale;
            AppliedFrame = -1;
        }

        private void Finish(bool success, string state, string error)
        {
            if (finished) return;
            finished = true;
            previewMotion = false;
            RestoreInitialTransform();
            IsBusy = false;
            captureCoroutine = null;
            if (manifest != null)
            {
                manifest.endedUtc = DateTime.UtcNow.ToString("o"); manifest.state = state; manifest.error = error;
                manifest.frames = frameRecords; manifest.previewAfterCapture = previewMotion;
                manifest.initialTransformRestored = movingOccluder != null && initialRemembered &&
                    (movingOccluder.position - initialPosition).sqrMagnitude <= PositionTolerance * PositionTolerance &&
                    Quaternion.Angle(movingOccluder.rotation, initialRotation) <= 0.001f &&
                    (movingOccluder.localScale - initialScale).sqrMagnitude <= PositionTolerance * PositionTolerance;
                try
                {
                    manifest.finalShaderDiagnostics = ShaderDiagnostics.Read();
                    File.WriteAllText(Path.Combine(outputDirectory, "frames.csv"), BuildCsv());
                    File.WriteAllText(Path.Combine(outputDirectory, "replay_manifest.json"), JsonUtility.ToJson(manifest, true));
                }
                catch (Exception ex) { success = false; error = (error ?? "") + " Output write failed: " + ex.Message; }
            }
            Status = (success ? "Completed" : "Failed") + ": " + (error ?? outputDirectory);
            if (!success) Debug.LogError("OccluderReplayRunner: " + Status, this);
            Quit(success ? 0 : 1);
        }

        private string BuildCsv()
        {
            var output = new StringBuilder("sequence,frame_index,simulation_time_seconds,applied_unity_frame,capture_frame,applied_index_before,applied_index_after,expected_x,expected_y,expected_z,actual_x,actual_y,actual_z,static_controls_hash,post_capture_controls_hash,png_sha256,width,height,success,png_file,metadata_file,error\n");
            foreach (FrameRecord frame in frameRecords)
            {
                output.Append(frame.sequence.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.frameIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Number(frame.simulationTimeSeconds)).Append(',').Append(frame.appliedUnityFrame.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.captureFrame.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(frame.appliedIndexBeforeCapture.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.appliedIndexAfterCapture.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Number(frame.expectedPosition.x)).Append(',').Append(Number(frame.expectedPosition.y)).Append(',').Append(Number(frame.expectedPosition.z)).Append(',')
                    .Append(Number(frame.actualPosition.x)).Append(',').Append(Number(frame.actualPosition.y)).Append(',').Append(Number(frame.actualPosition.z)).Append(',')
                    .Append(Csv(frame.staticControlsHash)).Append(',').Append(Csv(frame.postCaptureControlsHash)).Append(',').Append(Csv(frame.pngSha256)).Append(',')
                    .Append(frame.width.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.height.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(frame.success ? "true" : "false").Append(',').Append(Csv(frame.pngFile)).Append(',').Append(Csv(frame.metadataFile)).Append(',').Append(Csv(frame.error)).Append('\n');
            }
            return output.ToString();
        }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
        private static string HashText(string text) { return HashBytes(Encoding.UTF8.GetBytes(text)); }
        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private void Quit(int code)
        {
            if (!quitRequested) return;
#if !UNITY_EDITOR
            Application.Quit(code);
#endif
        }
        private void OnDisable()
        {
            previewMotion = false;
            if (IsBusy)
            {
                if (captureCoroutine != null) StopCoroutine(captureCoroutine);
                Finish(false, "interrupted", "Replay runner disabled during capture.");
            }
            else RestoreInitialTransform();
        }

        [Serializable] private sealed class RunManifest
        {
            public string startedUtc, endedUtc, state, error, requestedOrderName, movingObjectPath, fixedControlsHash;
            public int[] requestedOrder;
            public int logicalHz = LogicalHz, firstFrame = 0, lastFrame = LastFrame, endOfFrameWaitsPerIndex = 2, startupSettlingFrames = 30;
            public string pathFormula = "x = centre.x - amplitude + 2*amplitude*(index <= 60 ? index/60 : (120-index)/60); y,z = centre.y,z";
            public string classification = "Diagnostic frame-indexed occluder replay, not a performance experiment. Logical 60 Hz defines simulation time, not measured rendering FPS. Native High is an implementation reference, not ground truth. Preview remains stopped after capture and the occluder initial transform is restored.";
            public Vector3 pathCentre, initialPosition, initialScale;
            public Quaternion initialRotation;
            public float amplitude;
            public bool initialTransformRestored, previewAfterCapture;
            public Controls fixedControls;
            public DeviceData device;
            public ShaderDiagnostics startupShaderDiagnostics, finalShaderDiagnostics;
            public List<FrameRecord> frames;
        }
        [Serializable] private sealed class FrameRecord
        {
            public int sequence, frameIndex, appliedUnityFrame, captureFrame, captureFrameAfterReadback, appliedIndexBeforeCapture, appliedIndexAfterCapture, width, height;
            public double simulationTimeSeconds;
            public string captureUtc, staticControlsHash, postCaptureControlsHash, pngSha256, pngFile, metadataFile, error;
            public Vector3 expectedPosition, actualPosition, expectedScale, actualScale;
            public Quaternion expectedRotation, actualRotation;
            public bool staticControlsUnchanged, success;
            public ShaderDiagnostics shaderDiagnostics;
        }
        [Serializable] private sealed class DeviceData
        {
            public string unityVersion, platform, operatingSystem, cpu, gpu, graphicsVersion, graphicsAPI;
            public bool focusedAtStart, runInBackground;
            public static DeviceData Read()
            {
                return new DeviceData
                {
                    unityVersion = Application.unityVersion, platform = Application.platform.ToString(), operatingSystem = SystemInfo.operatingSystem,
                    cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName, graphicsVersion = SystemInfo.graphicsDeviceVersion,
                    graphicsAPI = SystemInfo.graphicsDeviceType.ToString(), focusedAtStart = Application.isFocused, runInBackground = Application.runInBackground
                };
            }
        }
        [Serializable] private sealed class ShaderDiagnostics
        {
            public Vector4 mainLightShadowParams, mainLightShadowmapSize, shadowBiasGlobalDiagnostic;
            public bool mainLightShadows, softShadows, softHigh;
            public string caveat = "Post-render shader globals are diagnostics; _ShadowBias is not independently verified per-draw effective caster bias.";
            public static ShaderDiagnostics Read()
            {
                return new ShaderDiagnostics
                {
                    mainLightShadowParams = Shader.GetGlobalVector("_MainLightShadowParams"), mainLightShadowmapSize = Shader.GetGlobalVector("_MainLightShadowmapSize"),
                    shadowBiasGlobalDiagnostic = Shader.GetGlobalVector("_ShadowBias"), mainLightShadows = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS"),
                    softShadows = Shader.IsKeywordEnabled("_SHADOWS_SOFT"), softHigh = Shader.IsKeywordEnabled("_SHADOWS_SOFT_HIGH")
                };
            }
        }
        [Serializable] private sealed class Controls
        {
            public string scenePath, cameraPath, lightPath, pipelineName, lightMode, perLightQuality, lightType, cameraAntialiasing, cameraClearFlags, activeColorSpace, ambientMode;
            public float renderScale, shadowDistance, depthBiasInput, normalBiasInput, lightDepthBiasInput, lightNormalBiasInput, lightStrength, lightIntensity, cameraFOV, cameraNear, cameraFar;
            public int msaaSamples, shadowResolution, cascades, screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight, vSyncCount, targetFrameRate, cameraCullingMask;
            public bool pipelineHDR, lightUsesPipelineBias, cameraHDR, cameraMSAA, cameraDynamicResolution, cameraPostProcessing, cameraOcclusionCulling, fog;
            public Color lightColor, cameraBackground, ambientLight;
            public Matrix4x4 cameraProjection;
            public List<TransformState> staticTransforms = new List<TransformState>();
            public List<RendererState> renderers = new List<RendererState>();
        }
        [Serializable] private sealed class TransformState
        {
            public string path;
            public Vector3 position, localScale;
            public Quaternion rotation;
            public bool activeSelf, activeInHierarchy, isStatic;
        }
        [Serializable] private sealed class RendererState
        {
            public string path, type, shadowCastingMode;
            public bool enabled, receiveShadows;
            public uint renderingLayerMask;
            public List<MaterialState> materials = new List<MaterialState>();
        }
        [Serializable] private sealed class MaterialState
        {
            public string name, shader;
            public bool hasBaseColor;
            public Color baseColor;
            public int renderQueue;
        }
    }
}
