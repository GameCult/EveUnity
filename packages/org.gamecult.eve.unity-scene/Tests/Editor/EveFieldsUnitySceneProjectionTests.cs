using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GameCult.Eve.PluginFields;
using GameCult.Eve.UnityScene.Fields;
using NUnit.Framework;
using UnityEngine;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveFieldsUnitySceneProjectionTests
    {
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
        public void PackageContainsGenericFieldsShader()
        {
            Assert.That(Shader.Find("Eve/Fields/Splats"), Is.Not.Null);
            Assert.That(File.Exists("Packages/org.gamecult.eve.unity-scene/Runtime/Fields/EveFieldsSplatsCore.hlsl"), Is.True);
        }

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

        private sealed class CaptureDocument : IEveFieldsSplatsDocument
        {
            public string Schema => EveFieldsSchemas.Splats;
            public long FrameId => 1;
            public double SimulationTimeSeconds => 0;
            public IEveFieldsViewport Viewport { get; } = new CaptureViewport();
            public IReadOnlyList<IEveFieldsSplatLayer> Layers { get; } = Array.Empty<IEveFieldsSplatLayer>();
            public IEveFieldsSplatSoa Splats { get; } = new CaptureSplats();
        }

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
        }
    }
}
