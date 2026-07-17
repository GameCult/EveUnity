using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using NUnit.Framework;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityUIToolkit.Tests
{
    public sealed class EveUiToolkitSurfaceLowererTests
    {
        [Test]
        public void DefaultOptionsExposeSaiNornAndTeXProjectionAdapters()
        {
            var options = EveUiToolkitSurfaceOptions.Default;

            Assert.That(options.FindPluginProjectionAdapter(Component("stage", "vn.stage"))?.PluginId, Is.EqualTo("sai.vn"));
            Assert.That(options.FindPluginProjectionAdapter(Component("graph", "embed.norn"))?.PluginId, Is.EqualTo("norn.graph"));
            Assert.That(options.FindPluginProjectionAdapter(Component("math", "embed.tex"))?.PluginId, Is.EqualTo("tex.math"));
        }

        [Test]
        public void SaiStageLowersThroughRuntimeProjectionAdapter()
        {
            var document = Document(Component(
                "stage",
                "vn.stage",
                new Dictionary<string, string>
                {
                    ["storyId"] = "sai.fixture",
                    ["currentPath"] = "intro"
                }));

            var root = new EveUiToolkitSurfaceLowerer().Lower(document);

            Assert.That(root.ClassListContains("eve-plugin-projection"), Is.True);
            Assert.That(root.ClassListContains("eve-plugin-sai-vn"), Is.True);
            Assert.That(root.ClassListContains("sai-vn-stage"), Is.True);
        }

        [Test]
        public void NornGraphLowersThroughRuntimeProjectionAdapter()
        {
            var document = Document(Component(
                "graph",
                "embed.norn",
                new Dictionary<string, string>
                {
                    ["label"] = "World graph",
                    ["mode"] = "embedded"
                }));

            var root = new EveUiToolkitSurfaceLowerer().Lower(document);

            Assert.That(root.ClassListContains("eve-plugin-projection"), Is.True);
            Assert.That(root.ClassListContains("eve-plugin-norn-graph"), Is.True);
            Assert.That(root.ClassListContains("norn-graph"), Is.True);
        }

        [Test]
        public void TeXMathLowersThroughRuntimeProjectionAdapterAsSourceFallback()
        {
            var document = Document(Component(
                "math",
                "embed.tex",
                new Dictionary<string, string>
                {
                    ["label"] = "Bifrost voting weight",
                    ["source"] = "\\\\mathrm{votes}(p)=1+\\\\lfloor\\\\log_b(1+p)\\\\rfloor",
                    ["display"] = "block"
                }));

            var root = new EveUiToolkitSurfaceLowerer().Lower(document);

            Assert.That(root.ClassListContains("eve-plugin-projection"), Is.True);
            Assert.That(root.ClassListContains("eve-plugin-tex-math"), Is.True);
            Assert.That(root.ClassListContains("tex-math"), Is.True);
            Assert.That(root.ClassListContains("tex-display-block"), Is.True);
        }

        [Test]
        public void EmbeddedDocumentSlotMountsResolverBackedChildSurface()
        {
            var child = Document(
                Component("child-root", "text", new Dictionary<string, string> { ["text"] = "Nested surface" }),
                surfaceId: "child-surface");
            var parentRoot = new EveSurfaceComponent(
                "parent-root",
                "surface",
                EmptyProps(),
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<CultMeshStateBindingDescriptor>(),
                new[]
                {
                    new EveEmbeddedDocumentSlot(
                        "child-slot",
                        "child-surface",
                        EveSurfaceDocument.SchemaId,
                        "inline")
                });
            var lowerer = new EveUiToolkitSurfaceLowerer(new EveUiToolkitSurfaceOptions(
                embeddedDocumentResolver: _ => child,
                pluginProjectionAdapters: Array.Empty<IEveUiToolkitPluginProjectionAdapter>()));

            var root = lowerer.Lower(Document(parentRoot));
            var embedded = root.Query<VisualElement>(className: "eve-embedded-document").First();

            Assert.That(embedded, Is.Not.Null);
            Assert.That(embedded.name, Is.EqualTo("child-slot"));
            Assert.That(embedded.ClassListContains("eve-embedded-kind-inline"), Is.True);
        }

        [Test]
        public void ProgressAndAbsoluteLayoutLowerToNativeUiToolkitPrimitives()
        {
            var progress = new EveSurfaceComponent(
                "hull",
                "progress",
                new Dictionary<string, string>
                {
                    ["label"] = "Hull",
                    ["ratio"] = "0.625"
                },
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<CultMeshStateBindingDescriptor>(),
                Array.Empty<EveEmbeddedDocumentSlot>(),
                new Dictionary<string, string>
                {
                    ["position"] = "absolute",
                    ["right"] = "24",
                    ["bottom"] = "32"
                });

            var root = new EveUiToolkitSurfaceLowerer().Lower(Document(progress));

            Assert.That(root, Is.TypeOf<ProgressBar>());
            Assert.That(((ProgressBar)root).value, Is.EqualTo(0.625f));
            Assert.That(root.style.position.value, Is.EqualTo(Position.Absolute));
            Assert.That(root.style.right.value.value, Is.EqualTo(24f));
            Assert.That(root.style.bottom.value.value, Is.EqualTo(32f));
        }

        [Test]
        public void InventoryDropBuildsAdvertisedTypedOperationPayload()
        {
            var source = Component("ore", EveInventoryInteraction.ItemKind, new Dictionary<string, string>
            {
                ["sourceKind"] = "cargo",
                ["sourceEntityKey"] = "zone.0.entity.1",
                ["sourceIndex"] = "2",
                ["itemKey"] = "ore",
                ["quantity"] = "4",
                ["x"] = "3",
                ["y"] = "5"
            });
            var target = Component("equipment", EveInventoryInteraction.GridKind, new Dictionary<string, string>
            {
                ["targetKind"] = "equipment",
                ["targetEntityKey"] = "zone.0.entity.1",
                ["targetIndex"] = "-1",
                ["dropCommand.cargo"] = "aetheria.daemon.commands.EquipItem"
            });

            Assert.That(EveInventoryInteraction.TryCreateDropRequest(
                Document(target), source, target, 7, 9, "unity-test", out var request), Is.True);
            Assert.That(request, Is.Not.Null);
            Assert.That(request!.Command, Is.EqualTo("aetheria.daemon.commands.EquipItem"));
            Assert.That(request.Payload.GetString("originEntityKey"), Is.EqualTo("zone.0.entity.1"));
            Assert.That(request.Payload.GetString("originCargoIndex"), Is.EqualTo("2"));
            Assert.That(request.Payload.GetString("destinationX"), Is.EqualTo("7"));
            Assert.That(request.Payload.GetString("destinationY"), Is.EqualTo("9"));
            Assert.That(request.Payload.GetString("hasDestinationPosition"), Is.EqualTo("true"));
        }

        [Test]
        public void InventoryGridAndItemsLowerToNativeSpatialElements()
        {
            var item = Component("ore", EveInventoryInteraction.ItemKind, new Dictionary<string, string>
            {
                ["label"] = "Iron Ore",
                ["sourceKind"] = "cargo",
                ["x"] = "1",
                ["y"] = "2"
            });
            var grid = new EveSurfaceComponent(
                "cargo",
                EveInventoryInteraction.GridKind,
                new Dictionary<string, string>
                {
                    ["columns"] = "4",
                    ["rows"] = "3",
                    ["cellSize"] = "32",
                    ["cellGap"] = "2"
                },
                new[] { item });

            var root = new EveUiToolkitSurfaceLowerer().Lower(Document(grid));
            var loweredItem = root.Query<Button>(className: "eve-inventory-item").First();

            Assert.That(root.ClassListContains("eve-inventory-grid"), Is.True);
            Assert.That(root.style.width.value.value, Is.EqualTo(134f));
            Assert.That(root.style.height.value.value, Is.EqualTo(100f));
            Assert.That(loweredItem, Is.Not.Null);
            Assert.That(loweredItem.text, Is.EqualTo("Iron Ore"));
            Assert.That(loweredItem.style.left.value.value, Is.EqualTo(34f));
            Assert.That(loweredItem.style.top.value.value, Is.EqualTo(68f));
        }

        private static EveSurfaceDocument Document(EveSurfaceComponent root, string surfaceId = "test-surface")
        {
            return new EveSurfaceDocument(
                "gamecult.eve.unity.tests",
                "runtime.test",
                "Unity UI Toolkit tests",
                1,
                "2026-07-08T00:00:00Z",
                new EveSurfaceTree(surfaceId, root, Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());
        }

        private static EveSurfaceComponent Component(
            string id,
            string kind,
            IReadOnlyDictionary<string, string>? props = null)
        {
            return new EveSurfaceComponent(
                id,
                kind,
                props ?? EmptyProps(),
                Array.Empty<EveSurfaceComponent>());
        }

        private static IReadOnlyDictionary<string, string> EmptyProps()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
