using System.Reflection;
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
    }
}
