using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.PluginFields;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#nullable enable

namespace GameCult.Eve.UnityScene.Fields
{
    public sealed class EveUnityFieldsVolumeRenderer : MonoBehaviour
    {
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityFieldsSplatsDocumentSource? _source;
        private EveFieldsSplatsDocument? _document;
        private EveFieldsSplatLayerRenderer? _layers;
        private readonly List<EveFieldsSplatLayerTarget> _targets = new List<EveFieldsSplatLayerTarget>();
        private Material? _material;
        private Shader? _sourceShader;
        private IReadOnlyDictionary<string, string>? _programMetadata;
        private readonly Dictionary<string, Texture> _assetTexturesByPort =
            new Dictionary<string, Texture>(StringComparer.Ordinal);
        private RenderTexture? _raymarchTexture;
        private readonly RenderTexture?[] _historyTextures = new RenderTexture?[2];
        private int _historyTextureIndex = -1;
        private Matrix4x4 _previousViewProjection;
        private EveUnityFieldsVolumePass? _renderPass;
        private string _activeNodeId = "";

        public long PresentedFrameId { get; private set; } = -1;
        public int PresentedLayerCount => _targets.Count(target => target.TargetTexture != null);
        public long CompositeCount { get; private set; }
        public bool UsesTemporalHistory => HasTemporalProgram();
        public static RenderPassEvent CompositionRenderPassEvent => RenderPassEvent.BeforeRenderingPostProcessing;

        public void Bind(EveUnityPlayableWorldClientHost host, IEveUnityFieldsSplatsDocumentSource? source)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (_source != null) _source.FieldsSplatsAvailable -= OnFieldsSplatsAvailable;
            _host = host;
            _source = source;
            if (_source != null) _source.FieldsSplatsAvailable += OnFieldsSplatsAvailable;
            enabled = _source != null;
        }

