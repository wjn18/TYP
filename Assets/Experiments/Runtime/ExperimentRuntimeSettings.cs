using UnityEngine;

namespace TYP.Experiments
{
    /// <summary>Applies the initial PCF experiment display and timing controls.</summary>
    [DisallowMultipleComponent]
    public sealed class ExperimentRuntimeSettings : MonoBehaviour
    {
        [SerializeField, Min(1)] private int outputWidth = 1920;
        [SerializeField, Min(1)] private int outputHeight = 1080;

        public int OutputWidth { get { return outputWidth; } }
        public int OutputHeight { get { return outputHeight; } }

        private void Awake()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
#if !UNITY_EDITOR
            Screen.SetResolution(outputWidth, outputHeight, FullScreenMode.Windowed);
#endif
        }
    }
}
