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
            var mapCapturePath = Environment.GetEnvironmentVariable("EVEUNITY_AETHERIA_MAP_CAPTURE_PATH") ??
                                 Path.Combine(Application.temporaryCachePath, "aetheria-daemon-map.png");
            var root = new GameObject("Generic Eve World Projection");
            var cameraObject = new GameObject("Generic Eve Capture Camera");
            var mapCameraObject = new GameObject("Generic Eve Map Camera");
            var lightObject = new GameObject("Generic Eve World Light");
            RenderTexture target = null;
            Texture2D pixels = null;
            EveUnityCultMeshPlayableWorldProvider provider = null;
            EveUnityPlayableWorldClientHost host = null;
            EveUnityPlayableWorldRuntime runtime = null;
            WitnessReceipt movementReceipt = null;
            WitnessReceipt lookReceipt = null;
            WitnessReceipt focusReceipt = null;
            WitnessReceipt actionReceipt = null;
            EveUnityShotReceipt observedShot = null;
            EveUnityCombatPresentation observedCombat = null;
            float shieldRatioBefore = 0f;
            float hullRatioBefore = 0f;
            long initialVersion = 0;
            float movementDistance = 0f;
            float aimDotDistance = 0f;

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
                var publicationDeadline = Time.realtimeSinceStartup + 10f;
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
                host = root.AddComponent<EveUnityPlayableWorldClientHost>();
                host.Configure(
                    root.transform,
                    provider,
                    provider,
                    provider,
                    provider,
                    provider);
                host.Connect();
                host.ShotAvailable += shot => observedShot = shot;
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
                var aimRenderer = root.GetComponent<EveUnityAimPresentationRenderer>();
                Assert.That(aimRenderer, Is.Not.Null);

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
                }
                AssertReconciledProviderReceipt(
                    runtime.LastReceipt,
                    action!.CommandId,
                    provider.Selection.ProviderId,
                    provider.Selection.SurfaceId,
                    focusVersion);
                Assert.That(runtime.ActiveVersion, Is.GreaterThanOrEqualTo(runtime.LastReceipt.SourceVersion));
                actionReceipt = WitnessReceipt.From("action", runtime.LastReceipt);

                var shotDeadline = Time.realtimeSinceStartup + 15f;
                while (Time.realtimeSinceStartup < shotDeadline && observedShot == null)
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    runtime.Refresh();
                }
                Assert.That(observedShot, Is.Not.Null,
                    "A reconciled fire request never reached daemon-owned lock acquisition and shot resolution.");
                observedCombat = EveUnityCombatPresentation.Find(runtime.ActiveProjection);
                Assert.That(observedShot.TargetEntityIndex, Is.EqualTo(targetEntityIndex));
                Assert.That(observedShot.LockQuality, Is.GreaterThan(0.99));
                var trajectories = root.GetComponent<EveUnityShotTrajectoryRenderer>();
                var combatRenderer = root.GetComponent<EveUnityCombatPresentationRenderer>();
                Assert.That(trajectories, Is.Not.Null);
                Assert.That(trajectories.ActiveTrajectoryCount, Is.GreaterThan(0));
                Assert.That(combatRenderer, Is.Not.Null);
                combatRenderer.RefreshNow();
                if (observedCombat != null && !string.IsNullOrWhiteSpace(observedCombat.SelectedTargetEntityId))
                {
                    Assert.That(combatRenderer.ReticleVisible, Is.True);
                    Assert.That(combatRenderer.LockVisible, Is.True);
                }
                if (observedShot.Hit && (observedShot.AppliedDamage > 0 || observedShot.ShieldAbsorbedDamage > 0))
                    Assert.That(combatRenderer.HitMarkerVisible, Is.True);

                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.4f;
                lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
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
                aimRenderer.RefreshNow();
                Assert.That(aimRenderer.ViewDotVisible, Is.True);
                aimDotDistance = Vector3.Distance(playerMarker.transform.position, aimRenderer.ViewDotPosition);
                Assert.That(aimDotDistance, Is.GreaterThanOrEqualTo(49.9f));
                Assert.That(camera.cullingMask & (1 << mapLayer), Is.Zero);
                Assert.That(camera.fieldOfView, Is.EqualTo(60f).Within(0.001f));
                var playerViewport = camera.WorldToViewportPoint(playerMarker.transform.position);
                Assert.That(playerViewport.x, Is.EqualTo(0.9f).Within(0.01f));
                Assert.That(playerViewport.y, Is.EqualTo(0.55f).Within(0.01f));

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
                Assert.That(pilotChangedPixels, Is.GreaterThan(25), "The pilot camera rendered no provider-authored world pixels.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath)));
                File.WriteAllBytes(capturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(capturePath).Length, Is.GreaterThan(1024));

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
                mapCamera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();
                var mapChangedPixels = CountChangedPixels(pixels, mapCamera.backgroundColor);
                Assert.That(mapChangedPixels, Is.GreaterThan(25), "The map-only camera rendered no provider-authored map glyph pixels.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mapCapturePath)));
                File.WriteAllBytes(mapCapturePath, pixels.EncodeToPNG());
                Assert.That(new FileInfo(mapCapturePath).Length, Is.GreaterThan(1024));
                WriteWitnessFacts(
                    initialVersion,
                    runtime.ActiveVersion,
                    movementDistance,
                    playerRenderers.Length,
                    pilotChangedPixels,
                    mapRenderers.Count,
                    mapChangedPixels,
                    (camera.cullingMask & (1 << mapLayer)) == 0,
                    (mapCamera.cullingMask & (1 << mapLayer)) != 0,
                    movementReceipt,
                    lookReceipt,
                    focusReceipt,
                    actionReceipt,
                    observedShot,
                    shieldRatioBefore,
                    observedCombat?.ShieldRatio ?? shieldRatioBefore,
                    hullRatioBefore,
                    observedCombat?.HullRatio ?? hullRatioBefore,
                    (float)observedShot.LockQuality,
                    aimDotDistance);
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
                UnityEngine.Object.DestroyImmediate(lightObject);
                UnityEngine.Object.DestroyImmediate(mapCameraObject);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void WriteWitnessFacts(
            long initialVersion,
            long finalVersion,
            float movementDistance,
            int playerRendererCount,
            int pilotChangedPixels,
            int mapChannelRendererCount,
            int mapChangedPixels,
            bool pilotCameraExcludesMapChannel,
            bool mapCameraIncludesMapChannel,
            WitnessReceipt movement,
            WitnessReceipt look,
            WitnessReceipt targeting,
            WitnessReceipt action,
            EveUnityShotReceipt shot,
            float shieldRatioBefore,
            float shieldRatioAfter,
            float hullRatioBefore,
            float hullRatioAfter,
            float lockProgress,
            float aimDotDistance)
        {
            var path = Environment.GetEnvironmentVariable("EVEUNITY_WITNESS_FACTS_PATH");
            if (string.IsNullOrWhiteSpace(path)) return;
            var document = new WitnessFacts
            {
                providerAdvertisement = true,
                providerAssets = true,
                movement = movement != null,
                aimPresentation = look != null && aimDotDistance >= 49.9f,
                targeting = targeting != null,
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
                pilotChangedPixels = pilotChangedPixels,
                mapChannelRendererCount = mapChannelRendererCount,
                mapChangedPixels = mapChangedPixels,
                pilotCameraExcludesMapChannel = pilotCameraExcludesMapChannel,
                mapCameraIncludesMapChannel = mapCameraIncludesMapChannel,
                receipts = new[] { movement, look, targeting, action }
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
            public bool aimPresentation;
            public bool targeting;
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
            public int pilotChangedPixels;
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
