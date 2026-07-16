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
    public sealed class EveUnityFieldsParticleRenderer : MonoBehaviour
    {
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityFieldsSplatsDocumentSource? _source;
        private EveFieldsSplatsDocument? _document;
        private EveFieldsSplatLayerRenderer? _layers;
        private GameObject? _layerRoot;
        private readonly List<EveFieldsSplatLayerTarget> _targets = new List<EveFieldsSplatLayerTarget>();
        private ComputeShader? _computeSource;
        private ComputeShader? _compute;
        private Material? _materialSource;
        private Material? _material;
        private IReadOnlyDictionary<string, string>? _computeMetadata;
        private IReadOnlyDictionary<string, string>? _materialMetadata;
        private readonly Dictionary<string, Texture> _assetTextures = new Dictionary<string, Texture>(StringComparer.Ordinal);
        private ComputeBuffer? _particles;
        private ComputeBuffer? _quadPoints;
        private EveUnityFieldsParticlePass? _renderPass;
        private string _activeNodeId = "";
        private long _rasterizedFrameId = -1;
        private Vector2 _rasterizedCenter = new Vector2(float.NaN, float.NaN);

        public long PresentedFrameId { get; private set; } = -1;
        public int ParticleCount { get; private set; }
        public int DispatchCount { get; private set; }
        public int DrawCount { get; private set; }
        public Vector2 LastGridCenter { get; private set; }
        public bool ProgramReady => _compute != null && _material != null && _particles != null && _quadPoints != null;
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
            ReleaseState();
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
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_host == null || _document == null || camera == null ||
                _host.ActiveCameraTransform != camera.transform)
                return;
            var field = ActiveField();
            if (field == null || !PrepareFrame(field, camera, _document)) return;
            _renderPass ??= new EveUnityFieldsParticlePass(this);
            camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_renderPass);
        }

        private EveUnityFieldParticlesProjection? ActiveField() =>
            _host?.ActiveWorld?.FieldParticles.FirstOrDefault(field =>
                string.Equals(field.DocumentSchema, EveFieldsSchemas.Splats, StringComparison.Ordinal));

        private bool PrepareFrame(
            EveUnityFieldParticlesProjection field,
            Camera camera,
            EveFieldsSplatsDocument document)
        {
            if (!EnsurePrograms(field) || !EnsureBuffers(field)) return false;
            EnsureTargets(field);
            EnsureLayerRenderer();
            if (_layers == null) return false;

            var baseWidth = PositiveInt(field, "textureWidth", 512);
            var baseHeight = PositiveInt(field, "textureHeight", 512);
            var viewportWidth = (float)(document.Viewport.MaxX - document.Viewport.MinX);
            var viewportHeight = (float)(document.Viewport.MaxY - document.Viewport.MinY);
            if (!float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight) ||
                viewportWidth <= 0f || viewportHeight <= 0f)
                return false;

            var center = new Vector2(
                (float)((document.Viewport.MinX + document.Viewport.MaxX) * 0.5),
                (float)((document.Viewport.MinY + document.Viewport.MaxY) * 0.5));
            if (string.Equals(Prop(field, "viewportAnchor"), "active-camera.xz", StringComparison.Ordinal))
            {
                var snapLayer = Prop(field, "viewportSnapLayer");
                var snapTarget = _targets.FirstOrDefault(target =>
                    string.Equals(target.LayerKey, snapLayer, StringComparison.Ordinal));
                var snapWidth = snapTarget == null
                    ? 0
                    : Mathf.Max(1, Mathf.RoundToInt(baseWidth * Mathf.Max(0.01f, snapTarget.WidthScale)));
                var snapHeight = snapTarget == null
                    ? 0
                    : Mathf.Max(1, Mathf.RoundToInt(baseHeight * Mathf.Max(0.01f, snapTarget.HeightScale)));
                var snapTexels = PositiveInt(field, "viewportSnapTexels", 0);
                var cellWorldSize = PositiveFloat(field, "cellWorldSize", 0f);
                var span = PositiveInt(field, "span", 0);
                if (!TryValidateSpatialLattice(
                        viewportWidth,
                        viewportHeight,
                        snapWidth,
                        snapHeight,
                        span,
                        cellWorldSize,
                        snapTexels)) return false;
                center.x = SnapCameraCoordinate(camera.transform.position.x, viewportWidth, snapWidth, snapTexels);
                center.y = SnapCameraCoordinate(camera.transform.position.z, viewportHeight, snapHeight, snapTexels);
            }

            if (_rasterizedFrameId != document.FrameId || _rasterizedCenter != center)
            {
                var projected = ProjectViewport(document, center, viewportWidth, viewportHeight);
                _layers.Render(projected, _targets, baseWidth, baseHeight);
                _rasterizedFrameId = document.FrameId;
                _rasterizedCenter = center;
            }

            ApplyProgramBindings(field, document, center, viewportWidth, viewportHeight);
            var kernel = ProgramKernel("update");
            if (kernel < 0) return false;
            var threadGroupSize = PositiveInt(field, "threadGroupSize", 1);
            var groups = Mathf.CeilToInt((float)ParticleCount / threadGroupSize);
            if (groups <= 0) return false;
            _compute!.Dispatch(kernel, groups, 1, 1);
            DispatchCount++;
            LastGridCenter = center;
            return true;
        }

        public static float SnapCameraCoordinate(
            float coordinate,
            float viewportWidth,
            int snapTargetWidth,
            float snapTexels)
        {
            if (!float.IsFinite(coordinate) || !float.IsFinite(viewportWidth) ||
                !float.IsFinite(snapTexels) || viewportWidth <= 0f ||
                snapTargetWidth <= 0 || snapTexels <= 0f)
                throw new ArgumentOutOfRangeException(nameof(viewportWidth));
            var snapLength = viewportWidth * snapTexels / snapTargetWidth;
            var cell = (int)(coordinate / snapLength);
            return cell * snapLength;
        }

        public static bool TryValidateSpatialLattice(
            float viewportWidth,
            float viewportHeight,
            int gravityTextureWidth,
            int gravityTextureHeight,
            int span,
            float cellWorldSize,
            int gravityTexelsPerCell)
        {
            if (!float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight) ||
                !float.IsFinite(cellWorldSize) || viewportWidth <= 0f || viewportHeight <= 0f ||
                gravityTextureWidth <= 0 || gravityTextureHeight <= 0 || span <= 0 ||
                cellWorldSize <= 0f || gravityTexelsPerCell <= 0)
                return false;
            var expectedExtent = span * cellWorldSize;
            var expectedCellFromX = viewportWidth * gravityTexelsPerCell / gravityTextureWidth;
            var expectedCellFromY = viewportHeight * gravityTexelsPerCell / gravityTextureHeight;
            const float tolerance = 0.0001f;
            return Mathf.Abs(viewportWidth - expectedExtent) <= tolerance &&
                   Mathf.Abs(viewportHeight - expectedExtent) <= tolerance &&
                   Mathf.Abs(expectedCellFromX - cellWorldSize) <= tolerance &&
                   Mathf.Abs(expectedCellFromY - cellWorldSize) <= tolerance;
        }

        private static EveFieldsSplatsDocument ProjectViewport(
            EveFieldsSplatsDocument source,
            Vector2 center,
            float width,
            float height) =>
            new EveFieldsSplatsDocument
            {
                Schema = source.Schema,
                FrameId = source.FrameId,
                PublishedAtUtc = source.PublishedAtUtc,
                SimulationTimeSeconds = source.SimulationTimeSeconds,
                RunId = source.RunId,
                ZoneIndex = source.ZoneIndex,
                ZoneName = source.ZoneName,
                Viewport = new EveFieldsViewport
                {
                    MinX = center.x - width * 0.5,
                    MinY = center.y - height * 0.5,
                    MaxX = center.x + width * 0.5,
                    MaxY = center.y + height * 0.5
                },
                Layers = source.Layers,
                Splats = source.Splats
            };

        private bool EnsurePrograms(EveUnityFieldParticlesProjection field)
        {
            var provider = _host?.NativeAssetProvider;
            if (provider == null || provider is not IEveUnityNativeAssetMetadataProvider metadataProvider ||
                string.IsNullOrWhiteSpace(field.ComputeProgramAssetRef) ||
                string.IsNullOrWhiteSpace(field.MaterialAssetRef))
                return false;

            var computeBinding = new EveUnityPlayableWorldAssetBinding(
                field.ComputeProgramAssetRef, "", "provider-asset-ref");
            var materialBinding = new EveUnityPlayableWorldAssetBinding(
                field.MaterialAssetRef, "", "provider-asset-ref");
            var compute = provider.ResolveAsset(computeBinding, typeof(ComputeShader)) as ComputeShader;
            var material = provider.ResolveAsset(materialBinding, typeof(Material)) as Material;
            if (compute == null || material == null || material.shader == null || !material.shader.isSupported ||
                !metadataProvider.TryResolveAssetMetadata(computeBinding, out var computeMetadata) ||
                !metadataProvider.TryResolveAssetMetadata(materialBinding, out var materialMetadata) ||
                !TryValidateProgramMetadata(computeMetadata, materialMetadata, out _) ||
                !TryValidateAdvertisedContract(field, compute, material, computeMetadata, materialMetadata))
                return false;

            var resolvedTextures = new Dictionary<string, Texture>(StringComparer.Ordinal);
            foreach (var binding in ParseBindings(Prop(field, "assetTextureBindings")))
            {
                var texture = provider.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(binding.Key, "", "provider-asset-ref"),
                    typeof(Texture)) as Texture;
                if (texture == null) return false;
                resolvedTextures[binding.Value] = texture;
            }

            if (ReferenceEquals(compute, _computeSource) && ReferenceEquals(material, _materialSource) &&
                string.Equals(field.NodeId, _activeNodeId, StringComparison.Ordinal) && ProgramReady &&
                resolvedTextures.Count == _assetTextures.Count && resolvedTextures.All(binding =>
                    _assetTextures.TryGetValue(binding.Key, out var current) && ReferenceEquals(current, binding.Value)))
                return true;

            ReleaseProgramsAndBuffers();
            _computeSource = compute;
            _materialSource = material;
            _computeMetadata = computeMetadata;
            _materialMetadata = materialMetadata;
            _activeNodeId = field.NodeId;
            _compute = Instantiate(compute);
            _material = new Material(material) { hideFlags = HideFlags.DontSave };
            _assetTextures.Clear();
            foreach (var binding in resolvedTextures) _assetTextures[binding.Key] = binding.Value;
            foreach (var feature in Prop(field, "features").Split(';'))
            {
                var logical = feature.Trim();
                if (!string.IsNullOrWhiteSpace(logical) &&
                    TryComputeProgramValue($"unity.particles.feature.{logical}.keyword", out var keyword) &&
                    !string.IsNullOrWhiteSpace(keyword))
                    _compute.EnableKeyword(keyword);
            }
            return true;
        }

        public static bool TryValidateProgramMetadata(
            IReadOnlyDictionary<string, string>? compute,
            IReadOnlyDictionary<string, string>? material,
            out string error)
        {
            if (compute == null || material == null)
                return Fail("Field-particle programs require compute and material metadata.", out error);
            var computeKeys = new[]
            {
                "unity.particles.kernel.update",
                "unity.particles.bufferPort.particles",
                "unity.particles.texturePort.surfaceHeight",
                "unity.particles.texturePort.tint",
                "unity.particles.texturePort.hue",
                "unity.particles.vectorPort.viewportTransform",
                "unity.particles.vectorPort.timeVector",
                "unity.particles.floatPort.time",
                "unity.particles.floatPort.period",
                "unity.particles.floatPort.spacing",
                "unity.particles.intPort.span"
            };
            if (computeKeys.Any(key => !compute.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
                return Fail("Field-particle compute metadata is missing a required logical port.", out error);
            var materialKeys = new[]
            {
                "unity.particles.pass.render",
                "unity.particles.bufferPort.particles",
                "unity.particles.bufferPort.quadPoints"
            };
            if (materialKeys.Any(key => !material.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) ||
                !int.TryParse(material["unity.particles.pass.render"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var pass) || pass < 0)
                return Fail("Field-particle material metadata is missing a valid render pass or buffer port.", out error);
            error = "";
            return true;
        }

        private static bool TryValidateAdvertisedContract(
            EveUnityFieldParticlesProjection field,
            ComputeShader compute,
            Material material,
            IReadOnlyDictionary<string, string> computeMetadata,
            IReadOnlyDictionary<string, string> materialMetadata)
        {
            var kernelName = computeMetadata["unity.particles.kernel.update"];
            if (!compute.HasKernel(kernelName)) return false;
            var kernel = compute.FindKernel(kernelName);
            compute.GetKernelThreadGroupSizes(kernel, out var nativeX, out var nativeY, out var nativeZ);
            if (!int.TryParse(Prop(field, "threadGroupSize"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var advertisedGroupSize) ||
                advertisedGroupSize <= 0 || nativeX != advertisedGroupSize || nativeY != 1 || nativeZ != 1)
                return false;

            if (!int.TryParse(materialMetadata["unity.particles.pass.render"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var pass) || pass < 0 || pass >= material.passCount)
                return false;

            foreach (var binding in ParseBindingPairs(Prop(field, "layerBindings")))
                if (!HasPort(computeMetadata, "texturePort", binding.Value)) return false;
            foreach (var binding in ParseBindingPairs(Prop(field, "assetTextureBindings")))
                if (!HasPort(computeMetadata, "texturePort", binding.Value)) return false;
            foreach (var parameter in ParseBindingPairs(Prop(field, "floatParameters")))
                if (!HasPort(computeMetadata, "floatPort", parameter.Key) ||
                    !TryFloat(parameter.Value, out _)) return false;
            var floatParameters = ParseBindings(Prop(field, "floatParameters"));
            if (!floatParameters.TryGetValue("spacing", out var spacingValue) ||
                !TryFloat(spacingValue, out var spacing) ||
                !TryFloat(Prop(field, "cellWorldSize"), out var cellWorldSize) ||
                Mathf.Abs(spacing - cellWorldSize) > 0.0001f ||
                PositiveInt(field, "gravityTexelsPerCell", 0) !=
                PositiveInt(field, "viewportSnapTexels", 0)) return false;
            foreach (var binding in ParseBindingPairs(Prop(field, "documentFloatBindings")))
            {
                var pieces = binding.Value.Split(',');
                if (pieces.Length != 3 || !HasPort(computeMetadata, "floatPort", pieces[0].Trim()) ||
                    !TryFloat(pieces[1], out _) || !TryFloat(pieces[2], out _)) return false;
            }

            var timeVectorPort = Prop(field, "documentTimeVectorPort");
            if (!string.IsNullOrWhiteSpace(timeVectorPort) &&
                !HasPort(computeMetadata, "vectorPort", timeVectorPort)) return false;
            foreach (var feature in Prop(field, "features").Split(';'))
            {
                var logical = feature.Trim();
                if (!string.IsNullOrWhiteSpace(logical) &&
                    (!computeMetadata.TryGetValue($"unity.particles.feature.{logical}.keyword", out var keyword) ||
                     string.IsNullOrWhiteSpace(keyword))) return false;
            }

            return PositiveInt(field, "span", 0) > 0 &&
                   PositiveInt(field, "particleStrideBytes", 0) > 0 &&
                   PositiveInt(field, "textureWidth", 0) > 0 &&
                   PositiveInt(field, "textureHeight", 0) > 0;
        }

        private static bool HasPort(
            IReadOnlyDictionary<string, string> metadata,
            string kind,
            string logical) =>
            metadata.TryGetValue($"unity.particles.{kind}.{logical}", out var native) &&
            !string.IsNullOrWhiteSpace(native);

        private bool EnsureBuffers(EveUnityFieldParticlesProjection field)
        {
            var span = PositiveInt(field, "span", 0);
            var stride = PositiveInt(field, "particleStrideBytes", 0);
            if (span <= 0 || span > 4096 || stride <= 0 || stride > 1024) return false;
            var count64 = (long)span * span;
            if (count64 <= 0 || count64 > 16_777_216) return false;
            var count = (int)count64;
            if (_particles != null && ParticleCount == count) return true;

            _particles?.Release();
            _quadPoints?.Release();
            _particles = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
            _quadPoints = new ComputeBuffer(6, sizeof(float) * 3, ComputeBufferType.Structured);
            _quadPoints.SetData(new[]
            {
                new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f), new Vector3(0.5f, -0.5f),
                new Vector3(0.5f, -0.5f), new Vector3(-0.5f, -0.5f), new Vector3(-0.5f, 0.5f)
            });
            ParticleCount = count;
            return true;
        }

        private void ApplyProgramBindings(
            EveUnityFieldParticlesProjection field,
            EveFieldsSplatsDocument document,
            Vector2 center,
            float viewportWidth,
            float viewportHeight)
        {
            var kernel = ProgramKernel("update");
            if (kernel < 0) return;
            SetComputeBuffer(kernel, "particles", _particles!);
            SetMaterialBuffer("particles", _particles!);
            SetMaterialBuffer("quadPoints", _quadPoints!);

            foreach (var binding in ParseBindings(Prop(field, "layerBindings")))
            {
                var target = _targets.FirstOrDefault(value => string.Equals(value.LayerKey, binding.Key, StringComparison.Ordinal));
                if (target?.TargetTexture != null) SetComputeTexture(kernel, binding.Value, target.TargetTexture);
            }
            foreach (var binding in _assetTextures)
                SetComputeTexture(kernel, binding.Key, binding.Value);
            SetComputeVector("viewportTransform", new Vector4(center.x, center.y, viewportWidth, viewportHeight));

            var time = (float)document.SimulationTimeSeconds;
            var timeVectorPort = Prop(field, "documentTimeVectorPort");
            if (!string.IsNullOrWhiteSpace(timeVectorPort))
                SetComputeVector(timeVectorPort, new Vector4(time / 20f, time, time * 2f, time * 3f));
            if (TryEvaluateDocumentFloatBindings(
                    document,
                    Prop(field, "documentFloatBindings"),
                    out var documentFloats))
                foreach (var binding in documentFloats)
                    SetComputeFloat(binding.Key, binding.Value);
            foreach (var parameter in ParseBindings(Prop(field, "floatParameters")))
                if (TryFloat(parameter.Value, out var value)) SetComputeFloat(parameter.Key, value);
            SetComputeInt("span", PositiveInt(field, "span", 0));
        }

        private void RenderParticles(
            UnsafeCommandBuffer commands,
            Camera camera,
            TextureHandle colorTarget,
            TextureHandle depthTarget)
        {
            if (_host == null || _host.ActiveCameraTransform != camera.transform || !ProgramReady) return;
            if (depthTarget.IsValid()) commands.SetRenderTarget(colorTarget, depthTarget);
            else commands.SetRenderTarget(colorTarget);
            commands.SetViewport(camera.pixelRect);
            commands.DrawProcedural(Matrix4x4.identity, _material!, MaterialPass("render"),
                MeshTopology.Triangles, 6, ParticleCount);
            DrawCount++;
        }

        private void EnsureTargets(EveUnityFieldParticlesProjection field)
        {
            var bindings = ParseBindings(Prop(field, "layerBindings"));
            var descriptors = ParseBindings(Prop(field, "layerTargetDescriptors"));
            if (string.Equals(_activeNodeId, field.NodeId, StringComparison.Ordinal) &&
                _targets.Count == bindings.Count && _targets.All(target => bindings.ContainsKey(target.LayerKey)))
                return;
            _targets.Clear();
            foreach (var binding in bindings)
            {
                var target = new EveFieldsSplatLayerTarget { LayerKey = binding.Key };
                if (descriptors.TryGetValue(binding.Key, out var descriptor))
                    EveUnityFieldsVolumeRenderer.TryApplyLayerTargetDescriptor(target, descriptor);
                _targets.Add(target);
            }
        }

        private void EnsureLayerRenderer()
        {
            if (_layers != null) return;
            _layerRoot = new GameObject("Eve Fields Particle Layers") { hideFlags = HideFlags.DontSave };
            _layerRoot.transform.SetParent(transform, false);
            _layers = _layerRoot.AddComponent<EveFieldsSplatLayerRenderer>();
        }

        private sealed class EveUnityFieldsParticlePass : ScriptableRenderPass
        {
            private readonly EveUnityFieldsParticleRenderer _owner;
            private sealed class PassData
            {
                internal Camera Camera = null!;
                internal EveUnityFieldsParticleRenderer Owner = null!;
                internal TextureHandle ColorTarget;
                internal TextureHandle DepthTarget;
            }

            internal EveUnityFieldsParticlePass(EveUnityFieldsParticleRenderer owner)
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
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.activeColorTexture.IsValid()) return;
                using var builder = renderGraph.AddUnsafePass<PassData>("Eve Fields Particles", out var data);
                data.Camera = cameraData.camera;
                data.Owner = _owner;
                data.ColorTarget = resources.activeColorTexture;
                data.DepthTarget = resources.activeDepthTexture;
                builder.UseTexture(data.ColorTarget, AccessFlags.ReadWrite);
                if (data.DepthTarget.IsValid()) builder.UseTexture(data.DepthTarget, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) =>
                    pass.Owner.RenderParticles(context.cmd, pass.Camera, pass.ColorTarget, pass.DepthTarget));
            }
        }

        private int ProgramKernel(string logical)
        {
            if (_compute == null || !TryComputeProgramValue($"unity.particles.kernel.{logical}", out var name) ||
                string.IsNullOrWhiteSpace(name) || !_compute.HasKernel(name))
                return -1;
            return _compute.FindKernel(name);
        }

        private int MaterialPass(string logical) =>
            TryMaterialProgramValue($"unity.particles.pass.{logical}", out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pass) && pass >= 0
                ? pass
                : -1;

        private void SetComputeTexture(int kernel, string logical, Texture texture)
        {
            if (TryComputeProgramValue($"unity.particles.texturePort.{logical}", out var property))
                _compute!.SetTexture(kernel, property, texture);
        }

        private void SetComputeBuffer(int kernel, string logical, ComputeBuffer buffer)
        {
            if (TryComputeProgramValue($"unity.particles.bufferPort.{logical}", out var property))
                _compute!.SetBuffer(kernel, property, buffer);
        }

        private void SetMaterialBuffer(string logical, ComputeBuffer buffer)
        {
            if (TryMaterialProgramValue($"unity.particles.bufferPort.{logical}", out var property))
                _material!.SetBuffer(property, buffer);
        }

        private void SetComputeFloat(string logical, float value)
        {
            if (TryComputeProgramValue($"unity.particles.floatPort.{logical}", out var property))
                _compute!.SetFloat(property, value);
        }

        private void SetComputeInt(string logical, int value)
        {
            if (TryComputeProgramValue($"unity.particles.intPort.{logical}", out var property))
                _compute!.SetInt(property, value);
        }

        private void SetComputeVector(string logical, Vector4 value)
        {
            if (TryComputeProgramValue($"unity.particles.vectorPort.{logical}", out var property))
                _compute!.SetVector(property, value);
        }

        private bool TryComputeProgramValue(string key, out string value)
        {
            value = "";
            return _computeMetadata != null && _computeMetadata.TryGetValue(key, out value!);
        }

        private bool TryMaterialProgramValue(string key, out string value)
        {
            value = "";
            return _materialMetadata != null && _materialMetadata.TryGetValue(key, out value!);
        }

        private void ReleaseState()
        {
            ReleaseProgramsAndBuffers();
            if (_layerRoot != null) Destroy(_layerRoot);
            _layerRoot = null;
            _layers = null;
            _targets.Clear();
            _document = null;
            PresentedFrameId = -1;
            DispatchCount = 0;
            DrawCount = 0;
            LastGridCenter = default;
            _rasterizedFrameId = -1;
            _rasterizedCenter = new Vector2(float.NaN, float.NaN);
        }

        private void ReleaseProgramsAndBuffers()
        {
            _particles?.Release();
            _quadPoints?.Release();
            _particles = null;
            _quadPoints = null;
            ParticleCount = 0;
            if (_compute != null) Destroy(_compute);
            if (_material != null) Destroy(_material);
            _compute = null;
            _material = null;
            _computeSource = null;
            _materialSource = null;
            _computeMetadata = null;
            _materialMetadata = null;
            _assetTextures.Clear();
            _activeNodeId = "";
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        private static Dictionary<string, string> ParseBindings(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in ParseBindingPairs(value)) result[pair.Key] = pair.Value;
            return result;
        }

        private static IReadOnlyList<KeyValuePair<string, string>> ParseBindingPairs(string value)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var pair in (value ?? "").Split(';'))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || separator >= pair.Length - 1) continue;
                result.Add(new KeyValuePair<string, string>(
                    pair.Substring(0, separator).Trim(),
                    pair.Substring(separator + 1).Trim()));
            }
            return result;
        }

        public static bool TryEvaluateDocumentFloatBindings(
            EveFieldsSplatsDocument document,
            string bindings,
            out IReadOnlyList<KeyValuePair<string, float>> values)
        {
            var result = new List<KeyValuePair<string, float>>();
            foreach (var binding in ParseBindingPairs(bindings))
            {
                if (!EveUnityFieldsVolumeRenderer.TryEvaluateDocumentFloatBinding(
                        document, binding.Key, binding.Value, out var port, out var value))
                {
                    values = Array.Empty<KeyValuePair<string, float>>();
                    return false;
                }
                result.Add(new KeyValuePair<string, float>(port, value));
            }
            values = result;
            return true;
        }

        private static string Prop(EveUnityFieldParticlesProjection field, string key) =>
            field.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static int PositiveInt(EveUnityFieldParticlesProjection field, string key, int fallback) =>
            int.TryParse(Prop(field, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : fallback;

        private static float PositiveFloat(EveUnityFieldParticlesProjection field, string key, float fallback) =>
            TryFloat(Prop(field, key), out var value) && value > 0f ? value : fallback;

        private static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && float.IsFinite(result);
    }
}
