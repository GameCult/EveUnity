using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO;
using GameCult.Eve.Surface;
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
        public void ProviderSubscriptionsDoNotDependOnPreparedTransportLifetime()
        {
            var gameObject = new GameObject("unprepared-eve-provider-subscriptions");
            var provider = gameObject.AddComponent<EveUnityCultMeshPlayableWorldProvider>();
            Action<EveUnitySceneProviderSurfaceDocument> documentHandler = _ => { };
            Action<EveUnitySceneCommandReceipt> receiptHandler = _ => { };

            Assert.DoesNotThrow(() => provider.DocumentAvailable += documentHandler);
            Assert.DoesNotThrow(() => provider.ReceiptAvailable += receiptHandler);
            Assert.DoesNotThrow(() => provider.DocumentAvailable -= documentHandler);
            Assert.DoesNotThrow(() => provider.ReceiptAvailable -= receiptHandler);
            Assert.DoesNotThrow(() => UnityEngine.Object.DestroyImmediate(gameObject));
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

        [Test]
        public void PublishingCatalogRegistersBundlesWithoutMaterializingThem()
        {
            var platformMethod = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "CurrentBundlePlatform",
                BindingFlags.Static | BindingFlags.NonPublic);
            var publishMethod = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "PublishAssetCatalog",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var bundlesField = typeof(EveUnityCultMeshLiveProviderTransport).GetField(
                "_assetBundles",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectionsField = typeof(EveUnityCultMeshLiveProviderTransport).GetField(
                "_assetSelections",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(platformMethod, Is.Not.Null);
            Assert.That(publishMethod, Is.Not.Null);

            using var transport = new EveUnityCultMeshLiveProviderTransport(
                Path.Combine(Path.GetTempPath(), $"eve-lazy-assets-{Guid.NewGuid():N}.cc"),
                "cultnet+tcp://127.0.0.1:1",
                "provider",
                "surface");
            var variant = new EveAssetVariant(
                "unity-scene",
                (string)platformMethod!.Invoke(null, Array.Empty<object>())!,
                "unity-assetbundle",
                "cdn:manifest:lazy",
                "sha256:00",
                1,
                "assets/prefab.prefab");
            var catalog = new EveAssetCatalogDocument(
                "provider",
                "catalog",
                1,
                DateTimeOffset.UtcNow.ToString("O"),
                new[] { new EveAssetCatalogEntry("prefab.entity", "prefab", new[] { variant }) });

            publishMethod!.Invoke(transport, new object[] { catalog });

            Assert.That(((System.Collections.ICollection)bundlesField!.GetValue(transport)!).Count, Is.Zero);
            Assert.That(((System.Collections.IDictionary)selectionsField!.GetValue(transport)!).Count, Is.EqualTo(1));
        }
    }
}
