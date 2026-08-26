using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityBeamPresentationTests
    {
        [Test]
        public void ProjectionPreservesProviderBeamSourceAssetAndFossilShape()
        {
            var beam = EveUnityBeamPresentation.FindAll(Projection(Node(
                "tractor", "beam.presentation", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceEntityId"] = "run.zone.0.entity.4",
                    ["assetRole"] = "effect.beam.tractor",
                    ["directionMode"] = "source-forward.v1",
                    ["renderChannel"] = "world.effects",
                    ["activationActionId"] = "pilot.scoop",
                    ["power"] = "0.625",
                    ["activationThreshold"] = "0.01",
                    ["radius"] = "25",
                    ["maximumDistance"] = "75"
                }))).Single();

            Assert.That(beam.SourceEntityId, Is.EqualTo("run.zone.0.entity.4"));
            Assert.That(beam.AssetRole, Is.EqualTo("effect.beam.tractor"));
            Assert.That(beam.ActivationActionId, Is.EqualTo("pilot.scoop"));
            Assert.That(beam.UsesSourceForward, Is.True);
            Assert.That(beam.Power, Is.EqualTo(0.625f).Within(0.0001f));
            Assert.That(beam.Radius, Is.EqualTo(25f));
            Assert.That(beam.MaximumDistance, Is.EqualTo(75f));
        }

        [Test]
        public void RendererReconcilesProviderPrefabAgainstPresentedSourceWithoutPhysics()
        {
            var host = new GameObject("beam-test-host");
            var source = new GameObject("beam-test-source");
            var prefab = new GameObject("beam-test-prefab");
            prefab.AddComponent<ParticleSystem>();
            var renderer = host.AddComponent<EveUnityBeamPresentationRenderer>();
            var registry = new Registry("source", source.transform);
            var provider = new Provider(prefab);
            try
            {
                renderer.Apply(new[] { Presentation(0.5f) }, registry, provider);

                Assert.That(provider.LastAssetRef, Is.EqualTo("effect.beam.tractor"));
                Assert.That(provider.LastRole, Is.EqualTo("effect.beam.tractor"));
                Assert.That(renderer.ActiveBeamCount, Is.EqualTo(1));
                Assert.That(renderer.TryGetPower("tractor", out var power), Is.True);
                Assert.That(power, Is.EqualTo(0.5f).Within(0.0001f));
                var instance = source.transform.GetChild(0).gameObject;
                Assert.That(instance.GetComponent<ParticleSystem>().emission.rateOverTimeMultiplier,
                    Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(instance.GetComponentsInChildren<Collider>(true), Is.Empty);
                Assert.That(instance.GetComponentsInChildren<Rigidbody>(true), Is.Empty);

                renderer.Apply(new[] { Presentation(0.005f) }, registry, provider);
                Assert.That(renderer.TryGetPower("tractor", out power), Is.True);
                Assert.That(power, Is.Zero);

                renderer.Apply(Array.Empty<EveUnityBeamPresentation>(), registry, provider);
                Assert.That(renderer.ActiveBeamCount, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void RendererReadsDaemonPowerFromThePresentedEntityGeneration()
        {
            var host = new GameObject("beam-state-test-host");
            var source = new GameObject("beam-state-test-source");
            var prefab = new GameObject("beam-state-test-prefab");
            prefab.AddComponent<ParticleSystem>();
            var renderer = host.AddComponent<EveUnityBeamPresentationRenderer>();
            var registry = new Registry(
                "source",
                source.transform,
                new Dictionary<string, float>(StringComparer.Ordinal) { ["effect.beam.power"] = 0.75f });
            try
            {
                var presentation = new EveUnityBeamPresentation(Node(
                    "tractor", "beam.presentation", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["sourceEntityId"] = "source",
                        ["assetRole"] = "effect.beam.tractor",
                        ["directionMode"] = "source-forward.v1",
                        ["power"] = "0.125",
                        ["powerStateSemantic"] = "effect.beam.power",
                        ["activationThreshold"] = "0.01"
                    }));

                renderer.Apply(new[] { presentation }, registry, new Provider(prefab));

                Assert.That(renderer.TryGetPower("tractor", out var power), Is.True);
                Assert.That(power, Is.EqualTo(0.75f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        private static EveUnityBeamPresentation Presentation(float power) => new EveUnityBeamPresentation(Node(
            "tractor", "beam.presentation", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceEntityId"] = "source",
                ["assetRole"] = "effect.beam.tractor",
                ["directionMode"] = "source-forward.v1",
                ["power"] = power.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["activationThreshold"] = "0.01"
            }));

        private static EveUnitySceneProjection Projection(params EveUnitySceneNode[] children) =>
            new EveUnitySceneProjection("provider", "surface", "world", "commands", "receipt", "provider", null,
                Node("root", "surface", Empty(), children));

        private static EveUnitySceneNode Node(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", props, Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class Registry : IEveUnityPresentedEntityRegistry
        {
            private readonly EveUnityPresentedEntityHandle _handle;
            public Registry(
                string entityId,
                Transform transform,
                IReadOnlyDictionary<string, float>? scalarState = null)
            {
                _handle = new EveUnityPresentedEntityHandle(
                    new EveUnityPresentedEntity(0, entityId, "ship", "", "", Vector3.zero, 0, Vector3.zero,
                        1, 1, 0, 0, true, true, "", scalarState),
                    transform);
            }
            public EveUnityPresentedEntityGeneration? CurrentGeneration => null;
            public bool TryGetByEntityId(string entityId, out EveUnityPresentedEntityHandle handle)
            {
                handle = _handle;
                return string.Equals(entityId, _handle.Entity.EntityId, StringComparison.Ordinal);
            }
            public bool TryGetBySourceIndex(int sourceIndex, out EveUnityPresentedEntityHandle handle)
            {
                handle = _handle;
                return sourceIndex == _handle.Entity.SourceIndex;
            }
        }

        private sealed class Provider : IEveUnityGameObjectAssetProvider
        {
            private readonly GameObject _prefab;
            public Provider(GameObject prefab) { _prefab = prefab; }
            public string LastAssetRef { get; private set; } = "";
            public string LastRole { get; private set; } = "";
            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
            {
                LastAssetRef = asset.AssetRef;
                LastRole = asset.EntityKind;
                return _prefab;
            }
        }
    }
}
