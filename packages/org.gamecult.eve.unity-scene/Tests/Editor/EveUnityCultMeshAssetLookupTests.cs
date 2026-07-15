using System.Reflection;
using System.Collections.Generic;
using GameCult.Eve.UnityScene;
using NUnit.Framework;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityCultMeshAssetLookupTests
    {
        [Test]
        public void ResolvesUnityNormalizedBundleNamesWithoutChangingLogicalAssetIdentity()
        {
            var method = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "ResolveBundleAssetName",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);
            var result = method!.Invoke(null, new object[]
            {
                new[] { "assets/generated/eve/thermal/death.asset" },
                "Assets/Generated/Eve/Thermal/Death.asset"
            });

            Assert.That(result, Is.EqualTo("assets/generated/eve/thermal/death.asset"));
        }

        [Test]
        public void RejectsBundleNamesThatDoNotMatchTheAdvertisedAsset()
        {
            var method = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "ResolveBundleAssetName",
                BindingFlags.Static | BindingFlags.NonPublic);

            var result = method!.Invoke(null, new object[]
            {
                new[] { "assets/generated/eve/thermal/heatstroke.asset" },
                "Assets/Generated/Eve/Thermal/Death.asset"
            });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void SelectedRuntimeVariantOwnsConcreteNativeProgramBindings()
        {
            var method = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "MergeAssetMetadata",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);
            var result = method!.Invoke(null, new object[]
            {
                new Dictionary<string, string> { ["presentationRole"] = "environment.volume" },
                new Dictionary<string, string>
                {
                    ["unity.volume.texturePort.surfaceHeight"] = "_NebulaSurfaceHeight",
                    ["unity.volume.pass.raymarch"] = "0"
                }
            }) as IReadOnlyDictionary<string, string>;

            Assert.That(result, Is.Not.Null);
            Assert.That(result!["presentationRole"], Is.EqualTo("environment.volume"));
            Assert.That(result["unity.volume.texturePort.surfaceHeight"], Is.EqualTo("_NebulaSurfaceHeight"));
            Assert.That(result["unity.volume.pass.raymarch"], Is.EqualTo("0"));
        }
    }
}
