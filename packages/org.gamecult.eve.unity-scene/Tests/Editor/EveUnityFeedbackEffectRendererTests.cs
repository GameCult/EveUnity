using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityFeedbackEffectRendererTests
    {
        [Test]
        public void RendererResolvesSemanticFeedbackRoleAtProviderPosition()
        {
            var root = new GameObject("feedback-renderer-test");
            var prefab = new GameObject("destruction-prefab");
            try
            {
                var host = root.AddComponent<EveUnityPlayableWorldClientHost>();
                var renderer = root.AddComponent<EveUnityFeedbackEffectRenderer>();
                var provider = new CapturingProvider(prefab);
                renderer.Bind(host, provider);
                renderer.Present(Event("destroyed-1", "entity.destroyed", "4,2,7"));

                Assert.That(provider.LastRole, Is.EqualTo("effect.feedback.entity.destroyed"));
                Assert.That(renderer.ActiveEffectCount, Is.EqualTo(1));
                Assert.That(root.transform.childCount, Is.EqualTo(1));
                Assert.That(root.transform.GetChild(0).position, Is.EqualTo(new Vector3(4, 2, 7)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        private static EveUnityFeedbackEvent Event(string id, string kind, string position) =>
            new EveUnityFeedbackEvent(new EveUnitySceneNode(id, "feedback.event", "scene-node",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["eventId"] = id, ["eventKind"] = kind, ["position"] = position
                }, Empty(), Empty(), 0, 0, Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null,
                Array.Empty<EveUnitySceneNode>()));

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        private sealed class CapturingProvider : IEveUnityGameObjectAssetProvider
        {
            private readonly GameObject _prefab;
            public CapturingProvider(GameObject prefab) { _prefab = prefab; }
            public string LastRole { get; private set; } = "";
            public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
            {
                LastRole = asset.EntityKind;
                return _prefab;
            }
        }
    }
}
