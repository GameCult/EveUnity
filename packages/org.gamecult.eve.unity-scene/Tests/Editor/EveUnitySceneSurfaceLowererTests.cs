using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnitySceneSurfaceLowererTests
    {
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
        public async System.Threading.Tasks.Task LiveTransportResolvesExactNetworkBodyThroughVerseDocuments()
        {
            var view = EntityLeaseDocument();
            var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var source = new CultCache();
            var generation = new CultMeshBodyGeneration
            {
                BodyId = view.Buffers[0].BufferId,
                ProducerId = view.ProviderId,
                SchemaId = view.BodySchemaId,
                LayoutVersion = view.LayoutVersion,
                Capacity = view.Capacity,
                ProducerEpoch = view.ProducerEpoch,
                Sequence = view.Sequence,
                Synchronization = CultMeshBodySynchronization.TripleBuffer,
                LeaseExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
            };
            var descriptor = await new CultMeshNetworkBodyPublisher(source, _ => true).PublishAsync(generation, bytes);
            var binding = source.Get<CultMeshNetworkBodyDocument>(
                CultMeshNetworkBodyDocument.CreateRecordKey(descriptor.CapabilityToken))!;
            var manifest = source.Get<CultMeshCdnArtifactManifest>(new CultRecordKey(binding.ManifestRecordKey))!;
            var chunks = manifest.Chunks.Select(reference =>
                source.Get<CultMeshCdnArtifactChunk>(new CultRecordKey(reference.RecordKey))!).ToArray();
            var method = typeof(EveUnityCultMeshLiveProviderTransport)
                .GetMethod("ResolveNetworkBody", BindingFlags.NonPublic | BindingFlags.Static)!;

            var resolved = (byte[])method.Invoke(null, new object[] { descriptor, binding, manifest, chunks })!;

            Assert.That(resolved, Is.EqualTo(bytes));
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
            Assert.That(projection.Root.Children[0].Children[3].SceneObjectKind, Is.EqualTo("world-field-3d"));
            Assert.That(projection.PlayableWorld, Is.Not.Null);
            Assert.That(projection.PlayableWorld!.WorldRootId, Is.EqualTo("aetheria.daemon.game.playable"));
            Assert.That(projection.PlayableWorld.StatePointerId, Is.EqualTo("cultmesh://aetheria/run/current"));
            Assert.That(projection.PlayableWorld.EntityViewPointerId, Is.EqualTo("cultmesh://aetheria/world/entities.soa"));
            Assert.That(projection.PlayableWorld.EntityViewSchema, Is.EqualTo(EveEntitySoaViewDocument.SchemaId));
            Assert.That(projection.PlayableWorld.ZoneRenderPointerId, Is.EqualTo("cultmesh://aetheria/world/zone-render"));
            Assert.That(projection.PlayableWorld.AssetManifest, Is.EqualTo("cultmesh://aetheria/assets/manifest"));
            Assert.That(projection.PlayableWorld.InputProfile, Is.EqualTo("arpg-third-person"));
            Assert.That(projection.PlayableWorld.CameraRig, Is.EqualTo("third-person-orbit"));
            Assert.That(projection.PlayableWorld.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(projection.PlayableWorld.MovementCommand, Is.EqualTo("aetheria.daemon.move_intent"));
            Assert.That(projection.PlayableWorld.FocusCommand, Is.EqualTo("aetheria.daemon.focus"));
            Assert.That(projection.PlayableWorld.TargetCommand, Is.EqualTo("aetheria.daemon.target"));
            Assert.That(projection.PlayableWorld.EntityCount, Is.EqualTo(3));

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
        public void PlayableWorldCameraRigFollowsAdvertisedPlayerEntityWithoutProviderTypes()
        {
            var rootObject = new GameObject("generic-eve-world-root");
            var hostObject = new GameObject("generic-eve-client");
            var cameraObject = new GameObject("generic-eve-camera");
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

                var rig = hostObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                rig.Host = host;
                rig.CameraTransform = cameraObject.transform;

                var player = rootObject.GetComponentInChildren<EveUnityPlayableWorldEntityMarker>();
                Assert.That(player, Is.Not.Null);
                var largeVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                largeVisual.transform.SetParent(player.transform, false);
                largeVisual.transform.localScale = Vector3.one * 100f;

                Assert.That(rig.ApplyRig(0f), Is.True);
                Assert.That(cameraObject.transform.position.y, Is.GreaterThan(0f));
                Assert.That(Vector3.Distance(cameraObject.transform.position, largeVisual.transform.position), Is.GreaterThan(100f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void ProviderPrefabKeepsAuthoredScaleWhileFallbackUsesSemanticRadius()
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
                Assert.That(instance.localScale, Is.EqualTo(Vector3.one));
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
            var firstPresentation = presenter.Apply(firstProjection);

            Assert.That(firstPresentation.WorldRootId, Is.EqualTo("aetheria.daemon.game.playable"));
            Assert.That(firstPresentation.PlayerEntityId, Is.EqualTo("player-vanguard"));
            Assert.That(firstPresentation.InputProfile, Is.EqualTo("arpg-third-person"));
            Assert.That(firstPresentation.CameraRig, Is.EqualTo("third-person-orbit"));
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
            var secondPresentation = presenter.Apply(secondProjection);

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
        public void LivePlayableWorldClientWaitsForProviderSurfaceUpdateAfterReceipt()
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

            source.Publish(new EveUnitySceneProviderSurfaceSnapshot(
                PlayableArpgDocument(includeRaider: false, playerPosition: "30,0,30"),
                Advertisement("aetheria.daemon.game"),
                "cultmesh://aetheria/eve/surfaces/aetheria.daemon.game",
                2));
            Assert.That(client.LastReceipt.State, Is.EqualTo("reconciled"));
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
            string playerPosition = "0,0,0")
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
                                    ["zoneRenderPointerId"] = "cultmesh://aetheria/world/zone-render",
                                    ["zoneRenderSchema"] = "gamecult.aetheria.zone_render.v1",
                                    ["assetManifest"] = "cultmesh://aetheria/assets/manifest",
                                    ["inputProfile"] = "arpg-third-person",
                                    ["cameraRig"] = "third-person-orbit",
                                    ["playerEntityId"] = "player-vanguard",
                                    ["movementCommand"] = "aetheria.daemon.move_intent",
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

            public event Action<EveUnitySceneProviderSurfaceDocument>? SurfaceDocumentAvailable;

            public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

            public event Action<EveUnitySceneCommandReceipt>? CommandReceiptAvailable;

            public void Connect()
            {
                ConnectCount++;
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
                SurfaceDocumentAvailable?.Invoke(document);
            }

            public void PublishAssetManifest(EveUnityPlayableWorldAssetManifestDocument document)
            {
                CurrentAssetManifestDocument = document;
                AssetManifestDocumentAvailable?.Invoke(document);
            }

            public void PublishReceipt(EveUnitySceneCommandReceipt receipt)
            {
                CommandReceiptAvailable?.Invoke(receipt);
            }
        }

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
                PreferredLocal = descriptor,
                NetworkFallback = network
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

        private sealed class FakePlayableWorldProviderComponent :
            MonoBehaviour,
            IEveUnitySceneProviderSurfaceDocumentSource,
            IEveUnityPlayableWorldAssetManifestDocumentSource,
            IEveUnitySceneCommandSink,
            IEveUnitySceneCommandReceiptSource,
            IEveUnityProviderRefreshSource
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
                EveUnityPlayableWorldAssetManifestDocument assetManifest)
            {
                CurrentSurfaceDocument = surfaceDocument;
                CurrentDocument = assetManifest;
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
