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

        [Test]
        public void ResolvePlugins_KeepsIndependentRequiredAndOptionalNestedCapabilities()
        {
            var surface = new EveAdvertisedSurface(
                "sai.stage", EveSurfaceDocument.SchemaId, "eve:surface:sai", "cultmesh-record", "active", "interface",
                requiresPlugins: new[]
                {
                    new EvePluginRequirement("sai.vn", "^0.1.0", "required", new[] { "vn.stage", "story.choose" }, Array.Empty<string>()),
                    new EvePluginRequirement("norn.graph", "^0.1.0", "optional-nested", Array.Empty<string>(), new[] { "embed.norn" }),
                    new EvePluginRequirement("tex.math", "^0.1.0", "optional-nested", Array.Empty<string>(), new[] { "embed.tex" })
                });
            var sai = Plugin("sai.vn", new[] { "vn.stage" }, new[] { "story.choose" });
            var norn = Plugin("norn.graph", new[] { "embed.norn" }, Array.Empty<string>());

            var selected = EveCultMeshSurfaceSelection.ResolvePlugins(surface, new[] { sai, norn });

            Assert.That(selected, Is.EqualTo(new[] { sai, norn }));
        }

        [Test]
        public void ResolvePlugins_RejectsMissingRequiredCapability()
        {
            var surface = new EveAdvertisedSurface(
                "sai.stage", EveSurfaceDocument.SchemaId, "eve:surface:sai", "cultmesh-record", "active", "interface",
                requiresPlugins: new[]
                {
                    new EvePluginRequirement("sai.vn", "^0.1.0", "required", new[] { "story.choose" }, Array.Empty<string>())
                });

            Assert.Throws<InvalidOperationException>(() =>
                EveCultMeshSurfaceSelection.ResolvePlugins(
                    surface,
                    new[] { Plugin("sai.vn", new[] { "vn.stage" }, Array.Empty<string>()) }));
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

        private static EvePluginAdvertisementDocument Plugin(
            string pluginId,
            string[] components,
            string[] commands) =>
            new EvePluginAdvertisementDocument(
                pluginId,
                pluginId.Split('.')[0],
                "0.1.0",
                "cultmesh://plugins/" + pluginId,
                new EvePluginRuntimeAdvertisement(
                    "sidecar",
                    "gamecult.eve.plugin_abi.v1",
                    new[] { "cultmesh" },
                    new[] { "plugin" },
                    new EvePluginSidecarAdvertisement(
                        "daemon", "cultmesh", "request", "response", Array.Empty<string>(), "command", "receipt", "plugin")),
                Array.Empty<string>(),
                components,
                commands,
                Array.Empty<string>());
    }
}
