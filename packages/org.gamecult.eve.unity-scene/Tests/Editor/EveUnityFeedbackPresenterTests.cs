using System;
using System.Collections.Generic;
using NUnit.Framework;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityFeedbackPresenterTests
    {
        [Test]
        public void PresenterPrimesRetainedEventsAndEmitsEachNewIdentityOnce()
        {
            var presenter = new EveUnityFeedbackPresenter();
            var emitted = new List<EveUnityFeedbackEvent>();
            presenter.FeedbackAvailable += emitted.Add;

            Assert.That(presenter.Apply(Projection(Event("old", "projectile.impact", 4))), Is.Zero);
            Assert.That(emitted, Is.Empty, "initial retained events must not replay on connect");

            var next = Projection(Event("old", "projectile.impact", 4), Event("new", "entity.damaged", 5));
            Assert.That(presenter.Apply(next), Is.EqualTo(1));
            Assert.That(emitted.Count, Is.EqualTo(1));
            Assert.That(emitted[0].EventId, Is.EqualTo("new"));
            Assert.That(emitted[0].Kind, Is.EqualTo("entity.damaged"));
            Assert.That(emitted[0].FrameId, Is.EqualTo(5));

            Assert.That(presenter.Apply(next), Is.Zero, "surface refresh must not replay an event identity");
            Assert.That(emitted.Count, Is.EqualTo(1));
        }

        [Test]
        public void FeedbackEventPreservesProviderOwnedCargoDeltaAndReason()
        {
            var node = Event("pickup", "pickup.collected", 9, new Dictionary<string, string>
            {
                ["itemKey"] = "scrap-metal",
                ["scalarValue"] = "1",
                ["auxiliaryValue"] = "0",
                ["cargoQuantityBefore"] = "0",
                ["cargoQuantityAfter"] = "1",
                ["reason"] = ""
            });

            var value = new EveUnityFeedbackEvent(node);

            Assert.That(value.ItemKey, Is.EqualTo("scrap-metal"));
            Assert.That(value.ScalarValue, Is.EqualTo(1));
            Assert.That(value.AuxiliaryValue, Is.Zero);
            Assert.That(value.CargoQuantityBefore, Is.Zero);
            Assert.That(value.CargoQuantityAfter, Is.EqualTo(1));
            Assert.That(value.Reason, Is.Empty);
        }

        private static EveUnitySceneProjection Projection(params EveUnitySceneNode[] events) =>
            new EveUnitySceneProjection("provider", "surface", "world", "commands", "receipt", "provider", null,
                Node("root", "surface", Node("feedback", "feedback.stream", events)));

        private static EveUnitySceneNode Event(
            string id,
            string kind,
            long frame,
            IReadOnlyDictionary<string, string>? extra = null)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["eventId"] = id,
                ["eventKind"] = kind,
                ["frameId"] = frame.ToString()
            };
            foreach (var pair in extra ?? Empty()) props[pair.Key] = pair.Value;
            return
            new EveUnitySceneNode(id, "feedback.event", "scene-node",
                props,
                Empty(), Empty(), 0, 0, Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, Array.Empty<EveUnitySceneNode>());
        }

        private static EveUnitySceneNode Node(string id, string kind, params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", Empty(), Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