        private void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseRenderState();
        }

        private void OnDestroy()
        {
            if (_source != null) _source.FieldsSplatsAvailable -= OnFieldsSplatsAvailable;
            _source = null;
            _host = null;
        }

        private void OnFieldsSplatsAvailable(EveFieldsSplatsDocument document)
        {
            if (document == null || document.FrameId < PresentedFrameId) return;
            _document = document;
            PresentedFrameId = document.FrameId;
            RenderLayers(document);
        }

        private void RenderLayers(EveFieldsSplatsDocument document)
        {
            var field = ActiveField();
            if (field == null) return;
            EnsureLayerRenderer();
            EnsureTargets(field);
            var width = PositiveInt(field, "textureWidth", 512);
            var height = PositiveInt(field, "textureHeight", 512);
            _layers!.Render(document, _targets, width, height);
            if (!EnsureMaterial(field)) return;
            ApplyFieldBindings(field, document);
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_host == null || _document == null || camera == null ||
                _host.ActiveCameraTransform != camera.transform)
                return;
            _renderPass ??= new EveUnityFieldsVolumePass(this);
            camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_renderPass);
        }

        private void RenderVolume(
            UnsafeCommandBuffer commands,
            Camera camera,
            TextureHandle colorTarget,
            TextureHandle depthTarget)
        {
            if (_host == null || _document == null || camera == null ||
                _host.ActiveCameraTransform != camera.transform)
                return;
            if (PresentedLayerCount == 0) RenderLayers(_document);
            var field = ActiveField();
            if (field == null || !EnsureMaterial(field)) return;
            ApplyFieldBindings(field, _document);
            camera.depthTextureMode |= DepthTextureMode.Depth;
            EnsureVolumeTextures(camera, field);
            if (_raymarchTexture == null || !ApplyViewportTextureScaleBindings(field, camera)) return;

            var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            var viewProjection = gpuProjection * camera.worldToCameraMatrix;
            SetMatrixPort("cameraInverseViewProjection", viewProjection.inverse);
            SetVectorPort("cameraProjectionExtents", ProjectionExtents(camera));
            SetFloatPort("raymarchOffset", Halton(Time.frameCount & 1023, 3));

            var raymarchPass = ProgramPass("raymarch");
            var compositePass = ProgramPass("composite");
            var temporalPass = ProgramPass("temporal");
            var useTemporalHistory = temporalPass >= 0;
            var historyWriteIndex = -1;
            RenderTexture finalCloudTexture = _raymarchTexture;

            if (useTemporalHistory)
            {
                historyWriteIndex = _historyTextureIndex < 0 ? 0 : (_historyTextureIndex + 1) % _historyTextures.Length;
                var historyReadTexture = _historyTextureIndex < 0
                    ? _raymarchTexture
                    : _historyTextures[_historyTextureIndex];
                finalCloudTexture = _historyTextures[historyWriteIndex]!;
                SetTexturePort("currentSample", _raymarchTexture);
                SetTexturePort("history", historyReadTexture!);
                SetMatrixPort("previousViewProjection", _historyTextureIndex < 0 ? viewProjection : _previousViewProjection);
                SetFloatPort("resetHistory", _historyTextureIndex < 0 ? 1f : 0f);
            }
            SetTexturePort("cloud", finalCloudTexture);

            commands.SetRenderTarget(_raymarchTexture);
            commands.SetViewport(new Rect(0f, 0f, _raymarchTexture.width, _raymarchTexture.height));
            commands.ClearRenderTarget(false, true, Color.clear);
            commands.DrawProcedural(Matrix4x4.identity, _material!, raymarchPass, MeshTopology.Triangles, 3, 1);
            if (useTemporalHistory)
            {
                commands.SetRenderTarget(finalCloudTexture);
                commands.SetViewport(new Rect(0f, 0f, finalCloudTexture.width, finalCloudTexture.height));
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.DrawProcedural(Matrix4x4.identity, _material!, temporalPass, MeshTopology.Triangles, 3, 1);
            }
            if (depthTarget.IsValid())
                commands.SetRenderTarget(colorTarget, depthTarget);
            else
                commands.SetRenderTarget(colorTarget);
            commands.SetViewport(camera.pixelRect);
            commands.DrawProcedural(Matrix4x4.identity, _material!, compositePass, MeshTopology.Triangles, 3, 1);
            if (useTemporalHistory)
            {
                _historyTextureIndex = historyWriteIndex;
                _previousViewProjection = viewProjection;
            }
            CompositeCount++;
        }

        private EveUnityFieldVolumeProjection? ActiveField() =>
            _host?.ActiveWorld?.FieldVolumes.FirstOrDefault(field =>
                string.Equals(field.DocumentSchema, EveFieldsSchemas.Splats, StringComparison.Ordinal));

        private bool EnsureMaterial(EveUnityFieldVolumeProjection field)
        {
            var provider = _host?.NativeAssetProvider;
            if (provider == null || string.IsNullOrWhiteSpace(field.MaterialAssetRef)) return false;
            var shader = provider.ResolveAsset(
                new EveUnityPlayableWorldAssetBinding(field.MaterialAssetRef, "", "provider-asset-ref"),
                typeof(Shader)) as Shader;
            if (shader == null || !shader.isSupported) return false;
            var shaderBinding = new EveUnityPlayableWorldAssetBinding(field.MaterialAssetRef, "", "provider-asset-ref");
            if (provider is not IEveUnityNativeAssetMetadataProvider metadataProvider ||
                !metadataProvider.TryResolveAssetMetadata(shaderBinding, out var metadata))
                return false;
            _programMetadata = metadata;
            if (!TryValidateProgramMetadata(metadata, out _))
                return false;
            if (_material != null && ReferenceEquals(shader, _sourceShader) &&
                string.Equals(_activeNodeId, field.NodeId, StringComparison.Ordinal)) return true;
            if (_material != null) Destroy(_material);
            _sourceShader = shader;
            _activeNodeId = field.NodeId;
            _material = new Material(shader) { hideFlags = HideFlags.DontSave };
            _assetTexturesByPort.Clear();
            ResetTemporalHistory();
            foreach (var parameter in ParseBindings(Prop(field, "floatParameters")))
            {
                if (TryFloat(parameter.Value, out var value)) SetFloatPort(parameter.Key, value);
            }
            foreach (var parameter in ParseBindings(Prop(field, "vectorParameters")))
                if (TryVector(parameter.Value, out var vector)) SetVectorPort(parameter.Key, vector);
            foreach (var binding in ParseBindings(Prop(field, "assetTextureBindings")))
            {
                var texture = provider.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(binding.Key, "", "provider-asset-ref"),
                    typeof(Texture)) as Texture;
                if (texture != null)
                {
                    _assetTexturesByPort[binding.Value] = texture;
                    SetTexturePort(binding.Value, texture);
                }
            }
            var quality = Prop(field, "quality").Trim().ToLowerInvariant();
            if (TryProgramValue($"unity.volume.quality.{quality}.keyword", out var keyword) &&
                !string.IsNullOrWhiteSpace(keyword))
                _material.EnableKeyword(keyword);
            foreach (var feature in Prop(field, "features").Split(';'))
            {
                var logicalFeature = feature.Trim();
                if (!string.IsNullOrWhiteSpace(logicalFeature) &&
                    TryProgramValue($"unity.volume.feature.{logicalFeature}.keyword", out var featureKeyword) &&
                    !string.IsNullOrWhiteSpace(featureKeyword))
                    _material.EnableKeyword(featureKeyword);
            }
            return true;
        }

        private void ApplyFieldBindings(EveUnityFieldVolumeProjection field, EveFieldsSplatsDocument document)
        {
            if (_material == null) return;
            var bindings = ParseBindings(Prop(field, "layerBindings"));
            foreach (var binding in bindings)
            {
                var target = _targets.FirstOrDefault(value => string.Equals(value.LayerKey, binding.Key, StringComparison.Ordinal));
                if (target?.TargetTexture != null) SetTexturePort(binding.Value, target.TargetTexture);
            }
            var viewport = document.Viewport;
            var width = viewport.MaxX - viewport.MinX;
            var height = viewport.MaxY - viewport.MinY;
            SetVectorPort("viewportTransform", new Vector4(
                (float)((viewport.MinX + viewport.MaxX) * 0.5),
                (float)((viewport.MinY + viewport.MaxY) * 0.5),
                (float)width,
                (float)height));
            foreach (var binding in ParseBindings(Prop(field, "documentFloatBindings")))
            {
                if (TryEvaluateDocumentFloatBinding(
                        document,
                        binding.Key,
                        binding.Value,
                        out var port,
                        out var value))
                    SetFloatPort(port, value);
            }
        }

        public static bool TryEvaluateDocumentFloatBinding(
            EveFieldsSplatsDocument? document,
            string source,
            string descriptor,
            out string port,
            out float value)
        {
            port = "";
            value = 0f;
            if (document == null) return false;
            var values = (descriptor ?? "").Split(',');
            if (values.Length != 3 ||
                string.IsNullOrWhiteSpace(values[0]) ||
                !TryFloat(values[1].Trim(), out var scale) ||
                !TryFloat(values[2].Trim(), out var offset))
                return false;

            double sourceValue;
            switch ((source ?? "").Trim())
            {
                case "simulationTimeSeconds":
                    sourceValue = document.SimulationTimeSeconds;
                    break;
                default:
                    return false;
            }

            port = values[0].Trim();
            value = (float)(sourceValue * scale + offset);
            return float.IsFinite(value);
        }

        private void EnsureTargets(EveUnityFieldVolumeProjection field)
        {
            var bindings = ParseBindings(Prop(field, "layerBindings"));
            var descriptors = ParseBindings(Prop(field, "layerTargetDescriptors"));
            if (string.Equals(_activeNodeId, field.NodeId, StringComparison.Ordinal) &&
                _targets.Count == bindings.Count &&
                _targets.All(target => bindings.ContainsKey(target.LayerKey)))
            {
                foreach (var target in _targets)
                    ApplyLayerTargetDescriptor(target, descriptors);
                return;
            }
            _targets.Clear();
            foreach (var binding in bindings)
            {
                var target = new EveFieldsSplatLayerTarget { LayerKey = binding.Key };
                ApplyLayerTargetDescriptor(target, descriptors);
                _targets.Add(target);
            }
        }

        private static void ApplyLayerTargetDescriptor(
            EveFieldsSplatLayerTarget target,
            IReadOnlyDictionary<string, string> descriptors)
        {
            target.WidthScale = 1f;
            target.HeightScale = 1f;
            target.UseMipMaps = false;
            target.FilterMode = FilterMode.Bilinear;
            if (descriptors.TryGetValue(target.LayerKey, out var descriptor))
                TryApplyLayerTargetDescriptor(target, descriptor);
        }

        public static bool TryApplyLayerTargetDescriptor(EveFieldsSplatLayerTarget? target, string descriptor)
        {
            if (target == null) return false;
            var values = (descriptor ?? "").Split(',');
            if (values.Length != 4 ||
                !TryFloat(values[0].Trim(), out var widthScale) || widthScale <= 0 ||
                !TryFloat(values[1].Trim(), out var heightScale) || heightScale <= 0 ||
                !bool.TryParse(values[2].Trim(), out var useMipMaps) ||
                !Enum.TryParse(values[3].Trim(), true, out FilterMode filterMode))
                return false;
            target.WidthScale = widthScale;
            target.HeightScale = heightScale;
            target.UseMipMaps = useMipMaps;
            target.FilterMode = filterMode;
            return true;
        }

        private void EnsureLayerRenderer()
        {
            if (_layers != null) return;
            _layers = GetComponent<EveFieldsSplatLayerRenderer>();
            if (_layers == null) _layers = gameObject.AddComponent<EveFieldsSplatLayerRenderer>();
        }

        private void EnsureVolumeTextures(Camera camera, EveUnityFieldVolumeProjection field)
        {
            var downsample = Mathf.Clamp(NonNegativeInt(field, "downsample", 1), 0, 3);
            var width = Mathf.Max(1, camera.pixelWidth >> downsample);
            var height = Mathf.Max(1, camera.pixelHeight >> downsample);
            var resetHistory = EnsureRenderTexture(ref _raymarchTexture, width, height, "Eve Fields Volume Raymarch");
            if (HasTemporalProgram())
            {
                resetHistory |= EnsureRenderTexture(ref _historyTextures[0], width, height, "Eve Fields Volume History A");
                resetHistory |= EnsureRenderTexture(ref _historyTextures[1], width, height, "Eve Fields Volume History B");
            }
            else
            {
                ReleaseRenderTexture(ref _historyTextures[0]);
                ReleaseRenderTexture(ref _historyTextures[1]);
                resetHistory = true;
            }
            if (resetHistory) ResetTemporalHistory();
        }

        private void ReleaseRenderState()
        {
            ReleaseRenderTexture(ref _raymarchTexture);
            ReleaseRenderTexture(ref _historyTextures[0]);
            ReleaseRenderTexture(ref _historyTextures[1]);
            if (_material != null) { Destroy(_material); _material = null; }
            _sourceShader = null;
            _programMetadata = null;
            _assetTexturesByPort.Clear();
            _activeNodeId = "";
            PresentedFrameId = -1;
            CompositeCount = 0;
            ResetTemporalHistory();
        }

        private sealed class EveUnityFieldsVolumePass : ScriptableRenderPass
        {
            private readonly EveUnityFieldsVolumeRenderer _owner;

            private sealed class PassData
            {
                internal Camera Camera = null!;
                internal EveUnityFieldsVolumeRenderer Owner = null!;
                internal TextureHandle ColorTarget;
                internal TextureHandle DepthTarget;
            }

            internal EveUnityFieldsVolumePass(EveUnityFieldsVolumeRenderer owner)
            {
                _owner = owner;
                renderPassEvent = CompositionRenderPassEvent;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                if (cameraData.camera == null || _owner._host == null ||
                    _owner._host.ActiveCameraTransform != cameraData.camera.transform)
                    return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var colorTarget = resourceData.activeColorTexture;
                if (!colorTarget.IsValid()) return;

                using var builder = renderGraph.AddUnsafePass<PassData>("Eve Fields Volume", out var passData);
                passData.Camera = cameraData.camera;
                passData.Owner = _owner;
                passData.ColorTarget = colorTarget;
                passData.DepthTarget = resourceData.activeDepthTexture;

                builder.UseTexture(passData.ColorTarget, AccessFlags.ReadWrite);
                if (passData.DepthTarget.IsValid())
                    builder.UseTexture(passData.DepthTarget, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    data.Owner.RenderVolume(context.cmd, data.Camera, data.ColorTarget, data.DepthTarget));
            }
        }

        private void ResetTemporalHistory()
        {
            _historyTextureIndex = -1;
            _previousViewProjection = Matrix4x4.identity;
        }

        private bool HasTemporalProgram() => ProgramPass("temporal") >= 0;

        private bool ApplyViewportTextureScaleBindings(EveUnityFieldVolumeProjection field, Camera camera)
        {
            foreach (var binding in ParseBindings(Prop(field, "viewportTextureScaleBindings")))
            {
                if (string.IsNullOrWhiteSpace(ProgramPort("vector", binding.Key)) ||
                    string.IsNullOrWhiteSpace(ProgramPort("texture", binding.Value)) ||
                    !_assetTexturesByPort.TryGetValue(binding.Value, out var texture) ||
                    !TryComputeViewportTextureScale(camera.pixelWidth, camera.pixelHeight, texture, out var scale))
                    return false;
                SetVectorPort(binding.Key, scale);
            }
            return true;
        }

        public static bool TryComputeViewportTextureScale(
            int viewportWidth,
            int viewportHeight,
            Texture? texture,
            out Vector4 scale)
        {
            scale = default;
            if (texture == null || texture.width <= 0 || texture.height <= 0) return false;
            scale = new Vector4(
                (float)Math.Max(1, viewportWidth) / texture.width,
                (float)Math.Max(1, viewportHeight) / texture.height,
                0f,
                0f);
            return true;
        }

        private static bool EnsureRenderTexture(ref RenderTexture? texture, int width, int height, string name)
        {
            if (texture != null && texture.width == width && texture.height == height) return false;
            ReleaseRenderTexture(ref texture);
            texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();
            return true;
        }

        private static void ReleaseRenderTexture(ref RenderTexture? texture)
        {
            if (texture == null) return;
            texture.Release();
            Destroy(texture);
            texture = null;
        }

        public static bool TryValidateProgramMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            out string error)
        {
            error = "";
            if (metadata == null) return Fail("Native volume program metadata is missing.", out error);
            if (!ValidPass(metadata, "raymarch")) return Fail("Native volume raymarch pass is missing.", out error);
            if (!ValidPass(metadata, "composite")) return Fail("Native volume composite pass is missing.", out error);
            if (!ValidPort(metadata, "texture", "cloud") ||
                !ValidPort(metadata, "matrix", "cameraInverseViewProjection") ||
                !ValidPort(metadata, "vector", "cameraProjectionExtents") ||
                !ValidPort(metadata, "vector", "viewportTransform") ||
                !ValidPort(metadata, "float", "raymarchOffset"))
                return Fail("Native volume program is missing a required raymarch or composite port.", out error);

            if (!metadata.ContainsKey("unity.volume.pass.temporal")) return true;
            if (!ValidPass(metadata, "temporal")) return Fail("Native volume temporal pass is invalid.", out error);
            if (!ValidPort(metadata, "texture", "currentSample") ||
                !ValidPort(metadata, "texture", "history") ||
                !ValidPort(metadata, "matrix", "previousViewProjection") ||
                !ValidPort(metadata, "float", "resetHistory"))
                return Fail("Native volume temporal pass is missing a required history port.", out error);
            return true;
        }

        private static bool ValidPass(IReadOnlyDictionary<string, string> metadata, string pass) =>
            metadata.TryGetValue($"unity.volume.pass.{pass}", out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0;

        private static bool ValidPort(IReadOnlyDictionary<string, string> metadata, string kind, string port) =>
            metadata.TryGetValue($"unity.volume.{kind}Port.{port}", out var value) &&
            !string.IsNullOrWhiteSpace(value);

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        private void SetMatrixPort(string port, Matrix4x4 value)
        {
            var property = ProgramPort("matrix", port);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetMatrix(property, value);
        }

        private void SetVectorPort(string port, Vector4 value)
        {
            var property = ProgramPort("vector", port);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetVector(property, value);
        }

        private void SetFloatPort(string port, float value)
        {
            var property = ProgramPort("float", port);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetFloat(property, value);
        }

        private void SetTexturePort(string port, Texture value)
        {
            var property = ProgramPort("texture", port);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetTexture(property, value);
        }

        private string ProgramPort(string kind, string port) =>
            TryProgramValue($"unity.volume.{kind}Port.{port}", out var value) ? value : "";

        private int ProgramPass(string pass) =>
            TryProgramValue($"unity.volume.pass.{pass}", out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : -1;

        private bool TryProgramValue(string key, out string value)
        {
            value = "";
            return _programMetadata != null && _programMetadata.TryGetValue(key, out value!);
        }

        private static Vector4 ProjectionExtents(Camera camera)
        {
            var y = camera.orthographic ? camera.orthographicSize : Mathf.Tan(0.5f * Mathf.Deg2Rad * camera.fieldOfView);
            return new Vector4(y * camera.aspect, y, 0f, 0f);
        }

        private static float Halton(int index, int radix)
        {
            var result = 0f;
            var fraction = 1f / radix;
            while (index > 0) { result += (index % radix) * fraction; index /= radix; fraction /= radix; }
            return result;
        }

        private static Dictionary<string, string> ParseBindings(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in (value ?? "").Split(';'))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || separator >= pair.Length - 1) continue;
                result[pair.Substring(0, separator).Trim()] = pair.Substring(separator + 1).Trim();
            }
            return result;
        }

        private static string Prop(EveUnityFieldVolumeProjection field, string key) =>
            field.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static int PositiveInt(EveUnityFieldVolumeProjection field, string key, int fallback) =>
            int.TryParse(Prop(field, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

        private static int NonNegativeInt(EveUnityFieldVolumeProjection field, string key, int fallback) =>
            int.TryParse(Prop(field, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : fallback;

        private static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !float.IsNaN(result) && !float.IsInfinity(result);

        private static bool TryVector(string value, out Vector4 result)
        {
            result = default;
            var values = (value ?? "").Split(',');
            if (values.Length < 2 || values.Length > 4) return false;
            var parsed = new float[4];
            for (var index = 0; index < values.Length; index++)
                if (!TryFloat(values[index], out parsed[index])) return false;
            result = new Vector4(parsed[0], parsed[1], parsed[2], parsed[3]);
            return true;
        }
    }
}
