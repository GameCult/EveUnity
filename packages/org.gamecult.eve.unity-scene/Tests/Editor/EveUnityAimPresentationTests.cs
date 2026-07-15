using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityAimPresentationTests
    {
        [Test]
        public void ProjectionPreservesProviderAimConvergenceSemantic()
        {
            var projection = new EveUnitySceneProjection(
                "provider", "surface", "world", "commands", "receipt", "provider", null,
                Node("root", "surface", Node("aim", "aim.presentation", new Dictionary<string, string>
                {
                    ["controlledEntityId"] = "run.zone.0.entity.4",
                    ["convergenceTargetEntityId"] = "run.zone.0.entity.9",
                    ["viewDotRole"] = "aim.marker.view-direction",
                    ["minimumConvergenceDistance"] = "50",
                    ["viewDotRadius"] = "0.8"
                })));

            var aim = EveUnityAimPresentation.Find(projection);

            Assert.That(aim, Is.Not.Null);
            Assert.That(aim!.ControlledEntityId, Is.EqualTo("run.zone.0.entity.4"));
            Assert.That(aim.ConvergenceTargetEntityId, Is.EqualTo("run.zone.0.entity.9"));
            Assert.That(aim.MinimumConvergenceDistance, Is.EqualTo(50f));
            Assert.That(aim.ViewDotRadius, Is.EqualTo(0.8f));
        }

        [Test]
        public void PlanarYawProducesPortableUnitLookDirection()
        {
            var forward = EveUnityPlayableWorldLookDirection.FromPlanarYaw(0f);
            var right = EveUnityPlayableWorldLookDirection.FromPlanarYaw(Mathf.PI * 0.5f);

            Assert.That(forward.DirectionX, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(forward.DirectionZ, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(right.DirectionX, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(right.DirectionZ, Is.EqualTo(0f).Within(0.0001f));
        }

        private static EveUnitySceneNode Node(string id, string kind, params EveUnitySceneNode[] children) =>
            Node(id, kind, Empty(), children);

        private static EveUnitySceneNode Node(string id, string kind, IReadOnlyDictionary<string, string> props, params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", props, Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
