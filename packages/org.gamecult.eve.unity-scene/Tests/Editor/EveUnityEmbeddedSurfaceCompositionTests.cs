using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using NUnit.Framework;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityEmbeddedSurfaceCompositionTests
    {
        [Test]
        public void ComposesAdvertisedReactiveSurfaceWithoutReplacingBaseTopology()
        {
            const string record = "eve:surface:test.reactive";
            var slot = new EveEmbeddedDocumentSlot(
                "reactive",
                record,
                EveSurfaceDocument.SchemaId,
                "reactive-state");
            var world = Component("world", "world.scene3d");
            var mount = new EveSurfaceComponent(
                "reactive-mount",
                "layer.reactive",
                EmptyProps(),
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<GameCult.Mesh.CultMeshStateBindingDescriptor>(),
                new[] { slot });
            var @base = Surface(4, Component("root", "surface", world, mount));
            var fragment = Surface(12, Component(
                "fragment",
                "layer.reactive",
                Component("shot", "shot.receipt")));

            var composed = EveUnityCultMeshLiveProviderTransport.ComposeSurface(
                @base,
                new Dictionary<string, EveSurfaceDocument>(StringComparer.Ordinal)
                {
                    [record] = fragment
                });

            Assert.That(composed.Version, Is.EqualTo(12));
            Assert.That(composed.Surface.Root.Children[0].Id, Is.EqualTo("world"));
            Assert.That(composed.Surface.Root.Children[1].Children.Single().Id, Is.EqualTo("fragment"));
            Assert.That(composed.Surface.Root.Children[1].Children.Single().Children.Single().Kind,
                Is.EqualTo("shot.receipt"));
        }

        private static EveSurfaceDocument Surface(long version, EveSurfaceComponent root) =>
            new EveSurfaceDocument(
                "provider",
                "test",
                "Test",
                version,
                "2026-07-19T00:00:00Z",
                new EveSurfaceTree("test", root, Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());

        private static EveSurfaceComponent Component(
            string id,
            string kind,
            params EveSurfaceComponent[] children) =>
            new EveSurfaceComponent(id, kind, EmptyProps(), children);

        private static IReadOnlyDictionary<string, string> EmptyProps() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
