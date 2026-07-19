using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameCult.Eve.UnityScene;
using NUnit.Framework;
using UnityEngine;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityCultMeshAssetLookupTests
    {
        [Test]
        public void UnavailableTcpRendezvousIsReportedAsRetryablePreparationFailure()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EveUnityCultMeshProviderDiscovery().DiscoverAsync($"cultnet+tcp://127.0.0.1:{port}"));

            StringAssert.Contains("Could not query CultMesh rendezvous endpoint", error!.Message);
            Assert.That(error.InnerException, Is.TypeOf<SocketException>());
        }

        [Test]
        public void UnpreparedProviderGetterFailsFastWithoutStartingNetworkDiscovery()
        {
            var gameObject = new GameObject("unprepared-eve-provider");
            try
            {
                var provider = gameObject.AddComponent<EveUnityCultMeshPlayableWorldProvider>();
                provider.Configure("rudp://127.0.0.1:1");
                var stopwatch = Stopwatch.StartNew();

                var error = Assert.Throws<InvalidOperationException>(() => _ = provider.CurrentInputCapability);

                stopwatch.Stop();
                StringAssert.Contains("Await PrepareAsync", error!.Message);
                Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(100));
                Assert.That(provider.Selection, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

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
