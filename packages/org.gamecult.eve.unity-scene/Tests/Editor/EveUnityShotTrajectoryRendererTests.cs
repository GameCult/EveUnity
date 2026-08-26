using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityShotTrajectoryRendererTests
    {
        [Test]
        public void ProviderShotEffectReplacesGenericFallbackLine()
        {
            var root = new GameObject("shot-renderer-provider-test");
            var prefab = new GameObject("provider-bolt");
            try
            {
                var host = root.AddComponent<EveUnityPlayableWorldClientHost>();
                var renderer = root.AddComponent<EveUnityShotTrajectoryRenderer>();
                renderer.Bind(host, new BoltProvider(prefab));

                renderer.Present(Shot("provider-shot"));

                Assert.That(renderer.ActiveTrajectoryCount, Is.EqualTo(1));
                Assert.That(renderer.ActiveFallbackTrajectoryCount, Is.Zero);
                Assert.That(root.GetComponentsInChildren<LineRenderer>(), Is.Empty);
                Assert.That(root.transform.GetChild(0).Find("provider-bolt(Clone)"), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void MissingProviderShotEffectUsesGenericFallbackLine()
        {
            var root = new GameObject("shot-renderer-fallback-test");
            try
            {
                var host = root.AddComponent<EveUnityPlayableWorldClientHost>();
                var renderer = root.AddComponent<EveUnityShotTrajectoryRenderer>();
                renderer.Bind(host, new BoltProvider(null));

                renderer.Present(Shot("fallback-shot"));

                Assert.That(renderer.ActiveTrajectoryCount, Is.EqualTo(1));
                Assert.That(renderer.ActiveFallbackTrajectoryCount, Is.EqualTo(1));
                Assert.That(root.GetComponentsInChildren<LineRenderer>(), Has.Length.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static EveUnityShotReceipt Shot(string id) =>
            new EveUnityShotReceipt(new EveUnitySceneNode(id, "shot.receipt", "scene-node",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["shotId"] = id,
                    ["origin"] = "0,0",
                    ["endpoint"] = "10,0",
                    ["presentationDuration"] = "0.2",
                    ["presentationKind"] = "bolt",
                    ["impactKind"] = "armor",
                    ["presentationIntensity"] = "85",
                    ["hit"] = "true"
                }, Empty(), Empty(), 0, 0, Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null,
                Array.Empty<EveUnitySceneNode>()));

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class BoltProvider : IEveUnityGameObjectAssetProvider
        {
            private readonly GameObject? _prefab;
            public BoltProvider(GameObject? prefab) { _prefab = prefab; }
            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset) =>
                asset.EntityKind == "effect.shot.bolt" ? _prefab : null;
        }
    }
}
