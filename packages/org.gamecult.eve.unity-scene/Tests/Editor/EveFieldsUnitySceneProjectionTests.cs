using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using GameCult.Eve.PluginFields;
using GameCult.Eve.UnityScene.Fields;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveFieldsUnitySceneProjectionTests
    {
        [Test]
        public void VolumeCameraToWorldPreservesUnityShaderPositiveZForward()
        {
            var cameraObject = new GameObject("eve-volume-camera-matrix");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.SetPositionAndRotation(
                    new Vector3(12f, -3f, 8f),
                    Quaternion.Euler(17f, 43f, -9f));
                var method = typeof(EveUnityFieldsVolumeRenderer).GetMethod(
                    "CameraTransformToWorld",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(method, Is.Not.Null);
                var matrix = (Matrix4x4)method!.Invoke(null, new object[] { camera });
                Assert.That(Vector3.Dot(
                    matrix.MultiplyVector(Vector3.forward).normalized,
                    camera.transform.forward), Is.EqualTo(1f).Within(0.00001f));
                Assert.That(Vector3.Distance(
                    matrix.MultiplyPoint3x4(Vector3.zero),
                    camera.transform.position), Is.LessThan(0.00001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void AdaptiveExposureMatchesFossilCompensationAndProgressiveDirection()
        {
            var exposureMethod = typeof(EveUnityAdaptiveExposureRenderer).GetMethod(
                "ExposureForAverageLuminance",
                BindingFlags.NonPublic | BindingFlags.Static);
            var adaptationMethod = typeof(EveUnityAdaptiveExposureRenderer).GetMethod(
                "AdaptExposure",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(exposureMethod, Is.Not.Null);
            Assert.That(adaptationMethod, Is.Not.Null);
            var brightBound = Mathf.Pow(2f, 0.3f);
            var exposure = (float)exposureMethod!.Invoke(null, new object[] { brightBound, 0.5f });
            Assert.That(exposure, Is.EqualTo(0.5f / brightBound).Within(0.000001f));

            var darkening = (float)adaptationMethod!.Invoke(
                null,
                new object[] { 1f, 0.25f, 0.5f, 2f, 1f });
            var brightening = (float)adaptationMethod.Invoke(
                null,
                new object[] { 1f, 4f, 0.5f, 2f, 1f });
            Assert.That(darkening, Is.EqualTo(0.625f).Within(0.000001f));
            Assert.That(brightening, Is.EqualTo(1f + 3f * (1f - Mathf.Pow(2f, -0.5f))).Within(0.000001f));
        }

        [Test]
        public void AdaptiveExposureGpuHistogramClampsBrightFrameToAdvertisedMaximumEv()
        {
            Assert.That(SystemInfo.supportsComputeShaders, Is.True);
            var compute = Resources.Load<ComputeShader>("EveUnity/AdaptiveExposure");
            Assert.That(compute, Is.Not.Null);
            var source = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
            var pixels = Enumerable.Repeat(new Color(16f, 16f, 16f, 1f), 32 * 32).ToArray();
            source.SetPixels(pixels);
            source.Apply(false, false);
            using var histogram = new ComputeBuffer(128, sizeof(uint), ComputeBufferType.Structured);
            var exposure = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true
            };
            exposure.Create();
            try
            {
                var clear = compute!.FindKernel("ClearHistogram");
                compute.SetBuffer(clear, "_Histogram", histogram);
                compute.Dispatch(clear, 2, 1, 1);

                var build = compute.FindKernel("BuildHistogram");
                compute.SetBuffer(build, "_Histogram", histogram);
                compute.SetTexture(build, "_Source", source);
                compute.SetVector("_HistogramScaleOffsetResolution", new Vector4(1f / 18f, 0.5f, 32f, 32f));
                compute.Dispatch(build, 1, 1, 1);

                var calculate = compute.FindKernel("CalculateFixedExposure");
                compute.SetBuffer(calculate, "_Histogram", histogram);
                compute.SetVector("_ExposureFilterAndBrightness", new Vector4(
                    0.4737294f,
                    0.99f,
                    Mathf.Pow(2f, -3f),
                    Mathf.Pow(2f, 0.3f)));
                compute.SetVector("_ExposureAdaptation", new Vector4(1f, 2f, 0.5f, 1f / 60f));
                compute.SetTexture(calculate, "_DestinationExposure", exposure);
                compute.Dispatch(calculate, 1, 1, 1);

                var previous = RenderTexture.active;
                RenderTexture.active = exposure;
                var readback = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
                readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                readback.Apply();
                RenderTexture.active = previous;
                var actual = readback.GetPixel(0, 0).r;
                UnityEngine.Object.DestroyImmediate(readback);
                Assert.That(actual, Is.EqualTo(0.5f / Mathf.Pow(2f, 0.3f)).Within(0.001f));
            }
            finally
            {
                exposure.Release();
                UnityEngine.Object.DestroyImmediate(exposure);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void RasterizerConsumesPluginOwnedDocumentContract()
        {
            var render = typeof(EveFieldsSplatRasterizer).GetMethod(
                "Render",
                new[] { typeof(IEveFieldsSplatsDocument), typeof(int), typeof(int) });

            Assert.That(render, Is.Not.Null);
            Assert.That(typeof(EveFieldsSplatRasterizer).Assembly.GetReferencedAssemblies().Any(assembly =>
                assembly.Name?.Contains("Aetheria", StringComparison.OrdinalIgnoreCase) == true), Is.False);
        }

        [Test]
        public void CanonicalFieldsPackageOwnsTheCultMeshWireDocument()
        {
            Assert.That(typeof(EveFieldsSplatsDocument).GetCustomAttributes(false)
                .Any(attribute => attribute.GetType().Name == "CultDocumentAttribute"), Is.True);
            Assert.That(new EveFieldsSplatsDocument().Schema, Is.EqualTo(EveFieldsSchemas.Splats));
            Assert.That(typeof(EveFieldsSplatsDocument).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name?.Contains("Aetheria", StringComparison.OrdinalIgnoreCase) == true), Is.False);
        }

        [Test]
        public void VolumeRendererConsumesCanonicalFieldsWithoutProviderCode()
        {
            Assert.That(typeof(EveUnityFieldsVolumeRenderer).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name?.Contains("Aetheria", StringComparison.OrdinalIgnoreCase) == true), Is.False);
            Assert.That(typeof(IEveUnityFieldsSplatsDocumentSource).GetEvent("FieldsSplatsAvailable"), Is.Not.Null);
        }

        [Test]
        public void VolumeRendererCompositesBeforeProviderPostProcessing()
        {
            Assert.That(
                EveUnityFieldsVolumeRenderer.CompositionRenderPassEvent,
                Is.EqualTo(RenderPassEvent.BeforeRenderingPostProcessing));
        }

        [Test]
        public void ParticleRendererExecutesProviderProgramWithoutProviderCode()
        {
            Assert.That(typeof(EveUnityFieldsParticleRenderer).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name?.Contains("Aetheria", StringComparison.OrdinalIgnoreCase) == true), Is.False);
            Assert.That(EveUnityFieldsParticleRenderer.CompositionRenderPassEvent,
                Is.EqualTo(RenderPassEvent.BeforeRenderingPostProcessing));
        }

        [TestCase(11f, 6f)]
        [TestCase(-11f, -6f)]
        [TestCase(5.999f, 0f)]
        public void FieldViewportFrameSnapsToAnIntegralGravityPixelMultiple(float coordinate, float expected)
        {
            Assert.That(EveUnityFieldsViewportFrame.SnapCameraCoordinate(
                coordinate,
                1536f,
                2048,
                8f), Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void FieldViewportFrameAcceptsAlignedGravityAndParticleLattices()
        {
            Assert.That(EveUnityFieldsViewportFrame.TryValidateSpatialLattice(
                1536f, 1536f, 2048, 2048, 256, 6f, 8), Is.True);
        }

        [Test]
        public void FieldViewportFrameRejectsAliasedGravityAndParticleLattices()
        {
            Assert.That(EveUnityFieldsViewportFrame.TryValidateSpatialLattice(
                2000f, 2000f, 2048, 2048, 256, 6f, 8), Is.False);
        }

        [Test]
        public void FogAndParticlesResolveTheSameSnappedWorldFrame()
        {
            var document = new EveFieldsSplatsDocument
            {
                FrameId = 7,
                Viewport = new EveFieldsViewport
                {
                    MinX = -768,
                    MinY = -768,
                    MaxX = 768,
                    MaxY = 768
                }
            };
            var contract = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["viewportAnchor"] = "active-camera.xz",
                ["span"] = "256",
                ["cellWorldSize"] = "6",
                ["viewportSnapTexels"] = "8"
            };

            Assert.That(EveUnityFieldsViewportFrame.TryResolve(
                document, contract, new Vector2(11f, -11f), 2048, 2048,
                out var fog, out var fogCenter), Is.True);
            Assert.That(EveUnityFieldsViewportFrame.TryResolve(
                document, contract, new Vector2(11f, -11f), 2048, 2048,
                out var particles, out var particleCenter), Is.True);

            Assert.That(fogCenter, Is.EqualTo(new Vector2(6f, -6f)));
            Assert.That(particleCenter, Is.EqualTo(fogCenter));
            Assert.That(particles.Viewport.MinX, Is.EqualTo(fog.Viewport.MinX));
            Assert.That(particles.Viewport.MinY, Is.EqualTo(fog.Viewport.MinY));
            Assert.That(particles.Viewport.MaxX, Is.EqualTo(fog.Viewport.MaxX));
            Assert.That(particles.Viewport.MaxY, Is.EqualTo(fog.Viewport.MaxY));
        }

        [Test]
        public void ParticleRendererFailsClosedWithoutCompleteNativeAbi()
        {
            var compute = RequiredParticleComputeMetadata();
            var material = RequiredParticleMaterialMetadata();
            Assert.That(EveUnityFieldsParticleRenderer.TryValidateProgramMetadata(compute, material, out var error),
                Is.True, error);

            compute.Remove("unity.particles.intPort.span");

            Assert.That(EveUnityFieldsParticleRenderer.TryValidateProgramMetadata(compute, material, out error),
                Is.False);
            Assert.That(error, Does.Contain("logical port"));
        }

        [Test]
        public void ParticleMaterialAcceptsAdvertisedTemporalDitherInputs()
        {
            var field = new EveUnityFieldParticlesProjection(
                "particles", "document", EveFieldsSchemas.Splats, "compute", "material", "world.transparent",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["materialAssetTextureBindings"] = "texture.noise=dither",
                    ["materialViewportTextureScaleBindings"] = "ditherCoordinates=dither",
                    ["materialRenderFrameIndexPort"] = "frameIndex"
                });
            var metadata = RequiredParticleMaterialMetadata();
            metadata["unity.particles.texturePort.dither"] = "_DitheringTex";
            metadata["unity.particles.vectorPort.ditherCoordinates"] = "_DitheringCoords";
            metadata["unity.particles.intPort.frameIndex"] = "_FrameNumber";

            Assert.That(EveUnityFieldsParticleRenderer.TryValidateMaterialPresentationContract(
                field, metadata, out var error), Is.True, error);
        }

        [Test]
        public void ParticleMaterialRejectsPartialTemporalDitherInputs()
        {
            var field = new EveUnityFieldParticlesProjection(
                "particles", "document", EveFieldsSchemas.Splats, "compute", "material", "world.transparent",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["materialAssetTextureBindings"] = "texture.noise=dither",
                    ["materialViewportTextureScaleBindings"] = "ditherCoordinates=dither",
                    ["materialRenderFrameIndexPort"] = "frameIndex"
                });
            var metadata = RequiredParticleMaterialMetadata();
            metadata["unity.particles.texturePort.dither"] = "_DitheringTex";
            metadata["unity.particles.vectorPort.ditherCoordinates"] = "_DitheringCoords";

            Assert.That(EveUnityFieldsParticleRenderer.TryValidateMaterialPresentationContract(
                field, metadata, out var error), Is.False);
            Assert.That(error, Does.Contain("render-frame"));
        }

        [Test]
        public void ParticleRendererPreservesRepeatedDocumentSourcesForDistinctPorts()
        {
            var document = new EveFieldsSplatsDocument { SimulationTimeSeconds = 12.5 };

            Assert.That(EveUnityFieldsParticleRenderer.TryEvaluateDocumentFloatBindings(
                document,
                "simulationTimeSeconds=time,1,0;simulationTimeSeconds=flowScroll,0.025,0",
                out var bindings), Is.True);
            Assert.That(bindings.Count, Is.EqualTo(2));
            Assert.That(bindings[0].Key, Is.EqualTo("time"));
            Assert.That(bindings[0].Value, Is.EqualTo(12.5f));
            Assert.That(bindings[1].Key, Is.EqualTo("flowScroll"));
            Assert.That(bindings[1].Value, Is.EqualTo(0.3125f));
        }

        [Test]
        public void VolumeProgramAcceptsPortableTwoPassLifecycle()
        {
            var metadata = RequiredVolumeProgramMetadata();

            Assert.That(EveUnityFieldsVolumeRenderer.TryValidateProgramMetadata(metadata, out var error), Is.True, error);
        }

        [Test]
        public void VolumeProgramAcceptsCompleteTemporalLifecycle()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.pass.temporal"] = "1";
            metadata["unity.volume.texturePort.currentSample"] = "_CurrentVolume";
            metadata["unity.volume.texturePort.history"] = "_HistoryVolume";
            metadata["unity.volume.matrixPort.previousViewProjection"] = "_PreviousViewProjection";
            metadata["unity.volume.floatPort.resetHistory"] = "_ResetHistory";

            Assert.That(EveUnityFieldsVolumeRenderer.TryValidateProgramMetadata(metadata, out var error), Is.True, error);
        }

        [Test]
        public void VolumeRendererUsesCurrentProjectionWithPreviousViewWhenAdvertised()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.matrixSemantic.previousViewProjection"] =
                "current-projection.previous-view.v1";
            var previousViewProjection = Matrix4x4.Translate(new Vector3(1f, 2f, 3f));
            var currentProjection = Matrix4x4.Scale(new Vector3(2f, 3f, 4f));
            var previousView = Matrix4x4.Rotate(Quaternion.Euler(10f, 20f, 30f));

            var resolved = EveUnityFieldsVolumeRenderer.ResolvePreviousViewProjection(
                metadata,
                previousViewProjection,
                currentProjection,
                Matrix4x4.identity,
                previousView);

            Assert.That(resolved, Is.EqualTo(currentProjection * previousView));
            Assert.That(resolved, Is.Not.EqualTo(previousViewProjection));
        }

        [Test]
        public void VolumeRendererDefaultsToTruePreviousViewProjection()
        {
            var previousViewProjection = Matrix4x4.Translate(new Vector3(1f, 2f, 3f));

            var resolved = EveUnityFieldsVolumeRenderer.ResolvePreviousViewProjection(
                RequiredVolumeProgramMetadata(),
                previousViewProjection,
                Matrix4x4.Scale(Vector3.one * 2f),
                Matrix4x4.identity,
                Matrix4x4.Rotate(Quaternion.Euler(10f, 20f, 30f)));

            Assert.That(resolved, Is.EqualTo(previousViewProjection));
        }

        [Test]
        public void VolumeRendererUsesNonRenderTargetProjectionWhenAdvertised()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.matrixSemantic.previousViewProjection"] =
                "non-render-target-projection.previous-view.v1";
            var previousViewProjection = Matrix4x4.Translate(new Vector3(1f, 2f, 3f));
            var renderTargetProjection = Matrix4x4.Scale(new Vector3(2f, -3f, 4f));
            var nonRenderTargetProjection = Matrix4x4.Scale(new Vector3(2f, 3f, 4f));
            var previousView = Matrix4x4.Rotate(Quaternion.Euler(10f, 20f, 30f));

            var resolved = EveUnityFieldsVolumeRenderer.ResolvePreviousViewProjection(
                metadata,
                previousViewProjection,
                renderTargetProjection,
                nonRenderTargetProjection,
                previousView);

            Assert.That(resolved, Is.EqualTo(nonRenderTargetProjection * previousView));
            Assert.That(resolved, Is.Not.EqualTo(renderTargetProjection * previousView));
        }

        [Test]
        public void VolumeProgramRejectsUnknownPreviousViewProjectionSemantic()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.pass.temporal"] = "1";
            metadata["unity.volume.texturePort.currentSample"] = "_CurrentVolume";
            metadata["unity.volume.texturePort.history"] = "_HistoryVolume";
            metadata["unity.volume.matrixPort.previousViewProjection"] = "_PreviousViewProjection";
            metadata["unity.volume.floatPort.resetHistory"] = "_ResetHistory";
            metadata["unity.volume.matrixSemantic.previousViewProjection"] = "made-up";

            Assert.That(EveUnityFieldsVolumeRenderer.TryValidateProgramMetadata(metadata, out var error), Is.False);
            Assert.That(error, Does.Contain("previous-view-projection semantic"));
        }

        [Test]
        public void VolumeRendererUsesAdvertisedBootstrapQualityOnlyForEmptyHistory()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.quality.bootstrap"] = "ultra";
            metadata["unity.volume.quality.ultra.keyword"] = "ULTRA_QUALITY";

            Assert.That(EveUnityFieldsVolumeRenderer.ResolveQuality(metadata, "high", true), Is.EqualTo("ultra"));
            Assert.That(EveUnityFieldsVolumeRenderer.ResolveQuality(metadata, "high", false), Is.EqualTo("high"));
        }

        [Test]
        public void VolumeProgramRejectsBootstrapQualityWithoutAdvertisedKeyword()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.pass.temporal"] = "1";
            metadata["unity.volume.texturePort.currentSample"] = "_CurrentVolume";
            metadata["unity.volume.texturePort.history"] = "_HistoryVolume";
            metadata["unity.volume.matrixPort.previousViewProjection"] = "_PreviousViewProjection";
            metadata["unity.volume.floatPort.resetHistory"] = "_ResetHistory";
            metadata["unity.volume.quality.bootstrap"] = "ultra";

            Assert.That(EveUnityFieldsVolumeRenderer.TryValidateProgramMetadata(metadata, out var error), Is.False);
            Assert.That(error, Does.Contain("bootstrap quality"));
        }

        [Test]
        public void VolumeProgramRejectsPartialTemporalLifecycle()
        {
            var metadata = RequiredVolumeProgramMetadata();
            metadata["unity.volume.pass.temporal"] = "1";
            metadata["unity.volume.texturePort.currentSample"] = "_CurrentVolume";

            Assert.That(EveUnityFieldsVolumeRenderer.TryValidateProgramMetadata(metadata, out var error), Is.False);
            Assert.That(error, Does.Contain("history port"));
        }

        [Test]
        public void VolumeRendererDerivesViewportTextureScaleFromNativeDimensions()
        {
            var texture = new Texture2D(128, 64, TextureFormat.RGBA32, false);
            try
            {
                Assert.That(EveUnityFieldsVolumeRenderer.TryComputeViewportTextureScale(
                    640,
                    360,
                    texture,
                    out var scale), Is.True);
                Assert.That(scale, Is.EqualTo(new Vector4(5f, 5.625f, 0f, 0f)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void VolumeRendererAppliesProviderAdvertisedLayerTargetShape()
        {
            var target = new EveFieldsSplatLayerTarget { LayerKey = "fog.tint" };

            Assert.That(
                EveUnityFieldsVolumeRenderer.TryApplyLayerTargetDescriptor(
                    target,
                    "0.5,0.25,true,trilinear"),
                Is.True);
            Assert.That(target.WidthScale, Is.EqualTo(0.5f));
            Assert.That(target.HeightScale, Is.EqualTo(0.25f));
            Assert.That(target.UseMipMaps, Is.True);
            Assert.That(target.FilterMode, Is.EqualTo(FilterMode.Trilinear));
        }

        [Test]
        public void VolumeRendererRejectsMalformedLayerTargetShape()
        {
            var target = new EveFieldsSplatLayerTarget
            {
                WidthScale = 2f,
                HeightScale = 2f,
                UseMipMaps = true,
                FilterMode = FilterMode.Trilinear
            };

            Assert.That(
                EveUnityFieldsVolumeRenderer.TryApplyLayerTargetDescriptor(target, "0,1,true,bilinear"),
                Is.False);
            Assert.That(target.WidthScale, Is.EqualTo(2f));
            Assert.That(target.HeightScale, Is.EqualTo(2f));
            Assert.That(target.UseMipMaps, Is.True);
            Assert.That(target.FilterMode, Is.EqualTo(FilterMode.Trilinear));
        }

        [Test]
        public void VolumeRendererBindsAuthoritativeDocumentTimeToLogicalPort()
        {
            var document = new EveFieldsSplatsDocument { SimulationTimeSeconds = 12.5 };

            Assert.That(
                EveUnityFieldsVolumeRenderer.TryEvaluateDocumentFloatBinding(
                    document,
                    "simulationTimeSeconds",
                    "flowScroll,0.025,-0.1",
                    out var port,
                    out var value),
                Is.True);
            Assert.That(port, Is.EqualTo("flowScroll"));
            Assert.That(value, Is.EqualTo(0.2125f).Within(0.00001f));
        }

        [Test]
        public void VolumeRendererRejectsUnknownDocumentFloatSource()
        {
            Assert.That(
                EveUnityFieldsVolumeRenderer.TryEvaluateDocumentFloatBinding(
                    new EveFieldsSplatsDocument(),
                    "unityTime",
                    "flowScroll,1,0",
                    out _,
                    out _),
                Is.False);
        }

        [Test]
        public void PackageContainsGenericFieldsShader()
        {
            Assert.That(Shader.Find("Eve/Fields/Splats"), Is.Not.Null);
            Assert.That(File.Exists("Packages/org.gamecult.eve.unity-scene/Runtime/Fields/EveFieldsSplatsCore.hlsl"), Is.True);
        }

        private static Dictionary<string, string> RequiredParticleComputeMetadata() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.particles.kernel.update"] = "UpdateParticles",
                ["unity.particles.bufferPort.particles"] = "particles",
                ["unity.particles.texturePort.surfaceHeight"] = "_SurfaceHeight",
                ["unity.particles.texturePort.tint"] = "_Tint",
                ["unity.particles.texturePort.hue"] = "_Hue",
                ["unity.particles.vectorPort.viewportTransform"] = "_GridTransform",
                ["unity.particles.vectorPort.timeVector"] = "_Time",
                ["unity.particles.floatPort.time"] = "time",
                ["unity.particles.floatPort.period"] = "period",
                ["unity.particles.floatPort.spacing"] = "spacing",
                ["unity.particles.intPort.span"] = "span"
            };

        private static Dictionary<string, string> RequiredParticleMaterialMetadata() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.particles.pass.render"] = "0",
                ["unity.particles.bufferPort.particles"] = "particles",
                ["unity.particles.bufferPort.quadPoints"] = "quadPoints"
            };

        [Test]
        public void LayerRendererConsumesPluginContractsWithoutProviderBindings()
        {
            var render = typeof(EveFieldsSplatLayerRenderer).GetMethod("Render");
            Assert.That(render, Is.Not.Null);
            Assert.That(render.GetParameters()[0].ParameterType, Is.EqualTo(typeof(IEveFieldsSplatsDocument)));
            Assert.That(typeof(EveFieldsSplatLayerRenderer).GetField("GlobalTextureName"), Is.Null);
        }

        [Test]
        public void RasterizerProducesProviderIndependentFieldsCapture()
        {
            var host = new GameObject("eve-fields-capture");
            var rasterizer = host.AddComponent<EveFieldsSplatRasterizer>();
            var output = rasterizer.Render(new CaptureDocument(), 64, 64);
            Assert.That(output, Is.Not.Null);

            var previous = RenderTexture.active;
            RenderTexture.active = output;
            var readback = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
            readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;
            Assert.That(readback.GetPixels().Any(pixel => pixel.r > 0.01f), Is.True);

            var outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/fields-surface/latest/unity-fields.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, readback.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(readback);
            UnityEngine.Object.DestroyImmediate(host);
        }

        [Test]
        public void RasterizerLowersPortablePowerPulseParameters()
        {
            var document = new EveFieldsSplatsDocument
            {
                Viewport = new EveFieldsViewport { MinX = -1, MinY = -1, MaxX = 1, MaxY = 1 },
                Splats = new EveFieldsSplatSoa
                {
                    Count = 1,
                    CenterX = new[] { 0d },
                    CenterY = new[] { 0d },
                    HalfExtentX = new[] { 1d },
                    HalfExtentY = new[] { 1d },
                    RotationCos = new[] { 1d },
                    RotationSin = new[] { 0d },
                    Channel = new[] { 0 },
                    Falloff = new[] { EveFieldsSplatFalloffs.PowerPulse },
                    FalloffScale = new[] { 2d },
                    FalloffExponent = new[] { 1d },
                    ValueR = new[] { 1d },
                    ValueA = new[] { 1d },
                    LayerIndex = new[] { 0 },
                    SourceKind = new[] { EveFieldsSplatSourceKinds.Constant }
                }
            };
            var host = new GameObject("eve-fields-power-pulse");
            var rasterizer = host.AddComponent<EveFieldsSplatRasterizer>();
            var output = rasterizer.Render(document, 128, 128);
            var previous = RenderTexture.active;
            RenderTexture.active = output;
            var readback = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
            readback.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;

            Assert.That(readback.GetPixel(64, 64).r, Is.GreaterThan(0.95f));
            Assert.That(readback.GetPixel(80, 64).r, Is.InRange(0.65f, 0.85f));
            Assert.That(readback.GetPixel(96, 64).r, Is.LessThan(0.02f));

            UnityEngine.Object.DestroyImmediate(readback);
            UnityEngine.Object.DestroyImmediate(host);
        }

        [Test]
        public void RasterizerAnchorsAnimatedSimplexToFieldWorldAndPublishedSimulationTime()
        {
            var first = ProceduralDocument(
                0, 100, 50, EveFieldsSplatSourceKinds.AnimatedSimplexNoise, 2);
            var shifted = ProceduralDocument(
                25, 125, 75, EveFieldsSplatSourceKinds.AnimatedSimplexNoise, 2);
            var later = ProceduralDocument(
                0, 100, 50, EveFieldsSplatSourceKinds.AnimatedSimplexNoise, 7);

            var firstValue = RenderFieldPixel(first, 64, 64);
            var shiftedValue = RenderFieldPixel(shifted, 32, 64);
            var laterValue = RenderFieldPixel(later, 64, 64);

            Assert.That(shiftedValue, Is.EqualTo(firstValue).Within(0.015f),
                "the same field-world coordinate must not swim when the viewport moves");
            Assert.That(Mathf.Abs(laterValue - firstValue), Is.GreaterThan(0.02f),
                "animation must use the document's simulation time rather than Unity wall time");
        }

        [Test]
        public void RasterizerLowersPortableAnimatedCellNoiseB()
        {
            var first = ProceduralDocument(
                0, 100, 50, EveFieldsSplatSourceKinds.AnimatedCellNoiseB, 2);
            var later = ProceduralDocument(
                0, 100, 50, EveFieldsSplatSourceKinds.AnimatedCellNoiseB, 11);

            var firstValue = RenderFieldPixel(first, 57, 71);
            var laterValue = RenderFieldPixel(later, 57, 71);

            Assert.That(firstValue, Is.GreaterThanOrEqualTo(0));
            Assert.That(Mathf.Abs(laterValue - firstValue), Is.GreaterThan(0.005f));
        }

        [Test]
        public void RasterizerLowersPortableAnimatedRadialCosine()
        {
            var initial = RadialCosineDocument(0);
            var halfTurnLater = RadialCosineDocument(Math.PI);

            var center = RenderFieldPixel(initial, 64, 64);
            var radial = RenderFieldPixel(initial, 80, 64);
            var laterCenter = RenderFieldPixel(halfTurnLater, 64, 64);
            var localX = (80.5f / 128f) * 2f - 1f;
            var localY = (64.5f / 128f) * 2f - 1f;
            var expected = Mathf.Cos(Mathf.Pow(Mathf.Sqrt(localX * localX + localY * localY), 1.25f) * Mathf.PI * 2f);

            Assert.That(center, Is.GreaterThan(0.98f));
            Assert.That(radial, Is.EqualTo(expected).Within(0.04f));
            Assert.That(laterCenter, Is.LessThan(-0.98f),
                "radial phase animation must use provider-published simulation time");
        }

        private static EveFieldsSplatsDocument RadialCosineDocument(double simulationTime)
        {
            return new EveFieldsSplatsDocument
            {
                SimulationTimeSeconds = simulationTime,
                Viewport = new EveFieldsViewport { MinX = -1, MinY = -1, MaxX = 1, MaxY = 1 },
                Splats = new EveFieldsSplatSoa
                {
                    Count = 1,
                    CenterX = new[] { 0d },
                    CenterY = new[] { 0d },
                    HalfExtentX = new[] { 1d },
                    HalfExtentY = new[] { 1d },
                    RotationCos = new[] { 1d },
                    RotationSin = new[] { 0d },
                    Channel = new[] { 0 },
                    Falloff = new[] { EveFieldsSplatFalloffs.Solid },
                    ValueR = new[] { 1d },
                    ValueA = new[] { 1d },
                    LayerIndex = new[] { 0 },
                    SourceKind = new[] { EveFieldsSplatSourceKinds.AnimatedRadialCosine },
                    FrequencyX = new[] { Math.PI * 2d },
                    FrequencyY = new[] { 1.25d },
                    PhaseX = new[] { 0d },
                    AnimationSpeed = new[] { 1d }
                }
            };
        }

        private static EveFieldsSplatsDocument ProceduralDocument(
            double min,
            double max,
            double center,
            int sourceKind,
            double simulationTime)
        {
            return new EveFieldsSplatsDocument
            {
                SimulationTimeSeconds = simulationTime,
                Viewport = new EveFieldsViewport { MinX = min, MinY = 0, MaxX = max, MaxY = 100 },
                Splats = new EveFieldsSplatSoa
                {
                    Count = 1,
                    CenterX = new[] { center },
                    CenterY = new[] { 50d },
                    HalfExtentX = new[] { 50d },
                    HalfExtentY = new[] { 50d },
                    RotationCos = new[] { 1d },
                    RotationSin = new[] { 0d },
                    Channel = new[] { 0 },
                    Falloff = new[] { EveFieldsSplatFalloffs.Solid },
                    ValueR = new[] { 1d },
                    ValueA = new[] { 1d },
                    LayerIndex = new[] { 0 },
                    SourceKind = new[] { sourceKind },
                    FrequencyX = new[] { 0.037d },
                    FrequencyY = new[] { 0.041d },
                    PhaseX = new[] { 0.17d },
                    PhaseY = new[] { -0.23d },
                    AnimationSpeed = new[] { 0.31d },
                    SourceFlags = new[] { (double)EveFieldsSplatSourceFlags.AbsoluteValue }
                }
            };
        }

        private static float RenderFieldPixel(EveFieldsSplatsDocument document, int x, int y)
        {
            var host = new GameObject("eve-fields-procedural-capture");
            var rasterizer = host.AddComponent<EveFieldsSplatRasterizer>();
            var output = rasterizer.Render(document, 128, 128);
            var previous = RenderTexture.active;
            RenderTexture.active = output;
            var readback = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
            readback.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
            readback.Apply();
            RenderTexture.active = previous;
            var value = readback.GetPixel(x, y).r;
            UnityEngine.Object.DestroyImmediate(readback);
            UnityEngine.Object.DestroyImmediate(host);
            return value;
        }

        private sealed class CaptureDocument : IEveFieldsSplatsDocument
        {
            public string Schema => EveFieldsSchemas.Splats;
            public long FrameId => 1;
            public double SimulationTimeSeconds => 0;
            public IEveFieldsViewport Viewport { get; } = new CaptureViewport();
            public IReadOnlyList<IEveFieldsSplatLayer> Layers { get; } = Array.Empty<IEveFieldsSplatLayer>();
            public IEveFieldsSplatSoa Splats { get; } = new CaptureSplats();
        }

        private static Dictionary<string, string> RequiredVolumeProgramMetadata() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.volume.pass.raymarch"] = "0",
                ["unity.volume.pass.composite"] = "2",
                ["unity.volume.texturePort.cloud"] = "_Cloud",
                ["unity.volume.matrixPort.cameraInverseViewProjection"] = "_CameraInverseViewProjection",
                ["unity.volume.vectorPort.cameraProjectionExtents"] = "_CameraProjectionExtents",
                ["unity.volume.vectorPort.viewportTransform"] = "_ViewportTransform",
                ["unity.volume.floatPort.raymarchOffset"] = "_RaymarchOffset"
            };

        private sealed class CaptureViewport : IEveFieldsViewport
        {
            public double MinX => -1;
            public double MinY => -1;
            public double MaxX => 1;
            public double MaxY => 1;
        }

        private sealed class CaptureSplats : IEveFieldsSplatSoa
        {
            private static readonly double[] One = { 1d };
            private static readonly double[] Zero = { 0d };
            private static readonly int[] ZeroInt = { 0 };
            public int Count => 1;
            public IReadOnlyList<double> CenterX => Zero;
            public IReadOnlyList<double> CenterY => Zero;
            public IReadOnlyList<double> HalfExtentX => One;
            public IReadOnlyList<double> HalfExtentY => One;
            public IReadOnlyList<double> RotationCos => One;
            public IReadOnlyList<double> RotationSin => Zero;
            public IReadOnlyList<int> Channel => ZeroInt;
            public IReadOnlyList<int> Falloff => ZeroInt;
            public IReadOnlyList<double> ValueR => One;
            public IReadOnlyList<double> ValueG => Zero;
            public IReadOnlyList<double> ValueB => Zero;
            public IReadOnlyList<double> ValueA => One;
            public IReadOnlyList<string> SourceKey { get; } = new[] { "capture" };
            public IReadOnlyList<int> LayerIndex => ZeroInt;
            public IReadOnlyList<int> SourceKind => ZeroInt;
            public IReadOnlyList<double> FrequencyX => One;
            public IReadOnlyList<double> FrequencyY => One;
            public IReadOnlyList<double> PhaseX => Zero;
            public IReadOnlyList<double> PhaseY => Zero;
            public IReadOnlyList<double> AnimationSpeed => Zero;
            public IReadOnlyList<double> SourceFlags => Zero;
            public IReadOnlyList<double> FalloffScale => One;
            public IReadOnlyList<double> FalloffExponent => One;
        }
    }
}
