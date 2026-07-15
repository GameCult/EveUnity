using System;
using System.Collections.Generic;
using NUnit.Framework;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityShotReceiptPresenterTests
    {
        [Test]
        public void PresenterPrimesRetainedShotsAndEmitsNewTrajectoryOnce()
        {
            var presenter = new EveUnityShotReceiptPresenter();
            var emitted = new List<EveUnityShotReceipt>();
            presenter.ShotAvailable += emitted.Add;

            Assert.That(presenter.Apply(Projection(Shot("old", "0,0", "10,5", false))), Is.Zero);
            Assert.That(emitted, Is.Empty);

            var next = Projection(
                Shot("old", "0,0", "10,5", false),
                Shot("new", "2,3", "20,30", true));
            Assert.That(presenter.Apply(next), Is.EqualTo(1));
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].ShotId, Is.EqualTo("new"));
            Assert.That(emitted[0].Hit, Is.True);
            Assert.That(emitted[0].Origin.X, Is.EqualTo(2));
            Assert.That(emitted[0].Origin.Z, Is.EqualTo(3));
            Assert.That(emitted[0].Endpoint.X, Is.EqualTo(20));
            Assert.That(emitted[0].Endpoint.Z, Is.EqualTo(30));
            Assert.That(emitted[0].DurationSeconds, Is.EqualTo(0.25));
            Assert.That(emitted[0].PresentationKind, Is.EqualTo("test-laser"));
            Assert.That(emitted[0].ItemKey, Is.EqualTo("test-cannon"));
            Assert.That(emitted[0].ImpactKind, Is.EqualTo("shield"));
            Assert.That(emitted[0].PresentationIntensity, Is.EqualTo(9));
            Assert.That(emitted[0].AppliedDamage, Is.EqualTo(12));
            Assert.That(emitted[0].ShieldAbsorbedDamage, Is.EqualTo(8));
            Assert.That(presenter.Apply(next), Is.Zero);
        }

        [Test]
        public void ManifestResolvesSemanticEffectPresentationRoles()
        {
            var bolt = new EveUnityPlayableWorldAssetManifestEntry(
                "prefab.effect.shot.bolt", "effect.shot.bolt", "Prefabs/Lightning", "bolt");
            var manifest = new EveUnityPlayableWorldAssetManifest("manifest", new[] { bolt });

            Assert.That(manifest.FindByPresentationRole("effect.shot.bolt"), Is.SameAs(bolt));
            Assert.That(manifest.FindByPresentationRole("effect.impact.shield"), Is.Null);
        }

        private static EveUnitySceneProjection Projection(params EveUnitySceneNode[] shots) =>
            new EveUnitySceneProjection("provider", "surface", "world", "commands", "receipt", "provider", null,
                Node("root", "surface", Node("shots", "shot.receipt-stream", shots)));

        private static EveUnitySceneNode Shot(string id, string origin, string endpoint, bool hit) =>
            new EveUnitySceneNode(id, "shot.receipt", "scene-node",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["shotId"] = id,
                    ["frameId"] = "12",
                    ["zoneIndex"] = "0",
                    ["sourceEntityIndex"] = "1",
                    ["targetEntityIndex"] = "2",
                    ["hit"] = hit.ToString(),
                    ["outcome"] = hit ? "hit" : "miss",
                    ["origin"] = origin,
                    ["endpoint"] = endpoint,
                    ["presentationDuration"] = "0.25",
                    ["presentationKind"] = "test-laser",
                    ["itemKey"] = "test-cannon",
                    ["impactKind"] = "shield",
                    ["presentationIntensity"] = "9",
                    ["appliedDamage"] = "12",
                    ["shieldAbsorbedDamage"] = "8"
                }, Empty(), Empty(), 0, 0, Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null,
                Array.Empty<EveUnitySceneNode>());

        private static EveUnitySceneNode Node(string id, string kind, params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", Empty(), Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
