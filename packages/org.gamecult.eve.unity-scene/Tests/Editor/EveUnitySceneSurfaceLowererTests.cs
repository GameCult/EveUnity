using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using GameCult.Eve.PluginFields;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnitySceneSurfaceLowererTests
    {
        [Test]
        public void LiveTransportDefersDefaultBodyAuthorizationUntilAdvertisementResolution()
        {
            Assert.DoesNotThrow(() =>
            {
                using var transport = new EveUnityCultMeshLiveProviderTransport(
                    "test-replica.cc",
                    "cultnet://127.0.0.1:3075",
                    "aetheria",
                    "aetheria.pilot");
            });
        }

        [Test]
        public async System.Threading.Tasks.Task LiveTransportResolvesViewGenerationWhenNextPublicationAlreadyExists()
        {
            var view = EntityLeaseDocument();
            var generationN = BodyPublication(view, sequence: view.Sequence);
            var generationNPlusOne = BodyPublication(view, sequence: view.Sequence + 1);
            var cache = new CultCache();
            await cache.UpsertAsync(generationN,
                new CultRecordHandle<CultMeshBodyPublicationDocument>(generationN.RecordKey));
            await cache.UpsertAsync(generationNPlusOne,
                new CultRecordHandle<CultMeshBodyPublicationDocument>(generationNPlusOne.RecordKey));

            var handle = (CultMeshBodyPublicationHandle)typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("BodyPublicationHandle", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { view })!;
            var resolved = cache.Get<CultMeshBodyPublicationDocument>(handle.RecordKey);

            Assert.That(resolved, Is.Not.Null);
            handle.Validate(resolved!);
            Assert.That(resolved!.Sequence, Is.EqualTo(view.Sequence));
            Assert.That(handle.RecordKey, Is.Not.EqualTo(generationNPlusOne.RecordKey));
        }

        [Test]
        public void LiveTransportReusesStableLayoutForNewBodyPublications()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-replica.cc",
                "cultnet://127.0.0.1:3075",
                "aetheria",
                "aetheria.pilot");
            var view = EntityLeaseDocument();
            var publication = BodyPublication(view, view.Sequence + 1);
            var queueView = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("QueueEntityView", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queuePublication = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("QueueBodyPublication", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queue = (System.Collections.Concurrent.ConcurrentQueue<object>)
                typeof(EveUnityCultMeshLiveProviderTransport)
                    .GetField("_liveDocuments", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(transport)!;

            queueView.Invoke(transport, new object[] { view });
            Assert.That(queue, Is.Empty, "a layout without its body publication is not a complete generation");
            queuePublication.Invoke(transport, new object[] { publication });

            Assert.That(queue.TryDequeue(out var queued), Is.True);
            var generation = queued!.GetType().GetProperty("View")!.GetValue(queued) as EveEntitySoaViewDocument;
            Assert.That(generation, Is.Not.Null);
            Assert.That(generation!.Sequence, Is.EqualTo(publication.Sequence));
            Assert.That(generation.ProducerEpoch, Is.EqualTo(publication.ProducerEpoch));
            Assert.That(generation.Buffers, Is.SameAs(view.Buffers));
            Assert.That(generation.Columns, Is.SameAs(view.Columns));
            Assert.That(generation.Identities, Is.SameAs(view.Identities));
            Assert.That(generation.DirtyRanges.All(range => range.Sequence == publication.Sequence), Is.True);
        }

        [Test]
        public void LiveTransportPairsBodyPublicationThatArrivesBeforeItsLayout()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-replica.cc",
                "cultnet://127.0.0.1:3075",
                "aetheria",
                "aetheria.pilot");
            var view = EntityLeaseDocument();
            var publication = BodyPublication(view, view.Sequence);
            var queueView = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("QueueEntityView", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queuePublication = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("QueueBodyPublication", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queue = (System.Collections.Concurrent.ConcurrentQueue<object>)
                typeof(EveUnityCultMeshLiveProviderTransport)
                    .GetField("_liveDocuments", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(transport)!;

            queuePublication.Invoke(transport, new object[] { publication });
            Assert.That(queue, Is.Empty, "a publication without its layout is not a complete generation");
            queueView.Invoke(transport, new object[] { view });

            Assert.That(queue.TryDequeue(out var queued), Is.True);
            Assert.That(queued!.GetType().GetProperty("View")!.GetValue(queued), Is.SameAs(view));
            Assert.That(queued.GetType().GetProperty("Publication")!.GetValue(queued), Is.SameAs(publication));
        }

        [Test]
        public void LiveTransportKeepsOnlyLatestPendingRealtimeGenerationPerBody()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-replica.cc",
                "cultnet://127.0.0.1:3075",
                "provider",
                "provider.pilot");
            var enqueue = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("EnqueueRealtimeEntityFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queue = (System.Collections.Concurrent.ConcurrentQueue<object>)
                typeof(EveUnityCultMeshLiveProviderTransport)
                    .GetField("_liveDocuments", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(transport)!;
            var latest = (Dictionary<string, CultMeshRealtimeFrame>)
                typeof(EveUnityCultMeshLiveProviderTransport)
                    .GetField("_latestRealtimeFrames", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(transport)!;

            for (var sequence = 0; sequence < 100; sequence++)
                enqueue.Invoke(transport, new object[] { RealtimeFrame("entities", 7, sequence) });
            enqueue.Invoke(transport, new object[] { RealtimeFrame("entities", 7, 50) });

            Assert.That(queue.Count, Is.EqualTo(1), "superseded frames must not become render-thread debt");
            Assert.That(latest, Has.Count.EqualTo(1));
            Assert.That(latest["entities"].ProducerEpoch, Is.EqualTo(7));
            Assert.That(latest["entities"].Sequence, Is.EqualTo(99));
        }

        [Test]
        public void LiveTransportReadsLaterMappedFramesWithoutNewControlDocuments()
        {
            using var transport = new EveUnityCultMeshLiveProviderTransport(
                "test-replica.cc",
                "cultnet://127.0.0.1:3075",
                "provider",
                "provider.pilot");
            var view = EntityLeaseDocument();
            view.Sequence = 0;
            using var publisher = new CultMeshFrameBodyPublisher(
                view.Buffers[0].BufferId,
                view.BodySchemaId,
                view.LayoutVersion,
                view.Capacity,
                view.ProducerEpoch,
                slotByteLength: 4);
            publisher.TryPublish(new byte[] { 1, 2, 3, 4 }, DateTimeOffset.UtcNow, out var bootstrap);
            var publication = new CultMeshBodyPublicationDocument
            {
                BodyId = bootstrap.BodyId,
                ProducerId = view.ProviderId,
                SchemaId = bootstrap.SchemaId,
                LayoutVersion = bootstrap.LayoutVersion,
                ByteSize = bootstrap.ByteSize,
                Capacity = bootstrap.Capacity,
                ProducerEpoch = bootstrap.ProducerEpoch,
                Sequence = bootstrap.Sequence,
                Synchronization = bootstrap.Synchronization,
                LivenessExpiresAtUnixMs = bootstrap.LeaseExpiresAtUnixMs,
                Representations = new[] { bootstrap }
            };
            var queueView = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("QueueEntityView", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var ensureCursor = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("EnsureMappedEntityFrameCursor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var pumpCursor = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("PumpMappedEntityFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var queue = (System.Collections.Concurrent.ConcurrentQueue<object>)
                typeof(EveUnityCultMeshLiveProviderTransport)
                    .GetField("_liveDocuments", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(transport)!;
            queueView.Invoke(transport, new object[] { view });
            queue.TryDequeue(out _);
            ensureCursor.Invoke(transport, new object[] { publication });

            publisher.TryPublish(new byte[] { 9, 8, 7, 6 }, DateTimeOffset.UtcNow, out _);
            EveEntitySoaViewDocument? presented = null;
            byte presentedByte = 0;
            transport.EntityViewAvailable += (generation, lease) =>
            {
                presented = generation;
                presentedByte = lease.ReadByte(0);
                lease.Dispose();
            };

            pumpCursor.Invoke(transport, Array.Empty<object>());

            Assert.That(presented, Is.Not.Null);
            Assert.That(presented!.Sequence, Is.EqualTo(1));
            Assert.That(presentedByte, Is.EqualTo(9));
            Assert.That(queue, Is.Empty, "mapped frames must not be represented as queued CultNet documents");
        }

        [Test]
        public void LiveTransportDropsExpiredHistoricalGenerationWithoutReinterpretingLatest()
        {
            var view = EntityLeaseDocument();
            var publication = BodyPublication(view, view.Sequence);
            publication.LivenessExpiresAtUnixMs = 999;
            var method = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("IsPublicationLive", BindingFlags.NonPublic | BindingFlags.Static)!;

            var live = (bool)method.Invoke(null, new object[]
            {
                publication,
                DateTimeOffset.FromUnixTimeMilliseconds(1000)
            })!;

            Assert.That(live, Is.False);
            Assert.That(publication.RecordKey, Is.EqualTo(
                new CultMeshBodyPublicationHandle(view.Buffers[0].BufferId, view.ProducerEpoch, view.Sequence).RecordKey));
        }

        [Test]
        public void LiveTransportDoesNotRegisterBodyBytesAsSnapshotDocuments()
        {
            var wireTypes = (Type[])typeof(EveUnityCultMeshLiveProviderTransport)
                .GetField("WireDocumentTypes", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

            Assert.That(wireTypes.Any(type => type == typeof(CultMeshCdnArtifactChunk)), Is.False);
            Assert.That(wireTypes.Any(type => type.Name == "CultMeshNetworkBodyDocument"), Is.False);
        }

        [Test]
        public void LiveTransportBuildsImmutableNetworkFallbackForMappedAssetContent()
        {
            var manifest = CultMeshCdn.PackArtifact(
                "provider/world/windows",
                Enumerable.Range(0, 64).Select(value => (byte)value).ToArray()).Manifest;
            var now = DateTimeOffset.UtcNow;
            var method = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("NetworkArtifactDescriptor", BindingFlags.NonPublic | BindingFlags.Static)!;

            var descriptor = (CultMeshBodyDescriptor)method.Invoke(
                null,
                new object[] { manifest, now, TimeSpan.FromMinutes(5) })!;

            Assert.That(descriptor.BodyId, Is.EqualTo(manifest.ArtifactId));
            Assert.That(descriptor.SchemaId, Is.EqualTo("gamecult.mesh.cdn-artifact.v1"));
            Assert.That(descriptor.ByteSize, Is.EqualTo(manifest.SizeBytes));
            Assert.That(descriptor.Capacity, Is.EqualTo(manifest.SizeBytes));
            Assert.That(descriptor.AccessMode, Is.EqualTo(CultMeshBodyAccessMode.ReadOnly));
            Assert.That(descriptor.Synchronization, Is.EqualTo(CultMeshBodySynchronization.ImmutableSequence));
            Assert.That(descriptor.TransportKind, Is.EqualTo(CultMeshBodyTransportKind.Network));
            Assert.That(descriptor.SemanticHash, Is.EqualTo(manifest.ContentHash));
            Assert.That(descriptor.CapabilityToken, Is.EqualTo(
                CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value));
        }

        [Test]
        public void LiveTransportAuthorizesOnlyExplicitlyAdvertisedBodyProducers()
        {
            var advertisement = ProviderAdvertisement("aetheria", "aetheria-daemon-17", "aetheria.daemon");
            var method = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("RequireAdvertisedBodyProducerIds", BindingFlags.NonPublic | BindingFlags.Static)!;

            var producerIds = (IReadOnlyList<string>)method.Invoke(null, new object?[] { advertisement })!;

            Assert.That(producerIds, Is.EqualTo(new[] { "aetheria.daemon" }));
            Assert.That(producerIds, Does.Not.Contain(advertisement.ProviderId));
            Assert.That(producerIds, Does.Not.Contain(advertisement.ServiceId));

            var authorizes = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("IsAdvertisedBodyProducer", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.That(authorizes.Invoke(null, new object[] { producerIds, "aetheria.daemon" }), Is.True);
            Assert.That(authorizes.Invoke(null, new object[] { producerIds, "aetheria" }), Is.False);
            Assert.That(authorizes.Invoke(null, new object[] { producerIds, "aetheria-daemon-17" }), Is.False);
            Assert.That(authorizes.Invoke(null, new object[] { producerIds, "impostor" }), Is.False);
        }

        [Test]
        public void LiveTransportRejectsAdvertisementWithoutBodyProducerIdentity()
        {
            var advertisement = ProviderAdvertisement("aetheria", "aetheria-daemon-17");
            var method = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("RequireAdvertisedBodyProducerIds", BindingFlags.NonPublic | BindingFlags.Static)!;

            var error = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object?[] { advertisement }));

            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("does not advertise an authorized body producer", error.InnerException!.Message);
        }

        [Test]
        public void ProviderAdvertisementRoundTripPreservesBodyProducerAuthority()
        {
            var advertisement = ProviderAdvertisement(
                "aetheria",
                "aetheria-daemon-17",
                "aetheria.daemon",
                "aetheria.asset-publisher");

            var bytes = MessagePack.MessagePackSerializer.Serialize(advertisement);
            var decoded = MessagePack.MessagePackSerializer.Deserialize<EveProviderAdvertisementDocument>(bytes);

            Assert.That(decoded.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(decoded.ServiceId, Is.EqualTo("aetheria-daemon-17"));
            Assert.That(decoded.AuthorizedBodyProducerIds,
                Is.EqualTo(new[] { "aetheria.daemon", "aetheria.asset-publisher" }));
        }

        [Test]
        public void EntitySoaViewReadsGenericSemanticColumnsFromInjectedLease()
        {
            var bytes = new byte[32];
            Buffer.BlockCopy(new[] { 4f, 5f, 6f, 7f, 8f, 9f }, 0, bytes, 0, 24);
            Buffer.BlockCopy(new[] { 41, 42 }, 0, bytes, 24, 8);
            var document = new EveEntitySoaViewDocument
            {
                ProviderId = "provider",
                ViewId = "pilot",
                BodySchemaId = "test.entity.slab.v1",
                LayoutVersion = 3,
                ProducerEpoch = 5,
                Sequence = 7,
                Capacity = 2,
                Buffers = new[] { new EveEntitySoaBuffer { BufferId = "hot", ByteLength = 32 } },
                Columns = new[]
                {
                    new EveEntitySoaColumn { ColumnId = "position", Semantic = "transform.position", BufferId = "hot", ScalarType = "float3", ElementStride = 12, ElementCount = 2 },
                    new EveEntitySoaColumn { ColumnId = "entity-index", Semantic = "entity.index", BufferId = "hot", ScalarType = "int32", ByteOffset = 24, ElementStride = 4, ElementCount = 2 }
                }
            };
            var lease = new FakeBodyReadLease(document, bytes);

            using var view = EveUnityEntitySoaView.Open(document, lease);

            Assert.That(view.TryReadVector3("transform.position", 1, out var position), Is.True);
            Assert.That(position, Is.EqualTo(new Vector3(7f, 8f, 9f)));
            Assert.That(view.TryReadInt32("entity.index", 0, out var entityIndex), Is.True);
            Assert.That(entityIndex, Is.EqualTo(41));
            Assert.That(view.Generation, Is.EqualTo(7));
            Assert.That(lease.Disposed, Is.False);
        }

        [TestCase("body")]
        [TestCase("schema")]
        [TestCase("layout")]
        [TestCase("epoch")]
        [TestCase("sequence")]
        [TestCase("capacity")]
        public void EntitySoaViewRejectsLeaseThatDisagreesWithLogicalLayout(string mismatch)
        {
            var document = EntityLeaseDocument();
            var descriptor = FakeBodyReadLease.DescriptorFor(document, 4);
            if (mismatch == "body") descriptor.BodyId = "wrong";
            if (mismatch == "schema") descriptor.SchemaId = "wrong";
            if (mismatch == "layout") descriptor.LayoutVersion++;
            if (mismatch == "epoch") descriptor.ProducerEpoch++;
            if (mismatch == "sequence") descriptor.Sequence++;
            if (mismatch == "capacity") descriptor.Capacity++;

            Assert.Throws<InvalidOperationException>(() => EveUnityEntitySoaView.Open(
                document, new FakeBodyReadLease(descriptor, new byte[4])));
        }

        [Test]
        public void EntitySoaPresenterPreservesProviderAuthoredIdentityFacts()
        {
            var bytes = new byte[16];
            Buffer.BlockCopy(new[] { 12 }, 0, bytes, 0, 4);
            Buffer.BlockCopy(new[] { 1f, 2f, 3f }, 0, bytes, 4, 12);
            var document = EntityLeaseDocument();
            document.Buffers[0].ByteLength = bytes.Length;
            document.Columns = new[]
            {
                new EveEntitySoaColumn { ColumnId = "entity-index", Semantic = "entity.index", BufferId = "hot", ScalarType = "int32", ElementStride = 4, ElementCount = 1 },
                new EveEntitySoaColumn { ColumnId = "position", Semantic = "transform.position", BufferId = "hot", ScalarType = "float3", ByteOffset = 4, ElementStride = 12, ElementCount = 1 }
            };
            var sink = new FakePlayableWorldSceneSink();

            new EveUnityEntitySoaPresenter(sink).Apply(document, new FakeBodyReadLease(document, bytes));

            var entity = sink.Upserts.Single().entity;
            Assert.That(entity.EntityId, Is.EqualTo("entity:pilot"));
            Assert.That(entity.Label, Is.EqualTo("Pilot"));
            Assert.That(entity.EntityKind, Is.EqualTo("player"));
            Assert.That(entity.Faction, Is.EqualTo("alliance"));
            Assert.That(entity.Selectable, Is.True);
            Assert.That(entity.Controllable, Is.True);
            Assert.That(entity.AssetRef, Is.EqualTo("cultmesh://assets/pilot"));
        }

        [Test]
        public void EntitySoaPresenterPublishesImmutableGenerationFactsAndRegistryLookups()
        {
            var root = new GameObject("presented-entities");
            try
            {
                var bytes = new byte[48];
                Buffer.BlockCopy(new[] { 12 }, 0, bytes, 0, 4);
                Buffer.BlockCopy(new[] { 1f, 2f, 3f }, 0, bytes, 4, 12);
                Buffer.BlockCopy(new[] { 0.5f }, 0, bytes, 16, 4);
                Buffer.BlockCopy(new[] { 4f, 5f, 6f }, 0, bytes, 20, 12);
                Buffer.BlockCopy(new[] { 7f }, 0, bytes, 32, 4);
                Buffer.BlockCopy(new[] { 1.5f }, 0, bytes, 36, 4);
                Buffer.BlockCopy(new[] { 9 }, 0, bytes, 40, 4);
                Buffer.BlockCopy(new[] { 2 }, 0, bytes, 44, 4);
                var document = EntityLeaseDocument();
                document.Buffers[0].ByteLength = bytes.Length;
                document.Columns = new[]
                {
                    SoaColumn("entity.index", "int32", 0, 4),
                    SoaColumn("transform.position", "float3", 4, 12),
                    SoaColumn("transform.rotation.radians", "float32", 16, 4),
                    SoaColumn("transform.velocity", "float3", 20, 12),
                    SoaColumn("physics.body.radius", "float32", 32, 4),
                    SoaColumn("render.scale", "float32", 36, 4),
                    SoaColumn("render.group.id", "uint32", 40, 4),
                    SoaColumn("render.lod", "int32", 44, 4)
                };
                var sink = new EveUnityGameObjectPlayableWorldSceneSink(
                    root.transform, new FixedGameObjectAssetProvider(null));

                new EveUnityEntitySoaPresenter(sink).Apply(document, new FakeBodyReadLease(document, bytes));

                Assert.That(sink.CurrentGeneration, Is.Not.Null);
                Assert.That(sink.CurrentGeneration!.ProducerEpoch, Is.EqualTo(document.ProducerEpoch));
                Assert.That(sink.CurrentGeneration.Sequence, Is.EqualTo(document.Sequence));
                Assert.That(sink.TryGetByEntityId("entity:pilot", out var byId), Is.True);
                Assert.That(sink.TryGetBySourceIndex(12, out var byIndex), Is.True);
                Assert.That(byIndex, Is.SameAs(byId));
                Assert.That(byId.Entity.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(byId.Entity.Velocity, Is.EqualTo(new Vector3(4f, 5f, 6f)));
                Assert.That(byId.Entity.Radius, Is.EqualTo(7f));
                Assert.That(byId.Entity.Scale, Is.EqualTo(1.5f));
                Assert.That(byId.Entity.RenderGroupId, Is.EqualTo(9));
                Assert.That(byId.Entity.Lod, Is.EqualTo(2));
                Assert.That(byId.Entity.Selectable, Is.True);
                Assert.That(byId.Entity.Controllable, Is.True);
                Assert.That(byId.Entity.AssetRef, Is.EqualTo("cultmesh://assets/pilot"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GameObjectGenerationRegistrySwapsOnlyAfterCompleteApplication()
        {
            var root = new GameObject("atomic-generation");
            EveUnityGameObjectPlayableWorldSceneSink? sink = null;
            long observedSequenceDuringApply = -1;
            try
            {
                var provider = new ObservingGameObjectAssetProvider(() =>
                    observedSequenceDuringApply = sink?.CurrentGeneration?.Sequence ?? -1);
                sink = new EveUnityGameObjectPlayableWorldSceneSink(root.transform, provider);
                sink.ApplyGeneration(new EveUnityPresentedEntityGeneration(
                    "entities", 3, 10,
                    new[] { PresentedEntity(1, "first", "asset:first") }));
                Assert.That(sink.TryGetByEntityId("first", out var firstGeneration), Is.True);
                var firstTransform = firstGeneration.Transform;

                observedSequenceDuringApply = -1;
                sink.ApplyGeneration(new EveUnityPresentedEntityGeneration(
                    "entities", 3, 11,
                    new[]
                    {
                        PresentedEntity(1, "first", "asset:first"),
                        PresentedEntity(2, "second", "asset:second")
                    }));

                Assert.That(observedSequenceDuringApply, Is.EqualTo(10));
                Assert.That(sink.CurrentGeneration!.Sequence, Is.EqualTo(11));
                Assert.That(sink.TryGetByEntityId("first", out var retained), Is.True);
                Assert.That(retained.Transform, Is.SameAs(firstTransform));
                Assert.That(sink.TryGetByEntityId("second", out var second), Is.True);
                Assert.That(sink.TryGetBySourceIndex(2, out var byIndex), Is.True);
                Assert.That(byIndex, Is.SameAs(second));
                Assert.That(sink.ActiveEntityCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LowerCarriesProviderAdvertisedWorldBoundary()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(Document("aetheria.daemon.game"), Advertisement("aetheria.daemon.game"));

            Assert.That(projection.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(projection.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(projection.ProjectionKind, Is.EqualTo("provider-authored-world-surface"));
            Assert.That(projection.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(projection.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));
            Assert.That(projection.Ownership, Is.EqualTo("provider-owns-world-state-assets-command-acceptance-and-receipts"));
            Assert.That(projection.Root.Id, Is.EqualTo("aetheria.daemon.game.root"));
            Assert.That(projection.Root.SceneObjectKind, Is.EqualTo("scene-node"));
        }

        [Test]
        public void LowerBuildsProviderAgnosticSceneGraphFromSurfaceTree()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(Document("aetheria.daemon.game"), Advertisement("aetheria.daemon.game"));

            Assert.That(projection.Root.Children.Count, Is.EqualTo(3));
            Assert.That(projection.Root.Children[0].SceneObjectKind, Is.EqualTo("world-projection-node"));
            Assert.That(projection.Root.Children[0].Props["binding"], Is.EqualTo("cultmesh://aetheria/world/entities"));
            Assert.That(projection.Root.Children[0].StateBindingCount, Is.EqualTo(1));
            Assert.That(projection.Root.Children[1].SceneObjectKind, Is.EqualTo("command-control"));
            Assert.That(projection.Root.Children[1].Props["command"], Is.EqualTo("aetheria.daemon.focus"));
            var nornNode = projection.Root.Children[2];
            Assert.That(nornNode.SceneObjectKind, Is.EqualTo("norn-graph-scene-projection"));
            Assert.That(nornNode.EmbeddedDocumentCount, Is.EqualTo(1));
            Assert.That(nornNode.EmbeddedDocuments.Count, Is.EqualTo(1));
            Assert.That(nornNode.EmbeddedDocuments[0].SlotId, Is.EqualTo("norn.map"));
            Assert.That(nornNode.EmbeddedDocuments[0].DocumentId, Is.EqualTo("cultmesh://aetheria/norn/map"));
            Assert.That(nornNode.EmbeddedDocuments[0].SchemaId, Is.EqualTo("gamecult.eve.surface.v1"));
            Assert.That(nornNode.EmbeddedDocuments[0].PresentationKind, Is.EqualTo("scene-overlay"));
            Assert.That(nornNode.PluginProjection, Is.Not.Null);
            Assert.That(nornNode.PluginProjection!.PluginId, Is.EqualTo("norn.graph"));
            Assert.That(nornNode.PluginProjection.ProjectionKind, Is.EqualTo("norn-scene-embedded-graph-shell"));
            Assert.That(nornNode.PluginProjection.AbiSchema, Is.EqualTo("gamecult.eve.plugin_abi.v1"));
            Assert.That(nornNode.PluginProjection.CommandBoundary, Is.EqualTo("sidecar-advertised-plugin-abi"));
            Assert.That(nornNode.PluginProjection.Capabilities, Does.Contain("embed.norn"));
            Assert.That(nornNode.PluginProjection.Command, Is.EqualTo("graph.focus"));
            Assert.That(nornNode.PluginProjection.DocumentId, Is.EqualTo("cultmesh://aetheria/norn/map"));
            Assert.That(nornNode.PluginProjection.SemanticOwner, Is.EqualTo("Norn"));
        }

        [Test]
        public void LowerExtractsPlayableArpgWorldFromGenericScene3dSurface()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(PlayableArpgDocument(), Advertisement("aetheria.daemon.game"));

            Assert.That(projection.Root.Children[0].SceneObjectKind, Is.EqualTo("playable-world-root"));
            Assert.That(projection.Root.Children[0].Children[0].SceneObjectKind, Is.EqualTo("playable-world-entity"));
            Assert.That(projection.Root.Children[0].Children.Any(child => child.SceneObjectKind == "world-field-3d"), Is.True);
            Assert.That(projection.PlayableWorld, Is.Not.Null);
            Assert.That(projection.PlayableWorld!.WorldRootId, Is.EqualTo("aetheria.daemon.game.playable"));
            Assert.That(projection.PlayableWorld.StatePointerId, Is.EqualTo("cultmesh://aetheria/run/current"));
            Assert.That(projection.PlayableWorld.EntityViewPointerId, Is.EqualTo("cultmesh://aetheria/world/entities.soa"));
            Assert.That(projection.PlayableWorld.EntityViewSchema, Is.EqualTo(EveEntitySoaViewDocument.SchemaId));
            Assert.That(projection.PlayableWorld.EntityBodyId, Is.EqualTo("eve:entity-soa:aetheria.daemon:pilot"));
            Assert.That(projection.PlayableWorld.ZoneRenderPointerId, Is.EqualTo("cultmesh://aetheria/world/zone-render"));
            Assert.That(projection.PlayableWorld.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(projection.PlayableWorld.InputProfile, Is.EqualTo("arpg-third-person"));
            Assert.That(projection.PlayableWorld.CameraRig, Is.EqualTo("planar.top-down-follow.v1"));
            Assert.That(projection.PlayableWorld.CameraLookAt, Is.Empty);
            Assert.That(projection.PlayableWorld.CameraTargetEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(projection.PlayableWorld.CameraDistance, Is.EqualTo(150f));
            Assert.That(projection.PlayableWorld.CameraVerticalFieldOfViewDegrees, Is.EqualTo(60f));
            Assert.That(projection.PlayableWorld.CameraTargetScreenX, Is.EqualTo(0.9f));
            Assert.That(projection.PlayableWorld.CameraTargetScreenY, Is.EqualTo(0.55f));
            Assert.That(projection.PlayableWorld.CameraPositionDamping, Is.EqualTo(5f));
            Assert.That(projection.PlayableWorld.CameraNearClipPlane, Is.EqualTo(0.3f));
            Assert.That(projection.PlayableWorld.CameraFarClipPlane, Is.EqualTo(4096f));
            Assert.That(projection.PlayableWorld.AmbientLightR, Is.EqualTo(0.2f));
            Assert.That(projection.PlayableWorld.AmbientLightG, Is.EqualTo(0.2f));
            Assert.That(projection.PlayableWorld.AmbientLightB, Is.EqualTo(0.2f));
            Assert.That(projection.PlayableWorld.AmbientLightIntensity, Is.EqualTo(1.46f));
            Assert.That(projection.PlayableWorld.SkyboxAssetRef, Is.Empty);
            Assert.That(projection.PlayableWorld.ReflectionAssetRef, Is.Empty);
            Assert.That(projection.PlayableWorld.PostProcessProfileAssetRef, Is.Empty);
            Assert.That(projection.PlayableWorld.ReflectionIntensity, Is.EqualTo(1f));
            Assert.That(projection.PlayableWorld.ExcludedRenderChannels, Is.EqualTo(new[] { "map" }));
            Assert.That(projection.PlayableWorld.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(projection.PlayableWorld.MovementCommand, Is.EqualTo("aetheria.daemon.move_intent"));
            Assert.That(projection.PlayableWorld.FocusCommand, Is.EqualTo("aetheria.daemon.focus"));
            Assert.That(projection.PlayableWorld.TargetCommand, Is.EqualTo("aetheria.daemon.target"));
            Assert.That(projection.PlayableWorld.EntityCount, Is.EqualTo(3));
            Assert.That(projection.PlayableWorld.FieldVolumes.Count, Is.EqualTo(1));
            Assert.That(projection.PlayableWorld.FieldVolumes[0].DocumentRef, Is.EqualTo("cultmesh://aetheria/world/fog-splats"));
            Assert.That(projection.PlayableWorld.FieldVolumes[0].MaterialAssetRef, Is.EqualTo("shader.environment.gravity-fog"));
            Assert.That(projection.PlayableWorld.FieldVolumes[0].RenderChannel, Is.EqualTo("world.transparent"));
            Assert.That(projection.PlayableWorld.FieldParticles.Count, Is.EqualTo(1));
            Assert.That(projection.PlayableWorld.FieldParticles[0].DocumentRef,
                Is.EqualTo("cultmesh://aetheria/world/fog-splats"));
            Assert.That(projection.PlayableWorld.FieldParticles[0].ComputeProgramAssetRef,
                Is.EqualTo("compute.environment.stardust"));
            Assert.That(projection.PlayableWorld.FieldParticles[0].MaterialAssetRef,
                Is.EqualTo("material.environment.stardust"));

            var player = FindEntity(projection.PlayableWorld, "player-vanguard");
            Assert.That(player.EntityKind, Is.EqualTo("player"));
            Assert.That(player.AssetRef, Is.EqualTo("cultmesh://aetheria/assets/map/entity/player"));
            Assert.That(player.PositionX, Is.EqualTo(0f));
            Assert.That(player.PositionY, Is.EqualTo(0f));
            Assert.That(player.PositionZ, Is.EqualTo(0f));
            Assert.That(player.Controllable, Is.True);
            Assert.That(player.MoveCommand, Is.EqualTo("aetheria.daemon.move_intent"));
            Assert.That(player.Props["presentationState"], Is.EqualTo("active"));

            var raider = FindEntity(projection.PlayableWorld, "raider-scout");
            Assert.That(raider.EntityKind, Is.EqualTo("enemy"));
            Assert.That(raider.PositionX, Is.EqualTo(9f));
            Assert.That(raider.PositionZ, Is.EqualTo(14f));
            Assert.That(raider.TargetCommand, Is.EqualTo("aetheria.daemon.target"));
        }

        [Test]
        public void SemanticEntityPresentationConsumesProviderPulseState()
        {
            var instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var presentation = instance.AddComponent<EveUnitySemanticEntityPresentation>();
                presentation.Apply(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["presentationState"] = "triggered",
                    ["activePulseSeconds"] = "1",
                    ["triggeredPulseSeconds"] = "0.25",
                    ["activeEmission"] = "100",
                    ["triggeredEmission"] = "1000"
                });
                presentation.ApplyAt(0.125f);

                Assert.That(presentation.PresentationState, Is.EqualTo("triggered"));
                Assert.That(instance.GetComponent<Renderer>().HasPropertyBlock(), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void GenericClientSessionConsumesAetheriaPlayableWorldSnapshotWithoutAetheriaTypes()
        {
            var session = new EveUnitySceneClientSession();
            var projection = session.Connect(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                42));

            Assert.That(session.ActiveSourcePointer, Is.EqualTo("cultmesh://aetheria/eve/surfaces/aetheria.daemon.game"));
            Assert.That(session.ActiveProjection, Is.SameAs(projection));
            Assert.That(projection.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(projection.PlayableWorld, Is.Not.Null);
            Assert.That(projection.PlayableWorld!.InputProfile, Is.EqualTo("arpg-third-person"));
            Assert.That(projection.PlayableWorld.EntityCount, Is.EqualTo(3));

            var moveIntent = session.CreateMoveIntent(
                "player-vanguard",
                12f,
                0f,
                8f,
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(moveIntent.Schema, Is.EqualTo(EveSurfaceCommandRequest.SchemaId));
            Assert.That(moveIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveIntent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(moveIntent.ClientId, Is.EqualTo("unity-scene"));
            Assert.That(moveIntent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));

            var focusIntent = session.CreateFocusIntent("anchor-station");
            Assert.That(focusIntent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(focusIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));

            var moveVectorIntent = session.CreateMoveVectorIntent(
                "player-vanguard",
                0.25f,
                0.75f,
                0.8f,
                DateTimeOffset.Parse("2026-07-09T00:00:01Z"));

            Assert.That(moveVectorIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveVectorIntent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(moveVectorIntent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveVectorIntent.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.move_intent"));
            Assert.That(moveVectorIntent.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
            Assert.That(moveVectorIntent.Payload.GetString("directionX"), Is.EqualTo("0.25"));
            Assert.That(moveVectorIntent.Payload.GetString("directionY"), Is.EqualTo("0.75"));
            Assert.That(moveVectorIntent.Payload.GetString("scalarValue"), Is.EqualTo("0.8"));

            var lookIntent = session.CreateLookDirectionIntent(
                "player-vanguard",
                0.6f,
                0f,
                0.8f,
                DateTimeOffset.Parse("2026-07-09T00:00:02Z"));
            Assert.That(lookIntent.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.look_intent"));
            Assert.That(lookIntent.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
            Assert.That(lookIntent.Payload.GetString("directionX"), Is.EqualTo("0.6"));
            Assert.That(lookIntent.Payload.GetString("directionY"), Is.EqualTo("0"));
            Assert.That(lookIntent.Payload.GetString("directionZ"), Is.EqualTo("0.8"));
        }

        [Test]
        public void GenericProviderConnectionAppliesLiveSnapshotsAndSubmitsCommandsThroughSink()
        {
            var source = new FakeProviderSurfaceSource(
                "aetheria",
                "aetheria.daemon.game",
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                new EveUnitySceneProviderSurfaceSnapshot(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1));
            var sink = new FakeCommandSink("cultmesh-command-sink");
            using var connection = new EveUnitySceneProviderConnection(source, sink);

            var projectionUpdates = 0;
            connection.ProjectionUpdated += _ => projectionUpdates++;

            var initialProjection = connection.Connect();

            Assert.That(initialProjection.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(connection.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(connection.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(connection.SourcePointer, Is.EqualTo("cultmesh://aetheria/eve/surfaces/aetheria.daemon.game"));
            Assert.That(connection.ActiveVersion, Is.EqualTo(1));
            Assert.That(projectionUpdates, Is.EqualTo(1));

            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));

            Assert.That(connection.ActiveVersion, Is.EqualTo(2));
            Assert.That(connection.ActiveProjection, Is.Not.Null);
            Assert.That(connection.ActiveProjection!.PlayableWorld, Is.Not.Null);
            Assert.That(connection.ActiveProjection.PlayableWorld!.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(projectionUpdates, Is.EqualTo(2));

            var moveIntent = connection.SubmitMoveIntent(
                "player-vanguard",
                20f,
                0f,
                15f,
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(sink.SinkKind, Is.EqualTo("cultmesh-command-sink"));
            Assert.That(sink.Submitted.Count, Is.EqualTo(1));
            Assert.That(sink.Submitted[0], Is.SameAs(moveIntent));
            Assert.That(moveIntent.Schema, Is.EqualTo(EveSurfaceCommandRequest.SchemaId));
            Assert.That(moveIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveIntent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(moveIntent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));

            connection.Disconnect();
            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                3));

            Assert.That(connection.ActiveVersion, Is.EqualTo(2));
            Assert.That(projectionUpdates, Is.EqualTo(2));
        }

        [Test]
        public void ProviderSurfaceDocumentSourceFeedsPlayableClientWithoutAetheriaTypes()
        {
            var documentSource = new FakeProviderSurfaceDocumentSource(new EveUnitySceneProviderSurfaceDocument(
                PlayableArpgDocument(),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                1));
            using var surfaceSource = new EveUnitySceneProviderSurfaceDocumentSource(documentSource);
            var commandSink = new FakeCommandSink("cultmesh-command-sink");
            var sceneSink = new FakePlayableWorldSceneSink();
            using var connection = new EveUnitySceneProviderConnection(surfaceSource, commandSink);
            using var client = new EveUnityPlayableWorldLiveClient(
                connection,
                new EveUnityPlayableWorldPresenter(sceneSink, new EveUnityAssetRefResolver()));

            var initialPresentation = client.Connect();

            Assert.That(surfaceSource.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(surfaceSource.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(surfaceSource.SourcePointer, Is.EqualTo("cultmesh://aetheria/eve/surfaces/aetheria.daemon.game"));
            Assert.That(initialPresentation.ActiveEntities, Is.EqualTo(3));
            Assert.That(client.ActiveWorld, Is.Not.Null);
            Assert.That(client.ActiveWorld!.StatePointerId, Is.EqualTo("cultmesh://aetheria/run/current"));
            Assert.That(client.ActiveWorld.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));

            documentSource.Publish(new EveUnitySceneProviderSurfaceDocument(
                PlayableArpgDocument(includeRaider: false, playerPosition: "11,0,5"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));

            Assert.That(client.ActiveVersion, Is.EqualTo(2));
            Assert.That(client.LastPresentation, Is.Not.Null);
            Assert.That(client.LastPresentation!.ActiveEntities, Is.EqualTo(2));
            Assert.That(sceneSink.RemovedEntityIds, Does.Contain("raider-scout"));
            Assert.That(sceneSink.Upserts[3].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sceneSink.Upserts[3].entity.PositionX, Is.EqualTo(11f));
            Assert.That(sceneSink.Upserts[3].entity.PositionZ, Is.EqualTo(5f));

            var moveIntent = client.SubmitMoveIntent("player-vanguard", 12f, 0f, 8f);

            Assert.That(commandSink.Submitted.Count, Is.EqualTo(1));
            Assert.That(commandSink.Submitted[0], Is.SameAs(moveIntent));
            Assert.That(moveIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveIntent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));
        }

        [Test]
        public void PlayableWorldRuntimeComposesProviderDocumentsAssetsReceiptsAndSceneSink()
        {
            var surfaceDocuments = new FakeProviderSurfaceDocumentSource(new EveUnitySceneProviderSurfaceDocument(
                PlayableArpgDocument(),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                1));
            var assetDocuments = new FakeAssetManifestDocumentSource(new EveUnityPlayableWorldAssetManifestDocument(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestDocumentEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "Resources/Aetheria/Entities/Vanguard.prefab",
                        "aetheria.vanguard"),
                    new EveUnityPlayableWorldAssetManifestDocumentEntry(
                        "cultmesh://aetheria/assets/map/entity/ship",
                        "enemy",
                        "Resources/Aetheria/Entities/Raider.prefab",
                        "aetheria.raider")
                },
                "aetheria"));
            var commandSink = new FakeCommandSink("cultmesh-command-sink");
            var receiptSource = new FakeCommandReceiptSource();
            var sceneSink = new FakePlayableWorldSceneSink();

            using var runtime = new EveUnityPlayableWorldRuntime(
                surfaceDocuments,
                commandSink,
                sceneSink,
                assetDocuments,
                receiptSource);

            var initialPresentation = runtime.Connect();

            Assert.That(initialPresentation.ActiveEntities, Is.EqualTo(3));
            Assert.That(runtime.ActiveWorld, Is.Not.Null);
            Assert.That(runtime.ActiveWorld!.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(runtime.AssetManifests.GetForWorld(runtime.ActiveWorld), Is.Not.Null);
            Assert.That(sceneSink.Upserts.Count, Is.EqualTo(3));

            var moveIntent = runtime.SubmitMoveIntent(
                "player-vanguard",
                14f,
                0f,
                9f,
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(commandSink.Submitted.Count, Is.EqualTo(1));
            Assert.That(commandSink.Submitted[0], Is.SameAs(moveIntent));
            Assert.That(moveIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveIntent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));

            surfaceDocuments.Publish(new EveUnitySceneProviderSurfaceDocument(
                PlayableArpgDocument(includeRaider: false, playerPosition: "14,0,9"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));

            Assert.That(runtime.ActiveVersion, Is.EqualTo(2));
            Assert.That(runtime.LastPresentation, Is.Not.Null);
            Assert.That(runtime.LastPresentation!.ActiveEntities, Is.EqualTo(2));
            Assert.That(sceneSink.RemovedEntityIds, Does.Contain("raider-scout"));
            Assert.That(sceneSink.Upserts[3].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sceneSink.Upserts[3].entity.PositionX, Is.EqualTo(14f));
            Assert.That(sceneSink.Upserts[3].entity.PositionZ, Is.EqualTo(9f));

            receiptSource.Publish(new EveUnitySceneCommandReceipt(
                "aetheria.daemon.move_intent.accepted",
                "aetheria.daemon.move_intent",
                "aetheria.daemon.move_intent",
                "accepted",
                "Aetheria",
                "provider-owned-daemon"));

            Assert.That(runtime.LastReceipt, Is.Not.Null);
            Assert.That(runtime.LastReceipt!.IsProviderOwned, Is.True);
            Assert.That(runtime.LastReceipt.ShouldRefreshProviderSurface, Is.True);
        }

        [Test]
        public void LiveProviderBridgeFeedsPlayableWorldRuntimeThroughTransportPorts()
        {
            var transport = new FakeLiveProviderTransport(
                new EveUnitySceneProviderSurfaceDocument(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1),
                new EveUnityPlayableWorldAssetManifestDocument(
                    "cultmesh://aetheria/assets/manifest",
                    new[]
                    {
                        new EveUnityPlayableWorldAssetManifestDocumentEntry(
                            "cultmesh://aetheria/assets/map/entity/player",
                            "player",
                            "Resources/Aetheria/Entities/Vanguard.prefab",
                            "aetheria.vanguard")
                    },
                    "aetheria"));
            using var bridge = new EveUnitySceneLiveProviderBridge(transport);
            var sceneSink = new FakePlayableWorldSceneSink();

            using var runtime = new EveUnityPlayableWorldRuntime(
                bridge,
                bridge,
                sceneSink,
                bridge,
                bridge);

            var initialPresentation = runtime.Connect();

            Assert.That(transport.ConnectCount, Is.EqualTo(1));
            Assert.That(bridge.TransportKind, Is.EqualTo("cultmesh-cultnet-provider-transport"));
            Assert.That(bridge.SurfacePointer, Is.EqualTo("cultmesh://aetheria/eve/surfaces/aetheria.daemon.game"));
            Assert.That(bridge.AssetManifestPointer, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(initialPresentation.ActiveEntities, Is.EqualTo(3));
            Assert.That(runtime.ActiveWorld, Is.Not.Null);
            Assert.That(runtime.AssetManifests.GetForWorld(runtime.ActiveWorld!), Is.Not.Null);

            var moveIntent = runtime.SubmitMoveVectorIntent(
                "player-vanguard",
                0.5f,
                1f,
                0.75f,
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(transport.Submitted.Count, Is.EqualTo(1));
            Assert.That(transport.Submitted[0], Is.SameAs(moveIntent));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));

            transport.PublishSurface(new EveUnitySceneProviderSurfaceDocument(
                PlayableArpgDocument(includeRaider: false, playerPosition: "6,0,2"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));

            Assert.That(runtime.ActiveVersion, Is.EqualTo(2));
            Assert.That(runtime.LastPresentation, Is.Not.Null);
            Assert.That(runtime.LastPresentation!.ActiveEntities, Is.EqualTo(2));
            Assert.That(sceneSink.RemovedEntityIds, Does.Contain("raider-scout"));

            transport.PublishReceipt(new EveUnitySceneCommandReceipt(
                "aetheria.daemon.move_intent.accepted",
                "aetheria.daemon.commands",
                "aetheria.daemon.move_intent",
                "accepted",
                "Aetheria",
                "provider-owned-daemon"));

            Assert.That(runtime.LastReceipt, Is.Not.Null);
            Assert.That(runtime.LastReceipt!.IsProviderOwned, Is.True);
            Assert.That(transport.RefreshCount, Is.EqualTo(0));
        }

        [Test]
        public void LiveProviderBridgeRollsBackSubscriptionsWhenTransportConnectFails()
        {
            var transport = new FakeLiveProviderTransport(
                new EveUnitySceneProviderSurfaceDocument(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1),
                new EveUnityPlayableWorldAssetManifestDocument(
                    "cultmesh://aetheria/assets/manifest",
                    Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                    "aetheria"))
            {
                FailNextConnect = true
            };
            using var bridge = new EveUnitySceneLiveProviderBridge(transport);

            Assert.Throws<IOException>(() => bridge.Connect());
            Assert.That(transport.SurfaceSubscriberCount, Is.Zero);
            Assert.That(transport.AssetManifestSubscriberCount, Is.Zero);
            Assert.That(transport.ReceiptSubscriberCount, Is.Zero);

            bridge.Connect();

            Assert.That(transport.ConnectCount, Is.EqualTo(2));
            Assert.That(transport.SurfaceSubscriberCount, Is.EqualTo(1));
            Assert.That(transport.AssetManifestSubscriberCount, Is.EqualTo(1));
            Assert.That(transport.ReceiptSubscriberCount, Is.EqualTo(1));
        }

        [Test]
        public void PlayableWorldClientHostMountsInterfaceProviderWithoutProviderTypes()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            hostObject.SetActive(false);

            try
            {
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://aetheria/assets/manifest",
                        new[]
                        {
                            new EveUnityPlayableWorldAssetManifestDocumentEntry(
                                "cultmesh://aetheria/assets/map/entity/player",
                                "player",
                                "",
                                "aetheria.vanguard")
                        },
                        "aetheria"));

                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider);

                var presentation = host.Connect();
                var moveIntent = host.SubmitMoveIntent("player-vanguard", 3f, 0f, 4f);

                Assert.That(presentation.ActiveEntities, Is.EqualTo(3));
                Assert.That(host.ActiveWorld, Is.Not.Null);
                Assert.That(host.ActiveWorld!.PlayerEntityId, Is.EqualTo("player-vanguard"));
                Assert.That(host.ActiveWorld.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
                Assert.That(rootObject.transform.childCount, Is.EqualTo(3));
                Assert.That(provider.RefreshCount, Is.EqualTo(1));
                Assert.That(provider.Submitted.Count, Is.EqualTo(1));
                Assert.That(provider.Submitted[0], Is.SameAs(moveIntent));
                Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PlayableWorldClientBootstrapWiresGenericUnityClientFromProviderInterfaces()
        {
            var hostObject = new GameObject("generic-eve-client");
            hostObject.SetActive(false);

            try
            {
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://aetheria/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "aetheria"));

                var bootstrap = hostObject.AddComponent<EveUnityPlayableWorldClientBootstrap>();
                bootstrap.ConfigureProvider(provider);

                bootstrap.Mount();

                Assert.That(bootstrap.Host, Is.Not.Null);
                Assert.That(bootstrap.Host!.ActiveWorld, Is.Not.Null);
                Assert.That(bootstrap.Host.ActiveWorld!.PlayerEntityId, Is.EqualTo("player-vanguard"));
                Assert.That(bootstrap.LastPresentation, Is.Not.Null);
                Assert.That(bootstrap.LastPresentation!.ActiveEntities, Is.EqualTo(3));
                Assert.That(bootstrap.SceneRoot, Is.Not.Null);
                Assert.That(bootstrap.SceneRoot!.childCount, Is.EqualTo(3));
                Assert.That(hostObject.GetComponent<EveUnityPlayableWorldInputDriver>(), Is.Not.Null);
                Assert.That(hostObject.GetComponent<EveUnityPlayableWorldCameraRig>(), Is.Not.Null);
                Assert.That(hostObject.GetComponent<EveUnityUiToolkitOverlay>(), Is.Not.Null);
                Assert.That(hostObject.GetComponent<EveUnityUiToolkitOverlay>().Root, Is.Not.Null);
                Assert.That(hostObject.GetComponent<UIDocument>().panelSettings.themeStyleSheet, Is.Not.Null);
                Assert.That(bootstrap.CameraTransform, Is.Not.Null);
                Assert.That(provider.RefreshCount, Is.EqualTo(1));

                var input = hostObject.GetComponent<EveUnityPlayableWorldInputDriver>();
                var moveVector = input.SubmitMoveVectorInput(0f, 1f);

                Assert.That(moveVector, Is.Not.Null);
                Assert.That(provider.Submitted.Count, Is.EqualTo(1));
                Assert.That(provider.Submitted[0], Is.SameAs(moveVector));
                Assert.That(moveVector!.ProviderId, Is.EqualTo("aetheria"));
                Assert.That(moveVector.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
                Assert.That(moveVector.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
                Assert.That(moveVector.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.move_intent"));
                Assert.That(moveVector.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hostObject);
            }
        }

        [Test]
        public void PlayableWorldClientHostSubmitsProviderOwnedMoveVector()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            hostObject.SetActive(false);

            try
            {
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://aetheria/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "aetheria"));

                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider);
                host.Connect();

                var moveVector = host.SubmitMoveVectorIntent("player-vanguard", 0.6f, -0.8f, 1f);

                Assert.That(provider.Submitted.Count, Is.EqualTo(1));
                Assert.That(provider.Submitted[0], Is.SameAs(moveVector));
                Assert.That(moveVector.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
                Assert.That(moveVector.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.move_intent"));
                Assert.That(moveVector.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
                Assert.That(moveVector.Payload.GetString("directionX"), Is.EqualTo("0.6"));
                Assert.That(moveVector.Payload.GetString("directionY"), Is.EqualTo("-0.8"));
                Assert.That(moveVector.Payload.GetString("scalarValue"), Is.EqualTo("1"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PlayableWorldInputDriverBuildsCameraRelativeMoveVectorWithoutProviderTypes()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            var cameraObject = new GameObject("generic-eve-camera");
            hostObject.SetActive(false);

            try
            {
                cameraObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://aetheria/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "aetheria"));

                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider);
                host.Connect();

                var driver = hostObject.AddComponent<EveUnityPlayableWorldInputDriver>();
                driver.Host = host;
                driver.CameraTransform = cameraObject.transform;
                var request = driver.SubmitMoveVectorInput(0f, 1f);

                Assert.That(request, Is.Not.Null);
                Assert.That(provider.Submitted.Count, Is.EqualTo(1));
                Assert.That(request!.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.move_intent"));
                Assert.That(request.Payload.GetDouble("directionX", 0), Is.EqualTo(1f).Within(0.0001f));
                Assert.That(request.Payload.GetDouble("directionY", 0), Is.EqualTo(0f).Within(0.0001f));
                Assert.That(request.Payload.GetDouble("scalarValue", 0), Is.EqualTo(1f).Within(0.0001f));

                var controlledYaw = rootObject.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true)
                    .Single(marker => marker.EntityId == "player-vanguard")
                    .transform.eulerAngles.y * Mathf.Deg2Rad;
                driver.QueueLookInput(250f);
                driver.QueueLookInput(250f);
                driver.QueueLookInput(500f);
                var look = driver.SubmitPendingLookInput();
                Assert.That(look, Is.Not.Null);
                Assert.That(provider.Submitted.Count, Is.EqualTo(2));
                Assert.That(look!.Payload.GetString("commandId"), Is.EqualTo("aetheria.daemon.look_intent"));
                Assert.That(look.Payload.GetDouble("directionX", 0), Is.EqualTo(Mathf.Sin(controlledYaw - 1f)).Within(0.0001f));
                Assert.That(look.Payload.GetDouble("directionZ", 0), Is.EqualTo(Mathf.Cos(controlledYaw - 1f)).Within(0.0001f));

                host.Connect();
                var rebasedYaw = rootObject.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true)
                    .Single(marker => marker.EntityId == "player-vanguard")
                    .transform.eulerAngles.y * Mathf.Deg2Rad;
                var rebasedLook = driver.SubmitLookInput(1000f);
                Assert.That(rebasedLook, Is.Not.Null);
                Assert.That(provider.Submitted.Count, Is.EqualTo(3));
                Assert.That(rebasedLook!.Payload.GetDouble("directionX", 0),
                    Is.EqualTo(Mathf.Sin(rebasedYaw - 1f)).Within(0.0001f));
                Assert.That(rebasedLook.Payload.GetDouble("directionZ", 0),
                    Is.EqualTo(Mathf.Cos(rebasedYaw - 1f)).Within(0.0001f));

                var movement = EveUnityPlayableWorldMoveVector.FromCameraRelativeInput(1f, 1f, null);
                Assert.That(movement.HasInput, Is.True);
                Assert.That(movement.ScalarValue, Is.EqualTo(1f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PlayableWorldInputDriverUsesAdvertisedPrimaryBinding()
        {
            var capability = new EveInputCapabilityDocument
            {
                Actions = new[]
                {
                    new EveInputActionDocument
                    {
                        ActionId = "weapon-group.0.fire",
                        Availability = "available"
                    }
                },
                DefaultProfiles = new[]
                {
                    new EveInputProfileDocument
                    {
                        ProfileId = "keyboard-mouse",
                        DeviceClass = "keyboard-mouse",
                        Bindings = new[]
                        {
                            new EveInputBindingDocument
                            {
                                ActionId = "weapon-group.0.fire",
                                Gesture = new EveInputGestureDocument
                                {
                                    Controls = new[] { "mouse.primary" }
                                },
                                ActionBar = true
                            }
                        }
                    }
                }
            };

            Assert.That(
                EveUnityPlayableWorldInputDriver.ResolvePrimaryActionId(capability),
                Is.EqualTo("weapon-group.0.fire"));
            capability.Actions[0].Availability = "unavailable";
            Assert.That(EveUnityPlayableWorldInputDriver.ResolvePrimaryActionId(capability), Is.Empty);
        }

        [Test]
        public void PlayableWorldInputDriverPerformsAdvertisedViewDirectionBindingOnPressEdge()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            var cameraObject = new GameObject("generic-eve-camera");
            hostObject.SetActive(false);

            try
            {
                cameraObject.transform.rotation = Quaternion.Euler(30f, 90f, 0f);
                var capability = new EveInputCapabilityDocument
                {
                    ProviderId = "generic-provider",
                    CapabilityId = "pilot.input",
                    Actions = new[]
                    {
                        new EveInputActionDocument
                        {
                            ActionId = "pilot.target-reticle",
                            Operation = "generic.commands.TargetReticle",
                            Availability = "available",
                            InputValue = new EveInputValueDocument
                            {
                                Model = EveUnityAdvertisedInputAction.ViewDirectionValueModel,
                                PayloadKeys = new[] { "directionX", "directionY", "directionZ" }
                            }
                        }
                    },
                    DefaultProfiles = new[]
                    {
                        new EveInputProfileDocument
                        {
                            ProfileId = "keyboard-mouse",
                            DeviceClass = "keyboard-mouse",
                            Bindings = new[]
                            {
                                new EveInputBindingDocument
                                {
                                    BindingId = "target.reticle.r",
                                    ActionId = "pilot.target-reticle",
                                    Gesture = new EveInputGestureDocument
                                    {
                                        Kind = "direct",
                                        Controls = new[] { "keyboard.r" }
                                    }
                                }
                            }
                        }
                    }
                };
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://generic/eve/surfaces/game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://generic/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "generic-provider"),
                    capability);
                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider);
                host.Connect();
                var pressed = false;
                var driver = hostObject.AddComponent<EveUnityPlayableWorldInputDriver>();
                driver.Host = host;
                driver.CameraTransform = cameraObject.transform;
                driver.DigitalControlReader = control => control == "keyboard.r" ? pressed : (bool?)null;

                driver.SubmitChangedAdvertisedActions();
                pressed = true;
                driver.SubmitChangedAdvertisedActions();
                driver.SubmitChangedAdvertisedActions();

                Assert.That(provider.Submitted.Count, Is.EqualTo(1));
                var request = provider.Submitted.Single();
                Assert.That(request.Payload.GetString("commandId"), Is.EqualTo("generic.commands.TargetReticle"));
                Assert.That(request.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
                Assert.That(request.Payload.GetString("actionId"), Is.EqualTo("pilot.target-reticle"));
                var expected = cameraObject.transform.forward.normalized;
                Assert.That(request.Payload.GetDouble("directionX", 0), Is.EqualTo(expected.x).Within(0.000001));
                Assert.That(request.Payload.GetDouble("directionY", 0), Is.EqualTo(expected.y).Within(0.000001));
                Assert.That(request.Payload.GetDouble("directionZ", 0), Is.EqualTo(expected.z).Within(0.000001));

                pressed = false;
                driver.SubmitChangedAdvertisedActions();
                pressed = true;
                driver.SubmitChangedAdvertisedActions();
                Assert.That(provider.Submitted.Count, Is.EqualTo(2),
                    "a released and re-pressed generic binding must perform exactly once per press edge");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void GenericActionBarSelectsSuggestionsAndSubmitsScalarThroughAdvertisedOperation()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            hostObject.SetActive(false);

            try
            {
                var capability = new EveInputCapabilityDocument
                {
                    ProviderId = "generic-provider",
                    CapabilityId = "pilot.input",
                    Actions = new[]
                    {
                        new EveInputActionDocument
                        {
                            ActionId = "equipment.0.temperature",
                            Label = "Target Temperature",
                            Operation = "generic.commands.SetTemperature",
                            Availability = "available",
                            ActionBar = true,
                            IconRef = "item.thermostat.icon",
                            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["equipmentIndex"] = "0",
                                ["behaviorIndex"] = "3"
                            },
                            InputValue = new EveInputValueDocument
                            {
                                Model = EveUnityAdvertisedInputAction.ScalarValueModel,
                                PayloadKey = "scalarValue",
                                CurrentValue = 300,
                                Unit = "kelvin"
                            }
                        },
                        new EveInputActionDocument
                        {
                            ActionId = "pilot.interact",
                            Label = "Interact",
                            Operation = "generic.commands.Interact",
                            Availability = "available"
                        },
                        new EveInputActionDocument
                        {
                            ActionId = "pilot.hidden",
                            Label = "Hidden",
                            Operation = "generic.commands.Hidden",
                            Availability = "available"
                        }
                    },
                    DefaultProfiles = new[]
                    {
                        new EveInputProfileDocument
                        {
                            ProfileId = "keyboard-mouse",
                            DeviceClass = "keyboard-mouse",
                            Bindings = new[]
                            {
                                new EveInputBindingDocument
                                {
                                    BindingId = "interact.f",
                                    ActionId = "pilot.interact",
                                    ActionBar = true,
                                    Gesture = new EveInputGestureDocument
                                    {
                                        Kind = "direct",
                                        Controls = new[] { "keyboard.f" }
                                    }
                                }
                            }
                        }
                    }
                };
                Assert.That(EveUnityInputActionBar.SelectActions(capability)
                        .Select(action => action.ActionId),
                    Is.EqualTo(new[] { "pilot.interact", "equipment.0.temperature" }));

                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://generic/eve/surfaces/game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://generic/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "generic-provider"),
                    capability);
                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider);
                host.Connect();
                var bar = hostObject.GetComponent<EveUnityInputActionBar>();
                Assert.That(bar, Is.Not.Null);
                bar!.RefreshNow();
                Assert.That(bar.PresentedActionIds,
                    Is.EquivalentTo(new[] { "pilot.interact", "equipment.0.temperature" }));

                bar.SubmitScalar("equipment.0.temperature", "425.5");

                var request = provider.Submitted.Single();
                Assert.That(request.Payload.GetString("commandId"), Is.EqualTo("generic.commands.SetTemperature"));
                Assert.That(request.Payload.GetString("entityId"), Is.EqualTo("player-vanguard"));
                Assert.That(request.Payload.GetString("actionId"), Is.EqualTo("equipment.0.temperature"));
                Assert.That(request.Payload.GetString("equipmentIndex"), Is.EqualTo("0"));
                Assert.That(request.Payload.GetDouble("scalarValue", 0), Is.EqualTo(425.5).Within(0.000001));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PlayableWorldCameraRigFollowsAdvertisedPlayerEntityWithoutProviderTypes()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            var cameraObject = new GameObject("generic-eve-camera");
            var competingCameraObject = new GameObject("competing-eve-camera");
            var secondaryRootObject = new GameObject("secondary-eve-world-root");
            var secondaryHostObject = new GameObject("secondary-eve-client");
            var secondaryCameraObject = new GameObject("secondary-eve-camera");
            hostObject.SetActive(false);
            secondaryHostObject.SetActive(false);
            var ambientMode = RenderSettings.ambientMode;
            var ambientLight = RenderSettings.ambientLight;
            var ambientIntensity = RenderSettings.ambientIntensity;
            var previousSkybox = RenderSettings.skybox;
            var previousReflectionMode = RenderSettings.defaultReflectionMode;
            var previousCustomReflection = RenderSettings.customReflectionTexture;
            var previousReflectionIntensity = RenderSettings.reflectionIntensity;
            var previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var previousQualityPipeline = QualitySettings.renderPipeline;
            var gradingRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            var gradingPipelineAsset = UniversalRenderPipelineAsset.Create(gradingRendererData);
            gradingPipelineAsset.colorGradingMode = ColorGradingMode.LowDynamicRange;
            GraphicsSettings.defaultRenderPipeline = gradingPipelineAsset;
            QualitySettings.renderPipeline = gradingPipelineAsset;
            var previousGradingMode = gradingPipelineAsset.colorGradingMode;
            Material? skyboxMaterial = null;
            Cubemap? reflectionCubemap = null;
            VolumeProfile? postProcessProfile = null;

            try
            {
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                var nativeAssets = hostObject.AddComponent<FixedNativeAssetProviderComponent>();
                var skyboxShader = Shader.Find("Skybox/Procedural");
                Assert.That(skyboxShader, Is.Not.Null);
                skyboxMaterial = new Material(skyboxShader!);
                reflectionCubemap = new Cubemap(4, TextureFormat.RGBA32, false);
                postProcessProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                nativeAssets.Set("material.environment.skybox", skyboxMaterial);
                nativeAssets.Set("texture.environment.reflection", reflectionCubemap);
                nativeAssets.Set("profile.environment.flight", postProcessProfile);
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(
                            skyboxAssetRef: "material.environment.skybox",
                            reflectionAssetRef: "texture.environment.reflection",
                            postProcessProfileAssetRef: "profile.environment.flight",
                            cameraReconstruction: "temporal-reprojection.v1",
                            temporalQuality: "high",
                            temporalHistoryBlend: "0.99",
                            temporalJitterScale: "0.1",
                            temporalSharpening: "0",
                            exposureMode: "histogram.v1",
                            exposureLowPercent: "47.37294",
                            exposureHighPercent: "99",
                            exposureMinimumEv: "-3",
                            exposureMaximumEv: "0.3",
                            exposureKeyValue: "0.5",
                            exposureAdaptation: "progressive",
                            exposureSpeedUp: "2",
                            exposureSpeedDown: "1",
                            colorGradingSpace: "hdr-before-tonemap.v1"),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://aetheria/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "aetheria"));

                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider, nativeAssets);
                host.Connect();
                hostObject.SetActive(true);
                var exposedWorld = host.ActiveProjection?.PlayableWorld;
                Assert.That(exposedWorld, Is.Not.Null);
                Assert.That(exposedWorld!.ExposureMode, Is.EqualTo("histogram.v1"));
                Assert.That(exposedWorld.ColorGradingSpace, Is.EqualTo("hdr-before-tonemap.v1"));
                Assert.That(exposedWorld.ExposureLowPercent, Is.EqualTo(47.37294f).Within(0.00001f));
                Assert.That(exposedWorld.ExposureHighPercent, Is.EqualTo(99f));
                Assert.That(exposedWorld.ExposureMinimumEv, Is.EqualTo(-3f));
                Assert.That(exposedWorld.ExposureMaximumEv, Is.EqualTo(0.3f).Within(0.00001f));
                Assert.That(exposedWorld.ExposureKeyValue, Is.EqualTo(0.5f));
                Assert.That(exposedWorld.ExposureAdaptation, Is.EqualTo("progressive"));
                Assert.That(exposedWorld.ExposureSpeedUp, Is.EqualTo(2f));
                Assert.That(exposedWorld.ExposureSpeedDown, Is.EqualTo(1f));

                var rig = hostObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                rig.Host = host;
                rig.CameraTransform = cameraObject.transform;
                rig.RenderPolicySource = new FixedRenderChannelPolicy("map", 14);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.aspect = 16f / 9f;
                camera.orthographic = true;
                camera.lensShift = new Vector2(0.25f, -0.2f);

                var player = rootObject.GetComponentInChildren<EveUnityPlayableWorldEntityMarker>();
                Assert.That(player, Is.Not.Null);
                var largeVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                largeVisual.transform.SetParent(player.transform, false);
                largeVisual.transform.localScale = Vector3.one * 100f;
                largeVisual.layer = 14;

                Assert.That(rig.ApplyRig(0f), Is.True);
                Assert.That(host.ActiveCameraTransform, Is.SameAs(cameraObject.transform));
                Assert.That(camera.cullingMask & (1 << 14), Is.Zero);
                Assert.That(camera.fieldOfView, Is.EqualTo(60f).Within(0.001f));
                Assert.That(camera.orthographic, Is.False);
                Assert.That(camera.lensShift, Is.EqualTo(Vector2.zero));
                Assert.That(camera.nearClipPlane, Is.EqualTo(0.3f).Within(0.001f));
                Assert.That(camera.farClipPlane, Is.EqualTo(4096f).Within(0.001f));
                Assert.That(cameraObject.transform.position.y - player.transform.position.y, Is.EqualTo(150f).Within(0.001f));
                var viewport = camera.WorldToViewportPoint(player.transform.position);
                Assert.That(viewport.x, Is.EqualTo(0.9f).Within(0.001f));
                Assert.That(viewport.y, Is.EqualTo(0.55f).Within(0.001f));
                Assert.That(RenderSettings.ambientMode, Is.EqualTo(UnityEngine.Rendering.AmbientMode.Skybox));
                Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(1.46f).Within(0.001f));
                Assert.That(RenderSettings.skybox, Is.SameAs(skyboxMaterial));
                Assert.That(RenderSettings.defaultReflectionMode, Is.EqualTo(DefaultReflectionMode.Custom));
                Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(reflectionCubemap));
                Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(1f).Within(0.001f));
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.Skybox));
                Assert.That(camera.GetComponent<UniversalAdditionalCameraData>(), Is.Not.Null);
                var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                Assert.That(cameraData.renderPostProcessing, Is.True);
                Assert.That(cameraData.antialiasing, Is.EqualTo(AntialiasingMode.TemporalAntiAliasing));
                Assert.That(cameraData.taaSettings.quality, Is.EqualTo(TemporalAAQuality.High));
                Assert.That(cameraData.taaSettings.baseBlendFactor, Is.EqualTo(0.99f).Within(0.0001f));
                Assert.That(cameraData.taaSettings.jitterScale, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(cameraData.taaSettings.contrastAdaptiveSharpening, Is.Zero);
                var adaptiveExposure = camera.GetComponent<EveUnityAdaptiveExposureRenderer>();
                Assert.That(adaptiveExposure, Is.Not.Null);
                Assert.That(adaptiveExposure.IsConfigured, Is.True);
                Assert.That(gradingPipelineAsset.colorGradingMode, Is.EqualTo(ColorGradingMode.HighDynamicRange));
                var postProcessTransform = hostObject.transform.Find("Eve World Post Process");
                Assert.That(postProcessTransform, Is.Not.Null);
                Assert.That(postProcessTransform.GetComponent<Volume>().sharedProfile, Is.SameAs(postProcessProfile));
                var keyLightTransform = hostObject.transform.Find("Eve World Key Light");
                Assert.That(keyLightTransform, Is.Not.Null);
                var keyLight = keyLightTransform.GetComponent<Light>();
                Assert.That(keyLight.type, Is.EqualTo(LightType.Directional));
                Assert.That(keyLight.intensity, Is.EqualTo(0.75f).Within(0.001f));
                Assert.That(keyLight.color, Is.EqualTo(new Color(1f, 0.95f, 0.9f, 1f)));
                Assert.That(Vector3.Dot(keyLightTransform.forward, new Vector3(0.4f, -1f, 0.25f).normalized),
                    Is.GreaterThan(0.999f));
                Assert.That(nativeAssets.LastBinding?.AssetRef, Is.EqualTo("profile.environment.flight"));
                Assert.That(nativeAssets.LastBinding?.EntityKind, Is.Empty);

                var secondaryHost = secondaryHostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                secondaryHost.Configure(secondaryRootObject.transform, provider, provider, provider, provider, nativeAssets);
                secondaryHost.Connect();
                secondaryHostObject.SetActive(true);
                secondaryCameraObject.AddComponent<Camera>();
                var secondaryRig = secondaryCameraObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                secondaryRig.Host = secondaryHost;
                secondaryRig.CameraTransform = secondaryCameraObject.transform;
                Assert.That(secondaryRig.ApplyRig(0f), Is.False);
                Assert.That(secondaryHost.ActiveCameraTransform, Is.Null);
                Assert.That(RenderSettings.skybox, Is.SameAs(skyboxMaterial));

                var competingRig = competingCameraObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                competingRig.Host = host;
                competingRig.CameraTransform = competingCameraObject.transform;
                Assert.That(competingRig.ApplyRig(0f), Is.False);
                Assert.That(host.ActiveCameraTransform, Is.SameAs(cameraObject.transform));

                var aim = hostObject.GetComponent<EveUnityAimPresentationRenderer>();
                Assert.That(aim, Is.Not.Null);
                var playerPosition = player.transform.position;
                var playerRotation = player.transform.rotation;
                aim.RefreshNow();
                Assert.That(aim.ViewDotVisible, Is.True);
                Assert.That(Vector3.Distance(playerPosition, aim.ViewDotPosition), Is.EqualTo(50f).Within(0.001f));
                Assert.That(player.transform.position, Is.EqualTo(playerPosition));
                Assert.That(player.transform.rotation, Is.EqualTo(playerRotation));

                var selectedTarget = rootObject.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>()
                    .Single(marker => marker.EntityId == "raider-scout");
                Assert.That(EveUnityCombatPresentation.Find(host.ActiveProjection)?.SelectedTargetEntityId,
                    Is.EqualTo("raider-scout"));
                var poseWithoutSelectedTarget = cameraObject.transform.position;
                selectedTarget.transform.position = new Vector3(10000f, 0f, -10000f);
                Assert.That(rig.ApplyRig(0f), Is.True);
                Assert.That(cameraObject.transform.position, Is.EqualTo(poseWithoutSelectedTarget));
                viewport = camera.WorldToViewportPoint(player.transform.position);
                Assert.That(viewport.x, Is.EqualTo(0.9f).Within(0.001f));
                Assert.That(viewport.y, Is.EqualTo(0.55f).Within(0.001f));

                var beforeFollow = cameraObject.transform.position;
                player.transform.position += Vector3.right * 10f;
                Assert.That(rig.ApplyRig(0.1f), Is.True);
                Assert.That(
                    cameraObject.transform.position.x - beforeFollow.x,
                    Is.EqualTo(10f * (1f - Mathf.Exp(-0.5f))).Within(0.001f));
                rig.CameraTransform = null;
                Assert.That(host.ActiveCameraTransform, Is.Null);
                Assert.That(hostObject.transform.Find("Eve World Key Light"), Is.Null);
                Assert.That(hostObject.transform.Find("Eve World Post Process"), Is.Null);
                Assert.That(camera.GetComponent<UniversalAdditionalCameraData>(), Is.Null);
                Assert.That(gradingPipelineAsset.colorGradingMode, Is.EqualTo(previousGradingMode));
                aim.RefreshNow();
                Assert.That(aim.ViewDotVisible, Is.False);
                Assert.That(RenderSettings.ambientMode, Is.EqualTo(ambientMode));
                Assert.That(RenderSettings.ambientLight, Is.EqualTo(ambientLight));
                Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(ambientIntensity));
                Assert.That(RenderSettings.skybox, Is.SameAs(previousSkybox));
                Assert.That(RenderSettings.defaultReflectionMode, Is.EqualTo(previousReflectionMode));
                Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(previousCustomReflection));
                Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(previousReflectionIntensity));
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                rig.CameraTransform = cameraObject.transform;
                nativeAssets.Clear();
                nativeAssets.Set("some.other.asset", skyboxMaterial);
                Assert.That(rig.ApplyRig(0f), Is.False);
                Assert.That(host.ActiveCameraTransform, Is.Null);
                nativeAssets.Set("material.environment.skybox", reflectionCubemap);
                nativeAssets.Set("texture.environment.reflection", skyboxMaterial);
                nativeAssets.Set("profile.environment.flight", skyboxMaterial);
                Assert.That(rig.ApplyRig(0f), Is.False);
                Assert.That(host.ActiveCameraTransform, Is.Null);
            }
            finally
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientIntensity = ambientIntensity;
                RenderSettings.skybox = previousSkybox;
                RenderSettings.defaultReflectionMode = previousReflectionMode;
                RenderSettings.customReflectionTexture = previousCustomReflection;
                RenderSettings.reflectionIntensity = previousReflectionIntensity;
                QualitySettings.renderPipeline = previousQualityPipeline;
                GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
                UnityEngine.Object.DestroyImmediate(gradingPipelineAsset);
                UnityEngine.Object.DestroyImmediate(gradingRendererData);
                if (skyboxMaterial != null) UnityEngine.Object.DestroyImmediate(skyboxMaterial);
                if (reflectionCubemap != null) UnityEngine.Object.DestroyImmediate(reflectionCubemap);
                if (postProcessProfile != null) UnityEngine.Object.DestroyImmediate(postProcessProfile);
                UnityEngine.Object.DestroyImmediate(secondaryCameraObject);
                UnityEngine.Object.DestroyImmediate(secondaryHostObject);
                UnityEngine.Object.DestroyImmediate(secondaryRootObject);
                UnityEngine.Object.DestroyImmediate(competingCameraObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void PlayableWorldCameraRigFramesEntityAndLooksAtAdvertisedConvergencePointWithoutProviderTypes()
        {
            var rootObject = new GameObject("generic-forward-world-root");
            var hostObject = new GameObject("generic-forward-client");
            var cameraObject = new GameObject("generic-forward-camera");
            hostObject.SetActive(false);
            try
            {
                var provider = hostObject.AddComponent<FakePlayableWorldProviderComponent>();
                var nativeAssets = hostObject.AddComponent<FixedNativeAssetProviderComponent>();
                provider.Set(
                    new EveUnitySceneProviderSurfaceDocument(
                        PlayableArpgDocument(
                            cameraRig: "perspective.entity-forward-follow.v1",
                            cameraDistance: "30",
                            cameraTargetScreenX: "0.64",
                            cameraTargetScreenY: "0.81",
                            cameraPositionDamping: "0",
                            cameraNearClipPlane: "1",
                            cameraFarClipPlane: "4096",
                            cameraLookAt: "aim.convergence-point.v1"),
                        Advertisement("aetheria.daemon.game"),
                        "cultmesh://generic/eve/surfaces/forward",
                        1),
                    new EveUnityPlayableWorldAssetManifestDocument(
                        "cultmesh://generic/assets/manifest",
                        Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                        "generic"));

                var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(rootObject.transform, provider, provider, provider, provider, nativeAssets);
                host.Connect();
                hostObject.SetActive(true);
                var camera = cameraObject.AddComponent<Camera>();
                camera.aspect = 16f / 9f;
                var rig = hostObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                rig.Host = host;
                rig.CameraTransform = cameraObject.transform;

                var player = rootObject.GetComponentInChildren<EveUnityPlayableWorldEntityMarker>();
                Assert.That(player, Is.Not.Null);
                Assert.That(rig.ApplyRig(0f), Is.True);
                var aimPoint = player!.transform.position + player.transform.forward * 50f;
                var cameraToAim = (aimPoint - cameraObject.transform.position).normalized;
                Assert.That(Vector3.Dot(cameraObject.transform.forward, cameraToAim), Is.GreaterThan(0.999f));
                Assert.That(cameraObject.transform.forward.y, Is.GreaterThan(0f));
                Assert.That(camera.fieldOfView, Is.EqualTo(60f).Within(0.001f));
                Assert.That(camera.nearClipPlane, Is.EqualTo(1f).Within(0.001f));
                Assert.That(camera.farClipPlane, Is.EqualTo(4096f).Within(0.001f));
                var viewport = camera.WorldToViewportPoint(player.transform.position);
                Assert.That(viewport.x, Is.EqualTo(0.64f).Within(0.001f));
                Assert.That(viewport.y, Is.EqualTo(0.81f).Within(0.001f));
                Assert.That(cameraObject.transform.position.y, Is.LessThan(player.transform.position.y));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void SemanticScaleWrapsProviderAuthoredScaleAndFallbackGeometry()
        {
            var root = new GameObject("world");
            var prefab = new GameObject("provider-prefab");
            prefab.transform.localScale = new Vector3(2f, 3f, 4f);
            try
            {
                var providerSink = new EveUnityGameObjectPlayableWorldSceneSink(
                    root.transform,
                    new FixedGameObjectAssetProvider(prefab));
                var entity = PlayableEntityModel("provider", "prefab.ship", 12f);
                providerSink.UpsertEntity(entity, new EveUnityPlayableWorldAssetBinding(
                    "prefab.ship", "ship", "provider-asset-ref"));

                var instance = root.transform.GetChild(0);
                Assert.That(instance.localScale, Is.EqualTo(Vector3.one * 12f));
                Assert.That(instance.GetChild(0).localScale, Is.EqualTo(new Vector3(2f, 3f, 4f)));

                var fallbackSink = new EveUnityGameObjectPlayableWorldSceneSink(
                    root.transform,
                    new FixedGameObjectAssetProvider(null));
                fallbackSink.UpsertEntity(
                    PlayableEntityModel("fallback", "", 7f),
                    new EveUnityPlayableWorldAssetBinding("", "ship", "unity-generated-placeholder"));
                Assert.That(root.transform.GetChild(1).localScale, Is.EqualTo(Vector3.one * 7f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PlayableWorldPresenterInstantiatesUpdatesAndDespawnsProviderEntities()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var sink = new FakePlayableWorldSceneSink();
            var presenter = new EveUnityPlayableWorldPresenter(sink, new EveUnityAssetRefResolver());

            var firstProjection = lowerer.Lower(PlayableArpgDocument(), Advertisement("aetheria.daemon.game"));
            var firstPresentation = presenter.Apply(SnapshotWorld(firstProjection.PlayableWorld!));

            Assert.That(firstPresentation.WorldRootId, Is.EqualTo("aetheria.daemon.game.playable"));
            Assert.That(firstPresentation.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(firstPresentation.InputProfile, Is.EqualTo("arpg-third-person"));
            Assert.That(firstPresentation.CameraRig, Is.EqualTo("planar.top-down-follow.v1"));
            Assert.That(firstPresentation.UpsertedEntities, Is.EqualTo(3));
            Assert.That(firstPresentation.RemovedEntities, Is.EqualTo(0));
            Assert.That(firstPresentation.ActiveEntities, Is.EqualTo(3));
            Assert.That(sink.ConfiguredWorlds.Count, Is.EqualTo(1));
            Assert.That(sink.Upserts.Count, Is.EqualTo(3));
            Assert.That(sink.Upserts[0].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sink.Upserts[0].entity.PositionX, Is.EqualTo(0f));
            Assert.That(sink.Upserts[0].asset.AssetRef, Is.EqualTo("cultmesh://aetheria/assets/map/entity/player"));
            Assert.That(sink.Upserts[0].asset.PresentationKind, Is.EqualTo("provider-asset-ref"));

            var secondProjection = lowerer.Lower(
                PlayableArpgDocument(includeRaider: false, playerPosition: "5,0,2"),
                Advertisement("aetheria.daemon.game"));
            var secondPresentation = presenter.Apply(SnapshotWorld(secondProjection.PlayableWorld!));

            Assert.That(secondPresentation.UpsertedEntities, Is.EqualTo(2));
            Assert.That(secondPresentation.RemovedEntities, Is.EqualTo(1));
            Assert.That(secondPresentation.ActiveEntities, Is.EqualTo(2));
            Assert.That(sink.ConfiguredWorlds.Count, Is.EqualTo(2));
            Assert.That(sink.RemovedEntityIds.Count, Is.EqualTo(1));
            Assert.That(sink.RemovedEntityIds[0], Is.EqualTo("raider-scout"));
            Assert.That(sink.Upserts[3].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sink.Upserts[3].entity.PositionX, Is.EqualTo(5f));
            Assert.That(sink.Upserts[3].entity.PositionZ, Is.EqualTo(2f));
        }

        [Test]
        public void PlayableWorldSurfaceCannotOverwriteLiveBodyEntities()
        {
            var projection = new EveUnitySceneSurfaceLowerer()
                .Lower(PlayableArpgDocument(), Advertisement("aetheria.daemon.game"));
            var sink = new FakePlayableWorldSceneSink();
            var presenter = new EveUnityPlayableWorldPresenter(
                sink,
                new EveUnityAssetRefResolver(),
                liveEntityBodyOwner: true);

            var presentation = presenter.Apply(projection);

            Assert.That(projection.PlayableWorld!.EntityBodyId, Is.Not.Empty);
            Assert.That(sink.ConfiguredWorlds.Count, Is.EqualTo(1));
            Assert.That(sink.Upserts, Is.Empty);
            Assert.That(sink.RemovedEntityIds, Is.Empty);
            Assert.That(presentation.UpsertedEntities, Is.Zero);
            Assert.That(presentation.RemovedEntities, Is.Zero);
        }

        [Test]
        public void LivePlayableWorldClientPresentsProviderSnapshotsAndKeepsCommandsProviderOwned()
        {
            var source = new FakeProviderSurfaceSource(
                "aetheria",
                "aetheria.daemon.game",
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                new EveUnitySceneProviderSurfaceSnapshot(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1));
            var commandSink = new FakeCommandSink("cultmesh-command-sink");
            var sceneSink = new FakePlayableWorldSceneSink();
            using var connection = new EveUnitySceneProviderConnection(source, commandSink);
            using var client = new EveUnityPlayableWorldLiveClient(
                connection,
                new EveUnityPlayableWorldPresenter(sceneSink, new EveUnityAssetRefResolver()));

            var initialPresentation = client.Connect();

            Assert.That(initialPresentation.ActiveEntities, Is.EqualTo(3));
            Assert.That(initialPresentation.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(client.ActiveWorld, Is.Not.Null);
            Assert.That(client.ActiveWorld!.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(1));
            Assert.That(sceneSink.Upserts.Count, Is.EqualTo(3));

            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(includeRaider: false, playerPosition: "7,0,4"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));

            Assert.That(client.ActiveVersion, Is.EqualTo(2));
            Assert.That(client.LastPresentation, Is.Not.Null);
            Assert.That(client.LastPresentation!.ActiveEntities, Is.EqualTo(2));
            Assert.That(client.LastPresentation.RemovedEntities, Is.EqualTo(1));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(2));
            Assert.That(sceneSink.RemovedEntityIds[0], Is.EqualTo("raider-scout"));
            Assert.That(sceneSink.Upserts[3].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sceneSink.Upserts[3].entity.PositionX, Is.EqualTo(7f));
            Assert.That(sceneSink.Upserts[3].entity.PositionZ, Is.EqualTo(4f));

            var moveIntent = client.SubmitMoveIntent(
                "player-vanguard",
                12f,
                0f,
                8f,
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(commandSink.Submitted.Count, Is.EqualTo(1));
            Assert.That(commandSink.Submitted[0], Is.SameAs(moveIntent));
            Assert.That(moveIntent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(moveIntent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(moveIntent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(moveIntent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));

            client.Disconnect();
            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(playerPosition: "99,0,99"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                3));

            Assert.That(client.ActiveVersion, Is.EqualTo(2));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(2));
        }

        [Test]
        public void LivePlayableWorldClientReconcilesReceiptAfterReactiveStateVersionAdvances()
        {
            var source = new FakeProviderSurfaceSource(
                "aetheria",
                "aetheria.daemon.game",
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                new EveUnitySceneProviderSurfaceSnapshot(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1));
            var commandSink = new FakeCommandSink("cultmesh-command-sink");
            var receiptSource = new FakeCommandReceiptSource();
            var sceneSink = new FakePlayableWorldSceneSink();
            using var connection = new EveUnitySceneProviderConnection(source, commandSink);
            using var client = new EveUnityPlayableWorldLiveClient(
                connection,
                new EveUnityPlayableWorldPresenter(sceneSink, new EveUnityAssetRefResolver()),
                receiptSource);

            client.Connect();
            client.SubmitMoveIntent("player-vanguard", 30f, 0f, 30f);

            Assert.That(sceneSink.Upserts[0].entity.PositionX, Is.EqualTo(0f));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(1));

            receiptSource.Publish(new EveUnitySceneCommandReceipt(
                "aetheria.daemon.move_intent.pending",
                "aetheria.daemon.commands",
                "aetheria.daemon.move_intent",
                "pending",
                "Aetheria",
                "AetheriaRuntimeDaemonCommandBoundaryDocument",
                "aetheria.eve_command_acceptance_status.v1",
                "aetheria",
                "aetheria.daemon.game"));

            Assert.That(client.LastReceipt, Is.Not.Null);
            Assert.That(client.LastReceipt!.State, Is.EqualTo("pending"));
            Assert.That(client.LastReceipt.IsProviderOwned, Is.True);
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(1));

            receiptSource.Publish(new EveUnitySceneCommandReceipt(
                "aetheria.daemon.move_intent.reconciled",
                "aetheria.daemon.commands",
                "aetheria.daemon.move_intent",
                "reconciled",
                "Aetheria",
                "AetheriaRuntimeDaemonCommandBoundaryDocument",
                "gamecult.eve.command_receipt.v1",
                "aetheria",
                "aetheria.daemon.game",
                sourceVersion: 2));

            Assert.That(client.LastReceipt.State, Is.EqualTo("pending"));
            Assert.That(client.ActiveVersion, Is.EqualTo(1));

            client.AdvanceStateVersion(2);

            Assert.That(client.LastReceipt.State, Is.EqualTo("reconciled"));
            Assert.That(client.ActiveVersion, Is.EqualTo(2));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(1),
                "reactive state visibility must not require republishing heavy surface topology");

            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(includeRaider: false, playerPosition: "30,0,30"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));
            Assert.That(client.ActiveVersion, Is.EqualTo(2));
            Assert.That(client.LastPresentation, Is.Not.Null);
            Assert.That(client.LastPresentation!.ActiveEntities, Is.EqualTo(2));
            Assert.That(sceneSink.ConfiguredWorlds.Count, Is.EqualTo(2));
            Assert.That(sceneSink.Upserts[3].entity.EntityId, Is.EqualTo("player-vanguard"));
            Assert.That(sceneSink.Upserts[3].entity.PositionX, Is.EqualTo(30f));
            Assert.That(sceneSink.Upserts[3].entity.PositionZ, Is.EqualTo(30f));
        }

        [Test]
        public void AssetManifestMapsProviderAssetRefsToUnityLoadKeysWithoutAetheriaTypes()
        {
            var manifest = new EveUnityPlayableWorldAssetManifest(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "resources://Aetheria/Entities/Vanguard.prefab",
                        "aetheria.vanguard"),
                    new EveUnityPlayableWorldAssetManifestEntry(
                        "",
                        "enemy",
                        "Resources/Aetheria/Entities/Raider.prefab",
                        "aetheria.raider")
                });

            var player = manifest.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(player, Is.Not.Null);
            Assert.That(player!.ResourcesPath, Is.EqualTo("Aetheria/Entities/Vanguard"));
            Assert.That(player.PrefabKey, Is.EqualTo("aetheria.vanguard"));

            var enemy = manifest.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/missing-raider",
                "enemy",
                "provider-asset-ref"));
            Assert.That(enemy, Is.Not.Null);
            Assert.That(enemy!.ResourcesPath, Is.EqualTo("Aetheria/Entities/Raider"));
            Assert.That(enemy.PrefabKey, Is.EqualTo("aetheria.raider"));
        }

        [Test]
        public void AssetManifestCacheTracksPlayableWorldManifestPointerAndLiveUpdates()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(PlayableArpgDocument(), Advertisement("aetheria.daemon.game"));
            Assert.That(projection.PlayableWorld, Is.Not.Null);

            var source = new FakeAssetManifestSource(new EveUnityPlayableWorldAssetManifest(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "resources://Aetheria/Entities/Vanguard.prefab",
                        "aetheria.vanguard")
                }));
            var cache = new EveUnityPlayableWorldAssetManifestCache();
            cache.Connect(source);

            var manifest = cache.GetForWorld(projection.PlayableWorld!);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest!.ManifestRef, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(cache.Count, Is.EqualTo(1));
            var player = manifest.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(player, Is.Not.Null);
            Assert.That(player!.ResourcesPath, Is.EqualTo("Aetheria/Entities/Vanguard"));

            source.Publish(new EveUnityPlayableWorldAssetManifest(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "resources://Aetheria/Entities/VanguardMk2.prefab",
                        "aetheria.vanguard.mk2")
                }));

            var updated = cache.GetForWorld(projection.PlayableWorld);
            Assert.That(updated, Is.Not.Null);
            var updatedPlayer = updated!.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(updatedPlayer, Is.Not.Null);
            Assert.That(updatedPlayer!.ResourcesPath, Is.EqualTo("Aetheria/Entities/VanguardMk2"));

            cache.Disconnect(source);
            source.Publish(new EveUnityPlayableWorldAssetManifest(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "resources://Aetheria/Entities/VanguardIgnored.prefab",
                        "aetheria.vanguard.ignored")
                }));

            var afterDisconnect = cache.GetForWorld(projection.PlayableWorld);
            var afterDisconnectPlayer = afterDisconnect!.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(afterDisconnectPlayer!.ResourcesPath, Is.EqualTo("Aetheria/Entities/VanguardMk2"));
        }

        [Test]
        public void AssetManifestDocumentSourceFeedsCacheWithoutChangingSceneLowering()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(PlayableArpgDocument(), Advertisement("aetheria.daemon.game"));
            Assert.That(projection.PlayableWorld, Is.Not.Null);

            var documentSource = new FakeAssetManifestDocumentSource(new EveUnityPlayableWorldAssetManifestDocument(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestDocumentEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "resources://Aetheria/Entities/Vanguard.prefab",
                        "aetheria.vanguard")
                },
                "aetheria"));
            using var manifestSource = new EveUnityPlayableWorldAssetManifestDocumentSource(documentSource);
            var cache = new EveUnityPlayableWorldAssetManifestCache();
            manifestSource.Connect();
            cache.Connect(manifestSource);

            var manifest = cache.GetForWorld(projection.PlayableWorld!);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest!.ManifestRef, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            var player = manifest.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(player, Is.Not.Null);
            Assert.That(player!.ResourcesPath, Is.EqualTo("Aetheria/Entities/Vanguard"));
            Assert.That(player.PrefabKey, Is.EqualTo("aetheria.vanguard"));

            documentSource.Publish(new EveUnityPlayableWorldAssetManifestDocument(
                "cultmesh://aetheria/assets/manifest",
                new[]
                {
                    new EveUnityPlayableWorldAssetManifestDocumentEntry(
                        "cultmesh://aetheria/assets/map/entity/player",
                        "player",
                        "Resources/Aetheria/Entities/VanguardAuthority.prefab",
                        "aetheria.vanguard.authority")
                },
                "aetheria"));

            var updated = cache.GetForWorld(projection.PlayableWorld);
            var updatedPlayer = updated!.Find(new EveUnityPlayableWorldAssetBinding(
                "cultmesh://aetheria/assets/map/entity/player",
                "player",
                "provider-asset-ref"));
            Assert.That(updatedPlayer, Is.Not.Null);
            Assert.That(updatedPlayer!.ResourcesPath, Is.EqualTo("Aetheria/Entities/VanguardAuthority"));
            Assert.That(updatedPlayer.PrefabKey, Is.EqualTo("aetheria.vanguard.authority"));
        }

        [Test]
        public void SaiVisualNovelLowersThroughRuntimeProjectionAdapterWithoutOwningStoryState()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var projection = lowerer.Lower(SaiDocument(), Advertisement("sai.visual_novel.surface"));

            Assert.That(projection.ProviderId, Is.EqualTo("gamecult.home.vn"));
            Assert.That(projection.SurfaceId, Is.EqualTo("sai.visual_novel.surface"));
            Assert.That(projection.Root.SceneObjectKind, Is.EqualTo("sai-vn-scene-stage"));
            Assert.That(projection.Root.PluginProjection, Is.Not.Null);
            Assert.That(projection.Root.PluginProjection!.PluginId, Is.EqualTo("sai.vn"));
            Assert.That(projection.Root.PluginProjection.ProjectionKind, Is.EqualTo("sai-vn-scene-stage-shell"));
            Assert.That(projection.Root.PluginProjection.AbiSchema, Is.EqualTo("gamecult.eve.plugin_abi.v1"));
            Assert.That(projection.Root.PluginProjection.CommandBoundary, Is.EqualTo("sidecar-advertised-plugin-abi"));
            Assert.That(projection.Root.PluginProjection.Capabilities, Does.Contain("vn.stage"));
            Assert.That(projection.Root.PluginProjection.Capabilities, Does.Contain("story.choose"));
            Assert.That(projection.Root.PluginProjection.DocumentId, Is.EqualTo("gamecult-compound"));
            Assert.That(projection.Root.PluginProjection.SemanticOwner, Is.EqualTo("Sai"));

            var dialogue = projection.Root.Children[0];
            Assert.That(dialogue.SceneObjectKind, Is.EqualTo("sai-vn-scene-dialogue"));
            Assert.That(dialogue.PluginProjection!.ProjectionKind, Is.EqualTo("sai-vn-scene-dialogue-shell"));
            Assert.That(dialogue.PluginProjection.SemanticOwner, Is.EqualTo("Sai"));

            var choice = projection.Root.Children[1].Children[0];
            Assert.That(choice.SceneObjectKind, Is.EqualTo("command-control"));
            Assert.That(choice.PluginProjection, Is.Not.Null);
            Assert.That(choice.PluginProjection!.PluginId, Is.EqualTo("sai.vn"));
            Assert.That(choice.PluginProjection.ProjectionKind, Is.EqualTo("sai-vn-scene-story-command-shell"));
            Assert.That(choice.PluginProjection.Command, Is.EqualTo("story.choose"));
            Assert.That(choice.PluginProjection.SemanticOwner, Is.EqualTo("Sai"));

            var tex = projection.Root.Children[2];
            Assert.That(tex.SceneObjectKind, Is.EqualTo("tex-math-scene-projection"));
            Assert.That(tex.PluginProjection, Is.Not.Null);
            Assert.That(tex.PluginProjection!.PluginId, Is.EqualTo("tex.math"));
            Assert.That(tex.PluginProjection.ProjectionKind, Is.EqualTo("tex-math-scene-block-fallback-shell"));
            Assert.That(tex.PluginProjection.Capabilities, Does.Contain("embed.tex"));
            Assert.That(tex.PluginProjection.Capabilities, Does.Contain("tex.scene-placement"));
            Assert.That(tex.PluginProjection.DocumentId, Is.EqualTo("\\\\mathrm{votes}(p)=1+\\\\lfloor\\\\log_b(1+p)\\\\rfloor"));
            Assert.That(tex.PluginProjection.SemanticOwner, Is.EqualTo("EvePlugins"));
        }

        [Test]
        public void CommandIntentCarriesAdvertisedBoundaryWithoutOwningReceipts()
        {
            var lowerer = new EveUnitySceneSurfaceLowerer();
            var intent = lowerer.CreateCommandIntent(
                Document("aetheria.daemon.game"),
                Advertisement("aetheria.daemon.game"),
                "aetheria.daemon.commands",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["commandId"] = "aetheria.daemon.focus"
                },
                DateTimeOffset.Parse("2026-07-09T00:00:00Z"));

            Assert.That(intent.Schema, Is.EqualTo(EveSurfaceCommandRequest.SchemaId));
            Assert.That(intent.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(intent.SurfaceId, Is.EqualTo("aetheria.daemon.game"));
            Assert.That(intent.Command, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(intent.ClientId, Is.EqualTo("unity-scene"));
            Assert.That(intent.CommandBoundary, Is.EqualTo("aetheria.daemon.commands"));
            Assert.That(intent.ReceiptSchema, Is.EqualTo("aetheria.eve_command_acceptance_status.v1"));
        }

        private static EveSurfaceDocument Document(string surfaceId)
        {
            return new EveSurfaceDocument(
                "aetheria",
                "game.provider",
                "Aetheria world",
                1,
                "2026-07-09T00:00:00Z",
                new EveSurfaceTree(
                    surfaceId,
                    new EveSurfaceComponent(
                        $"{surfaceId}.root",
                        "surface",
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        new[]
                        {
                            new EveSurfaceComponent(
                                $"{surfaceId}.entities",
                                "world.entities",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["binding"] = "cultmesh://aetheria/world/entities"
                                },
                                Array.Empty<EveSurfaceComponent>(),
                                new[]
                                {
                                    new CultMeshStateBindingDescriptor("entities", "cultmesh://aetheria/world/entities")
                                }),
                            new EveSurfaceComponent(
                                $"{surfaceId}.focus",
                                "control.button",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["command"] = "aetheria.daemon.focus"
                                },
                                Array.Empty<EveSurfaceComponent>()),
                            new EveSurfaceComponent(
                                $"{surfaceId}.norn",
                                "embed.norn",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["graph.document"] = "cultmesh://aetheria/norn/map",
                                    ["interaction.nodeAction"] = "graph.focus"
                                },
                                Array.Empty<EveSurfaceComponent>(),
                                Array.Empty<CultMeshStateBindingDescriptor>(),
                                new[]
                                {
                                    new EveEmbeddedDocumentSlot(
                                        "norn.map",
                                        "cultmesh://aetheria/norn/map",
                                        "gamecult.eve.surface.v1",
                                        "scene-overlay")
                                })
                        }),
                    Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());
        }

        private static EveSurfaceDocument PlayableArpgDocument(
            bool includeRaider = true,
            string playerPosition = "0,0,0",
            string skyboxAssetRef = "",
            string reflectionAssetRef = "",
            string postProcessProfileAssetRef = "",
            string cameraRig = "planar.top-down-follow.v1",
            string cameraDistance = "150",
            string cameraTargetScreenX = "0.9",
            string cameraTargetScreenY = "0.55",
            string cameraPositionDamping = "5",
            string cameraNearClipPlane = "0.3",
            string cameraFarClipPlane = "4096",
            string cameraLookAt = "",
            string cameraReconstruction = "",
            string temporalQuality = "high",
            string temporalHistoryBlend = "0",
            string temporalJitterScale = "0",
            string temporalSharpening = "0",
            string exposureMode = "",
            string exposureLowPercent = "50",
            string exposureHighPercent = "95",
            string exposureMinimumEv = "0",
            string exposureMaximumEv = "0",
            string exposureKeyValue = "1",
            string exposureAdaptation = "progressive",
            string exposureSpeedUp = "2",
            string exposureSpeedDown = "1",
            string colorGradingSpace = "")
        {
            var playableChildren = new List<EveSurfaceComponent>
            {
                PlayableEntity(
                    "aetheria.daemon.game.entity.player",
                    "player-vanguard",
                    "player",
                    "Vanguard",
                    "player",
                    "cultmesh://aetheria/assets/map/entity/player",
                    playerPosition,
                    "35",
                    "1.15",
                    true,
                    true,
                    "aetheria.daemon.focus",
                    "aetheria.daemon.move_intent",
                    "",
                    "")
            };

            if (includeRaider)
            {
                playableChildren.Add(PlayableEntity(
                    "aetheria.daemon.game.entity.raider",
                    "raider-scout",
                    "enemy",
                    "Raider Scout",
                    "raider",
                    "cultmesh://aetheria/assets/map/entity/ship",
                    "9,0,14",
                    "220",
                    "0.9",
                    true,
                    false,
                    "",
                    "",
                    "aetheria.daemon.target",
                    ""));
            }

            playableChildren.Add(PlayableEntity(
                "aetheria.daemon.game.entity.station",
                "anchor-station",
                "station",
                "Anchor Station",
                "neutral",
                "cultmesh://aetheria/assets/map/entity/station",
                "-18,0,6",
                "0",
                "3.4",
                true,
                false,
                "aetheria.daemon.focus",
                "",
                "",
                ""));

            playableChildren.Add(new EveSurfaceComponent(
                "aetheria.daemon.game.aim",
                "aim.presentation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["controlledEntityId"] = "player-vanguard",
                    ["convergenceTargetEntityId"] = "",
                    ["viewDotRole"] = "pilot.view-direction",
                    ["minimumConvergenceDistance"] = "50",
                    ["viewDotRadius"] = "0.8"
                },
                Array.Empty<EveSurfaceComponent>()));

            playableChildren.Add(new EveSurfaceComponent(
                "aetheria.daemon.game.combat",
                "combat.presentation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["controlledEntityId"] = "player-vanguard",
                    ["selectedTargetEntityId"] = "raider-scout",
                    ["targetVisible"] = "true",
                    ["targetHostile"] = "true"
                },
                Array.Empty<EveSurfaceComponent>()));

            playableChildren.Add(new EveSurfaceComponent(
                "aetheria.daemon.game.flow",
                "field.vector3d",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fieldId"] = "aetheria.zone.flow",
                    ["bind"] = "aetheria.daemon.soaView.flowField3d",
                    ["visualizer"] = "particles",
                    ["bounds"] = "-32,0,-32,32,24,32"
                },
                Array.Empty<EveSurfaceComponent>()));

            playableChildren.Add(new EveSurfaceComponent(
                "aetheria.daemon.game.gravity-fog",
                "field.volume3d",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["documentRef"] = "cultmesh://aetheria/world/fog-splats",
                    ["documentSchema"] = EveFieldsSchemas.Splats,
                    ["materialAssetRef"] = "shader.environment.gravity-fog",
                    ["renderChannel"] = "world.transparent",
                    ["compositeMode"] = "premultiplied-alpha",
                    ["quality"] = "normal",
                    ["layerBindings"] = "fog.surface_height=surfaceHeight;fog.tint=tint"
                },
                Array.Empty<EveSurfaceComponent>()));

            playableChildren.Add(new EveSurfaceComponent(
                "aetheria.daemon.game.stardust",
                "field.particles3d",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["documentRef"] = "cultmesh://aetheria/world/fog-splats",
                    ["documentSchema"] = EveFieldsSchemas.Splats,
                    ["computeProgramAssetRef"] = "compute.environment.stardust",
                    ["materialAssetRef"] = "material.environment.stardust",
                    ["renderChannel"] = "world.transparent",
                    ["span"] = "256",
                    ["spacing"] = "6"
                },
                Array.Empty<EveSurfaceComponent>()));

            return new EveSurfaceDocument(
                "aetheria",
                "game.runtime",
                "Aetheria daemon game",
                1,
                "2026-07-09T00:00:00Z",
                new EveSurfaceTree(
                    "aetheria.daemon.game",
                    new EveSurfaceComponent(
                        "aetheria.daemon.game.root",
                        "surface",
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        new[]
                        {
                            new EveSurfaceComponent(
                                "aetheria.daemon.game.playable",
                                "world.scene3d",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["statePointerId"] = "cultmesh://aetheria/run/current",
                                    ["entityViewPointerId"] = "cultmesh://aetheria/world/entities.soa",
                                    ["entityViewSchema"] = EveEntitySoaViewDocument.SchemaId,
                                    ["entityBodyId"] = "eve:entity-soa:aetheria.daemon:pilot",
                                    ["zoneRenderPointerId"] = "cultmesh://aetheria/world/zone-render",
                                    ["zoneRenderSchema"] = "gamecult.aetheria.zone_render.v1",
                                    ["assetManifest"] = "cultmesh://aetheria/assets/manifest",
                                    ["inputProfile"] = "arpg-third-person",
                                    ["cameraRig"] = cameraRig,
                                    ["cameraLookAt"] = cameraLookAt,
                                    ["cameraTargetEntityId"] = "player-vanguard",
                                    ["cameraDistance"] = cameraDistance,
                                    ["cameraVerticalFieldOfViewDegrees"] = "60",
                                    ["cameraTargetScreenX"] = cameraTargetScreenX,
                                    ["cameraTargetScreenY"] = cameraTargetScreenY,
                                    ["cameraPositionDamping"] = cameraPositionDamping,
                                    ["cameraNearClipPlane"] = cameraNearClipPlane,
                                    ["cameraFarClipPlane"] = cameraFarClipPlane,
                                    ["ambientLightColor"] = "0.2,0.2,0.2",
                                    ["ambientLightIntensity"] = "1.46",
                                    ["skyboxAssetRef"] = skyboxAssetRef,
                                    ["reflectionAssetRef"] = reflectionAssetRef,
                                    ["reflectionIntensity"] = "1",
                                    ["postProcessProfileAssetRef"] = postProcessProfileAssetRef,
                                    ["cameraReconstruction"] = cameraReconstruction,
                                    ["temporalQuality"] = temporalQuality,
                                    ["temporalHistoryBlend"] = temporalHistoryBlend,
                                    ["temporalJitterScale"] = temporalJitterScale,
                                    ["temporalSharpening"] = temporalSharpening,
                                    ["exposureMode"] = exposureMode,
                                    ["exposureLowPercent"] = exposureLowPercent,
                                    ["exposureHighPercent"] = exposureHighPercent,
                                    ["exposureMinimumEv"] = exposureMinimumEv,
                                    ["exposureMaximumEv"] = exposureMaximumEv,
                                    ["exposureKeyValue"] = exposureKeyValue,
                                    ["exposureAdaptation"] = exposureAdaptation,
                                    ["exposureSpeedUp"] = exposureSpeedUp,
                                    ["exposureSpeedDown"] = exposureSpeedDown,
                                    ["colorGradingSpace"] = colorGradingSpace,
                                    ["keyLightDirection"] = "0.4,-1,0.25",
                                    ["keyLightColor"] = "1,0.95,0.9",
                                    ["keyLightIntensity"] = "0.75",
                                    ["excludedRenderChannels"] = "map",
                                    ["playerEntityId"] = "player-vanguard",
                                    ["movementCommand"] = "aetheria.daemon.move_intent",
                                    ["lookCommand"] = "aetheria.daemon.look_intent",
                                    ["lookModel"] = "planar-yaw.v1",
                                    ["lookSensitivityRadians"] = "-0.001",
                                    ["focusCommand"] = "aetheria.daemon.focus",
                                    ["targetCommand"] = "aetheria.daemon.target",
                                    ["actionCommand"] = "aetheria.daemon.use_equipment"
                                },
                                playableChildren,
                                new[]
                                {
                                    new CultMeshStateBindingDescriptor("run", "cultmesh://aetheria/run/current"),
                                    new CultMeshStateBindingDescriptor("assets", "cultmesh://aetheria/assets/manifest")
                                })
                        }),
                    Array.Empty<EveStyleToken>()),
                new[]
                {
                    new EveCommandTemplate(CultMesh.OperationBinding("aetheria.daemon.commands", "Aetheria daemon commands"))
                });
        }

        private static EveSurfaceComponent PlayableEntity(
            string nodeId,
            string entityId,
            string entityKind,
            string label,
            string faction,
            string assetRef,
            string position,
            string rotationY,
            string radius,
            bool selectable,
            bool controllable,
            string focusCommand,
            string moveCommand,
            string targetCommand,
            string actionCommand)
        {
            return new EveSurfaceComponent(
                nodeId,
                "world.entity3d",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["entityId"] = entityId,
                    ["entityKind"] = entityKind,
                    ["label"] = label,
                    ["faction"] = faction,
                    ["assetRef"] = assetRef,
                    ["position"] = position,
                    ["rotationY"] = rotationY,
                    ["radius"] = radius,
                    ["presentationState"] = "active",
                    ["selectable"] = selectable ? "true" : "false",
                    ["controllable"] = controllable ? "true" : "false",
                    ["focusCommand"] = focusCommand,
                    ["moveCommand"] = moveCommand,
                    ["targetCommand"] = targetCommand,
                    ["actionCommand"] = actionCommand
                },
                Array.Empty<EveSurfaceComponent>());
        }

        private static EveUnityPlayableWorldEntity FindEntity(
            EveUnityPlayableWorldProjection playableWorld,
            string entityId)
        {
            foreach (var entity in playableWorld.Entities)
            {
                if (string.Equals(entity.EntityId, entityId, StringComparison.Ordinal))
                    return entity;
            }
            throw new AssertionException($"Playable entity not found: {entityId}");
        }

        private static EveSurfaceDocument SaiDocument()
        {
            return new EveSurfaceDocument(
                "gamecult.home.vn",
                "sai.visual_novel",
                "GameCult Compound VN",
                1,
                "2026-07-09T00:00:00Z",
                new EveSurfaceTree(
                    "sai.visual_novel.surface",
                    new EveSurfaceComponent(
                        "sai.root",
                        "vn.stage",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["storyId"] = "gamecult-compound",
                            ["currentPath"] = "hub"
                        },
                        new[]
                        {
                            new EveSurfaceComponent(
                                "sai.dialogue",
                                "panel.dialogue",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["speaker"] = "Void",
                                    ["text"] = "Pick a door."
                                },
                                Array.Empty<EveSurfaceComponent>()),
                            new EveSurfaceComponent(
                                "sai.choices",
                                "rail.actions",
                                new Dictionary<string, string>(StringComparer.Ordinal),
                                new[]
                                {
                                    new EveSurfaceComponent(
                                        "sai.choice.eve",
                                        "control.button",
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["label"] = "What is Eve?",
                                            ["action.command"] = "story.choose",
                                            ["targetPath"] = "eve"
                                        },
                                        Array.Empty<EveSurfaceComponent>())
                                }),
                            new EveSurfaceComponent(
                                "sai.tex.log-power",
                                "embed.tex",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["label"] = "Bifrost voting weight",
                                    ["source"] = "\\\\mathrm{votes}(p)=1+\\\\lfloor\\\\log_b(1+p)\\\\rfloor",
                                    ["format"] = "latex",
                                    ["display"] = "block"
                                },
                                Array.Empty<EveSurfaceComponent>())
                        }),
                    Array.Empty<EveStyleToken>()),
                new[]
                {
                    new EveCommandTemplate(CultMesh.OperationBinding("story.choose", "Choose"))
                });
        }

        private static EveUnitySceneProviderSurfaceAdvertisement Advertisement(string surfaceId)
        {
            return new EveUnitySceneProviderSurfaceAdvertisement(
                surfaceId,
                "interactive-world",
                new EveUnitySceneWorldInteraction(
                    "provider-authored-world-surface",
                    "aetheria.daemon.commands",
                    "aetheria.eve_command_acceptance_status.v1",
                    "provider-owns-world-state-assets-command-acceptance-and-receipts"));
        }

        private sealed class FakeProviderSurfaceSource : IEveUnitySceneProviderSurfaceSource
        {
            public FakeProviderSurfaceSource(
                string providerId,
                string surfaceId,
                string sourcePointer,
                EveUnitySceneProviderSurfaceSnapshot currentSnapshot)
            {
                ProviderId = providerId;
                SurfaceId = surfaceId;
                SourcePointer = sourcePointer;
                CurrentSnapshot = currentSnapshot;
            }

            public string ProviderId { get; }

            public string SurfaceId { get; }

            public string SourcePointer { get; }

            public EveUnitySceneProviderSurfaceSnapshot CurrentSnapshot { get; private set; }

            public event Action<EveUnitySceneProviderSurfaceSnapshot>? SnapshotAvailable;

            public void Connect()
            {
            }

            public void Refresh()
            {
            }

            public void Disconnect()
            {
            }

            public void Stage(EveUnitySceneProviderSurfaceSnapshot snapshot)
            {
                CurrentSnapshot = snapshot;
            }

            public void Publish(EveUnitySceneProviderSurfaceSnapshot snapshot)
            {
                CurrentSnapshot = snapshot;
                SnapshotAvailable?.Invoke(snapshot);
            }
        }

        private sealed class FakeProviderSurfaceDocumentSource : IEveUnitySceneProviderSurfaceDocumentSource
        {
            public FakeProviderSurfaceDocumentSource(EveUnitySceneProviderSurfaceDocument currentDocument)
            {
                CurrentDocument = currentDocument;
            }

            public EveUnitySceneProviderSurfaceDocument CurrentDocument { get; private set; }

            public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable;

            public void Publish(EveUnitySceneProviderSurfaceDocument document)
            {
                CurrentDocument = document;
                DocumentAvailable?.Invoke(document);
            }
        }

        private sealed class FakeCommandReceiptSource : IEveUnitySceneCommandReceiptSource
        {
            public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;

            public void Publish(EveUnitySceneCommandReceipt receipt)
            {
                ReceiptAvailable?.Invoke(receipt);
            }
        }

        private sealed class FakeCommandSink : IEveUnitySceneCommandSink
        {
            public FakeCommandSink(string sinkKind)
            {
                SinkKind = sinkKind;
            }

            public string SinkKind { get; }

            public List<EveSurfaceCommandRequest> Submitted { get; } = new List<EveSurfaceCommandRequest>();

            public void Submit(EveSurfaceCommandRequest request)
            {
                Submitted.Add(request);
            }
        }

        private sealed class FakePlayableWorldSceneSink : IEveUnityPlayableWorldSceneSink
        {
            public List<EveUnityPlayableWorldProjection> ConfiguredWorlds { get; } = new List<EveUnityPlayableWorldProjection>();

            public List<(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)> Upserts { get; } =
                new List<(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)>();

            public List<string> RemovedEntityIds { get; } = new List<string>();

            public void ConfigureWorld(EveUnityPlayableWorldProjection world)
            {
                ConfiguredWorlds.Add(world);
            }

            public void UpsertEntity(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)
            {
                Upserts.Add((entity, asset));
            }

            public void RemoveEntity(string entityId)
            {
                RemovedEntityIds.Add(entityId);
            }
        }

        private sealed class FakeAssetManifestSource : IEveUnityPlayableWorldAssetManifestSource
        {
            public FakeAssetManifestSource(EveUnityPlayableWorldAssetManifest currentManifest)
            {
                CurrentManifest = currentManifest;
            }

            public string ManifestRef => CurrentManifest.ManifestRef;

            public EveUnityPlayableWorldAssetManifest CurrentManifest { get; private set; }

            public event Action<EveUnityPlayableWorldAssetManifest>? ManifestAvailable;

            public void Publish(EveUnityPlayableWorldAssetManifest manifest)
            {
                CurrentManifest = manifest;
                ManifestAvailable?.Invoke(manifest);
            }
        }

        private sealed class FakeAssetManifestDocumentSource : IEveUnityPlayableWorldAssetManifestDocumentSource
        {
            public FakeAssetManifestDocumentSource(EveUnityPlayableWorldAssetManifestDocument currentDocument)
            {
                CurrentDocument = currentDocument;
            }

            public string ManifestRef => CurrentDocument.ManifestRef;

            public EveUnityPlayableWorldAssetManifestDocument CurrentDocument { get; private set; }

            public event Action<EveUnityPlayableWorldAssetManifestDocument>? DocumentAvailable;

            public void Publish(EveUnityPlayableWorldAssetManifestDocument document)
            {
                CurrentDocument = document;
                DocumentAvailable?.Invoke(document);
            }
        }

        private sealed class FakeLiveProviderTransport : IEveUnitySceneLiveProviderTransport
        {
            private Action<EveUnitySceneProviderSurfaceDocument>? _surfaceDocumentAvailable;
            private Action<EveUnityPlayableWorldAssetManifestDocument>? _assetManifestDocumentAvailable;
            private Action<EveUnitySceneCommandReceipt>? _commandReceiptAvailable;

            public FakeLiveProviderTransport(
                EveUnitySceneProviderSurfaceDocument surfaceDocument,
                EveUnityPlayableWorldAssetManifestDocument assetManifestDocument)
            {
                CurrentSurfaceDocument = surfaceDocument;
                CurrentAssetManifestDocument = assetManifestDocument;
            }

            public string TransportKind => "cultmesh-cultnet-provider-transport";

            public string SurfacePointer => CurrentSurfaceDocument.SourcePointer;

            public string AssetManifestPointer => CurrentAssetManifestDocument.ManifestRef;

            public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

            public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

            public List<EveSurfaceCommandRequest> Submitted { get; } = new List<EveSurfaceCommandRequest>();

            public int ConnectCount { get; private set; }

            public int DisconnectCount { get; private set; }

            public int RefreshCount { get; private set; }

            public bool FailNextConnect { get; set; }

            public int SurfaceSubscriberCount => _surfaceDocumentAvailable?.GetInvocationList().Length ?? 0;

            public int AssetManifestSubscriberCount => _assetManifestDocumentAvailable?.GetInvocationList().Length ?? 0;

            public int ReceiptSubscriberCount => _commandReceiptAvailable?.GetInvocationList().Length ?? 0;

            public event Action<EveUnitySceneProviderSurfaceDocument>? SurfaceDocumentAvailable
            {
                add => _surfaceDocumentAvailable += value;
                remove => _surfaceDocumentAvailable -= value;
            }

            public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable
            {
                add => _assetManifestDocumentAvailable += value;
                remove => _assetManifestDocumentAvailable -= value;
            }

            public event Action<EveUnitySceneCommandReceipt>? CommandReceiptAvailable
            {
                add => _commandReceiptAvailable += value;
                remove => _commandReceiptAvailable -= value;
            }

            public void Connect()
            {
                ConnectCount++;
                if (!FailNextConnect) return;
                FailNextConnect = false;
                throw new IOException("transient transport failure");
            }

            public void Disconnect()
            {
                DisconnectCount++;
            }

            public void Refresh()
            {
                RefreshCount++;
            }

            public void SubmitCommand(EveSurfaceCommandRequest request)
            {
                Submitted.Add(request);
            }

            public void PublishSurface(EveUnitySceneProviderSurfaceDocument document)
            {
                CurrentSurfaceDocument = document;
                _surfaceDocumentAvailable?.Invoke(document);
            }

            public void PublishAssetManifest(EveUnityPlayableWorldAssetManifestDocument document)
            {
                CurrentAssetManifestDocument = document;
                _assetManifestDocumentAvailable?.Invoke(document);
            }

            public void PublishReceipt(EveUnitySceneCommandReceipt receipt)
            {
                _commandReceiptAvailable?.Invoke(receipt);
            }
        }

        private static EveUnityPlayableWorldProjection SnapshotWorld(EveUnityPlayableWorldProjection source) =>
            new EveUnityPlayableWorldProjection(
                source.WorldRootId,
                source.StatePointerId,
                source.AssetManifest,
                source.InputProfile,
                source.CameraRig,
                source.ViewId,
                source.PlayerEntityId,
                source.MovementCommand,
                source.FocusCommand,
                source.TargetCommand,
                source.ActionCommand,
                source.Entities);

        private static EveUnityPlayableWorldEntity PlayableEntityModel(
            string entityId,
            string assetRef,
            float radius)
        {
            return new EveUnityPlayableWorldEntity(
                entityId,
                entityId,
                "ship",
                entityId,
                "player",
                assetRef,
                0f, 0f, 0f,
                0f,
                radius,
                true,
                true,
                "", "", "", "");
        }

        private static EveEntitySoaViewDocument EntityLeaseDocument() => new EveEntitySoaViewDocument
        {
            ProviderId = "provider",
            ViewId = "entities",
            BodySchemaId = "test.entity.slab.v1",
            LayoutVersion = 3,
            ProducerEpoch = 5,
            Sequence = 7,
            Capacity = 1,
            Buffers = new[] { new EveEntitySoaBuffer { BufferId = "hot", ByteLength = 4 } },
            Identities = new[]
            {
                new EveEntityIdentity
                {
                    Index = 12,
                    EntityId = "entity:pilot",
                    EntityKind = "player",
                    Label = "Pilot",
                    Faction = "alliance",
                    Selectable = true,
                    Controllable = true,
                    AssetRef = "cultmesh://assets/pilot"
                }
            },
            Columns = new[]
            {
                new EveEntitySoaColumn
                {
                    ColumnId = "entity-index",
                    Semantic = "entity.index",
                    BufferId = "hot",
                    ScalarType = "int32",
                    ElementStride = 4,
                    ElementCount = 1
                }
            }
        };

        private static CultMeshRealtimeFrame RealtimeFrame(string bodyId, long epoch, long sequence) =>
            new CultMeshRealtimeFrame
            {
                ChannelId = "state",
                BodyId = bodyId,
                SchemaId = "test.entity.slab.v1",
                ProducerEpoch = epoch,
                Sequence = sequence,
                Delivery = CultMeshRealtimeDelivery.LatestOnly,
                Payload = new byte[4]
            };

        private static EveProviderAdvertisementDocument ProviderAdvertisement(
            string providerId,
            string serviceId,
            params string[] authorizedBodyProducerIds) =>
            new EveProviderAdvertisementDocument(
                providerId,
                serviceId,
                "test-verse",
                "Test Provider",
                "test",
                "cultmesh://test/provider",
                DateTimeOffset.UtcNow.ToString("O"),
                new EveProviderFreshness("fresh", DateTimeOffset.UtcNow.ToString("O"), 5000),
                Array.Empty<string>(),
                Array.Empty<EveProviderWitness>(),
                Array.Empty<EveAdvertisedSurface>(),
                Array.Empty<EveAdvertisedCommand>(),
                authorizedBodyProducerIds);

        private static CultMeshBodyPublicationDocument BodyPublication(
            EveEntitySoaViewDocument view,
            long sequence)
        {
            var descriptor = FakeBodyReadLease.DescriptorFor(view, view.Buffers[0].ByteLength);
            descriptor.Sequence = sequence;
            descriptor.Synchronization = CultMeshBodySynchronization.ImmutableSequence;
            descriptor.LeaseExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            descriptor.TransportKind = CultMeshBodyTransportKind.SharedFileMapping;
            var network = FakeBodyReadLease.DescriptorFor(view, view.Buffers[0].ByteLength);
            network.Sequence = sequence;
            network.Synchronization = descriptor.Synchronization;
            network.LeaseExpiresAtUnixMs = descriptor.LeaseExpiresAtUnixMs;
            network.TransportKind = CultMeshBodyTransportKind.Network;
            return new CultMeshBodyPublicationDocument
            {
                BodyId = descriptor.BodyId,
                ProducerId = view.ProviderId,
                SchemaId = descriptor.SchemaId,
                LayoutVersion = descriptor.LayoutVersion,
                ByteSize = descriptor.ByteSize,
                Capacity = descriptor.Capacity,
                ProducerEpoch = descriptor.ProducerEpoch,
                Sequence = sequence,
                Synchronization = descriptor.Synchronization,
                LivenessExpiresAtUnixMs = descriptor.LeaseExpiresAtUnixMs,
                Representations = new[] { descriptor, network }
            };
        }

        private static EveEntitySoaColumn SoaColumn(
            string semantic,
            string scalarType,
            long byteOffset,
            int stride) => new EveEntitySoaColumn
        {
            ColumnId = semantic,
            Semantic = semantic,
            BufferId = "hot",
            ScalarType = scalarType,
            ByteOffset = byteOffset,
            ElementStride = stride,
            ElementCount = 1
        };

        private static EveUnityPresentedEntity PresentedEntity(int index, string entityId, string assetRef) =>
            new EveUnityPresentedEntity(
                index, entityId, "entity", entityId, "", Vector3.zero, 0f, Vector3.zero,
                1f, 1f, 0, 0, true, false, assetRef);

        private sealed class FakeBodyReadLease : ICultMeshBodyReadLease
        {
            private readonly byte[] _bytes;

            public FakeBodyReadLease(EveEntitySoaViewDocument document, byte[] bytes)
                : this(DescriptorFor(document, bytes.Length), bytes) { }

            public FakeBodyReadLease(CultMeshBodyDescriptor descriptor, byte[] bytes)
            {
                Descriptor = descriptor;
                _bytes = bytes;
            }

            public CultMeshBodyDescriptor Descriptor { get; }
            public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.Network;
            public bool Disposed { get; private set; }
            public byte ReadByte(long offset) => _bytes[checked((int)offset)];
            public int ReadInt32(long offset) => BitConverter.ToInt32(_bytes, checked((int)offset));
            public long ReadInt64(long offset) => BitConverter.ToInt64(_bytes, checked((int)offset));
            public float ReadSingle(long offset) => BitConverter.ToSingle(_bytes, checked((int)offset));
            public double ReadDouble(long offset) => BitConverter.ToDouble(_bytes, checked((int)offset));
            public int CopyTo(long offset, byte[] destination, int destinationOffset, int count)
            {
                Buffer.BlockCopy(_bytes, checked((int)offset), destination, destinationOffset, count);
                return count;
            }
            public void Dispose() => Disposed = true;

            public static CultMeshBodyDescriptor DescriptorFor(EveEntitySoaViewDocument document, long byteSize) =>
                new CultMeshBodyDescriptor
                {
                    BodyId = document.Buffers[0].BufferId,
                    SchemaId = document.BodySchemaId,
                    LayoutVersion = document.LayoutVersion,
                    ByteSize = byteSize,
                    Capacity = document.Capacity,
                    ProducerEpoch = document.ProducerEpoch,
                    Sequence = document.Sequence
                };
        }

        private sealed class FixedGameObjectAssetProvider : IEveUnityGameObjectAssetProvider
        {
            private readonly GameObject? _prefab;

            public FixedGameObjectAssetProvider(GameObject? prefab)
            {
                _prefab = prefab;
            }

            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset) => _prefab;
        }

        private sealed class FixedNativeAssetProviderComponent : MonoBehaviour, IEveUnityNativeAssetProvider
        {
            private readonly Dictionary<string, UnityEngine.Object> _assets =
                new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);

            public EveUnityPlayableWorldAssetBinding? LastBinding { get; private set; }

            public void Set(string assetRef, UnityEngine.Object asset)
            {
                _assets[assetRef ?? ""] = asset;
            }

            public void Clear() => _assets.Clear();

            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset) => null;

            public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
            {
                LastBinding = asset;
                return _assets.TryGetValue(asset?.AssetRef ?? "", out var value) && assetType.IsInstanceOfType(value)
                    ? value
                    : null;
            }
        }

        private sealed class ObservingGameObjectAssetProvider : IEveUnityGameObjectAssetProvider
        {
            private readonly Action _onResolve;

            public ObservingGameObjectAssetProvider(Action onResolve)
            {
                _onResolve = onResolve;
            }

            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
            {
                _onResolve();
                return null;
            }
        }

        private sealed class FixedRenderChannelPolicy : IEveUnityCameraRenderPolicySource
        {
            private readonly string _channel;
            private readonly int _layer;

            public FixedRenderChannelPolicy(string channel, int layer)
            {
                _channel = channel;
                _layer = layer;
            }

            public bool TryGetRenderChannelLayer(string channel, out int layer)
            {
                layer = _layer;
                return string.Equals(channel, _channel, StringComparison.Ordinal);
            }
        }

        private sealed class FakePlayableWorldProviderComponent :
            MonoBehaviour,
            IEveUnitySceneProviderSurfaceDocumentSource,
            IEveUnityPlayableWorldAssetManifestDocumentSource,
            IEveUnitySceneCommandSink,
            IEveUnitySceneCommandReceiptSource,
            IEveUnityProviderRefreshSource,
            IEveUnityInputCapabilitySource
        {
            public List<EveSurfaceCommandRequest> Submitted { get; } = new List<EveSurfaceCommandRequest>();

            public int RefreshCount { get; private set; }

            public string SinkKind => "fake-provider-command-sink";

            public string ManifestRef => CurrentDocument.ManifestRef;

            public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; } =
                new EveUnitySceneProviderSurfaceDocument(
                    PlayableArpgDocument(),
                    Advertisement("aetheria.daemon.game"),
                    "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                    1);

            public EveUnityPlayableWorldAssetManifestDocument CurrentDocument { get; private set; } =
                new EveUnityPlayableWorldAssetManifestDocument(
                    "cultmesh://aetheria/assets/manifest",
                    Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                    "aetheria");

            public EveInputCapabilityDocument CurrentInputCapability { get; private set; } =
                new EveInputCapabilityDocument();

            EveUnitySceneProviderSurfaceDocument IEveUnitySceneProviderSurfaceDocumentSource.CurrentDocument =>
                CurrentSurfaceDocument;

            public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable;

            public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

            event Action<EveUnityPlayableWorldAssetManifestDocument> IEveUnityPlayableWorldAssetManifestDocumentSource.DocumentAvailable
            {
                add => AssetManifestDocumentAvailable += value;
                remove => AssetManifestDocumentAvailable -= value;
            }

            public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;

            public void Set(
                EveUnitySceneProviderSurfaceDocument surfaceDocument,
                EveUnityPlayableWorldAssetManifestDocument assetManifest,
                EveInputCapabilityDocument? inputCapability = null)
            {
                CurrentSurfaceDocument = surfaceDocument;
                CurrentDocument = assetManifest;
                CurrentInputCapability = inputCapability ?? new EveInputCapabilityDocument();
            }

            public void Refresh()
            {
                RefreshCount++;
                DocumentAvailable?.Invoke(CurrentSurfaceDocument);
                AssetManifestDocumentAvailable?.Invoke(CurrentDocument);
            }

            public void Submit(EveSurfaceCommandRequest request)
            {
                Submitted.Add(request);
            }

            public void PublishReceipt(EveUnitySceneCommandReceipt receipt)
            {
                ReceiptAvailable?.Invoke(receipt);
            }
        }
    }
}
