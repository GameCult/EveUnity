using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityAdaptiveExposureRenderer : MonoBehaviour, IEveUnityRenderPassSource
    {
        private const int HistogramBins = 128;
        private ComputeShader? _compute;
        private Material? _multiplyMaterial;
        private ComputeBuffer? _histogram;
        private readonly RenderTexture?[] _exposure = new RenderTexture?[2];
        private int _currentExposure = -1;
        private Camera? _camera;
        private AdaptiveExposurePass? _pass;

        private float _lowPercent;
        private float _highPercent;
        private float _minimumEv;
        private float _maximumEv;
        private float _keyValue;
        private float _speedUp;
        private float _speedDown;
        private bool _progressive;

        public long AppliedFrameCount { get; private set; }
        public bool IsConfigured { get; private set; }
        public static RenderPassEvent ExposureRenderPassEvent =>
            (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingPostProcessing + 1);
        int IEveUnityRenderPassSource.RenderOrder => 300;

        public void Configure(
            float lowPercent,
            float highPercent,
            float minimumEv,
            float maximumEv,
            float keyValue,
            string adaptation,
            float speedUp,
            float speedDown)
        {
            _lowPercent = Mathf.Clamp(lowPercent, 1f, 98.99f);
            _highPercent = Mathf.Clamp(highPercent, _lowPercent + 0.01f, 99f);
            _minimumEv = Mathf.Min(minimumEv, maximumEv);
            _maximumEv = Mathf.Max(minimumEv, maximumEv);
            _keyValue = Mathf.Max(0f, keyValue);
            _speedUp = Mathf.Max(0f, speedUp);
            _speedDown = Mathf.Max(0f, speedDown);
            _progressive = string.Equals(adaptation, "progressive", StringComparison.Ordinal);
            IsConfigured = true;
            enabled = true;
        }

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            EveUnityRenderPassRegistry.Register(this);
        }

        private void OnDisable()
        {
            EveUnityRenderPassRegistry.Unregister(this);
            ReleaseResources();
        }

        private void OnDestroy() => ReleaseResources();

        bool IEveUnityRenderPassSource.TryEnqueuePass(Camera camera, ScriptableRenderer renderer)
        {
            if (!IsConfigured || _camera == null || camera != _camera) return false;
            _pass ??= new AdaptiveExposurePass(this);
            renderer.EnqueuePass(_pass);
            return true;
        }

        private bool EnsureResources()
        {
            _compute ??= Resources.Load<ComputeShader>("EveUnity/AdaptiveExposure");
            if (_multiplyMaterial == null)
            {
                var shader = Shader.Find("Hidden/EveUnity/AdaptiveExposureMultiply");
                if (shader != null) _multiplyMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            _histogram ??= new ComputeBuffer(HistogramBins, sizeof(uint), ComputeBufferType.Structured);
            for (var index = 0; index < _exposure.Length; index++)
            {
                if (_exposure[index] != null && _exposure[index]!.IsCreated()) continue;
                _exposure[index] = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    name = $"Eve Adaptive Exposure {index}",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _exposure[index]!.Create();
            }
            return _compute != null && _multiplyMaterial != null && _histogram != null;
        }

        private void Apply(UnsafeCommandBuffer commands, TextureHandle colorTarget, int width, int height)
        {
            if (!EnsureResources() || _compute == null || _histogram == null || _multiplyMaterial == null) return;

            var clear = _compute.FindKernel("ClearHistogram");
            commands.SetComputeBufferParam(_compute, clear, "_Histogram", _histogram);
            commands.DispatchCompute(_compute, clear, Mathf.CeilToInt(HistogramBins / 64f), 1, 1);

            var build = _compute.FindKernel("BuildHistogram");
            commands.SetComputeBufferParam(_compute, build, "_Histogram", _histogram);
            commands.SetComputeTextureParam(_compute, build, "_Source", colorTarget);
            commands.SetComputeVectorParam(_compute, "_HistogramScaleOffsetResolution", new Vector4(
                1f / 18f,
                0.5f,
                Mathf.Max(1, width),
                Mathf.Max(1, height)));
            commands.DispatchCompute(
                _compute,
                build,
                Mathf.CeilToInt(width / 2f / 16f),
                Mathf.CeilToInt(height / 2f / 16f),
                1);

            var destination = _currentExposure < 0 ? 0 : (_currentExposure + 1) % 2;
            var calculate = _compute.FindKernel(
                _currentExposure < 0 || !_progressive ? "CalculateFixedExposure" : "CalculateProgressiveExposure");
            commands.SetComputeBufferParam(_compute, calculate, "_Histogram", _histogram);
            commands.SetComputeVectorParam(_compute, "_ExposureFilterAndBrightness", new Vector4(
                _lowPercent * 0.01f,
                _highPercent * 0.01f,
                Mathf.Pow(2f, _minimumEv),
                Mathf.Pow(2f, _maximumEv)));
            commands.SetComputeVectorParam(_compute, "_ExposureAdaptation", new Vector4(
                _speedDown,
                _speedUp,
                _keyValue,
                Mathf.Max(0f, Time.deltaTime)));
            if (_currentExposure >= 0)
                commands.SetComputeTextureParam(_compute, calculate, "_PreviousExposure", _exposure[_currentExposure]!);
            commands.SetComputeTextureParam(_compute, calculate, "_DestinationExposure", _exposure[destination]!);
            commands.DispatchCompute(_compute, calculate, 1, 1, 1);

            _currentExposure = destination;
            _multiplyMaterial.SetTexture("_ExposureTexture", _exposure[_currentExposure]);
            commands.SetRenderTarget(colorTarget);
            commands.DrawProcedural(Matrix4x4.identity, _multiplyMaterial, 0, MeshTopology.Triangles, 3, 1);
            AppliedFrameCount++;
        }

        internal static float ExposureForAverageLuminance(float averageLuminance, float keyValue) =>
            Mathf.Max(0f, keyValue) / Mathf.Max(1e-6f, averageLuminance);

        internal static float AdaptExposure(float previous, float next, float deltaTime, float speedUp, float speedDown)
        {
            var speed = next > previous ? Mathf.Max(0f, speedDown) : Mathf.Max(0f, speedUp);
            return previous + (next - previous) * (1f - Mathf.Pow(2f, -Mathf.Max(0f, deltaTime) * speed));
        }

        private void ReleaseResources()
        {
            _histogram?.Release();
            _histogram = null;
            foreach (var texture in _exposure)
            {
                if (texture == null) continue;
                texture.Release();
                if (Application.isPlaying) Destroy(texture); else DestroyImmediate(texture);
            }
            _exposure[0] = null;
            _exposure[1] = null;
            _currentExposure = -1;
            if (_multiplyMaterial != null)
            {
                if (Application.isPlaying) Destroy(_multiplyMaterial); else DestroyImmediate(_multiplyMaterial);
                _multiplyMaterial = null;
            }
        }

        private sealed class AdaptiveExposurePass : ScriptableRenderPass
        {
            private readonly EveUnityAdaptiveExposureRenderer _owner;

            private sealed class PassData
            {
                internal EveUnityAdaptiveExposureRenderer Owner = null!;
                internal TextureHandle ColorTarget;
                internal int Width;
                internal int Height;
            }

            internal AdaptiveExposurePass(EveUnityAdaptiveExposureRenderer owner)
            {
                _owner = owner;
                renderPassEvent = ExposureRenderPassEvent;
                // Histogram construction samples the active camera color. URP must therefore
                // provide an intermediate color texture instead of exposing the back buffer.
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                if (cameraData.camera == null || cameraData.camera != _owner._camera) return;
                var resourceData = frameData.Get<UniversalResourceData>();
                if (resourceData.isActiveTargetBackBuffer) return;
                var colorTarget = resourceData.activeColorTexture;
                if (!colorTarget.IsValid()) return;

                using var builder = renderGraph.AddUnsafePass<PassData>("Eve Adaptive Exposure", out var passData);
                passData.Owner = _owner;
                passData.ColorTarget = colorTarget;
                passData.Width = cameraData.camera.pixelWidth;
                passData.Height = cameraData.camera.pixelHeight;
                builder.UseTexture(passData.ColorTarget, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    data.Owner.Apply(context.cmd, data.ColorTarget, data.Width, data.Height));
            }
        }
    }
}
