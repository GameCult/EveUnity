using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityScene;
using GameCult.Mesh;
using NUnit.Framework;
using UnityEngine;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityCultMeshAssetLookupTests
    {
        [Test]
        public void ConnectCannotPerformRemoteWorkBeforeAsynchronousPreparation()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-cache",
                "cultnet+tcp://127.0.0.1:1",
                "test.verse",
                "test.runtime",
                "test.provider",
                "test.surface");

            var elapsed = Stopwatch.StartNew();
            var failure = Assert.Throws<InvalidOperationException>(() => transport.Connect());

            StringAssert.Contains("Await PrepareAsync", failure!.Message);
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(100)));
        }

        [Test]
        public async Task CommandOutboxCompletesOnlyAfterCanonicalReceipt()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = 0;
            using var outbox = new EveUnityCultMeshCommandOutbox(
                async (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref sends);
                    entered.TrySetResult(true);
                    await AwaitOrCancel(release.Task, cancellationToken);
                },
                _ => null,
                retryDelay: TimeSpan.FromMilliseconds(250));

            var elapsed = Stopwatch.StartNew();
            outbox.Enqueue(Request("one-shot", "dock"));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(100)));
            await RequireCompletion(entered.Task);
            Assert.That(outbox.PendingCount, Is.EqualTo(1));

            release.TrySetResult(true);
            await WaitUntilAsync(() => outbox.StateOf("one-shot") ==
                EveUnityCultMeshCommandOutbox.DeliveryState.AwaitingCanonicalReceipt);
            Assert.That(outbox.PendingCount, Is.EqualTo(1));
            Assert.That(sends, Is.EqualTo(1));
            Assert.That(outbox.Acknowledge("one-shot"), Is.True);
            Assert.That(outbox.Acknowledge("one-shot"), Is.False);
            await WaitUntilAsync(() => outbox.PendingCount == 0);
        }

        [Test]
        public async Task CommandOutboxCoalescesBlockedContinuousInputToTheLatestValue()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new List<string>();
            using var outbox = new EveUnityCultMeshCommandOutbox(
                async (request, cancellationToken) =>
                {
                    lock (sent) sent.Add(request.CommandId);
                    entered.TrySetResult(true);
                    await AwaitOrCancel(release.Task, cancellationToken);
                },
                request => request.PayloadFields["commandId"] + "\u001f" + request.PayloadFields["entityId"],
                retryDelay: TimeSpan.FromMilliseconds(250));

            outbox.Enqueue(Request("move-0", "move"));
            await RequireCompletion(entered.Task);
            for (var index = 1; index <= 1000; index++)
                outbox.Enqueue(Request("move-" + index, "move"));

            Assert.That(outbox.PendingCount, Is.LessThanOrEqualTo(2));
            release.TrySetResult(true);
            await WaitUntilAsync(() => outbox.StateOf("move-1000") ==
                EveUnityCultMeshCommandOutbox.DeliveryState.AwaitingCanonicalReceipt);
            Assert.That(outbox.Acknowledge("move-1000"), Is.True);
            await WaitUntilAsync(() => outbox.PendingCount == 0);
            lock (sent)
            {
                CollectionAssert.AreEqual(new[] { "move-0", "move-1000" }, sent);
            }
        }

        [Test]
        public async Task CommandOutboxRetriesSameIdWithoutLettingPoisonCommandStarveLaterWork()
        {
            var attempts = new List<string>();
            using var outbox = new EveUnityCultMeshCommandOutbox(
                (request, _) =>
                {
                    lock (attempts) attempts.Add(request.CommandId);
                    if (request.CommandId == "poison") throw new IOException("route unavailable");
                    return Task.CompletedTask;
                },
                _ => null,
                retryDelay: TimeSpan.FromMilliseconds(20),
                maximumRetryDelay: TimeSpan.FromMilliseconds(40));

            outbox.Enqueue(Request("poison", "dock"));
            outbox.Enqueue(Request("healthy", "undock"));

            await WaitUntilAsync(() => outbox.StateOf("healthy") ==
                EveUnityCultMeshCommandOutbox.DeliveryState.AwaitingCanonicalReceipt);
            Assert.That(outbox.Acknowledge("healthy"), Is.True);
            lock (attempts)
            {
                Assert.That(attempts.IndexOf("healthy"), Is.GreaterThanOrEqualTo(0));
                Assert.That(attempts.FindAll(value => value == "poison"), Is.Not.Empty);
            }
            Assert.That(outbox.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CommandOutboxRetransmitsUnacknowledgedCommandWithOriginalId()
        {
            var attempts = 0;
            using var outbox = new EveUnityCultMeshCommandOutbox(
                (request, _) =>
                {
                    Assert.That(request.CommandId, Is.EqualTo("retry-me"));
                    Interlocked.Increment(ref attempts);
                    return Task.CompletedTask;
                },
                _ => null,
                retryDelay: TimeSpan.FromMilliseconds(20),
                maximumRetryDelay: TimeSpan.FromMilliseconds(20));

            outbox.Enqueue(Request("retry-me", "dock"));
            await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2);
            Assert.That(outbox.PendingCount, Is.EqualTo(1));
            Assert.That(outbox.Acknowledge("retry-me"), Is.True);
            await WaitUntilAsync(() => outbox.PendingCount == 0);
        }

        [Test]
        public void PendingReceiptIsObservedWithoutRetiringTheCommand()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-cache",
                "cultnet+tcp://127.0.0.1:1",
                "test.verse",
                "test.runtime",
                "test.provider",
                "test.surface");
            var request = Request("launch-1", "launch");
            var pendingField = typeof(EveUnityCultMeshLiveProviderTransport).GetField(
                "_pendingCommands",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var publish = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "PublishReceipt",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(pendingField, Is.Not.Null);
            Assert.That(publish, Is.Not.Null);
            var pending = (IDictionary<string, EveSurfaceCommandRequest>)pendingField!.GetValue(transport)!;
            pending.Add(request.CommandId, request);
            var observed = new List<EveUnitySceneCommandReceipt>();
            transport.CommandReceiptAvailable += observed.Add;

            publish!.Invoke(transport, new object[] { Receipt(request, "pending", "receipt-pending") });

            Assert.That(pending.ContainsKey(request.CommandId), Is.True);
            Assert.That(observed.Select(value => value.State), Is.EqualTo(new[] { "pending" }));

            publish.Invoke(transport, new object[] { Receipt(request, "accepted", "receipt-accepted") });

            Assert.That(pending.ContainsKey(request.CommandId), Is.False);
            Assert.That(observed.Select(value => value.State), Is.EqualTo(new[] { "pending", "accepted" }));
        }

        [Test]
        public void TerminalReceiptWaitsForItsPresentationGenerationAndFreezesTheOldSurface()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-cache",
                "cultnet+tcp://127.0.0.1:1",
                "test.verse",
                "test.runtime",
                "test.provider",
                "test.surface");
            var request = Request("select-verse-1", "selectVerse");
            var type = typeof(EveUnityCultMeshLiveProviderTransport);
            var pending = (IDictionary<string, EveSurfaceCommandRequest>)type.GetField(
                "_pendingCommands",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
            var publish = type.GetMethod("PublishReceipt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var complete = type.GetMethod(
                "CompletePresentationTransition",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            type.GetField("_bootstrapped", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(transport, true);
            complete.Invoke(transport, new object[] { 1L });
            pending.Add(request.CommandId, request);
            var observed = new List<EveUnitySceneCommandReceipt>();
            transport.CommandReceiptAvailable += observed.Add;

            publish.Invoke(transport, new object[]
            {
                Receipt(request, "accepted", "receipt-accepted", sourceVersion: 7)
            });

            Assert.That(pending.ContainsKey(request.CommandId), Is.True);
            Assert.That(observed, Is.Empty);
            type.GetProperty("CurrentSurfaceDocument")!.SetValue(transport, SceneSurface(100));
            var blocked = Assert.Throws<InvalidOperationException>(() =>
                transport.SubmitCommand(Request("stale-launch", "launch")));
            StringAssert.Contains("read-only", blocked!.Message);

            complete.Invoke(transport, new object[] { 7L });

            Assert.That(pending.ContainsKey(request.CommandId), Is.False);
            Assert.That(observed.Select(value => value.State), Is.EqualTo(new[] { "accepted" }));
        }

        [Test]
        public void ReceiptForAnotherInvocationCannotRetireTheCommand()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-cache",
                "cultnet+tcp://127.0.0.1:1",
                "test.verse",
                "test.runtime",
                "test.provider",
                "test.surface");
            var request = Request("launch-1", "launch");
            var pendingField = typeof(EveUnityCultMeshLiveProviderTransport).GetField(
                "_pendingCommands",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var publish = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "PublishReceipt",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var pending = (IDictionary<string, EveSurfaceCommandRequest>)pendingField!.GetValue(transport)!;
            pending.Add(request.CommandId, request);
            var observed = new List<EveUnitySceneCommandReceipt>();
            transport.CommandReceiptAvailable += observed.Add;
            var collision = new EveCommandReceiptDocument(
                "receipt-collision",
                request.CommandId,
                request.Command,
                "accepted",
                "AetheriaEve",
                "test.runtime",
                request.ProviderId,
                request.SurfaceId,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                1,
                invocationHash: EveCommandInvocationHash.Compute(Request(request.CommandId, "another-payload")));

            publish!.Invoke(transport, new object[] { collision });

            Assert.That(pending.ContainsKey(request.CommandId), Is.True);
            Assert.That(observed, Is.Empty);
        }

        [Test]
        public void ProviderSelectionCarriesStableIdentityWithoutExposingAPhysicalRoute()
        {
            var selection = new EveUnityCultMeshProviderSelection(
                "cultnet+tcp://odin.example:3075",
                "aetheria.public",
                "aetheria.public",
                "aetheria.daemon",
                "aetheria.pilot",
                "interactive-world");

            Assert.That(selection.RendezvousEndpoint, Is.EqualTo("cultnet+tcp://odin.example:3075"));
            Assert.That(selection.VerseId, Is.EqualTo("aetheria.public"));
            Assert.That(selection.AuthorityRuntimeId, Is.EqualTo("aetheria.public"));
            Assert.That(typeof(EveUnityCultMeshProviderSelection).GetProperty("Endpoint"), Is.Null);
        }

        [Test]
        public void NavigationAuthorityCannotBeReplacedByAlphabeticallyEarlierPeer()
        {
            var target = new EveUnitySceneNavigationTarget(
                "gamecult.aetheria",
                "aetheria.daemon",
                "aetheria.pilot",
                "interactive-world",
                new[] { "cultnet+tcp://odin.example:3075" },
                "z-owner");
            var eligible = EveUnityCultMeshProviderDiscovery.EligibleAuthorityRuntimeIds(
                new[] { "a-decoy", "z-owner" },
                target.AuthorityRuntimeId);

            Assert.That(target.AuthorityRuntimeId, Is.EqualTo("z-owner"));
            CollectionAssert.AreEqual(new[] { "z-owner" }, eligible);
        }

        private static EveSurfaceCommandRequest Request(string id, string command) => new EveSurfaceCommandRequest(
            "test.provider",
            "test.surface",
            CultMesh.OperationInvocation("test.command", idempotencyKey: id),
            CultMesh.OperationPayload(new Dictionary<string, string>
            {
                ["commandId"] = command,
                ["entityId"] = "ship"
            }),
            DateTimeOffset.UtcNow,
            "test-client");

        private static EveCommandReceiptDocument Receipt(
            EveSurfaceCommandRequest request,
            string state,
            string receiptId,
            long sourceVersion = 0) => new EveCommandReceiptDocument(
                receiptId,
                request.CommandId,
                request.Command,
                state,
                "AetheriaEve",
                "test.runtime",
                request.ProviderId,
                request.SurfaceId,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                sourceVersion,
                invocationHash: EveCommandInvocationHash.Compute(request));

        private static EveUnitySceneProviderSurfaceDocument SceneSurface(long version)
        {
            var surface = new EveSurfaceDocument(
                "test.provider",
                "",
                "",
                version,
                "",
                new EveSurfaceTree(
                    "test.surface",
                    new EveSurfaceComponent(
                        "root",
                        "surface",
                        new Dictionary<string, string>(),
                        Array.Empty<EveSurfaceComponent>()),
                    Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());
            return new EveUnitySceneProviderSurfaceDocument(
                surface,
                new EveUnitySceneProviderSurfaceAdvertisement(
                    "test.surface",
                    "interactive-world",
                    new EveUnitySceneWorldInteraction("", "", EveCommandReceiptDocument.SchemaId, "")),
                "test:surface",
                version);
        }

        private static async Task AwaitOrCancel(Task task, CancellationToken cancellationToken)
        {
            var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(task, cancelled) != task)
                cancellationToken.ThrowIfCancellationRequested();
            await task;
        }

        private static async Task RequireCompletion(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))) != task)
                Assert.Fail("Timed out waiting for asynchronous command delivery.");
            await task;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (!predicate() && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(10);
            Assert.That(predicate(), Is.True, "Timed out waiting for the command outbox to drain.");
        }

        [Test]
        public void LiveTransportOwnsOneCultMeshClientAndNoDirectSnapshotReplica()
        {
            var fields = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            var names = Array.ConvertAll(fields, field => field.Name);

            CollectionAssert.Contains(names, "_meshClient");
            CollectionAssert.DoesNotContain(names, "_node");
            CollectionAssert.DoesNotContain(names, "_snapshot");
            CollectionAssert.DoesNotContain(names, "_networkRegistry");
            CollectionAssert.DoesNotContain(names, "_replicaShard");
            CollectionAssert.DoesNotContain(names, "_subscriptions");
            CollectionAssert.DoesNotContain(names, "_entitySubscriptions");
        }

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
        public void ConfiguringCandidateCatalogRegistersBundlesWithoutMaterializingThem()
        {
            var platformMethod = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "CurrentBundlePlatform",
                BindingFlags.Static | BindingFlags.NonPublic);
            var configureMethod = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "ConfigureAssetCatalog",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(platformMethod, Is.Not.Null);
            Assert.That(configureMethod, Is.Not.Null);
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
            var candidate = CreateAssetGeneration("verse", "authority", "provider", "catalog", "candidate", 1);

            configureMethod!.Invoke(null, new[] { candidate, catalog });

            Assert.That(GenerationCollection(candidate, "AssetBundles").Count, Is.Zero);
            Assert.That(GenerationDictionary(candidate, "AssetSelections").Count, Is.EqualTo(1));
            ((IDisposable)candidate).Dispose();
        }

        [Test]
        public void FailedAssetCandidatePreservesCommittedGeneration()
        {
            var transportType = typeof(EveUnityCultMeshLiveProviderTransport);
            var generationField = transportType.GetField(
                "_assetGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var configureMethod = transportType.GetMethod(
                "ConfigureAssetCatalog",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                Path.Combine(Path.GetTempPath(), $"eve-asset-generation-{Guid.NewGuid():N}.cc"),
                "cultnet+tcp://127.0.0.1:1",
                "verse-a",
                "authority-a",
                "provider-a",
                "surface-a");
            var initial = (IDisposable)generationField.GetValue(transport)!;
            var committed = CreateAssetGeneration(
                "verse-a", "authority-a", "provider-a", "catalog-a", "source-a", 1);
            var prefab = new GameObject("committed-a");
            GenerationDictionary(committed, "Prefabs")["prefab.a"] = prefab;
            initial.Dispose();
            generationField.SetValue(transport, committed);

            var invalidVariant = new EveAssetVariant(
                "browser",
                "web",
                "url",
                "https://example.invalid/a",
                "sha256:00",
                1,
                "prefab.a");
            var invalidCatalog = new EveAssetCatalogDocument(
                "provider-b",
                "catalog-b",
                2,
                DateTimeOffset.UtcNow.ToString("O"),
                new[] { new EveAssetCatalogEntry("prefab.b", "prefab", new[] { invalidVariant }) });
            var rejected = CreateAssetGeneration(
                "verse-b", "authority-b", "provider-b", "catalog-b", "source-b", -1);
            var error = Assert.Throws<TargetInvocationException>(() =>
                configureMethod.Invoke(null, new[] { rejected, invalidCatalog }));
            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
            ((IDisposable)rejected).Dispose();
            Assert.That(generationField.GetValue(transport), Is.SameAs(committed));
            Assert.That(GenerationProperty<string>(committed, "SourceIdentity"), Is.EqualTo("source-a"));
            Assert.That(GenerationDictionary(committed, "Prefabs")["prefab.a"], Is.SameAs(prefab));
            UnityEngine.Object.DestroyImmediate(prefab);
        }

        [Test]
        public void CandidateCatalogObservationSurvivesPrecommitWindow()
        {
            var candidate = CreateAssetGeneration(
                "verse-b", "authority-b", "provider-b", "catalog-b", "source-b", 1);
            var candidateType = candidate.GetType();
            var observe = candidateType.GetMethod("ObserveCatalog")!;
            var activate = candidateType.GetMethod("ActivateCatalogObservation")!;
            var variant = new EveAssetVariant(
                "unity-scene", "StandaloneWindows64", "url", "https://example.invalid/b", "sha256:01", 1, "prefab.b");
            var catalogB = new EveAssetCatalogDocument(
                "provider-b", "catalog-b", 2, DateTimeOffset.UtcNow.ToString("O"),
                new[] { new EveAssetCatalogEntry("prefab.b", "prefab", new[] { variant }) });

            Assert.That(observe.Invoke(candidate, new object[] { catalogB }), Is.Null);
            Assert.That(activate.Invoke(candidate, Array.Empty<object>()), Is.SameAs(catalogB));
            ((IDisposable)candidate).Dispose();
        }

        [Test]
        public void CrossTargetAssetsUseConfiguredRemoteTrust()
        {
            var localTrust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.LocalDevelopment);
            var remoteTrust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.AuthenticatedRemote);
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                Path.Combine(Path.GetTempPath(), $"eve-asset-trust-{Guid.NewGuid():N}.cc"),
                "cultnet+tcp://127.0.0.1:1",
                "verse-a",
                "authority-a",
                "provider-a",
                "surface-a",
                authorityTrust: localTrust,
                crossTargetAuthorityTrust: remoteTrust);
            var trustMethod = typeof(EveUnityCultMeshLiveProviderTransport).GetMethod(
                "AssetAuthorityTrust",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var localSource = CreateAssetSource("verse-a", "authority-a", "provider-a", "catalog-a");
            var remoteSource = CreateAssetSource("verse-b", "authority-b", "provider-b", "catalog-b");

            Assert.That(trustMethod.Invoke(transport, new[] { localSource }), Is.SameAs(localTrust));
            Assert.That(trustMethod.Invoke(transport, new[] { remoteSource }), Is.SameAs(remoteTrust));
        }

        private static object CreateAssetSource(
            string verseId,
            string authorityRuntimeId,
            string providerId,
            string manifestRecordRef)
        {
            var type = typeof(EveUnityCultMeshLiveProviderTransport).GetNestedType(
                "AssetSource",
                BindingFlags.NonPublic)!;
            return Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    new CultMeshSessionTarget(verseId, authorityRuntimeId),
                    providerId,
                    manifestRecordRef,
                    Array.Empty<string>()
                },
                null)!;
        }

        private static object CreateAssetGeneration(
            string verseId,
            string authorityRuntimeId,
            string providerId,
            string manifestRecordRef,
            string sourceIdentity,
            long catalogVersion)
        {
            var type = typeof(EveUnityCultMeshLiveProviderTransport).GetNestedType(
                "AssetGeneration",
                BindingFlags.NonPublic)!;
            return Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[]
                {
                    CreateAssetSource(verseId, authorityRuntimeId, providerId, manifestRecordRef),
                    sourceIdentity,
                    catalogVersion,
                    null
                },
                null)!;
        }

        private static System.Collections.IList GenerationCollection(object generation, string property) =>
            (System.Collections.IList)generation.GetType().GetProperty(property)!.GetValue(generation)!;

        private static System.Collections.IDictionary GenerationDictionary(object generation, string property) =>
            (System.Collections.IDictionary)generation.GetType().GetProperty(property)!.GetValue(generation)!;

        private static T GenerationProperty<T>(object generation, string property) =>
            (T)generation.GetType().GetProperty(property)!.GetValue(generation)!;
    }
}
