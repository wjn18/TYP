using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TYP.Experiments
{
    public enum NativeBaselinePreset { Hard, Low, Medium, High }

    /// <summary>
    /// Selects native URP shadow presets and records diagnostic images/configurations.
    /// Captures are deliberately separate from performance measurements.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NativeBaselineRunner : MonoBehaviour
    {
        [SerializeField] private Light mainLight;
        [SerializeField] private Camera captureCamera;
        [SerializeField] private NativeBaselinePreset defaultPreset = NativeBaselinePreset.High;
        [SerializeField, Min(1)] private int settlingFrames = 30;
        [SerializeField] private string expectedPipelineName = "PCF_Baseline_URP";

        private static readonly NativeBaselinePreset[] SuitePresets =
        {
            NativeBaselinePreset.High, NativeBaselinePreset.Hard, NativeBaselinePreset.Low,
            NativeBaselinePreset.Medium, NativeBaselinePreset.High
        };
        private static readonly string[] SuiteNames = { "High", "Hard", "Low", "Medium", "High_repeat" };

        private UniversalAdditionalLightData lightData;
        private LightShadows previousShadows;
        private SoftShadowQuality previousQuality;
        private bool hasSavedLightState;
        private Coroutine suiteCoroutine;
        private SuiteManifest manifest;
        private string suiteDirectory;
        private bool quitAfterSuite;

        public string Status { get; private set; } = "Idle";
        public bool IsBusy { get; private set; }
        public NativeBaselinePreset CurrentPreset { get; private set; }

        private void Start()
        {
            try
            {
                NativeBaselinePreset selected = defaultPreset;
                string output = null;
                bool captureSuite = false;
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (arg == "--pcf-capture-suite") captureSuite = true;
                    else if (arg == "--pcf-quit") quitAfterSuite = true;
                    else if (arg.StartsWith("--pcf-preset=", StringComparison.Ordinal))
                    {
                        if (!Enum.TryParse(arg.Substring("--pcf-preset=".Length), true, out selected))
                            throw new ArgumentException("Unknown native PCF preset: " + arg);
                    }
                    else if (arg.StartsWith("--pcf-output=", StringComparison.Ordinal))
                        output = arg.Substring("--pcf-output=".Length);
                }
                if (captureSuite && Array.IndexOf(Environment.GetCommandLineArgs(), "--pcf-occluder-replay") >= 0)
                    throw new InvalidOperationException("Native capture and occluder replay must run separately.");
                ResolveReferences();
                ApplyPreset(selected);
                if (captureSuite)
                {
                    if (string.IsNullOrWhiteSpace(output))
                        output = Path.Combine(Application.dataPath, "../Captures/NativeBaselines/" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
                    StartCoroutine(BeginWhenDisplayReady(output));
                }
            }
            catch (Exception ex)
            {
                Fail("Initialisation failed", ex);
                QuitIfRequested(1);
            }
        }

        private IEnumerator BeginWhenDisplayReady(string output)
        {
            // Screen.SetResolution is deferred; allow the existing display-control
            // component to finish before validating the first standalone capture.
            for (int i = 0; i < 120 && (Screen.width != 1920 || Screen.height != 1080); i++)
            {
                if (!isActiveAndEnabled) yield break;
                yield return null;
            }
            if (!isActiveAndEnabled) yield break;
            try { BeginCaptureSuite(output); }
            catch (Exception ex)
            {
                Fail("Capture suite startup failed", ex);
                QuitIfRequested(1);
            }
        }

        private void ResolveReferences()
        {
            if (mainLight == null) mainLight = RenderSettings.sun;
            if (captureCamera == null) captureCamera = Camera.main;
            if (mainLight == null || captureCamera == null)
                throw new InvalidOperationException("Assign the main directional light and capture camera.");
            if (mainLight.type != LightType.Directional)
                throw new InvalidOperationException("Native baseline captures require a directional light.");
            lightData = mainLight.GetComponent<UniversalAdditionalLightData>();
            if (lightData == null)
                throw new InvalidOperationException("The main light requires existing UniversalAdditionalLightData.");
        }

        public void ApplyPreset(NativeBaselinePreset preset)
        {
            ResolveReferences();
            if (!Enum.IsDefined(typeof(NativeBaselinePreset), preset))
                throw new ArgumentOutOfRangeException(nameof(preset));
            // Use explicit per-light quality. Do not write to the shared URP asset.
            lightData.softShadowQuality = preset == NativeBaselinePreset.Low ? SoftShadowQuality.Low :
                preset == NativeBaselinePreset.Medium ? SoftShadowQuality.Medium : SoftShadowQuality.High;
            mainLight.shadows = preset == NativeBaselinePreset.Hard ? LightShadows.Hard : LightShadows.Soft;
            CurrentPreset = preset;
            if (!IsBusy) Status = "Ready: " + preset;
        }

        public void BeginCaptureSuite(string outputDirectory)
        {
            if (IsBusy) throw new InvalidOperationException("A native baseline capture suite is already running.");
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode before capturing.");
            ResolveReferences();
            ValidateCaptureConditions();
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Provide an output directory.");
            string fullPath = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length != 0)
                throw new IOException("Use a new or empty output directory to preserve earlier captures: " + fullPath);
            Directory.CreateDirectory(fullPath);
            suiteDirectory = fullPath;
            manifest = new SuiteManifest { startedUtc = DateTime.UtcNow.ToString("o"), state = "running" };
            previousShadows = mainLight.shadows;
            previousQuality = lightData.softShadowQuality;
            hasSavedLightState = true;
            IsBusy = true;
            Status = "Capturing native baseline suite";
            suiteCoroutine = StartCoroutine(CaptureSuite());
        }

        private void ValidateCaptureConditions()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null || pipeline.name != expectedPipelineName)
                throw new InvalidOperationException("Expected active URP asset " + expectedPipelineName + ".");
            if (Screen.width != 1920 || Screen.height != 1080 ||
                captureCamera.pixelWidth != 1920 || captureCamera.pixelHeight != 1080)
                throw new InvalidOperationException("Screen and camera must both be 1920 x 1080 before capture.");
            if (captureCamera.targetTexture != null)
                throw new InvalidOperationException("Screen capture requires a camera rendering to the screen.");
            if (!captureCamera.enabled || !captureCamera.gameObject.activeInHierarchy)
                throw new InvalidOperationException("The capture camera must be active.");
        }

        private IEnumerator CaptureSuite()
        {
            try
            {
                for (int i = 0; i < SuitePresets.Length; i++)
                {
                    if (!TryApplyForCapture(SuitePresets[i])) yield break;
                    Status = "Settling: " + SuiteNames[i];
                    for (int j = 0; j < settlingFrames; j++) yield return null;
                    yield return new WaitForEndOfFrame();
                    if (!TryCapture(SuiteNames[i], SuitePresets[i])) yield break;
                }
                manifest.state = "completed";
                Status = "Completed: " + suiteDirectory;
            }
            finally
            {
                RestoreLightState();
                manifest.endedUtc = DateTime.UtcNow.ToString("o");
                TryWriteManifest();
                IsBusy = false;
                suiteCoroutine = null;
                QuitIfRequested(manifest.state == "completed" ? 0 : 1);
            }
        }

        private bool TryApplyForCapture(NativeBaselinePreset preset)
        {
            try { ApplyPreset(preset); return true; }
            catch (Exception ex) { Fail("Preset application failed", ex); return false; }
        }

        private bool TryCapture(string name, NativeBaselinePreset preset)
        {
            Texture2D image = null;
            try
            {
                ValidateCaptureConditions();
                CaptureMetadata metadata = CreateMetadata(name, preset);
                image = ScreenCapture.CaptureScreenshotAsTexture();
                if (image == null || image.width != 1920 || image.height != 1080)
                    throw new InvalidOperationException("Captured image dimensions do not match 1920 x 1080.");
                metadata.imageWidth = image.width;
                metadata.imageHeight = image.height;
                metadata.captureUtc = DateTime.UtcNow.ToString("o");
                File.WriteAllBytes(Path.Combine(suiteDirectory, name + ".png"), image.EncodeToPNG());
                File.WriteAllText(Path.Combine(suiteDirectory, name + ".json"), JsonUtility.ToJson(metadata, true));
                manifest.completedCaptures.Add(name);
                Status = "Captured: " + name;
                return true;
            }
            catch (Exception ex) { Fail("Capture failed for " + name, ex); return false; }
            finally { if (image != null) Destroy(image); }
        }

        private CaptureMetadata CreateMetadata(string name, NativeBaselinePreset preset)
        {
            UniversalRenderPipelineAsset pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var data = new CaptureMetadata
            {
                captureName = name, preset = preset.ToString(), frameIndex = Time.frameCount,
                scenePath = captureCamera.gameObject.scene.path,
                unityVersion = Application.unityVersion, platform = Application.platform.ToString(),
                projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                operatingSystem = SystemInfo.operatingSystem, cpu = SystemInfo.processorType,
                systemMemoryMB = SystemInfo.systemMemorySize, gpu = SystemInfo.graphicsDeviceName,
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                pipelineAsset = pipeline.name, renderScale = pipeline.renderScale,
                msaaSamples = pipeline.msaaSampleCount, pipelineHDR = pipeline.supportsHDR,
                mainShadowResolution = pipeline.mainLightShadowmapResolution,
                cascades = pipeline.shadowCascadeCount, shadowDistance = pipeline.shadowDistance,
                pipelineDepthBiasInput = pipeline.shadowDepthBias,
                pipelineNormalBiasInput = pipeline.shadowNormalBias,
                lightUsesPipelineBias = lightData.usePipelineSettings,
                lightDepthBiasInput = mainLight.shadowBias, lightNormalBiasInput = mainLight.shadowNormalBias,
                selectedDepthBiasInput = lightData.usePipelineSettings ? pipeline.shadowDepthBias : mainLight.shadowBias,
                selectedNormalBiasInput = lightData.usePipelineSettings ? pipeline.shadowNormalBias : mainLight.shadowNormalBias,
                lightShadows = mainLight.shadows.ToString(), perLightQuality = lightData.softShadowQuality.ToString(),
                shadowStrength = mainLight.shadowStrength, lightPosition = mainLight.transform.position,
                lightRotation = mainLight.transform.eulerAngles, cameraPosition = captureCamera.transform.position,
                cameraRotation = captureCamera.transform.eulerAngles, cameraFOV = captureCamera.fieldOfView,
                cameraNear = captureCamera.nearClipPlane, cameraFar = captureCamera.farClipPlane,
                cameraHDR = captureCamera.allowHDR, cameraMSAA = captureCamera.allowMSAA,
                cameraDynamicResolution = captureCamera.allowDynamicResolution,
                screenWidth = Screen.width, screenHeight = Screen.height,
                cameraPixelWidth = captureCamera.pixelWidth, cameraPixelHeight = captureCamera.pixelHeight,
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                shadowBiasGlobalDiagnostic = Shader.GetGlobalVector("_ShadowBias"),
                mainLightShadowParams = Shader.GetGlobalVector("_MainLightShadowParams"),
                mainLightShadowmapSize = Shader.GetGlobalVector("_MainLightShadowmapSize"),
                shadowmapMatricesDiagnostic = Shader.GetGlobalMatrixArray("_MainLightWorldToShadow"),
                softKeyword = Shader.IsKeywordEnabled("_SHADOWS_SOFT"),
                softLowKeyword = Shader.IsKeywordEnabled("_SHADOWS_SOFT_LOW"),
                softMediumKeyword = Shader.IsKeywordEnabled("_SHADOWS_SOFT_MEDIUM"),
                softHighKeyword = Shader.IsKeywordEnabled("_SHADOWS_SOFT_HIGH"),
                mainShadowsKeyword = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS"),
                cascadeKeyword = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_CASCADE"),
                screenSpaceShadowsKeyword = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_SCREEN"),
                screenSpaceOcclusionKeyword = Shader.IsKeywordEnabled("_SCREEN_SPACE_OCCLUSION"),
                sourceComparisonSamplingCalls = preset == NativeBaselinePreset.Hard ? 1 :
                    preset == NativeBaselinePreset.Low ? 4 : preset == NativeBaselinePreset.Medium ? 9 : 16,
                filterDescription = preset == NativeBaselinePreset.Hard ? "Native hard-shadow path, one comparison-sampling call" :
                    preset == NativeBaselinePreset.Low ? "Native Low: four offsets with equal 0.25 weights" :
                    preset == NativeBaselinePreset.Medium ? "Native Medium: 5x5 tent helper, nine comparison-sampling calls" :
                    "Native High: 7x7 tent helper, sixteen comparison-sampling calls",
                sourceBiasQualityScale = preset == NativeBaselinePreset.Hard ? 1f :
                    preset == NativeBaselinePreset.Low ? 1.5f : preset == NativeBaselinePreset.Medium ? 2.5f : 3.5f
            };
            var cameraData = captureCamera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData != null)
            {
                data.cameraAntialiasing = cameraData.antialiasing.ToString();
                data.cameraPostProcessing = cameraData.renderPostProcessing;
            }
            if (data.shadowmapMatricesDiagnostic != null && data.shadowmapMatricesDiagnostic.Length > 0)
            {
                Matrix4x4 m = data.shadowmapMatricesDiagnostic[0];
                data.worldToShadowRow0LengthDiagnostic = new Vector3(m.m00, m.m01, m.m02).magnitude;
                data.worldToShadowRow1LengthDiagnostic = new Vector3(m.m10, m.m11, m.m12).magnitude;
            }
            return data;
        }

        private void RestoreLightState()
        {
            if (!hasSavedLightState) return;
            if (mainLight != null) mainLight.shadows = previousShadows;
            if (lightData != null) lightData.softShadowQuality = previousQuality;
            hasSavedLightState = false;
        }

        private void Fail(string context, Exception ex)
        {
            Status = context + ": " + ex.Message;
            if (manifest != null) { manifest.state = "failed"; manifest.error = Status; }
            Debug.LogError("NativeBaselineRunner: " + Status, this);
        }

        private void TryWriteManifest()
        {
            try { File.WriteAllText(Path.Combine(suiteDirectory, "suite.json"), JsonUtility.ToJson(manifest, true)); }
            catch (Exception ex) { Fail("Suite manifest write failed", ex); }
        }

        private void QuitIfRequested(int exitCode)
        {
            if (!quitAfterSuite) return;
#if !UNITY_EDITOR
            Application.Quit(exitCode);
#endif
        }

        private void OnDisable()
        {
            if (!IsBusy) return;
            if (manifest != null)
            {
                manifest.state = "interrupted";
                manifest.error = "Runner disabled before completion.";
                manifest.endedUtc = DateTime.UtcNow.ToString("o");
            }
            if (suiteCoroutine != null) StopCoroutine(suiteCoroutine);
            RestoreLightState();
            TryWriteManifest();
            IsBusy = false;
            suiteCoroutine = null;
            Status = "Interrupted; light settings restored";
        }

        [Serializable]
        private sealed class SuiteManifest
        {
            public string startedUtc, endedUtc, state, error;
            public string[] requestedOrder = SuiteNames;
            public List<string> completedCaptures = new List<string>();
            public string notes = "Diagnostic screenshot/configuration suite only; no performance measurements or algorithm equivalence claims. Native preset differences include filter and effective bias changes. High_repeat checks image reproducibility.";
        }

        [Serializable]
        private sealed class CaptureMetadata
        {
            public string captureName, preset, captureUtc, scenePath, unityVersion, platform, projectRoot;
            public int frameIndex, imageWidth, imageHeight, screenWidth, screenHeight, cameraPixelWidth, cameraPixelHeight;
            public string operatingSystem, cpu, gpu, graphicsDeviceVersion, graphicsAPI, colorSpace;
            public int systemMemoryMB;
            public string pipelineAsset, lightShadows, perLightQuality, cameraAntialiasing;
            public float renderScale, shadowDistance, pipelineDepthBiasInput, pipelineNormalBiasInput;
            public float lightDepthBiasInput, lightNormalBiasInput, selectedDepthBiasInput, selectedNormalBiasInput;
            public bool pipelineHDR, lightUsesPipelineBias, cameraHDR, cameraMSAA, cameraDynamicResolution, cameraPostProcessing;
            public int msaaSamples, mainShadowResolution, cascades, vSyncCount, targetFrameRate;
            public float shadowStrength, cameraFOV, cameraNear, cameraFar;
            public Vector3 lightPosition, lightRotation, cameraPosition, cameraRotation;
            public Vector4 shadowBiasGlobalDiagnostic, mainLightShadowParams, mainLightShadowmapSize;
            public Matrix4x4[] shadowmapMatricesDiagnostic;
            public float worldToShadowRow0LengthDiagnostic, worldToShadowRow1LengthDiagnostic;
            public bool softKeyword, softLowKeyword, softMediumKeyword, softHighKeyword;
            public bool mainShadowsKeyword, cascadeKeyword, screenSpaceShadowsKeyword, screenSpaceOcclusionKeyword;
            public int sourceComparisonSamplingCalls;
            public string filterDescription;
            public float sourceBiasQualityScale;
            public string globalReadbackCaveat = "Shader globals are diagnostic readbacks after rendering, not proof of the actual per-draw caster bias. World-to-shadow row lengths need independent validation before deriving world units per shadow texel.";
            public string tapCountCaveat = "Counts are source-level comparison-sampling calls on the selected path, not hardware depth comparisons or a measured shader execution count. Bilinear comparison sampling may combine multiple texels.";
            public string experimentScope = "Static native-preset reproduction. Initial development settings, not frozen final experiment settings. Native High is an implementation reference, not physical ground truth.";
        }
    }
}
