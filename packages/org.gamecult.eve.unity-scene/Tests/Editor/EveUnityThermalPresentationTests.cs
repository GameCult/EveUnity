using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace GameCult.Eve.UnityScene.Tests
{
    public sealed class EveUnityThermalPresentationTests
    {
        [Test]
        public void AlivePilotUsesProviderThermalFactsAndOriginalSeverePulse()
        {
            var state = new EveUnityThermalPresentationState();
            var entity = Entity(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cockpitTemperature"] = "340",
                ["heatstroke"] = "0.5",
                ["hypothermia"] = "0.1",
                ["heatstrokeRisk"] = "true",
                ["hypothermiaRisk"] = "false",
                ["heatstrokePostWeight"] = "1",
                ["severeHeatstrokeWeight"] = "0.5",
                ["heatstrokePhasingFloor"] = "0",
                ["heatstrokePhasingFrequency"] = "5"
            });

            var frame = state.Apply(World(entity), Projection(World(entity), Node("root", "surface", Empty())),
                (float)(Math.PI / 10));

            Assert.That(frame.EntityId, Is.EqualTo("pilot"));
            Assert.That(frame.CockpitTemperature, Is.EqualTo(340).Within(0.0001));
            Assert.That(frame.HeatstrokeRisk, Is.True);
            Assert.That(frame.HeatstrokeWeight, Is.EqualTo(1).Within(0.0001));
            Assert.That(frame.SevereHeatstrokeWeight, Is.EqualTo(0.75).Within(0.0001));
            Assert.That(frame.DeathWeight, Is.Zero);
        }

        [Test]
        public void CurrentThermalDeathCrossfadesAndRetainedDeathSettlesOnReconnect()
        {
            var current = Projection(World(), Node("root", "surface", Empty(),
                Node("death", "feedback.event", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["eventId"] = "death-1", ["eventKind"] = "entity.destroyed",
                    ["reason"] = "heatstroke", ["frameId"] = "20", ["currentFrameId"] = "20"
                })));
            var live = new EveUnityThermalPresentationState();
            var start = live.Apply(World(), current, 10f);
            var middle = live.Apply(World(), current, 10.5f);
            Assert.That(start.DeathWeight, Is.Zero);
            Assert.That(start.HeatstrokeWeight, Is.EqualTo(1).Within(0.0001));
            Assert.That(middle.DeathWeight, Is.EqualTo(0.5).Within(0.0001));
            Assert.That(middle.HeatstrokeWeight, Is.EqualTo(0.5).Within(0.0001));

            var retained = Projection(World(), Node("root", "surface", Empty(),
                Node("death", "feedback.event", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["eventId"] = "death-1", ["eventKind"] = "entity.destroyed",
                    ["reason"] = "heatstroke", ["frameId"] = "20", ["currentFrameId"] = "25"
                })));
            var reconnect = new EveUnityThermalPresentationState().Apply(World(), retained, 99f);
            Assert.That(reconnect.DeathCause, Is.EqualTo("heatstroke"));
            Assert.That(reconnect.DeathWeight, Is.EqualTo(1).Within(0.0001));
            Assert.That(reconnect.HeatstrokeWeight, Is.Zero);
        }

        private static EveUnityPlayableWorldProjection World(params EveUnityPlayableWorldEntity[] entities) =>
            new EveUnityPlayableWorldProjection("world", "state", "assets", "input", "camera", "pilot",
                "pilot", "move", "focus", "target", "action", entities);

        private static EveUnityPlayableWorldEntity Entity(IReadOnlyDictionary<string, string> props) =>
            new EveUnityPlayableWorldEntity("node", "pilot", "ship", "Pilot", "player", "ship", 0, 0, 0,
                0, 1, true, true, "focus", "move", "target", "action", props);

        private static EveUnitySceneProjection Projection(EveUnityPlayableWorldProjection world, EveUnitySceneNode root) =>
            new EveUnitySceneProjection("provider", "surface", "scene", "commands", "receipts", "provider",
                world, root);

        private static EveUnitySceneNode Node(string id, string kind, IReadOnlyDictionary<string, string> props,
            params EveUnitySceneNode[] children) =>
            new EveUnitySceneNode(id, kind, "scene-node", props, Empty(), Empty(), 0, 0,
                Array.Empty<EveUnitySceneEmbeddedDocumentSlot>(), null, children);

        private static IReadOnlyDictionary<string, string> Empty() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
