using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityUIToolkit;
using GameCult.Eve.UnityScene;
using GameCult.Eve.UnityScene.Fields;
using GameCult.Mesh;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace GameCult.EveUnity.GenericClient.PlayModeTests
{
    public sealed class GenericWorldCaptureTests
    {
        [Test]
        public void CoreProviderDocumentsRoundTripThroughCultCacheSerialization()
        {
            var advertisement = new EveProviderAdvertisementDocument(
                "eve.world-smoke",
                "eve-world-smoke",
                "eve.local",
                "Generic world",
                "game.daemon",
                "127.0.0.1:7777",
                "2026-07-10T00:00:00Z",
                new EveProviderFreshness("fresh", "2026-07-10T00:00:00Z", 5000),
                new[] { EveSurfaceDocument.SchemaId, EveCommandReceiptDocument.SchemaId },
                Array.Empty<EveProviderWitness>(),
                new[]
                {
                    new EveAdvertisedSurface(
                        "eve.world-smoke.surface",
                        EveSurfaceDocument.SchemaId,
                        "surface:eve.world-smoke",
                        "cultmesh-record",
                        "active",
                        "interactive-world",
                        new EveWorldInteractionAdvertisement(
                            "provider-authored-world-surface",
                            Array.Empty<string>(),
                            "world.commands",
                            "commands:eve.world-smoke",
                            EveCommandReceiptDocument.SchemaId,
                            "receipts:eve.world-smoke",
                            "assets:eve.world-smoke",
                            new[] { "unity-scene" },
                            "provider-owns-world-state-command-acceptance-and-receipts"))
                },
                Array.Empty<EveAdvertisedCommand>());

            var payload = CultDocumentMessagePackSerialization.Serialize(advertisement);
            var decoded = CultDocumentMessagePackSerialization.Deserialize<EveProviderAdvertisementDocument>(payload);

            Assert.That(decoded.ProviderId, Is.EqualTo("eve.world-smoke"));
            Assert.That(decoded.Surfaces.Count, Is.EqualTo(1));
            Assert.That(decoded.Surfaces[0].WorldInteraction.CommandRecordRef, Is.EqualTo("commands:eve.world-smoke"));
        }

        [UnityTest]
        public IEnumerator GenericClientRendersWorldSmokeAndEmitsCommand()
        {
            var hostObject = new GameObject("EveUnity Generic Client");
            var sceneRoot = new GameObject("World Projection");
            var provider = hostObject.AddComponent<WorldSmokeProvider>();
            var host = hostObject.AddComponent<EveUnityPlayableWorldClientHost>();
            var cameraObject = new GameObject("Capture Camera");
            var lightObject = new GameObject("World Light");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            RenderTexture target = null;
            Texture2D pixels = null;

            try
            {
                provider.CurrentDocument = WorldDocument();
                host.Configure(sceneRoot.transform, provider, provider);
                var presentation = host.Connect();

                Assert.That(presentation.ActiveEntities, Is.EqualTo(3));
                Assert.That(
                    sceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(),
                    Has.Length.EqualTo(3));
                foreach (var marker in sceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>())
                {
                    var renderer = marker.GetComponent<Renderer>();
                    renderer.material.color = marker.Controllable
                        ? new Color(0.18f, 0.75f, 1f)
                        : marker.EntityKind == "enemy"
                            ? new Color(1f, 0.25f, 0.2f)
                            : new Color(1f, 0.78f, 0.15f);
                }

                ground.name = "Projection Ground";
                ground.transform.position = new Vector3(0f, -0.65f, 3f);
                ground.transform.localScale = new Vector3(2.2f, 1f, 2.2f);
                ground.GetComponent<Renderer>().material.color = new Color(0.12f, 0.16f, 0.18f);

                host.SubmitMoveVectorIntent("player", 0f, 1f);
                Assert.That(provider.Submitted, Has.Count.EqualTo(1));
                Assert.That(provider.Submitted[0].ProviderId, Is.EqualTo("eve.world-smoke"));

                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.4f;
                lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.055f, 0.08f, 1f);
                camera.transform.position = new Vector3(10f, 8f, -12f);
                camera.transform.LookAt(new Vector3(0f, 1f, 3f));
                target = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;

                yield return null;
                camera.Render();
                RenderTexture.active = target;
                pixels = new Texture2D(640, 360, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();

                var changedPixels = 0;
                var background = camera.backgroundColor;
                foreach (var pixel in pixels.GetPixels())
                {
                    if (Mathf.Abs(pixel.r - background.r) +
                        Mathf.Abs(pixel.g - background.g) +
                        Mathf.Abs(pixel.b - background.b) > 0.08f)
                        changedPixels++;
                }
                Assert.That(changedPixels, Is.GreaterThan(500));

                var capturePath = Environment.GetEnvironmentVariable("EVEUNITY_CAPTURE_PATH");
                if (string.IsNullOrWhiteSpace(capturePath))
                {
                    capturePath = Path.GetFullPath(Path.Combine(
                        Application.dataPath,
                        "..",
                        "..",
                        "artifacts",
                        "playmode",
                        "world-smoke.png"));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath));
                File.WriteAllBytes(capturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(capturePath).Length, Is.GreaterThan(1024));
            }
            finally
            {
                RenderTexture.active = null;
                var camera = cameraObject.GetComponent<Camera>();
                if (camera != null) camera.targetTexture = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(lightObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(ground);
                UnityEngine.Object.DestroyImmediate(sceneRoot);
                UnityEngine.Object.DestroyImmediate(hostObject);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator GenericCultMeshClientLowersAndMovesAdvertisedWorld()
        {
            var rendezvousEndpoint = Environment.GetEnvironmentVariable("EVEUNITY_RENDEZVOUS_ENDPOINT");
            if (string.IsNullOrWhiteSpace(rendezvousEndpoint))
                Assert.Ignore("EVEUNITY_RENDEZVOUS_ENDPOINT is not configured for the live provider witness.");

            var providerId = Environment.GetEnvironmentVariable("EVEUNITY_PROVIDER_ID") ?? "";
            var surfaceId = Environment.GetEnvironmentVariable("EVEUNITY_SURFACE_ID") ?? "";
            var replicaPath = Environment.GetEnvironmentVariable("EVEUNITY_REPLICA_PATH") ??
                              Path.Combine(Application.temporaryCachePath, $"eve-unity-{Guid.NewGuid():N}.cc");
            var capturePath = Environment.GetEnvironmentVariable("EVEUNITY_AETHERIA_CAPTURE_PATH") ??
                              Path.Combine(Application.temporaryCachePath, "aetheria-daemon-world.png");
            var mapCapturePath = Environment.GetEnvironmentVariable("EVEUNITY_AETHERIA_MAP_CAPTURE_PATH") ??
                                 Path.Combine(Application.temporaryCachePath, "aetheria-daemon-map.png");
            var root = new GameObject("Generic Eve World Projection");
            var cameraObject = new GameObject("Generic Eve Capture Camera");
            var mapCameraObject = new GameObject("Generic Eve Map Camera");
            RenderTexture target = null;
            Texture2D pixels = null;
            EveUnityCultMeshPlayableWorldProvider provider = null;
            EveUnityPlayableWorldClientHost host = null;
            EveUnityPlayableWorldRuntime runtime = null;
            WitnessReceipt movementReceipt = null;
            WitnessReceipt lookReceipt = null;
            WitnessReceipt focusReceipt = null;
            WitnessReceipt tractorReceipt = null;
            WitnessReceipt tractorReleaseReceipt = null;
            WitnessReceipt actionReceipt = null;
            EveUnityShotReceipt observedShot = null;
            EveUnityCombatPresentation observedCombat = null;
            float shieldRatioBefore = 0f;
            float hullRatioBefore = 0f;
            long initialVersion = 0;
            float movementDistance = 0f;
            float aimDotDistance = 0f;
            int cockpitProgressCount = 0;
            float tractorBeamPower = 0f;
            float tractorReleasedPower = 1f;
            int tractorBeamParticleSystemCount = 0;
            EveUnityFeedbackEvent pickupCollection = null;
            int pickupCollectionEventCount = 0;
            int initialPickupEntityCount = 0;
            int finalPickupEntityCount = -1;
            bool destructionPickupObserved = false;
            int maximumTrajectoryCount = 0;
            bool hitMarkerObserved = false;
            var witnessProfile = Environment.GetEnvironmentVariable("EVEUNITY_WITNESS_PROFILE") ??
                                 "full-session-gameplay";
            Assert.That(witnessProfile,
                Is.EqualTo("cold-start-lowering").Or.EqualTo("full-session-gameplay"),
                "The witness profile must name an explicit proof boundary.");
            var requireSessionGameplay = string.Equals(
                witnessProfile,
                "full-session-gameplay",
                StringComparison.Ordinal);
            EveUnityAimPresentationRenderer aimRenderer = null;
            EveUnityBeamPresentation beamPresentation = null;
            EveUnityBeamPresentationRenderer beamRenderer = null;
            EveUnityShotTrajectoryRenderer trajectories = null;

            try
            {
                var providerReadyPath = Environment.GetEnvironmentVariable("EVEUNITY_PROVIDER_READY_PATH");
                if (!string.IsNullOrWhiteSpace(providerReadyPath))
                    File.WriteAllText(providerReadyPath, DateTimeOffset.UtcNow.ToString("O"));
                provider = root.AddComponent<EveUnityCultMeshPlayableWorldProvider>();
                provider.Configure(
                    rendezvousEndpoint,
                    replicaPath,
                    providerId,
                    surfaceId,
                    requiredSurfaceKind: "interactive-world",
                    clientRuntimeId: $"eve-unity-test-{Guid.NewGuid():N}");
                var publicationDeadline = Time.realtimeSinceStartup + 30f;
                while (true)
                {
                    var published = false;
                    try
                    {
                        provider.Refresh();
                        published = true;
                    }
                    catch (InvalidOperationException) when (Time.realtimeSinceStartup < publicationDeadline)
                    {
                    }
                    catch (TimeoutException) when (Time.realtimeSinceStartup < publicationDeadline)
                    {
                    }
                    if (published) break;
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                Assert.That(provider.Selection, Is.Not.Null);
                Assert.That(provider.Selection.VerseId, Is.Not.Empty);
                Assert.That(provider.Selection.ProviderId, Is.Not.Empty);
                Assert.That(provider.Selection.SurfaceId, Is.Not.Empty);
                Assert.That(provider.CurrentAssetBodyTransportKind,
                    Is.EqualTo(CultMeshBodyTransportKind.SharedFileMapping),
                    "The generic client did not negotiate its verified provider bundle as a mapped body.");
                host = root.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(
                    root.transform,
                    provider,
                    provider,
                    provider,
                    provider,
                    provider);
                host.Connect();
                var loweredSurface = new EveUiToolkitSurfaceLowerer().Lower(
                    provider.CurrentDocument.SurfaceDocument,
                    provider.Submit);
                var cockpit = loweredSurface.Q<VisualElement>("aetheria.daemon.game.cockpit");
                Assert.That(cockpit, Is.Not.Null, "The provider did not publish a generic cockpit overlay.");
                cockpitProgressCount = cockpit.Query<ProgressBar>().ToList().Count;
                Assert.That(cockpitProgressCount, Is.GreaterThanOrEqualTo(7));
                Assert.That(loweredSurface.Q<VisualElement>("aetheria.daemon.game.frame").style.display.value,
                    Is.EqualTo(DisplayStyle.None), "Daemon diagnostics leaked into the pilot UI.");
                trajectories = root.GetComponent<EveUnityShotTrajectoryRenderer>();
                Assert.That(trajectories, Is.Not.Null);
                host.ShotAvailable += shot =>
                {
                    observedShot = shot;
                    maximumTrajectoryCount = Math.Max(maximumTrajectoryCount, trajectories.ActiveTrajectoryCount);
                };
                runtime = host.Runtime;
                Assert.That(runtime, Is.Not.Null);
                Assert.That(runtime.ActiveWorld.PlayerEntityId, Is.Not.Empty);
                var entityDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < entityDeadline &&
                       (host.PresentedEntities?.CurrentGeneration == null ||
                        host.PresentedEntities.CurrentGeneration.Entities.Count == 0))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    host.Refresh();
                }
                Assert.That(host.PresentedEntities?.CurrentGeneration, Is.Not.Null);
                Assert.That(host.PresentedEntities.CurrentGeneration.Entities.Count, Is.GreaterThan(0));
                initialPickupEntityCount = host.PresentedEntities.CurrentGeneration.Entities.Count(entity =>
                    string.Equals(entity.EntityKind, "pickup", StringComparison.Ordinal));
                if (requireSessionGameplay)
                    Assert.That(initialPickupEntityCount, Is.Zero,
                        "The witness scenario must obtain salvage from daemon-owned destruction, not a boot-seeded pickup.");
                runtime.FeedbackAvailable += feedback =>
                {
                    if (!string.Equals(feedback.Kind, "pickup.collected", StringComparison.Ordinal)) return;
                    pickupCollectionEventCount++;
                    pickupCollection = feedback;
                };

                var playerId = runtime.ActiveWorld.PlayerEntityId;
                var playerFact = host.PresentedEntities.CurrentGeneration.Entities
                    .Single(entity => entity.EntityId == playerId);
                var playerPrefab = provider.ResolvePrefab(new EveUnityPlayableWorldAssetBinding(
                    playerFact.AssetRef,
                    playerFact.EntityKind,
                    "provider-asset-ref"));
                Assert.That(playerPrefab, Is.Not.Null,
                    $"Provider asset catalog did not resolve player AssetRef '{playerFact.AssetRef}'.");
                var marker = FindMarker(root, playerId);
                Assert.That(
                    marker.GetComponentsInChildren<Transform>(includeInactive: true).Length,
                    Is.GreaterThan(1),
                    "The generic client substituted its primitive fallback instead of the provider-authored AssetBundle prefab.");
                var initialPosition = marker.transform.position;
                initialVersion = runtime.ActiveVersion;
                var request = runtime.SubmitMoveVectorIntent(playerId, 1f, 0f, 1f);

                var deadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < deadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != request.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion <= initialVersion ||
                        Vector3.Distance(FindMarker(root, playerId).transform.position, initialPosition) < 0.01f))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }

                AssertReconciledProviderReceipt(
                    runtime.LastReceipt,
                    request.CommandId,
                    provider.Selection.ProviderId,
                    provider.Selection.SurfaceId,
                    initialVersion);
                Assert.That(runtime.ActiveVersion, Is.GreaterThan(initialVersion));
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(runtime.LastReceipt.SourceVersion));
                movementDistance = Vector3.Distance(FindMarker(root, playerId).transform.position, initialPosition);
                Assert.That(movementDistance, Is.GreaterThan(0.01f));
                movementReceipt = WitnessReceipt.From("movement", runtime.LastReceipt);

                var releaseVersion = runtime.ActiveVersion;
                var release = runtime.SubmitMoveVectorIntent(playerId, 0f, 0f, 0f);
                var releaseDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < releaseDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != release.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(runtime.LastReceipt, release.CommandId,
                    provider.Selection.ProviderId, provider.Selection.SurfaceId, releaseVersion);

                if (requireSessionGameplay)
                {
                var movementVersion = runtime.ActiveVersion;
                var focus = runtime.SubmitFocusIntent(playerId);
                var focusDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < focusDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != focus.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion ||
                        string.IsNullOrWhiteSpace(EveUnityCombatPresentation.Find(runtime.ActiveProjection)?.SelectedTargetEntityId)))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(
                    runtime.LastReceipt,
                    focus.CommandId,
                    provider.Selection.ProviderId,
                    provider.Selection.SurfaceId,
                    movementVersion);
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(runtime.LastReceipt.SourceVersion));
                focusReceipt = WitnessReceipt.From("targeting", runtime.LastReceipt);
                observedCombat = EveUnityCombatPresentation.Find(runtime.ActiveProjection);
                Assert.That(observedCombat, Is.Not.Null);
                Assert.That(observedCombat.TargetVisible, Is.True);
                Assert.That(observedCombat.TargetHostile, Is.True);
                var targetEntityId = observedCombat.SelectedTargetEntityId;
                var targetEntityIndex = observedCombat.SelectedTargetEntityIndex;
                Assert.That(targetEntityId, Is.Not.Empty);
                Assert.That(host.PresentedEntities.CurrentGeneration.Entities.Any(entity => entity.EntityId == targetEntityId), Is.True,
                    "The provider-selected combat target is absent from the advertised SoA generation.");
                shieldRatioBefore = observedCombat.ShieldRatio;
                hullRatioBefore = observedCombat.HullRatio;

                var targetFact = host.PresentedEntities.CurrentGeneration.Entities
                    .Single(entity => entity.EntityId == targetEntityId);
                playerFact = host.PresentedEntities.CurrentGeneration.Entities
                    .Single(entity => entity.EntityId == playerId);
                var aim = targetFact.Position - playerFact.Position;
                aim.y = 0;
                Assert.That(aim.sqrMagnitude, Is.GreaterThan(0.0001f));
                aim.Normalize();
                var lookVersion = runtime.ActiveVersion;
                var look = runtime.SubmitLookDirectionIntent(playerId, aim.x, 0f, aim.z);
                var lookDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < lookDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != look.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion ||
                        Vector3.Dot(FindMarker(root, playerId).transform.forward, aim) < 0.99f))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(runtime.LastReceipt, look.CommandId,
                    provider.Selection.ProviderId, provider.Selection.SurfaceId, lookVersion);
                Assert.That(Vector3.Dot(FindMarker(root, playerId).transform.forward, aim), Is.GreaterThan(0.99f));
                lookReceipt = WitnessReceipt.From("look", runtime.LastReceipt);
                aimRenderer = root.GetComponent<EveUnityAimPresentationRenderer>();
                Assert.That(aimRenderer, Is.Not.Null);

                beamPresentation = EveUnityBeamPresentation.FindAll(runtime.ActiveProjection).SingleOrDefault();
                Assert.That(beamPresentation, Is.Not.Null,
                    "The provider did not advertise its continuous beam presentation.");
                Assert.That(beamPresentation.ActivationActionId, Is.Not.Empty,
                    "The beam presentation does not name an advertised generic action.");
                var tractorVersion = runtime.ActiveVersion;
                var tractor = host.SubmitAdvertisedActionValueIntent(playerId, beamPresentation.ActivationActionId, 1f);
                var tractorDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < tractorDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != tractor.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion ||
                        EveUnityBeamPresentation.FindAll(runtime.ActiveProjection).Single().Power < 0.99f))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(runtime.LastReceipt, tractor.CommandId,
                    provider.Selection.ProviderId, provider.Selection.SurfaceId, tractorVersion);
                tractorReceipt = WitnessReceipt.From("beam", runtime.LastReceipt);
                Assert.That(beamPresentation.SourceEntityId, Is.EqualTo(playerId),
                    "The daemon beam source does not identify the presented player entity.");
                Assert.That(host.PresentedEntities.TryGetByEntityId(beamPresentation.SourceEntityId, out _), Is.True,
                    "The generic beam lowerer cannot resolve the advertised source entity.");
                Assert.That(provider.ResolvePrefab(new EveUnityPlayableWorldAssetBinding(
                        beamPresentation.AssetRole, beamPresentation.AssetRole, "provider-asset-ref")), Is.Not.Null,
                    $"The provider asset catalog did not resolve beam role '{beamPresentation.AssetRole}'.");
                beamRenderer = root.GetComponent<EveUnityBeamPresentationRenderer>();
                Assert.That(beamRenderer, Is.Not.Null);
                beamRenderer.RefreshNow();
                Assert.That(beamRenderer.ActiveBeamCount, Is.EqualTo(1));
                Assert.That(beamRenderer.TryGetPower(beamPresentation.Id, out tractorBeamPower), Is.True);
                Assert.That(tractorBeamPower, Is.GreaterThanOrEqualTo(0.99f),
                    "Held input did not reach full daemon-authored tractor power before capture.");
                tractorBeamParticleSystemCount = beamRenderer.ActiveParticleSystemCount;
                Assert.That(tractorBeamParticleSystemCount, Is.GreaterThan(0),
                    "The generic beam lowerer did not instantiate the provider-owned effect prefab.");
                targetFact = host.PresentedEntities.CurrentGeneration.Entities
                    .Single(entity => entity.EntityId == targetEntityId);
                playerFact = host.PresentedEntities.CurrentGeneration.Entities
                    .Single(entity => entity.EntityId == playerId);
                aim = targetFact.Position - playerFact.Position;
                aim.y = 0;
                Assert.That(aim.sqrMagnitude, Is.GreaterThan(0.0001f));
                aim.Normalize();
                lookVersion = runtime.ActiveVersion;
                look = runtime.SubmitLookDirectionIntent(playerId, aim.x, 0f, aim.z);
                lookDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < lookDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != look.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion ||
                        Vector3.Dot(FindMarker(root, playerId).transform.forward, aim) < 0.99f))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(runtime.LastReceipt, look.CommandId,
                    provider.Selection.ProviderId, provider.Selection.SurfaceId, lookVersion);
                Assert.That(Vector3.Dot(FindMarker(root, playerId).transform.forward, aim), Is.GreaterThan(0.99f));
                lookReceipt = WitnessReceipt.From("look", runtime.LastReceipt);

                var combatRenderer = root.GetComponent<EveUnityCombatPresentationRenderer>();
                Assert.That(combatRenderer, Is.Not.Null);
                var focusVersion = runtime.ActiveVersion;
                var inputDriver = root.GetComponent<EveUnityPlayableWorldInputDriver>() ??
                    root.AddComponent<EveUnityPlayableWorldInputDriver>();
                inputDriver.Host = host;
                var action = inputDriver.SubmitPrimaryAction();
                Assert.That(action, Is.Not.Null,
                    "The advertised input capability did not bind a primary action for this generic client.");
                var actionDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < actionDeadline &&
                        (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != action!.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                    combatRenderer.RefreshNow();
                    hitMarkerObserved |= combatRenderer.HitMarkerVisible;
                }
                AssertReconciledProviderReceipt(
                    runtime.LastReceipt,
                    action!.CommandId,
                    provider.Selection.ProviderId,
                    provider.Selection.SurfaceId,
                    focusVersion);
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(runtime.LastReceipt.SourceVersion));
                actionReceipt = WitnessReceipt.From("action", runtime.LastReceipt);

                var shotDeadline = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < shotDeadline &&
                       (observedShot == null ||
                        host.PresentedEntities.CurrentGeneration.Entities.Any(entity =>
                            entity.EntityId == targetEntityId) ||
                        (!destructionPickupObserved && pickupCollection == null)))
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    runtime.Refresh();
                    destructionPickupObserved |= host.PresentedEntities.CurrentGeneration.Entities.Any(entity =>
                        string.Equals(entity.EntityKind, "pickup", StringComparison.Ordinal));
                    maximumTrajectoryCount = Math.Max(maximumTrajectoryCount, trajectories.ActiveTrajectoryCount);
                    combatRenderer.RefreshNow();
                    hitMarkerObserved |= combatRenderer.HitMarkerVisible;
                }
                Assert.That(observedShot, Is.Not.Null,
                    "A reconciled fire request never reached daemon-owned lock acquisition and shot resolution.");
                Assert.That(host.PresentedEntities.CurrentGeneration.Entities.Any(entity =>
                        entity.EntityId == targetEntityId), Is.False,
                    "The low-hull witness raider was not destroyed by daemon combat.");
                Assert.That(destructionPickupObserved || pickupCollection != null, Is.True,
                    "Daemon destruction did not publish its guaranteed cargo as a pickup.");
                var collectionDeadline = Time.realtimeSinceStartup + 15f;
                var nextTractorAimAt = 0f;
                while (Time.realtimeSinceStartup < collectionDeadline && pickupCollection == null)
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    runtime.Refresh();
                    maximumTrajectoryCount = Math.Max(maximumTrajectoryCount, trajectories.ActiveTrajectoryCount);
                    combatRenderer.RefreshNow();
                    hitMarkerObserved |= combatRenderer.HitMarkerVisible;
                    var pickupFact = host.PresentedEntities.CurrentGeneration.Entities.FirstOrDefault(entity =>
                        string.Equals(entity.EntityKind, "pickup", StringComparison.Ordinal));
                    if (pickupFact == null || Time.realtimeSinceStartup < nextTractorAimAt)
                        continue;
                    playerFact = host.PresentedEntities.CurrentGeneration.Entities
                        .Single(entity => entity.EntityId == playerId);
                    var tractorAim = pickupFact.Position - playerFact.Position;
                    tractorAim.y = 0;
                    if (tractorAim.sqrMagnitude <= 0.0001f)
                        continue;
                    tractorAim.Normalize();
                    runtime.SubmitLookDirectionIntent(playerId, tractorAim.x, 0f, tractorAim.z);
                    nextTractorAimAt = Time.realtimeSinceStartup + 0.25f;
                }
                finalPickupEntityCount = host.PresentedEntities.CurrentGeneration.Entities.Count(entity =>
                    string.Equals(entity.EntityKind, "pickup", StringComparison.Ordinal));
                Assert.That(pickupCollection, Is.Not.Null,
                    "Destruction-created salvage never reached cargo through a Ymir contact fact.");
                Assert.That(pickupCollectionEventCount, Is.EqualTo(1),
                    "One destruction-loot Ymir contact transaction must emit one collection event.");
                Assert.That(pickupCollection.ItemKey, Is.Not.Empty,
                    "Destruction loot must retain its canonical typed item identity.");
                Assert.That(pickupCollection.EventId, Does.StartWith("ymir-fact:"),
                    "Collection feedback must retain the consumed Ymir fact identity.");
                Assert.That(pickupCollection.ScalarValue, Is.EqualTo(1));
                Assert.That(pickupCollection.CargoQuantityBefore, Is.Zero);
                Assert.That(pickupCollection.CargoQuantityAfter, Is.EqualTo(1));
                Assert.That(finalPickupEntityCount, Is.Zero,
                    "Collected destruction loot remained in the provider-authored entity generation.");
                observedCombat = EveUnityCombatPresentation.Find(runtime.ActiveProjection);
                Assert.That(observedShot.TargetEntityIndex, Is.EqualTo(targetEntityIndex));
                Assert.That(observedShot.LockQuality, Is.GreaterThanOrEqualTo(0.99));
                Assert.That(maximumTrajectoryCount, Is.GreaterThan(0));
                combatRenderer.RefreshNow();
                if (observedCombat != null && !string.IsNullOrWhiteSpace(observedCombat.SelectedTargetEntityId))
                {
                    Assert.That(combatRenderer.ReticleVisible, Is.True);
                    Assert.That(combatRenderer.LockVisible, Is.True);
                }
                if (observedShot.Hit && (observedShot.AppliedDamage > 0 || observedShot.ShieldAbsorbedDamage > 0))
                    Assert.That(hitMarkerObserved, Is.True);
                }

                aimRenderer ??= root.GetComponent<EveUnityAimPresentationRenderer>();
                Assert.That(aimRenderer, Is.Not.Null);
                beamPresentation ??= EveUnityBeamPresentation.FindAll(runtime.ActiveProjection).SingleOrDefault();
                Assert.That(beamPresentation, Is.Not.Null,
                    "The provider did not advertise its continuous beam presentation.");
                beamRenderer ??= root.GetComponent<EveUnityBeamPresentationRenderer>();
                finalPickupEntityCount = Math.Max(0, finalPickupEntityCount);

                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.055f, 0.08f, 1f);
                camera.cullingMask = -1;
                var playerMarker = FindMarker(root, playerId);
                var playerRenderers = playerMarker.GetComponentsInChildren<Renderer>();
                Assert.That(playerRenderers, Is.Not.Empty, "The provider-authored player prefab has no renderable visual.");
                Assert.That(playerMarker.GetComponentsInChildren<Collider>(), Is.Empty,
                    "The provider-authored player presentation retained gameplay colliders.");
                Assert.That(playerMarker.GetComponentsInChildren<Rigidbody>(), Is.Empty,
                    "The provider-authored player presentation retained gameplay rigid bodies.");
                foreach (var renderer in playerRenderers)
                foreach (var material in renderer.sharedMaterials)
                {
                    Assert.That(material, Is.Not.Null, $"Provider renderer {renderer.name} has a null material.");
                    Assert.That(material.shader, Is.Not.Null, $"Provider material {material.name} has no shader.");
                    Assert.That(material.shader.isSupported, Is.True,
                        $"Provider material {material.name} uses unsupported shader {material.shader.name}.");
                    Assert.That(material.shader.name, Does.Not.Contain("FallbackError"),
                        $"Provider material {material.name} resolved to Unity's error shader.");
                }
                Assert.That(provider.TryGetRenderChannelLayer("map", out var mapLayer), Is.True);
                var mapRenderers = new List<Renderer>();
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (renderer.gameObject.layer == mapLayer)
                        mapRenderers.Add(renderer);
                }
                Assert.That(mapRenderers, Is.Not.Empty, "The live provider prefab has no renderer assigned to its advertised map channel.");

                target = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                var cameraRig = cameraObject.AddComponent<EveUnityPlayableWorldCameraRig>();
                cameraRig.Host = host;
                cameraRig.CameraTransform = camera.transform;
                cameraRig.RenderPolicySource = provider;
                Assert.That(cameraRig.ApplyRig(0f), Is.True);
                var skybox = provider.ResolveAsset(new EveUnityPlayableWorldAssetBinding(
                    runtime.ActiveWorld.SkyboxAssetRef, "", "provider-asset-ref"), typeof(Material)) as Material;
                var reflection = provider.ResolveAsset(new EveUnityPlayableWorldAssetBinding(
                    runtime.ActiveWorld.ReflectionAssetRef, "", "provider-asset-ref"), typeof(Cubemap)) as Cubemap;
                Assert.That(runtime.ActiveWorld.SkyboxAssetRef, Is.Not.Empty);
                Assert.That(runtime.ActiveWorld.ReflectionAssetRef, Is.Not.Empty);
                Assert.That(skybox, Is.Not.Null);
                Assert.That(reflection, Is.Not.Null);
                Assert.That(RenderSettings.skybox, Is.SameAs(skybox));
                Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(reflection));
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.Skybox));
                var environmentPresentation = RenderSettings.skybox == skybox &&
                    RenderSettings.customReflectionTexture == reflection;
                aimRenderer.RefreshNow();
                Assert.That(aimRenderer.ViewDotVisible, Is.True);
                aimDotDistance = Vector3.Distance(playerMarker.transform.position, aimRenderer.ViewDotPosition);
                Assert.That(aimDotDistance, Is.GreaterThanOrEqualTo(49.9f));
                Assert.That(camera.cullingMask & (1 << mapLayer), Is.Zero);
                Assert.That(runtime.ActiveWorld.CameraRig, Is.EqualTo("perspective.entity-forward-follow.v1"));
                Assert.That(runtime.ActiveWorld.CameraLookAt, Is.EqualTo("aim.convergence-point.v1"));
                Assert.That(runtime.ActiveWorld.CameraDistance, Is.EqualTo(30f).Within(0.001f));
                Assert.That(camera.fieldOfView, Is.EqualTo(60f).Within(0.001f));
                Assert.That(runtime.ActiveWorld.CameraPositionDamping, Is.EqualTo(0f).Within(0.001f));
                var playerViewport = camera.WorldToViewportPoint(playerMarker.transform.position);
                Assert.That(runtime.ActiveWorld.CameraTargetScreenX, Is.EqualTo(0.64f).Within(0.001f));
                Assert.That(runtime.ActiveWorld.CameraTargetScreenY, Is.EqualTo(0.19f).Within(0.001f));
                Assert.That(playerViewport.x, Is.EqualTo(runtime.ActiveWorld.CameraTargetScreenX).Within(0.01f));
                Assert.That(playerViewport.y, Is.EqualTo(runtime.ActiveWorld.CameraTargetScreenY).Within(0.01f));
                MeasureViewportCoverage(camera, playerRenderers, out var playerViewportWidth, out var playerViewportHeight);
                var playerRendererFacts = playerRenderers
                    .Select(renderer => RendererFact.From(camera, renderer))
                    .ToArray();
                var texturedPlayerMaterialCount = playerRenderers
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null)
                    .Distinct()
                    .Count(HasAnyTexture);
                Assert.That(texturedPlayerMaterialCount, Is.GreaterThanOrEqualTo(9),
                    "The provider bundle must retain its advertised native material textures.");
                var directionalLightIntensity = UnityEngine.Object.FindObjectsOfType<Light>()
                    .Where(light => light != null && light.enabled && light.type == LightType.Directional)
                    .Sum(light => light.intensity);
                Assert.That(directionalLightIntensity,
                    Is.EqualTo(runtime.ActiveWorld.KeyLightIntensity).Within(0.001f),
                    "The live witness contains directional lighting not owned by the advertised provider surface.");

                var mapCamera = mapCameraObject.AddComponent<Camera>();
                mapCamera.cullingMask = 1 << mapLayer;
                Assert.That(mapCamera.cullingMask, Is.EqualTo(1 << mapLayer));
                yield return null;
                camera.Render();
                RenderTexture.active = target;
                pixels = new Texture2D(640, 360, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();
                var pilotChangedPixels = CountChangedPixels(pixels, camera.backgroundColor);
                MeasureLuminance(pixels, out var pilotAverageLuminance, out var pilotBrightPixelCount);
                Assert.That(pilotChangedPixels, Is.GreaterThan(25), "The pilot camera rendered no provider-authored world pixels.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath)));
                File.WriteAllBytes(capturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(capturePath).Length, Is.GreaterThan(1024));
                WriteFieldLayerCaptures(root, host.Runtime.ActiveWorld, capturePath);
                WriteFieldCloudCapture(root, capturePath);
                MeasureRendererOutputs(
                    camera,
                    target,
                    pixels,
                    root.GetComponentsInChildren<Renderer>(includeInactive: true),
                    playerRenderers,
                    playerRendererFacts);
                var playerMapRendererFacts = playerRendererFacts
                    .Where(fact => fact.layer == mapLayer)
                    .ToArray();
                Assert.That(playerMapRendererFacts, Is.Not.Empty,
                    "The provider-authored player prefab has no renderer on its advertised map channel.");
                Assert.That(playerMapRendererFacts.Sum(fact => fact.renderedPixelCount), Is.Zero,
                    "A provider-prefab map-channel renderer leaked into the pilot camera.");

                mapRenderers = mapRenderers.Where(renderer => renderer != null).ToList();
                Assert.That(mapRenderers, Is.Not.Empty,
                    "All advertised map-channel renderers were destroyed before map capture.");
                var mapBounds = mapRenderers[0].bounds;
                foreach (var renderer in mapRenderers)
                    mapBounds.Encapsulate(renderer.bounds);
                mapCamera.clearFlags = CameraClearFlags.SolidColor;
                mapCamera.backgroundColor = camera.backgroundColor;
                mapCamera.orthographic = true;
                mapCamera.orthographicSize = Mathf.Max(0.1f, mapBounds.extents.x, mapBounds.extents.z) * 1.25f;
                mapCamera.nearClipPlane = 0.01f;
                mapCamera.farClipPlane = Mathf.Max(10f, mapBounds.size.magnitude * 4f);
                mapCamera.transform.position = mapBounds.center + Vector3.up * (mapCamera.farClipPlane * 0.25f);
                mapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                mapCamera.targetTexture = target;
                var fieldParticlesForMap = root.GetComponent<EveUnityFieldsParticleRenderer>();
                var particleDrawsBeforeMap = fieldParticlesForMap?.DrawCount ?? 0;
                mapCamera.Render();
                var particleMapCameraIsolated = fieldParticlesForMap != null &&
                    fieldParticlesForMap.DrawCount == particleDrawsBeforeMap;
                Assert.That(particleMapCameraIsolated, Is.True,
                    "The map camera executed the pilot-only field-particle pass.");
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();
                var mapChangedPixels = CountChangedPixels(pixels, mapCamera.backgroundColor);
                Assert.That(mapChangedPixels, Is.GreaterThan(25), "The map-only camera rendered no provider-authored map glyph pixels.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mapCapturePath)));
                File.WriteAllBytes(mapCapturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(mapCapturePath).Length, Is.GreaterThan(1024));
                var tractorReleaseVersion = runtime.ActiveVersion;
                var tractorRelease = host.SubmitAdvertisedActionValueIntent(
                    playerId,
                    beamPresentation.ActivationActionId,
                    0f);
                var tractorReleaseDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < tractorReleaseDeadline &&
                       (runtime.LastReceipt == null || runtime.LastReceipt.CommandId != tractorRelease.CommandId ||
                        !string.Equals(runtime.LastReceipt.State, "reconciled", StringComparison.OrdinalIgnoreCase) ||
                        runtime.ActiveVersion < runtime.LastReceipt.SourceVersion ||
                        EveUnityBeamPresentation.FindAll(runtime.ActiveProjection).Single().Power >
                            beamPresentation.ActivationThreshold))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                AssertReconciledProviderReceipt(runtime.LastReceipt, tractorRelease.CommandId,
                    provider.Selection.ProviderId, provider.Selection.SurfaceId, tractorReleaseVersion);
                tractorReleaseReceipt = WitnessReceipt.From("beam-release", runtime.LastReceipt);
                beamRenderer.RefreshNow();
                Assert.That(beamRenderer.TryGetPower(beamPresentation.Id, out tractorReleasedPower), Is.True);
                Assert.That(tractorReleasedPower, Is.Zero,
                    "Released held input did not return the daemon-authored tractor presentation to zero.");
                var fieldVolume = root.GetComponent<EveUnityFieldsVolumeRenderer>();
                Assert.That(fieldVolume, Is.Not.Null, "The generic client did not install its Fields volume lowerer.");
                Assert.That(fieldVolume.PresentedFrameId, Is.GreaterThanOrEqualTo(0),
                    "The advertised Fields splat document never reached the generic volume lowerer.");
                Assert.That(fieldVolume.PresentedLayerCount, Is.GreaterThan(0),
                    "The generic volume lowerer did not rasterize any advertised Fields layers.");
                Assert.That(fieldVolume.CompositeCount, Is.GreaterThan(0),
                    "The generic volume lowerer never composited into the pilot camera.");
                var fieldParticles = fieldParticlesForMap;
                Assert.That(fieldParticles, Is.Not.Null,
                    "The generic client did not install its field-particle lowerer.");
                var particleProjection = host.ActiveWorld.FieldParticles.Single();
                var particleComputeBinding = new EveUnityPlayableWorldAssetBinding(
                    particleProjection.ComputeProgramAssetRef, "", "provider-asset-ref");
                var particleMaterialBinding = new EveUnityPlayableWorldAssetBinding(
                    particleProjection.MaterialAssetRef, "", "provider-asset-ref");
                var particleCompute = host.NativeAssetProvider.ResolveAsset(
                    particleComputeBinding, typeof(ComputeShader)) as ComputeShader;
                var particleMaterial = host.NativeAssetProvider.ResolveAsset(
                    particleMaterialBinding, typeof(Material)) as Material;
                Assert.That(particleCompute, Is.Not.Null,
                    $"Provider asset '{particleProjection.ComputeProgramAssetRef}' did not lower as a ComputeShader.");
                Assert.That(particleMaterial, Is.Not.Null,
                    $"Provider asset '{particleProjection.MaterialAssetRef}' did not lower as a Material.");
                var particleMetadata = host.NativeAssetProvider as IEveUnityNativeAssetMetadataProvider;
                Assert.That(particleMetadata, Is.Not.Null);
                Assert.That(particleMetadata.TryResolveAssetMetadata(
                    particleComputeBinding, out var particleComputeMetadata), Is.True);
                Assert.That(particleMetadata.TryResolveAssetMetadata(
                    particleMaterialBinding, out var particleMaterialMetadata), Is.True);
                Assert.That(EveUnityFieldsParticleRenderer.TryValidateProgramMetadata(
                    particleComputeMetadata, particleMaterialMetadata, out var particleAbiError), Is.True,
                    particleAbiError);
                Assert.That(fieldParticles.ProgramReady, Is.True,
                    "The generic field-particle lowerer rejected the advertised provider ABI or assets.");
                Assert.That(fieldParticles.PresentedFrameId, Is.GreaterThanOrEqualTo(0));
                Assert.That(fieldParticles.ParticleCount, Is.EqualTo(65536));
                Assert.That(fieldParticles.DispatchCount, Is.GreaterThan(0));
                Assert.That(fieldParticles.DrawCount, Is.GreaterThan(0));
                Assert.That(fieldVolume.LastGridCenter, Is.EqualTo(fieldParticles.LastGridCenter),
                    "Fog and stateless particles were lowered against different world-grid frames.");
                var pilotCameraData = camera.GetUniversalAdditionalCameraData();
                Assert.That(pilotCameraData.antialiasing, Is.EqualTo(AntialiasingMode.TemporalAntiAliasing),
                    "The advertised temporal reconstruction contract did not reach the pilot camera.");
                Assert.That(pilotCameraData.taaSettings.baseBlendFactor, Is.EqualTo(0.99f).Within(0.0001f));
                Assert.That(pilotCameraData.taaSettings.jitterScale, Is.EqualTo(0.1f).Within(0.0001f));
                var fieldDocument = typeof(EveUnityFieldsVolumeRenderer)
                    .GetField("_document", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(fieldVolume);
                Assert.That(fieldDocument, Is.Not.Null, "The volume lowerer did not retain its presented Fields document.");
                var fieldViewport = fieldDocument.GetType().GetProperty("Viewport")?.GetValue(fieldDocument);
                var fieldSplats = fieldDocument.GetType().GetProperty("Splats")?.GetValue(fieldDocument);
                Assert.That(fieldViewport, Is.Not.Null);
                Assert.That(fieldSplats, Is.Not.Null);
                WriteWitnessFacts(
                    witnessProfile,
                    provider.CurrentAssetBodyTransportKind?.ToString() ?? "",
                    initialVersion,
                    runtime.ActiveVersion,
                    movementDistance,
                    playerRenderers.Length,
                    pilotChangedPixels,
                    mapRenderers.Count,
                    mapChangedPixels,
                    (camera.cullingMask & (1 << mapLayer)) == 0,
                    (mapCamera.cullingMask & (1 << mapLayer)) != 0,
                    environmentPresentation,
                    fieldVolume.PresentedFrameId,
                    fieldVolume.PresentedLayerCount,
                    fieldVolume.CompositeCount,
                    fieldParticles.PresentedFrameId,
                    fieldParticles.ParticleCount,
                    fieldParticles.DispatchCount,
                    fieldParticles.DrawCount,
                    particleMapCameraIsolated,
                    fieldParticles.LastGridCenter,
                    movementReceipt,
                    lookReceipt,
                    focusReceipt,
                    tractorReceipt,
                    tractorReleaseReceipt,
                    actionReceipt,
                    observedShot,
                    shieldRatioBefore,
                    observedCombat?.ShieldRatio ?? shieldRatioBefore,
                    hullRatioBefore,
                    observedCombat?.HullRatio ?? hullRatioBefore,
                    (float)(observedShot?.LockQuality ?? 0),
                    aimDotDistance,
                    playerViewportWidth,
                    playerViewportHeight,
                    texturedPlayerMaterialCount,
                    directionalLightIntensity,
                    pilotAverageLuminance,
                    pilotBrightPixelCount,
                    cockpitProgressCount,
                    tractorBeamPower,
                    tractorReleasedPower,
                    tractorBeamParticleSystemCount,
                    initialPickupEntityCount,
                    finalPickupEntityCount,
                    pickupCollectionEventCount,
                    destructionPickupObserved,
                    pickupCollection,
                    camera.transform.position,
                    camera.transform.forward,
                    playerMarker.transform.position,
                    aimRenderer.ViewDotPosition,
                    Convert.ToSingle(fieldViewport.GetType().GetProperty("MinX")?.GetValue(fieldViewport)),
                    Convert.ToSingle(fieldViewport.GetType().GetProperty("MinY")?.GetValue(fieldViewport)),
                    Convert.ToSingle(fieldViewport.GetType().GetProperty("MaxX")?.GetValue(fieldViewport)),
                    Convert.ToSingle(fieldViewport.GetType().GetProperty("MaxY")?.GetValue(fieldViewport)),
                    Convert.ToInt32(fieldSplats.GetType().GetProperty("Count")?.GetValue(fieldSplats)),
                    playerRendererFacts,
                    root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(includeInactive: true)
                        .Select(marker => PresentedEntityFact.From(camera, marker))
                        .ToArray());
            }
            finally
            {
                RenderTexture.active = null;
                var camera = cameraObject.GetComponent<Camera>();
                if (camera != null) camera.targetTexture = null;
                var mapCamera = mapCameraObject.GetComponent<Camera>();
                if (mapCamera != null) mapCamera.targetTexture = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                host?.Disconnect();
                UnityEngine.Object.DestroyImmediate(mapCameraObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void WriteWitnessFacts(
            string witnessProfile,
            string assetBodyTransport,
            long initialVersion,
            long finalVersion,
            float movementDistance,
            int playerRendererCount,
            int pilotChangedPixels,
            int mapChannelRendererCount,
            int mapChangedPixels,
            bool pilotCameraExcludesMapChannel,
            bool mapCameraIncludesMapChannel,
            bool environmentPresentation,
            long fieldVolumeFrameId,
            int fieldVolumeLayerCount,
            long fieldVolumeCompositeCount,
            long fieldParticleFrameId,
            int fieldParticleCount,
            int fieldParticleDispatchCount,
            int fieldParticleDrawCount,
            bool fieldParticleMapCameraIsolated,
            Vector2 fieldParticleGridCenter,
            WitnessReceipt movement,
            WitnessReceipt look,
            WitnessReceipt targeting,
            WitnessReceipt tractor,
            WitnessReceipt tractorRelease,
            WitnessReceipt action,
            EveUnityShotReceipt shot,
            float shieldRatioBefore,
            float shieldRatioAfter,
            float hullRatioBefore,
            float hullRatioAfter,
            float lockProgress,
            float aimDotDistance,
            float playerViewportWidth,
            float playerViewportHeight,
            int texturedPlayerMaterialCount,
            float directionalLightIntensity,
            float pilotAverageLuminance,
            int pilotBrightPixelCount,
            int cockpitProgressCount,
            float tractorBeamPower,
            float tractorReleasedPower,
            int tractorBeamParticleSystemCount,
            int initialPickupEntityCount,
            int finalPickupEntityCount,
            int pickupCollectionEventCount,
            bool destructionPickupObserved,
            EveUnityFeedbackEvent pickupCollection,
            Vector3 cameraPosition,
            Vector3 cameraForward,
            Vector3 playerPosition,
            Vector3 aimPosition,
            float fieldViewportMinX,
            float fieldViewportMinY,
            float fieldViewportMaxX,
            float fieldViewportMaxY,
            int fieldSplatCount,
            RendererFact[] playerRendererFacts,
            PresentedEntityFact[] presentedEntityFacts)
        {
            var path = Environment.GetEnvironmentVariable("EVEUNITY_WITNESS_FACTS_PATH");
            if (string.IsNullOrWhiteSpace(path)) return;
            var document = new WitnessFacts
            {
                witnessProfile = witnessProfile,
                providerAdvertisement = true,
                providerAssets = true,
                assetBodyTransport = assetBodyTransport,
                environmentPresentation = environmentPresentation,
                fieldVolumeFrameId = fieldVolumeFrameId,
                fieldVolumeLayerCount = fieldVolumeLayerCount,
                fieldVolumeCompositeCount = fieldVolumeCompositeCount,
                fieldParticleFrameId = fieldParticleFrameId,
                fieldParticleCount = fieldParticleCount,
                fieldParticleDispatchCount = fieldParticleDispatchCount,
                fieldParticleDrawCount = fieldParticleDrawCount,
                fieldParticleMapCameraIsolated = fieldParticleMapCameraIsolated,
                fieldParticleGridCenter = fieldParticleGridCenter,
                movement = movement != null,
                aimPresentation = look != null && aimDotDistance >= 49.9f,
                targeting = targeting != null,
                beamPresentation = tractor != null && tractorBeamPower > 0.01f && tractorBeamParticleSystemCount > 0,
                beamRelease = tractorRelease != null && tractorReleasedPower == 0f,
                action = action != null,
                combatPresentation = shot != null,
                shotId = shot?.ShotId ?? "",
                shotHit = shot?.Hit ?? false,
                impactKind = shot?.ImpactKind ?? "",
                appliedDamage = (float)(shot?.AppliedDamage ?? 0),
                shieldAbsorbedDamage = (float)(shot?.ShieldAbsorbedDamage ?? 0),
                shieldRatioBefore = shieldRatioBefore,
                shieldRatioAfter = shieldRatioAfter,
                hullRatioBefore = hullRatioBefore,
                hullRatioAfter = hullRatioAfter,
                lockProgress = lockProgress,
                initialVersion = initialVersion,
                finalVersion = finalVersion,
                movementDistance = movementDistance,
                aimDotDistance = aimDotDistance,
                playerRendererCount = playerRendererCount,
                playerViewportWidth = playerViewportWidth,
                playerViewportHeight = playerViewportHeight,
                texturedPlayerMaterialCount = texturedPlayerMaterialCount,
                directionalLightIntensity = directionalLightIntensity,
                pilotChangedPixels = pilotChangedPixels,
                pilotAverageLuminance = pilotAverageLuminance,
                pilotBrightPixelCount = pilotBrightPixelCount,
                cockpitProgressCount = cockpitProgressCount,
                tractorBeamPower = tractorBeamPower,
                tractorReleasedPower = tractorReleasedPower,
                tractorBeamParticleSystemCount = tractorBeamParticleSystemCount,
                pickupCollection = pickupCollection != null && pickupCollectionEventCount == 1 &&
                    pickupCollection.CargoQuantityAfter - pickupCollection.CargoQuantityBefore == pickupCollection.ScalarValue,
                destructionLoot = pickupCollection != null &&
                    pickupCollection.EventId.StartsWith("ymir-fact:", StringComparison.Ordinal),
                initialPickupEntityCount = initialPickupEntityCount,
                finalPickupEntityCount = finalPickupEntityCount,
                pickupCollectionEventCount = pickupCollectionEventCount,
                pickupEventId = pickupCollection?.EventId ?? "",
                pickupItemKey = pickupCollection?.ItemKey ?? "",
                pickupQuantity = (float)(pickupCollection?.ScalarValue ?? 0),
                cargoQuantityBeforePickup = (float)(pickupCollection?.CargoQuantityBefore ?? 0),
                cargoQuantityAfterPickup = (float)(pickupCollection?.CargoQuantityAfter ?? 0),
                cameraPosition = cameraPosition,
                cameraForward = cameraForward,
                playerPosition = playerPosition,
                aimPosition = aimPosition,
                fieldViewportMinX = fieldViewportMinX,
                fieldViewportMinY = fieldViewportMinY,
                fieldViewportMaxX = fieldViewportMaxX,
                fieldViewportMaxY = fieldViewportMaxY,
                fieldSplatCount = fieldSplatCount,
                playerRenderers = playerRendererFacts,
                presentedEntities = presentedEntityFacts,
                mapChannelRendererCount = mapChannelRendererCount,
                mapChangedPixels = mapChangedPixels,
                pilotCameraExcludesMapChannel = pilotCameraExcludesMapChannel,
                mapCameraIncludesMapChannel = mapCameraIncludesMapChannel,
                receipts = new[] { movement, look, targeting, tractor, tractorRelease, action }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, JsonUtility.ToJson(document, true));
        }

        [Serializable]
        private sealed class WitnessFacts
        {
            public string witnessProfile;
            public bool providerAdvertisement;
            public bool providerAssets;
            public string assetBodyTransport;
            public bool environmentPresentation;
            public long fieldVolumeFrameId;
            public int fieldVolumeLayerCount;
            public long fieldVolumeCompositeCount;
            public long fieldParticleFrameId;
            public int fieldParticleCount;
            public int fieldParticleDispatchCount;
            public int fieldParticleDrawCount;
            public bool fieldParticleMapCameraIsolated;
            public Vector2 fieldParticleGridCenter;
            public bool movement;
            public bool aimPresentation;
            public bool targeting;
            public bool beamPresentation;
            public bool beamRelease;
            public bool action;
            public bool combatPresentation;
            public string shotId;
            public bool shotHit;
            public string impactKind;
            public float appliedDamage;
            public float shieldAbsorbedDamage;
            public float shieldRatioBefore;
            public float shieldRatioAfter;
            public float hullRatioBefore;
            public float hullRatioAfter;
            public float lockProgress;
            public long initialVersion;
            public long finalVersion;
            public float movementDistance;
            public float aimDotDistance;
            public int playerRendererCount;
            public float playerViewportWidth;
            public float playerViewportHeight;
            public int texturedPlayerMaterialCount;
            public float directionalLightIntensity;
            public int pilotChangedPixels;
            public float pilotAverageLuminance;
            public int pilotBrightPixelCount;
            public int cockpitProgressCount;
            public float tractorBeamPower;
            public float tractorReleasedPower;
            public int tractorBeamParticleSystemCount;
            public bool pickupCollection;
            public bool destructionLoot;
            public int initialPickupEntityCount;
            public int finalPickupEntityCount;
            public int pickupCollectionEventCount;
            public string pickupEventId;
            public string pickupItemKey;
            public float pickupQuantity;
            public float cargoQuantityBeforePickup;
            public float cargoQuantityAfterPickup;
            public Vector3 cameraPosition;
            public Vector3 cameraForward;
            public Vector3 playerPosition;
            public Vector3 aimPosition;
            public float fieldViewportMinX;
            public float fieldViewportMinY;
            public float fieldViewportMaxX;
            public float fieldViewportMaxY;
            public int fieldSplatCount;
            public RendererFact[] playerRenderers;
            public PresentedEntityFact[] presentedEntities;
            public int mapChannelRendererCount;
            public int mapChangedPixels;
            public bool pilotCameraExcludesMapChannel;
            public bool mapCameraIncludesMapChannel;
            public WitnessReceipt[] receipts;
        }

        [Serializable]
        private sealed class WitnessReceipt
        {
            public string commandKind;
            public string commandId;
            public string state;
            public string receiptId;
            public string ownerRepo;
            public string authority;
            public string providerId;
            public string surfaceId;
            public bool isProviderOwned;
            public long sourceVersion;

            public static WitnessReceipt From(string commandKind, EveUnitySceneCommandReceipt receipt)
            {
                return new WitnessReceipt
                {
                    commandKind = commandKind,
                    commandId = receipt.CommandId,
                    state = receipt.State,
                    receiptId = receipt.ReceiptId,
                    ownerRepo = receipt.OwnerRepo,
                    authority = receipt.Authority,
                    providerId = receipt.ProviderId,
                    surfaceId = receipt.SurfaceId,
                    isProviderOwned = receipt.IsProviderOwned,
                    sourceVersion = receipt.SourceVersion
                };
            }
        }

        [Serializable]
        private sealed class RendererFact
        {
            public string name;
            public string rendererType;
            public bool enabled;
            public bool activeInHierarchy;
            public int layer;
            public string boundsSize;
            public float viewportWidth;
            public float viewportHeight;
            public int renderedPixelCount;
            public float renderedAverageLuminance;
            public int renderedBrightPixelCount;
            public MaterialFact[] materials;

            public static RendererFact From(Camera camera, Renderer renderer)
            {
                MeasureViewportCoverage(camera, new[] { renderer }, out var width, out var height);
                return new RendererFact
                {
                    name = renderer.name,
                    rendererType = renderer.GetType().Name,
                    enabled = renderer.enabled,
                    activeInHierarchy = renderer.gameObject.activeInHierarchy,
                    layer = renderer.gameObject.layer,
                    boundsSize = FormatVector(renderer.bounds.size),
                    viewportWidth = width,
                    viewportHeight = height,
                    materials = renderer.sharedMaterials
                        .Where(material => material != null)
                        .Select(MaterialFact.From)
                        .ToArray()
                };
            }
        }

        [Serializable]
        private sealed class PresentedEntityFact
        {
            public string entityId;
            public string entityKind;
            public string label;
            public string assetRef;
            public string worldPosition;
            public string worldScale;
            public int rendererCount;
            public int enabledRendererCount;
            public bool intersectsPilotFrustum;
            public float viewportWidth;
            public float viewportHeight;

            public static PresentedEntityFact From(Camera camera, EveUnityPlayableWorldEntityMarker marker)
            {
                var renderers = marker.GetComponentsInChildren<Renderer>(includeInactive: true);
                MeasureViewportCoverage(camera, renderers, out var width, out var height);
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                return new PresentedEntityFact
                {
                    entityId = marker.EntityId,
                    entityKind = marker.EntityKind,
                    label = marker.Label,
                    assetRef = marker.AssetRef,
                    worldPosition = FormatVector(marker.transform.position),
                    worldScale = FormatVector(marker.transform.lossyScale),
                    rendererCount = renderers.Length,
                    enabledRendererCount = renderers.Count(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy),
                    intersectsPilotFrustum = renderers.Any(renderer =>
                        renderer.enabled && renderer.gameObject.activeInHierarchy &&
                        GeometryUtility.TestPlanesAABB(planes, renderer.bounds)),
                    viewportWidth = width,
                    viewportHeight = height
                };
            }
        }

        [Serializable]
        private sealed class MaterialFact
        {
            public string name;
            public string shader;
            public bool shaderSupported;
            public string baseColor;
            public float surface;
            public int textureCount;
            public int renderQueue;

            public static MaterialFact From(Material material) => new MaterialFact
            {
                name = material.name,
                shader = material.shader?.name ?? "",
                shaderSupported = material.shader != null && material.shader.isSupported,
                baseColor = material.HasProperty("_BaseColor")
                    ? FormatColor(material.GetColor("_BaseColor"))
                    : "",
                surface = material.HasProperty("_Surface") ? material.GetFloat("_Surface") : -1f,
                textureCount = material.GetTexturePropertyNames().Count(name => material.GetTexture(name) != null),
                renderQueue = material.renderQueue
            };
        }

        private static void AssertReconciledProviderReceipt(
            EveUnitySceneCommandReceipt receipt,
            string commandId,
            string providerId,
            string surfaceId,
            long minimumSourceVersion)
        {
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.CommandId, Is.EqualTo(commandId));
            Assert.That(receipt.State, Is.EqualTo("reconciled"), receipt.Message);
            Assert.That(receipt.IsProviderOwned, Is.True);
            Assert.That(receipt.ProviderId, Is.EqualTo(providerId));
            Assert.That(receipt.SurfaceId, Is.EqualTo(surfaceId));
            Assert.That(receipt.ReceiptId, Is.Not.Empty);
            Assert.That(receipt.OwnerRepo, Is.Not.Empty);
            Assert.That(receipt.Authority, Is.Not.Empty);
            Assert.That(receipt.SourceVersion, Is.GreaterThan(minimumSourceVersion));
        }

        private static EveUnityPlayableWorldEntityMarker FindMarker(GameObject root, string entityId)
        {
            foreach (var marker in root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>())
            {
                if (string.Equals(marker.EntityId, entityId, StringComparison.Ordinal))
                    return marker;
            }
            throw new InvalidOperationException($"Projected world has no entity marker '{entityId}'.");
        }

        private static bool HasAnyTexture(Material material)
        {
            foreach (var propertyName in material.GetTexturePropertyNames())
            {
                if (material.GetTexture(propertyName) != null)
                    return true;
            }
            return false;
        }

        private static string FormatVector(Vector3 value) =>
            $"{value.x:R},{value.y:R},{value.z:R}";

        private static string FormatColor(Color value) =>
            $"{value.r:R},{value.g:R},{value.b:R},{value.a:R}";

        private static void MeasureViewportCoverage(
            Camera camera,
            IReadOnlyList<Renderer> renderers,
            out float width,
            out float height)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var renderer in renderers)
            {
                var bounds = renderer.bounds;
                for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                for (var z = -1; z <= 1; z += 2)
                {
                    var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                    var viewport = camera.WorldToViewportPoint(point);
                    if (viewport.z <= 0f) continue;
                    min = Vector2.Min(min, viewport);
                    max = Vector2.Max(max, viewport);
                }
            }
            width = float.IsInfinity(min.x) ? 0f : Mathf.Max(0f, max.x - min.x);
            height = float.IsInfinity(min.y) ? 0f : Mathf.Max(0f, max.y - min.y);
        }

        private static void MeasureLuminance(
            Texture2D texture,
            out float average,
            out int brightPixelCount)
        {
            double total = 0;
            brightPixelCount = 0;
            var pixels = texture.GetPixels32();
            foreach (var pixel in pixels)
            {
                var luminance = (0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b) / 255.0;
                total += luminance;
                if (luminance >= 0.2)
                    brightPixelCount++;
            }
            average = pixels.Length == 0 ? 0f : (float)(total / pixels.Length);
        }

        private static void MeasureRendererOutputs(
            Camera camera,
            RenderTexture target,
            Texture2D pixels,
            IReadOnlyList<Renderer> allRenderers,
            IReadOnlyList<Renderer> measuredRenderers,
            IReadOnlyList<RendererFact> facts)
        {
            var enabled = allRenderers.Select(renderer => renderer.enabled).ToArray();
            var clearFlags = camera.clearFlags;
            var background = camera.backgroundColor;
            try
            {
                foreach (var renderer in allRenderers)
                    renderer.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                pixels.Apply();
                var baseline = pixels.GetPixels();
                for (var index = 0; index < measuredRenderers.Count; index++)
                {
                    var fact = facts[index];
                    var rendererLayer = measuredRenderers[index].gameObject.layer;
                    if (rendererLayer >= 0 && rendererLayer < 32 &&
                        (camera.cullingMask & (1 << rendererLayer)) == 0)
                    {
                        fact.renderedPixelCount = 0;
                        fact.renderedAverageLuminance = 0;
                        fact.renderedBrightPixelCount = 0;
                        continue;
                    }
                    measuredRenderers[index].enabled = true;
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    pixels.Apply();
                    MeasureDifference(
                        pixels.GetPixels(),
                        baseline,
                        out fact.renderedPixelCount,
                        out fact.renderedAverageLuminance,
                        out fact.renderedBrightPixelCount);
                    measuredRenderers[index].enabled = false;
                }
            }
            finally
            {
                for (var index = 0; index < allRenderers.Count; index++)
                    allRenderers[index].enabled = enabled[index];
                camera.clearFlags = clearFlags;
                camera.backgroundColor = background;
            }
        }

        private static void MeasureDifference(
            IReadOnlyList<Color> actual,
            IReadOnlyList<Color> baseline,
            out int changedPixelCount,
            out float averageLuminance,
            out int brightPixelCount)
        {
            changedPixelCount = 0;
            brightPixelCount = 0;
            double total = 0;
            var count = Math.Min(actual.Count, baseline.Count);
            for (var index = 0; index < count; index++)
            {
                var delta = new Color(
                    Mathf.Abs(actual[index].r - baseline[index].r),
                    Mathf.Abs(actual[index].g - baseline[index].g),
                    Mathf.Abs(actual[index].b - baseline[index].b));
                var magnitude = delta.r + delta.g + delta.b;
                if (magnitude > 0.08f) changedPixelCount++;
                var luminance = delta.r * 0.2126f + delta.g * 0.7152f + delta.b * 0.0722f;
                total += luminance;
                if (luminance >= 0.2f) brightPixelCount++;
            }
            averageLuminance = count == 0 ? 0f : (float)(total / count);
        }

        private static int CountChangedPixels(Texture2D texture, Color background)
        {
            var changed = 0;
            foreach (var pixel in texture.GetPixels())
            {
                if (Mathf.Abs(pixel.r - background.r) +
                    Mathf.Abs(pixel.g - background.g) +
                    Mathf.Abs(pixel.b - background.b) > 0.08f)
                    changed++;
            }
            return changed;
        }

        private static void WriteFieldLayerCaptures(GameObject root, EveUnityPlayableWorldProjection world, string capturePath)
        {
            var renderer = root.GetComponent<EveFieldsSplatLayerRenderer>();
            if (renderer == null || world == null || string.IsNullOrWhiteSpace(capturePath)) return;
            var directory = Path.GetDirectoryName(Path.GetFullPath(capturePath));
            foreach (var field in world.FieldVolumes)
            {
                if (!field.Props.TryGetValue("layerBindings", out var bindings)) continue;
                foreach (var pair in (bindings ?? "").Split(';'))
                {
                    var separator = pair.IndexOf('=');
                    if (separator <= 0) continue;
                    var layerKey = pair.Substring(0, separator).Trim();
                    if (!renderer.TryGetTexture(layerKey, out var texture) || texture == null) continue;
                    var previous = RenderTexture.active;
                    RenderTexture.active = texture;
                    var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                    copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    copy.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(Path.Combine(directory, $"field-{layerKey}.png"), copy.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(copy);

                    RenderTexture.active = texture;
                    var floatCopy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
                    floatCopy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    floatCopy.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(
                        Path.Combine(directory, $"field-{layerKey}.exr"),
                        floatCopy.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
                    UnityEngine.Object.DestroyImmediate(floatCopy);
                }
            }
        }

        private static void WriteFieldCloudCapture(GameObject root, string capturePath)
        {
            var renderer = root.GetComponent<EveUnityFieldsVolumeRenderer>();
            if (renderer == null || string.IsNullOrWhiteSpace(capturePath)) return;
            var raymarchField = typeof(EveUnityFieldsVolumeRenderer).GetField(
                "_raymarchTexture",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var historyTexturesField = typeof(EveUnityFieldsVolumeRenderer).GetField(
                "_historyTextures",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var historyIndexField = typeof(EveUnityFieldsVolumeRenderer).GetField(
                "_historyTextureIndex",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var materialField = typeof(EveUnityFieldsVolumeRenderer).GetField(
                "_material",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var metadataField = typeof(EveUnityFieldsVolumeRenderer).GetField(
                "_programMetadata",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var raymarchTexture = raymarchField?.GetValue(renderer) as RenderTexture;
            var historyTextures = historyTexturesField?.GetValue(renderer) as RenderTexture[];
            var historyIndex = historyIndexField?.GetValue(renderer) is int value ? value : -1;
            var previous = RenderTexture.active;
            var directory = Path.GetDirectoryName(Path.GetFullPath(capturePath));
            WriteFloatTexture(raymarchTexture, Path.Combine(directory, "field-volume-raymarch.exr"));
            var historyTexture = historyIndex >= 0 && historyTextures != null && historyIndex < historyTextures.Length
                ? historyTextures[historyIndex]
                : null;
            WriteFloatTexture(historyTexture, Path.Combine(directory, "field-volume-history.exr"));
            WriteFloatTexture(historyTexture ?? raymarchTexture, Path.Combine(directory, "field-volume-cloud.exr"));
            WriteVolumeMaterialFacts(
                materialField?.GetValue(renderer) as Material,
                metadataField?.GetValue(renderer) as IReadOnlyDictionary<string, string>,
                Path.Combine(directory, "field-volume-material.txt"));
            RenderTexture.active = previous;
        }

        private static void WriteVolumeMaterialFacts(
            Material material,
            IReadOnlyDictionary<string, string> metadata,
            string path)
        {
            if (material == null || metadata == null) return;
            var facts = new List<string>();
            foreach (var binding in metadata.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                if (binding.Key.Contains(".floatPort.", StringComparison.Ordinal))
                    facts.Add($"{binding.Key}={binding.Value}:{material.GetFloat(binding.Value):R}");
                else if (binding.Key.Contains(".vectorPort.", StringComparison.Ordinal))
                    facts.Add($"{binding.Key}={binding.Value}:{material.GetVector(binding.Value)}");
                else if (binding.Key.Contains(".texturePort.", StringComparison.Ordinal))
                {
                    var texture = material.HasProperty(binding.Value) ? material.GetTexture(binding.Value) : null;
                    facts.Add($"{binding.Key}={binding.Value}:{texture?.name ?? "<none>"}:{texture?.width ?? 0}x{texture?.height ?? 0}");
                }
            }
            File.WriteAllLines(path, facts);
        }

        private static void WriteFloatTexture(RenderTexture texture, string path)
        {
            if (texture == null || string.IsNullOrWhiteSpace(path)) return;
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(path, copy.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
            UnityEngine.Object.DestroyImmediate(copy);
        }

        private static EveUnitySceneProviderSurfaceDocument WorldDocument()
        {
            var entities = new[]
            {
                Entity("player-node", "player", "player", "Vanguard", "0,0,0", "1.2", true),
                Entity("enemy-node", "enemy", "enemy", "Raider", "3,0,4", "1", false),
                Entity("landmark-node", "landmark", "landmark", "Beacon", "-4,0,6", "1.8", false)
            };
            var document = new EveSurfaceDocument(
                "eve.world-smoke",
                "game.runtime",
                "Generic interactive world",
                1,
                "2026-07-10T00:00:00Z",
                new EveSurfaceTree(
                    "eve.world-smoke.surface",
                    new EveSurfaceComponent(
                        "world.root",
                        "surface",
                        Props(),
                        new[]
                        {
                            new EveSurfaceComponent(
                                "world.scene",
                                "world.scene3d",
                                Props(
                                    ("playerEntityId", "player"),
                                    ("movementCommand", "world.move"),
                                    ("inputProfile", "third-person"),
                                    ("cameraRig", "third-person-orbit")),
                                entities)
                        }),
                    Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());
            return new EveUnitySceneProviderSurfaceDocument(
                document,
                new EveUnitySceneProviderSurfaceAdvertisement(
                    "eve.world-smoke.surface",
                    "interactive-world",
                    new EveUnitySceneWorldInteraction(
                        "provider-authored-world-surface",
                        "world.commands",
                        "gamecult.eve.command_receipt.v1",
                        "provider-owns-world-state-command-acceptance-and-receipts")),
                "cultmesh://eve.world-smoke/surfaces/world",
                1);
        }

        private static EveSurfaceComponent Entity(
            string nodeId,
            string entityId,
            string entityKind,
            string label,
            string position,
            string radius,
            bool controllable)
        {
            return new EveSurfaceComponent(
                nodeId,
                "world.entity3d",
                Props(
                    ("entityId", entityId),
                    ("entityKind", entityKind),
                    ("label", label),
                    ("position", position),
                    ("radius", radius),
                    ("assetRef", $"cultmesh://eve.world-smoke/assets/{entityKind}"),
                    ("selectable", "true"),
                    ("controllable", controllable ? "true" : "false"),
                    ("moveCommand", controllable ? "world.move" : "")),
                Array.Empty<EveSurfaceComponent>());
        }

        private static Dictionary<string, string> Props(params (string Key, string Value)[] values)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values) props[value.Key] = value.Value;
            return props;
        }

        private sealed class WorldSmokeProvider : MonoBehaviour,
            IEveUnitySceneProviderSurfaceDocumentSource,
            IEveUnitySceneCommandSink
        {
            public EveUnitySceneProviderSurfaceDocument CurrentDocument { get; set; }
            public string SinkKind => "world-smoke-command-sink";
            public List<EveSurfaceCommandRequest> Submitted { get; } = new List<EveSurfaceCommandRequest>();
            public event Action<EveUnitySceneProviderSurfaceDocument> DocumentAvailable;
            public void Submit(EveSurfaceCommandRequest request) => Submitted.Add(request);
        }
    }
}
