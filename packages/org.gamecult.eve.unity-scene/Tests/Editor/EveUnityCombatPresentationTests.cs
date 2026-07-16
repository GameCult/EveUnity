using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityCombatPresentationTests
    {
        [Test]
        public void ProjectionPreservesProviderSelectionLockMetersAndFossilTiming()
        {
            var projection = Projection(Combat());
            var combat = EveUnityCombatPresentation.Find(projection);

            Assert.That(combat, Is.Not.Null);
            Assert.That(combat!.ControlledEntityIndex, Is.EqualTo(4));
            Assert.That(combat.SelectedTargetEntityId, Is.EqualTo("run.zone.0.entity.9"));
            Assert.That(combat.TargetVisible, Is.True);
            Assert.That(combat.TargetHostile, Is.True);
            Assert.That(combat.LockProgress, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(combat.RadialFill(0), Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(combat.RadialFill(1), Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(combat.HitMarkerDurationSeconds, Is.EqualTo(0.25f));
        }

        [Test]
        public void HitMarkerRequiresControlledSourceSelectedTargetAndAuthoritativeDamage()
        {
            var combat = new EveUnityCombatPresentation(Combat());

            Assert.That(combat.PresentsHit(Shot(4, 9, true, 12, 0)), Is.True);
            Assert.That(combat.PresentsHit(Shot(3, 9, true, 12, 0)), Is.False);
            Assert.That(combat.PresentsHit(Shot(4, 8, true, 12, 0)), Is.False);
            Assert.That(combat.PresentsHit(Shot(4, 9, false, 0, 0)), Is.False);
            Assert.That(combat.PresentsHit(Shot(4, 9, true, 0, 0)), Is.False);
            Assert.That(combat.PresentsHit(Shot(4, 9, true, 0, 12)), Is.True);
        }

        [Test]
        public void HitMarkerRetainsAuthoritativeEndpointAfterTargetLeavesTheWorld()
        {
            var root = new GameObject("combat-hit-retention-test");
            try
            {
                var renderer = root.AddComponent<EveUnityCombatPresentationRenderer>();
                var presentation = new EveUnityCombatPresentation(Combat());
                typeof(EveUnityCombatPresentationRenderer)
                    .GetField("_lastPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(renderer, presentation);
                typeof(EveUnityCombatPresentationRenderer)
                    .GetMethod("OnShotAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(renderer, new object[] { Shot(4, 9, true, 12, 0, "7.5,-3.25") });

                renderer.RefreshNow();

                Assert.That(renderer.HitMarkerVisible, Is.True,
                    "A destroyed target must not erase receipt-owned hit feedback.");
                var line = root.transform.Find("Eve combat presentation/Hit marker A")!
                    .GetComponent<LineRenderer>();
                var center = (line.GetPosition(0) + line.GetPosition(1)) * 0.5f;
                Assert.That(center.x, Is.EqualTo(7.5f).Within(0.0001f));
                Assert.That(center.z, Is.EqualTo(-3.25f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static EveUnitySceneProjection Projection(params EveUnitySceneNode[] children) =>
            new EveUnitySceneProjection("provider", "surface", "world", "commands", "receipt", "provider", null,
                Node("root", "surface", children));

        private static EveUnitySceneNode Combat() => Node("combat", "combat.presentation", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["controlledEntityId"] = "run.zone.0.entity.4",
            ["controlledEntityIndex"] = "4",
            ["selectedTargetEntityId"] = "run.zone.0.entity.9",
            ["selectedTargetEntityIndex"] = "9",
            ["targetVisible"] = "true",
            ["targetHostile"] = "true",
            ["contactInformation"] = "0.8",
            ["shieldRatio"] = "0.4",
            ["hullRatio"] = "0.9",
            ["lockProgress"] = "0.6",
            ["hitMarkerDurationSeconds"] = "0.25",
            ["radialFillMinimum"] = "0.25",
            ["radialFillMaximum"] = "0.75"
        });

        private static EveUnityShotReceipt Shot(
            int source,
            int target,
            bool hit,
            double damage,
            double shield,
            string endpoint = "0,0") =>
            new EveUnityShotReceipt(Node("shot", "shot.receipt", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shotId"] = Guid.NewGuid().ToString("N"),
                ["sourceEntityIndex"] = source.ToString(),
                ["targetEntityIndex"] = target.ToString(),
                ["hit"] = hit.ToString(),
                ["appliedDamage"] = damage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["shieldAbsorbedDamage"] = shield.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["endpoint"] = endpoint
            }));

        private static EveUnitySceneNode Node(string id, string kind, params EveUnitySceneNode[] children) =>
            Node(id, kind, Empty(), children);

        private static EveUnitySceneNode Node(string id, string kind, IReadOnlyDictionary<string, string> props, params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", props, Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
