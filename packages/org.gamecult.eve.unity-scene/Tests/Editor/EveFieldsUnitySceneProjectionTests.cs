using System;
using System.IO;
using System.Linq;
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
    }
}
