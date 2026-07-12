using System;
using GameCult.Eve.Surface;
using NUnit.Framework;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveCultMeshSurfaceSelectionTests
    {
        [Test]
        public void Select_MatchesDeveloperIntentWithoutPhysicalTopology()
        {
            var provider = Provider(
                new EveAdvertisedSurface(
                    "aetheria.pilot",
                    EveSurfaceDocument.SchemaId,
                    "eve:surface:aetheria.pilot",
                    "cultmesh-record",
                    "active",
                    "interactive-world",
                    new EveWorldInteractionAdvertisement(
                        "world3d",
                        Array.Empty<string>(),
                        "provider-command",
                        "eve:commands",
                        EveCommandReceiptDocument.SchemaId,
                        "eve:receipts",
                        "eve:assets",
                        new[] { "unity-scene", "web" },
                        "provider")));

            var selected = EveCultMeshSurfaceSelection.Select(
                new[] { provider },
                new EveSurfaceRequest
                {
                    ProviderId = "aetheria.daemon",
                    SurfaceKind = "interactive-world",
                    LoweringTarget = "unity-scene"
                });

            Assert.That(selected.Provider, Is.SameAs(provider));
            Assert.That(selected.Surface.SurfaceId, Is.EqualTo("aetheria.pilot"));
            Assert.That(selected.Surface.RecordRef, Is.EqualTo("eve:surface:aetheria.pilot"));
        }

        [Test]
        public void Select_RejectsUnsupportedLoweringTarget()
        {
            var provider = Provider(new EveAdvertisedSurface(
                "aetheria.pilot",
                EveSurfaceDocument.SchemaId,
                "eve:surface:aetheria.pilot",
                "cultmesh-record",
                "active",
                "interactive-world",
                new EveWorldInteractionAdvertisement(
                    "world3d", Array.Empty<string>(), "", "", "", "", "",
                    new[] { "unity-scene" }, "provider")));

            Assert.Throws<InvalidOperationException>(() => EveCultMeshSurfaceSelection.Select(
                new[] { provider },
                new EveSurfaceRequest { LoweringTarget = "flutter" }));
        }

        private static EveProviderAdvertisementDocument Provider(EveAdvertisedSurface surface) =>
            new EveProviderAdvertisementDocument(
                "aetheria.daemon",
                "aetheria",
                "aetheria",
                "Aetheria",
                "game",
                "aetheria.daemon",
                "2026-07-12T00:00:00Z",
                new EveProviderFreshness("fresh", "2026-07-12T00:00:00Z", 5000),
                Array.Empty<string>(),
                Array.Empty<EveProviderWitness>(),
                new[] { surface },
                Array.Empty<EveAdvertisedCommand>());
    }
}
