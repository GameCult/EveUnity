using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityScene;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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
            var root = new GameObject("Generic Eve World Projection");
            var cameraObject = new GameObject("Generic Eve Capture Camera");
            var lightObject = new GameObject("Generic Eve World Light");
            RenderTexture target = null;
            Texture2D pixels = null;
            EveUnityCultMeshPlayableWorldProvider provider = null;
            EveUnityPlayableWorldRuntime runtime = null;
            WitnessReceipt movementReceipt = null;
            WitnessReceipt focusReceipt = null;
            WitnessReceipt actionReceipt = null;
            EveUnityShotReceipt combatShot = null;
            string combatActionId = "";
            string collectedPickupId = "";
            bool pickupCollected = false;
            long initialVersion = 0;
            float movementDistance = 0f;
            var commandReceipts = new Dictionary<string, EveUnitySceneCommandReceipt>(StringComparer.Ordinal);

            try
            {
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
                    if (published) break;
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                Assert.That(provider.Selection, Is.Not.Null);
                Assert.That(provider.Selection.VerseId, Is.Not.Empty);
                Assert.That(provider.Selection.ProviderId, Is.Not.Empty);
                Assert.That(provider.Selection.SurfaceId, Is.Not.Empty);
                runtime = EveUnityPlayableWorldRuntime.CreateForGameObjectScene(
                    root.transform,
                    provider,
                    provider,
                    null,
                    provider,
                    provider);
                runtime.ReceiptAvailable += receipt => commandReceipts[receipt.CommandId] = receipt;
                var initial = runtime.Connect();
                var entityDeadline = Time.realtimeSinceStartup + 12f;
                while (initial.ActiveEntities == 0 && Time.realtimeSinceStartup < entityDeadline)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    initial = runtime.Refresh();
                }
                Assert.That(initial.ActiveEntities, Is.GreaterThan(0));
                Assert.That(runtime.ActiveWorld.PlayerEntityId, Is.Not.Empty);

                var playerId = runtime.ActiveWorld.PlayerEntityId;
                var marker = FindMarker(root, playerId);
                var seededPickup = root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>()
                    .SingleOrDefault(candidate => string.Equals(candidate.EntityKind, "pickup", StringComparison.Ordinal));
                Assert.That(seededPickup, Is.Not.Null, "The daemon-authored salvage pickup was not lowered through the Eve entity view.");
                collectedPickupId = seededPickup.EntityId;
                Assert.That(
                    seededPickup.GetComponentsInChildren<Transform>(includeInactive: true).Length,
                    Is.GreaterThan(1),
                    "The generic client did not instantiate the provider-authored pickup asset.");
                var realtimeAction = provider.CurrentInputCapability?.Actions
                    .FirstOrDefault(action => string.Equals(action.ActionId, "simulation.rate.realtime", StringComparison.Ordinal));
                Assert.That(realtimeAction, Is.Not.Null, "Terminus did not advertise its user-controlled simulation clock.");
                var resume = runtime.SubmitActionIntent(playerId, realtimeAction.ActionId);
                var resumeDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < resumeDeadline &&
                       !commandReceipts.TryGetValue(resume.CommandId, out _))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                Assert.That(commandReceipts.TryGetValue(resume.CommandId, out var resumeReceipt), Is.True);
                Assert.That(resumeReceipt.State, Is.EqualTo("accepted").Or.EqualTo("reconciled"), resumeReceipt.Message);
                Assert.That(
                    marker.GetComponentsInChildren<Transform>(includeInactive: true).Length,
                    Is.GreaterThan(1),
                    "The generic client substituted its primitive fallback instead of the provider-authored AssetBundle prefab.");
                var initialPosition = marker.transform.position;
                initialVersion = runtime.ActiveVersion;
                var request = runtime.SubmitMoveVectorIntent(playerId, 1f, 0f, 1f);

                var deadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < deadline &&
                       (!commandReceipts.TryGetValue(request.CommandId, out var observedMovement) ||
                        runtime.ActiveVersion < observedMovement.SourceVersion ||
                        Vector3.Distance(FindMarker(root, playerId).transform.position, initialPosition) < 0.01f))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }

                Assert.That(commandReceipts.TryGetValue(request.CommandId, out var movementCommandReceipt), Is.True);
                Assert.That(
                    movementCommandReceipt.State,
                    Is.EqualTo("accepted").Or.EqualTo("reconciled"),
                    movementCommandReceipt.Message);
                Assert.That(movementCommandReceipt.SourceVersion, Is.GreaterThan(initialVersion));
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(movementCommandReceipt.SourceVersion));
                movementDistance = Vector3.Distance(FindMarker(root, playerId).transform.position, initialPosition);
                Assert.That(movementDistance, Is.GreaterThan(0.01f));
                var pickupDeadline = Time.realtimeSinceStartup + 8f;
                while (Time.realtimeSinceStartup < pickupDeadline &&
                       root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>()
                           .Any(candidate => string.Equals(candidate.EntityId, collectedPickupId, StringComparison.Ordinal)))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                pickupCollected = !root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>()
                    .Any(candidate => string.Equals(candidate.EntityId, collectedPickupId, StringComparison.Ordinal));
                Assert.That(pickupCollected, Is.True, "Ymir/daemon simulation did not collect the seeded pickup when the player crossed it.");
                movementReceipt = WitnessReceipt.From("movement", movementCommandReceipt);

                var stop = runtime.SubmitMoveVectorIntent(playerId, 0f, 0f, 0f);
                var stopDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < stopDeadline &&
                       (!commandReceipts.TryGetValue(stop.CommandId, out var observedStop) ||
                        runtime.ActiveVersion < observedStop.SourceVersion))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                Assert.That(commandReceipts.TryGetValue(stop.CommandId, out var stopCommandReceipt), Is.True);
                Assert.That(
                    stopCommandReceipt.State,
                    Is.EqualTo("accepted").Or.EqualTo("reconciled"),
                    stopCommandReceipt.Message);

                var pauseAction = provider.CurrentInputCapability.Actions
                    .Single(candidate => string.Equals(candidate.ActionId, "simulation.pause", StringComparison.Ordinal));
                var pause = runtime.SubmitActionIntent(playerId, pauseAction.ActionId);
                var pauseDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < pauseDeadline &&
                       (!commandReceipts.TryGetValue(pause.CommandId, out var observedPause) ||
                        runtime.ActiveVersion < observedPause.SourceVersion))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                Assert.That(commandReceipts.TryGetValue(pause.CommandId, out var pauseReceipt), Is.True);
                Assert.That(pauseReceipt.State, Is.EqualTo("accepted").Or.EqualTo("reconciled"), pauseReceipt.Message);

                var movementVersion = runtime.ActiveVersion;
                var visibleMarkers = root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>();
                var hostileTargets = visibleMarkers
                    .Where(candidate => candidate.Selectable && !candidate.Controllable &&
                                        !string.Equals(candidate.Faction, marker.Faction, StringComparison.Ordinal))
                    .OrderBy(candidate => Vector3.Distance(candidate.transform.position, marker.transform.position))
                    .ToArray();
                Assert.That(
                    hostileTargets,
                    Is.Not.Empty,
                    "No selectable hostile contact was lowered. Visible entities: " +
                    string.Join(", ", visibleMarkers.Select(candidate =>
                        $"{candidate.EntityId}[kind={candidate.EntityKind},faction={candidate.Faction},selectable={candidate.Selectable}]")));
                var targetMarker = hostileTargets[0];
                var focus = runtime.SubmitTargetIntent(playerId, targetMarker.EntityId);
                var focusVersion = runtime.ActiveVersion;
                var trajectoryRenderer = root.AddComponent<EveUnityShotTrajectoryRenderer>();
                runtime.ShotAvailable += shot =>
                {
                    var playerIdentity = provider.CurrentEntityView?.Identities
                        .FirstOrDefault(identity => string.Equals(identity.EntityId, playerId, StringComparison.Ordinal));
                    if (playerIdentity != null && shot.SourceEntityIndex == playerIdentity.EntityIndex)
                    {
                        combatShot = shot;
                        trajectoryRenderer.Present(shot);
                    }
                };
                Assert.That(provider.CurrentInputCapability, Is.Not.Null, "The pilot world did not advertise a typed input capability document.");
                Assert.That(provider.CurrentInputCapability.Actions, Is.Not.Empty, "The pilot input capability advertised no available actions.");
                var advertisedAction = provider.CurrentInputCapability.Actions
                    .First(candidate => string.Equals(candidate.Category, "weapon-group", StringComparison.Ordinal) &&
                                        string.Equals(candidate.Availability, "available", StringComparison.OrdinalIgnoreCase));
                combatActionId = advertisedAction.ActionId;
                var action = runtime.SubmitActionIntent(playerId, advertisedAction.ActionId);
                var resumeCombat = runtime.SubmitActionIntent(playerId, realtimeAction.ActionId);
                var resumeCombatDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < resumeCombatDeadline &&
                       (!commandReceipts.TryGetValue(resumeCombat.CommandId, out var observedResumeCombat) ||
                        runtime.ActiveVersion < observedResumeCombat.SourceVersion))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                Assert.That(commandReceipts.TryGetValue(resumeCombat.CommandId, out var resumeCombatReceipt), Is.True);
                Assert.That(resumeCombatReceipt.State, Is.EqualTo("accepted").Or.EqualTo("reconciled"), resumeCombatReceipt.Message);
                var actionDeadline = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < actionDeadline &&
                       (!commandReceipts.TryGetValue(focus.CommandId, out var observedFocus) ||
                        runtime.ActiveVersion < observedFocus.SourceVersion ||
                        !commandReceipts.TryGetValue(action.CommandId, out var observedAction) ||
                        runtime.ActiveVersion < observedAction.SourceVersion || combatShot == null))
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    runtime.Refresh();
                }
                Assert.That(commandReceipts.TryGetValue(focus.CommandId, out var focusCommandReceipt), Is.True);
                Assert.That(
                    focusCommandReceipt.State,
                    Is.EqualTo("accepted").Or.EqualTo("reconciled"),
                    focusCommandReceipt.Message);
                Assert.That(focusCommandReceipt.SourceVersion, Is.GreaterThan(movementVersion));
                focusReceipt = WitnessReceipt.From("targeting", focusCommandReceipt);
                Assert.That(commandReceipts.TryGetValue(action.CommandId, out var actionCommandReceipt), Is.True);
                Assert.That(
                    actionCommandReceipt.State,
                    Is.EqualTo("accepted").Or.EqualTo("reconciled"),
                    actionCommandReceipt.Message);
                Assert.That(actionCommandReceipt.SourceVersion, Is.GreaterThan(focusVersion));
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(actionCommandReceipt.SourceVersion));
                Assert.That(combatShot, Is.Not.Null,
                    $"The daemon accepted fire but published no shot receipt through Eve. Weapon state: {DescribeWeaponStates(provider)}");
                Assert.That(combatShot.ShotId, Is.Not.Empty);
                var playerEntityIndex = provider.CurrentEntityView.Identities
                    .Single(identity => string.Equals(identity.EntityId, playerId, StringComparison.Ordinal)).EntityIndex;
                Assert.That(combatShot.SourceEntityIndex, Is.EqualTo(playerEntityIndex));
                Assert.That(combatShot.TargetEntityIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(combatShot.PresentationKind, Is.Not.Empty);
                Assert.That(trajectoryRenderer.ActiveTrajectoryCount, Is.GreaterThan(0));
                actionReceipt = WitnessReceipt.From("action", actionCommandReceipt);

                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.4f;
                lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.055f, 0.08f, 1f);
                foreach (var entityMarker in root.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>())
                {
                    foreach (var renderer in entityMarker.GetComponentsInChildren<Renderer>())
                    {
                        renderer.material.color = entityMarker.Controllable
                            ? new Color(0.18f, 0.75f, 1f)
                            : entityMarker.EntityKind == "enemy"
                                ? new Color(1f, 0.25f, 0.2f)
                                : new Color(1f, 0.78f, 0.15f);
                    }
                }
                var playerMarker = FindMarker(root, playerId);
                var playerRenderers = playerMarker.GetComponentsInChildren<Renderer>();
                Assert.That(playerRenderers, Is.Not.Empty, "The provider-authored player prefab has no renderable visual.");
                var playerBounds = playerRenderers[0].bounds;
                foreach (var renderer in playerRenderers)
                    playerBounds.Encapsulate(renderer.bounds);
                var halfFovRadians = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                var cameraDistance = Mathf.Max(8f, playerBounds.extents.magnitude / Mathf.Tan(halfFovRadians) * 1.35f);
                camera.nearClipPlane = Mathf.Max(0.01f, cameraDistance * 0.001f);
                camera.farClipPlane = Mathf.Max(1000f, cameraDistance + playerBounds.size.magnitude * 4f);
                camera.transform.position = playerBounds.center + new Vector3(1f, 0.65f, -1.2f).normalized * cameraDistance;
                camera.transform.LookAt(playerBounds.center);
                target = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                yield return null;
                camera.Render();
                RenderTexture.active = target;
                pixels = new Texture2D(640, 360, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath)));
                File.WriteAllBytes(capturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(capturePath).Length, Is.GreaterThan(1024));
                WriteWitnessFacts(
                    initialVersion,
                    runtime.ActiveVersion,
                    movementDistance,
                    movementReceipt,
                    focusReceipt,
                    actionReceipt,
                    combatActionId,
                    combatShot,
                    collectedPickupId,
                    pickupCollected);
            }
            finally
            {
                RenderTexture.active = null;
                var camera = cameraObject.GetComponent<Camera>();
                if (camera != null) camera.targetTexture = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                runtime?.Dispose();
                UnityEngine.Object.DestroyImmediate(lightObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void WriteWitnessFacts(
            long initialVersion,
            long finalVersion,
            float movementDistance,
            WitnessReceipt movement,
            WitnessReceipt targeting,
            WitnessReceipt action,
            string combatActionId,
            EveUnityShotReceipt combatShot,
            string collectedPickupId,
            bool pickupCollected)
        {
            var path = Environment.GetEnvironmentVariable("EVEUNITY_WITNESS_FACTS_PATH");
            if (string.IsNullOrWhiteSpace(path)) return;
            var document = new WitnessFacts
            {
                providerAdvertisement = true,
                providerAssets = true,
                movement = movement != null,
                targeting = targeting != null,
                action = action != null,
                combatShot = combatShot != null,
                combatActionId = combatActionId ?? "",
                shotId = combatShot?.ShotId ?? "",
                shotOutcome = combatShot?.Outcome ?? "",
                shotPresentationKind = combatShot?.PresentationKind ?? "",
                shotHit = combatShot?.Hit ?? false,
                pickupVisible = !string.IsNullOrWhiteSpace(collectedPickupId),
                pickupCollected = pickupCollected,
                pickupId = collectedPickupId ?? "",
                initialVersion = initialVersion,
                finalVersion = finalVersion,
                movementDistance = movementDistance,
                receipts = new[] { movement, targeting, action }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, JsonUtility.ToJson(document, true));
        }

        [Serializable]
        private sealed class WitnessFacts
        {
            public bool providerAdvertisement;
            public bool providerAssets;
            public bool movement;
            public bool targeting;
            public bool action;
            public bool combatShot;
            public string combatActionId;
            public string shotId;
            public string shotOutcome;
            public string shotPresentationKind;
            public bool shotHit;
            public bool pickupVisible;
            public bool pickupCollected;
            public string pickupId;
            public long initialVersion;
            public long finalVersion;
            public float movementDistance;
            public WitnessReceipt[] receipts;
        }

        [Serializable]
        private sealed class WitnessReceipt
        {
            public string commandKind;
            public string commandId;
            public string state;
            public long sourceVersion;

            public static WitnessReceipt From(string commandKind, EveUnitySceneCommandReceipt receipt)
            {
                return new WitnessReceipt
                {
                    commandKind = commandKind,
                    commandId = receipt.CommandId,
                    state = receipt.State,
                    sourceVersion = receipt.SourceVersion
                };
            }
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

        private static string DescribeWeaponStates(EveUnityCultMeshPlayableWorldProvider provider)
        {
            return string.Join("; ", Flatten(provider.CurrentDocument.SurfaceDocument.Surface.Root)
                .Where(node => string.Equals(node.Kind, "weapon.state", StringComparison.Ordinal))
                .Select(node => string.Join(",", new[]
                {
                    Prop(node, "behaviorKind"),
                    $"pending={Prop(node, "triggerPending")}",
                    $"firing={Prop(node, "firing")}",
                    $"charging={Prop(node, "charging")}",
                    $"charged={Prop(node, "charged")}",
                    $"lock={Prop(node, "lockProgress")}",
                    $"target={Prop(node, "targetEntityId")}",
                    $"refusal={Prop(node, "lastRefusalReason")}"
                })));
        }

        private static string Prop(EveSurfaceComponent node, string key) =>
            node.Props.TryGetValue(key, out var value) ? value : "";

        private static IEnumerable<EveSurfaceComponent> Flatten(EveSurfaceComponent root)
        {
            yield return root;
            foreach (var child in root.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
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
