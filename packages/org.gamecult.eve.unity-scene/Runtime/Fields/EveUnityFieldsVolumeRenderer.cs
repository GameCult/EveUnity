using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.PluginFields;
using UnityEngine;
using UnityEngine.Rendering;

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
        private RenderTexture? _cloudTexture;
        private CommandBuffer? _commands;
        private string _activeNodeId = "";

        public long PresentedFrameId { get; private set; } = -1;
        public int PresentedLayerCount => _targets.Count(target => target.TargetTexture != null);
        public long CompositeCount { get; private set; }

        public void Bind(EveUnityPlayableWorldClientHost host, IEveUnityFieldsSplatsDocumentSource? source)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (_source != null) _source.FieldsSplatsAvailable -= OnFieldsSplatsAvailable;
            _host = host;
            _source = source;
            if (_source != null) _source.FieldsSplatsAvailable += OnFieldsSplatsAvailable;
            enabled = _source != null;
        }

        private void OnEnable() => RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        private void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
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

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_host == null || _document == null || camera == null ||
                _host.ActiveCameraTransform != camera.transform)
                return;
            if (PresentedLayerCount == 0) RenderLayers(_document);
            var field = ActiveField();
            if (field == null || !EnsureMaterial(field)) return;
            ApplyFieldBindings(field, _document);
            camera.depthTextureMode |= DepthTextureMode.Depth;
            EnsureCloudTexture(camera, field);
            if (_cloudTexture == null) return;

            var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            SetMatrix(field, "cameraInverseViewProjectionProperty", (gpuProjection * camera.worldToCameraMatrix).inverse);
            SetVector(field, "cameraProjectionExtentsProperty", ProjectionExtents(camera));
            SetFloat(field, "raymarchOffsetProperty", Halton(Time.frameCount & 1023, 3));
            foreach (var binding in ParseBindings(Prop(field, "screenTextureScaleBindings")))
            {
                var texture = _material!.GetTexture(binding.Key);
                if (texture != null)
                    _material.SetVector(binding.Value, new Vector4(
                        (float)Math.Max(1, camera.pixelWidth) / Math.Max(1, texture.width),
                        (float)Math.Max(1, camera.pixelHeight) / Math.Max(1, texture.height),
                        0f,
                        0f));
            }

            var raymarchPass = NonNegativeInt(field, "raymarchPass", 0);
            var compositePass = NonNegativeInt(field, "compositePass", 2);
            var cloudTextureProperty = Prop(field, "cloudTextureProperty");
            if (!string.IsNullOrWhiteSpace(cloudTextureProperty))
                _material!.SetTexture(cloudTextureProperty, _cloudTexture);

            _commands ??= new CommandBuffer { name = "Eve Fields Volume" };
            _commands.Clear();
            _commands.SetRenderTarget(_cloudTexture);
            _commands.ClearRenderTarget(false, true, Color.clear);
            _commands.DrawProcedural(Matrix4x4.identity, _material!, raymarchPass, MeshTopology.Triangles, 3, 1);
            _commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _commands.DrawProcedural(Matrix4x4.identity, _material!, compositePass, MeshTopology.Triangles, 3, 1);
            context.ExecuteCommandBuffer(_commands);
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
            if (_material != null && ReferenceEquals(shader, _sourceShader) &&
                string.Equals(_activeNodeId, field.NodeId, StringComparison.Ordinal)) return true;
            if (_material != null) Destroy(_material);
            _sourceShader = shader;
            _activeNodeId = field.NodeId;
            _material = new Material(shader) { hideFlags = HideFlags.DontSave };
            foreach (var prop in field.Props)
            {
                if (prop.Key.StartsWith("materialFloat.", StringComparison.Ordinal) &&
                    TryFloat(prop.Value, out var value))
                    _material.SetFloat(prop.Key.Substring("materialFloat.".Length), value);
                else if (prop.Key.StartsWith("materialVector.", StringComparison.Ordinal) &&
                         TryVector(prop.Value, out var vector))
                    _material.SetVector(prop.Key.Substring("materialVector.".Length), vector);
            }
            foreach (var binding in ParseBindings(Prop(field, "assetTextureBindings")))
            {
                var texture = provider.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(binding.Key, "", "provider-asset-ref"),
                    typeof(Texture)) as Texture;
                if (texture != null) _material.SetTexture(binding.Value, texture);
            }
            foreach (var keyword in Prop(field, "shaderKeywords").Split(';'))
                if (!string.IsNullOrWhiteSpace(keyword)) _material.EnableKeyword(keyword.Trim());
            return true;
        }

        private void ApplyFieldBindings(EveUnityFieldVolumeProjection field, EveFieldsSplatsDocument document)
        {
            if (_material == null) return;
            var bindings = ParseBindings(Prop(field, "layerBindings"));
            foreach (var binding in bindings)
            {
                var target = _targets.FirstOrDefault(value => string.Equals(value.LayerKey, binding.Key, StringComparison.Ordinal));
                if (target?.TargetTexture != null) _material.SetTexture(binding.Value, target.TargetTexture);
            }
            var viewportProperty = Prop(field, "viewportTransformProperty");
            if (!string.IsNullOrWhiteSpace(viewportProperty))
            {
                var viewport = document.Viewport;
                var width = viewport.MaxX - viewport.MinX;
                var height = viewport.MaxY - viewport.MinY;
                _material.SetVector(viewportProperty, new Vector4(
                    (float)((viewport.MinX + viewport.MaxX) * 0.5),
                    (float)((viewport.MinY + viewport.MaxY) * 0.5),
                    (float)width,
                    (float)height));
            }
        }

        private void EnsureTargets(EveUnityFieldVolumeProjection field)
        {
            var bindings = ParseBindings(Prop(field, "layerBindings"));
            if (string.Equals(_activeNodeId, field.NodeId, StringComparison.Ordinal) &&
                _targets.Count == bindings.Count) return;
            _targets.Clear();
            foreach (var binding in bindings)
                _targets.Add(new EveFieldsSplatLayerTarget { LayerKey = binding.Key, UseMipMaps = true });
        }

        private void EnsureLayerRenderer()
        {
            if (_layers != null) return;
            _layers = GetComponent<EveFieldsSplatLayerRenderer>();
            if (_layers == null) _layers = gameObject.AddComponent<EveFieldsSplatLayerRenderer>();
        }

        private void EnsureCloudTexture(Camera camera, EveUnityFieldVolumeProjection field)
        {
            var downsample = Mathf.Clamp(NonNegativeInt(field, "downsample", 1), 0, 3);
            var width = Mathf.Max(1, camera.pixelWidth >> downsample);
            var height = Mathf.Max(1, camera.pixelHeight >> downsample);
            if (_cloudTexture != null && _cloudTexture.width == width && _cloudTexture.height == height) return;
            if (_cloudTexture != null) { _cloudTexture.Release(); Destroy(_cloudTexture); }
            _cloudTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat)
            {
                name = "Eve Fields Volume",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _cloudTexture.Create();
        }

        private void ReleaseRenderState()
        {
            if (_commands != null) { _commands.Release(); _commands = null; }
            if (_cloudTexture != null) { _cloudTexture.Release(); Destroy(_cloudTexture); _cloudTexture = null; }
            if (_material != null) { Destroy(_material); _material = null; }
            _sourceShader = null;
            _activeNodeId = "";
            PresentedFrameId = -1;
            CompositeCount = 0;
        }

        private void SetMatrix(EveUnityFieldVolumeProjection field, string propKey, Matrix4x4 value)
        {
            var property = Prop(field, propKey);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetMatrix(property, value);
        }

        private void SetVector(EveUnityFieldVolumeProjection field, string propKey, Vector4 value)
        {
            var property = Prop(field, propKey);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetVector(property, value);
        }

        private void SetFloat(EveUnityFieldVolumeProjection field, string propKey, float value)
        {
            var property = Prop(field, propKey);
            if (!string.IsNullOrWhiteSpace(property)) _material!.SetFloat(property, value);
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
