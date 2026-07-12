using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityThermalPresentationSink
    {
        void ApplyThermalPresentation(EveUnityThermalPresentationFrame frame);
    }

    public readonly struct EveUnityThermalPresentationFrame
    {
        public EveUnityThermalPresentationFrame(string entityId, float cockpitTemperature,
            float heatstroke, float hypothermia, bool heatstrokeRisk, bool hypothermiaRisk,
            float heatstrokeWeight, float severeHeatstrokeWeight, float hypothermiaWeight,
            float severeHypothermiaWeight, string deathCause, float deathWeight)
        {
            EntityId = entityId ?? "";
            CockpitTemperature = cockpitTemperature;
            Heatstroke = heatstroke;
            Hypothermia = hypothermia;
            HeatstrokeRisk = heatstrokeRisk;
            HypothermiaRisk = hypothermiaRisk;
            HeatstrokeWeight = heatstrokeWeight;
            SevereHeatstrokeWeight = severeHeatstrokeWeight;
            HypothermiaWeight = hypothermiaWeight;
            SevereHypothermiaWeight = severeHypothermiaWeight;
            DeathCause = deathCause ?? "";
            DeathWeight = deathWeight;
        }

        public string EntityId { get; }
        public float CockpitTemperature { get; }
        public float Heatstroke { get; }
        public float Hypothermia { get; }
        public bool HeatstrokeRisk { get; }
        public bool HypothermiaRisk { get; }
        public float HeatstrokeWeight { get; }
        public float SevereHeatstrokeWeight { get; }
        public float HypothermiaWeight { get; }
        public float SevereHypothermiaWeight { get; }
        public string DeathCause { get; }
        public float DeathWeight { get; }
    }

    public sealed class EveUnityThermalPresentationState
    {
        private string _deathEventId = "";
        private string _deathCause = "";
        private float _deathStartedAt = float.NegativeInfinity;

        public EveUnityThermalPresentationFrame Apply(
            EveUnityPlayableWorldProjection? world,
            EveUnitySceneProjection? projection,
            float timeSeconds)
        {
            var entity = ResolvePlayer(world);
            var props = entity?.Props ?? EmptyProps;
            var death = LatestThermalDeath(projection);
            if (death.HasValue && !string.Equals(_deathEventId, death.Value.EventId, StringComparison.Ordinal))
            {
                _deathEventId = death.Value.EventId;
                _deathCause = death.Value.Cause;
                _deathStartedAt = death.Value.FrameId >= death.Value.CurrentFrameId
                    ? timeSeconds
                    : float.NegativeInfinity;
            }
            else if (!death.HasValue && entity != null)
            {
                _deathEventId = "";
                _deathCause = "";
                _deathStartedAt = float.NegativeInfinity;
            }

            var duration = Positive(Float(props, "deathTransitionSeconds", 1f), 1f);
            var deathWeight = string.IsNullOrWhiteSpace(_deathCause) ? 0f :
                float.IsNegativeInfinity(_deathStartedAt) ? 1f :
                Mathf.Clamp01((timeSeconds - _deathStartedAt) / duration);
            var heatstroke = Mathf.Clamp01(Float(props, "heatstroke", 0f));
            var hypothermia = Mathf.Clamp01(Float(props, "hypothermia", 0f));
            var heatWeight = Mathf.Clamp01(Float(props, "heatstrokePostWeight", 0f));
            var severeNormalized = Mathf.Clamp01(Float(props, "severeHeatstrokeWeight", 0f));
            var phaseFloor = Float(props, "heatstrokePhasingFloor", 0f);
            var phaseFrequency = Float(props, "heatstrokePhasingFrequency", 5f);
            var severeWeight = Mathf.Clamp01(severeNormalized + severeNormalized * (1f - severeNormalized) *
                Mathf.Max(phaseFloor, Mathf.Sin(timeSeconds * phaseFrequency)));
            var thermalFade = 1f - deathWeight;
            if (entity == null && string.Equals(_deathCause, "heatstroke", StringComparison.Ordinal))
            {
                heatWeight = thermalFade;
                severeWeight = thermalFade;
            }
            var coldWeight = entity == null && string.Equals(_deathCause, "hypothermia", StringComparison.Ordinal)
                ? thermalFade : 0f;

            return new EveUnityThermalPresentationFrame(
                entity?.EntityId ?? "",
                Float(props, "cockpitTemperature", 0f), heatstroke, hypothermia,
                Bool(props, "heatstrokeRisk"), Bool(props, "hypothermiaRisk"),
                heatWeight, severeWeight, coldWeight, coldWeight, _deathCause, deathWeight);
        }

        private static EveUnityPlayableWorldEntity? ResolvePlayer(EveUnityPlayableWorldProjection? world)
        {
            if (world == null) return null;
            return world.Entities.FirstOrDefault(value =>
                       string.Equals(value.EntityId, world.PlayerEntityId, StringComparison.Ordinal)) ??
                   world.Entities.FirstOrDefault(value => value.Controllable);
        }

        private static ThermalDeath? LatestThermalDeath(EveUnitySceneProjection? projection)
        {
            if (projection == null) return null;
            ThermalDeath? latest = null;
            Visit(projection.Root);
            return latest;

            void Visit(EveUnitySceneNode node)
            {
                if (string.Equals(node.ComponentKind, "feedback.event", StringComparison.Ordinal) &&
                    string.Equals(Read(node.Props, "eventKind"), "entity.destroyed", StringComparison.Ordinal))
                {
                    var cause = Read(node.Props, "reason");
                    if (string.Equals(cause, "heatstroke", StringComparison.Ordinal) ||
                        string.Equals(cause, "hypothermia", StringComparison.Ordinal))
                    {
                        var candidate = new ThermalDeath(Read(node.Props, "eventId"), cause,
                            Long(node.Props, "frameId", -1), Long(node.Props, "currentFrameId", -1));
                        if (!latest.HasValue || candidate.FrameId > latest.Value.FrameId ||
                            candidate.FrameId == latest.Value.FrameId && string.CompareOrdinal(
                                candidate.EventId, latest.Value.EventId) > 0)
                            latest = candidate;
                    }
                }
                foreach (var child in node.Children) Visit(child);
            }
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyProps =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static string Read(IReadOnlyDictionary<string, string> props, string key) =>
            props.TryGetValue(key, out var value) ? value ?? "" : "";
        private static float Float(IReadOnlyDictionary<string, string> props, string key, float fallback) =>
            float.TryParse(Read(props, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        private static long Long(IReadOnlyDictionary<string, string> props, string key, long fallback) =>
            long.TryParse(Read(props, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        private static bool Bool(IReadOnlyDictionary<string, string> props, string key) =>
            bool.TryParse(Read(props, key), out var value) && value;
        private static float Positive(float value, float fallback) => value > 0f ? value : fallback;

        private readonly struct ThermalDeath
        {
            public ThermalDeath(string eventId, string cause, long frameId, long currentFrameId)
            { EventId = eventId ?? ""; Cause = cause ?? ""; FrameId = frameId; CurrentFrameId = currentFrameId; }
            public string EventId { get; }
            public string Cause { get; }
            public long FrameId { get; }
            public long CurrentFrameId { get; }
        }
    }

    public sealed class EveUnityThermalPresenter : MonoBehaviour
    {
        private readonly EveUnityThermalPresentationState _state = new EveUnityThermalPresentationState();
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityThermalPresentationSink[] _sinks = Array.Empty<IEveUnityThermalPresentationSink>();

        public EveUnityThermalPresentationFrame LastFrame { get; private set; }

        public void Bind(EveUnityPlayableWorldClientHost host)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            _sinks = GetComponents<MonoBehaviour>().OfType<IEveUnityThermalPresentationSink>().ToArray();
            ApplyAt(Time.unscaledTime);
        }

        public void ApplyAt(float timeSeconds)
        {
            LastFrame = _state.Apply(_host?.ActiveWorld, _host?.ActiveProjection, timeSeconds);
            foreach (var sink in _sinks) sink.ApplyThermalPresentation(LastFrame);
        }

        private void Update() => ApplyAt(Time.unscaledTime);
    }
}
