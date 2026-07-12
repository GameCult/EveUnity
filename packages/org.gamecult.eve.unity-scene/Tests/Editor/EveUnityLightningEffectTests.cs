using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityLightningEffectTests
    {
        [Test]
        public void GraduatedLightningBundleLoadsWithoutAetheriaAssemblies()
        {
            const string root = "Packages/org.gamecult.eve.unity-scene/Runtime/Effects/Lightning/";
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Lightning.compute");
            var material = AssetDatabase.LoadAssetAtPath<Material>(root + "Lightning.mat");
            var host = new GameObject("lightning-import-proof");
            try
            {
                var effect = host.AddComponent<LightningCompute>();
                effect.ComputeShader = compute;
                effect.RenderMaterial = material;

                Assert.That(compute, Is.Not.Null);
                Assert.That(material, Is.Not.Null);
                Assert.That(effect.GetType().Assembly.GetName().Name, Is.EqualTo("GameCult.Eve.UnityScene"));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
