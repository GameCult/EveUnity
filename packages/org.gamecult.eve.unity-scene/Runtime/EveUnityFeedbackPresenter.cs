using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityFeedbackEvent
    {
        public EveUnityFeedbackEvent(EveUnitySceneNode node)
        {
            EventId = Get(node, "eventId"); Kind = Get(node, "eventKind"); SubjectKey = Get(node, "subjectKey");
            ItemKey = Get(node, "itemKey"); Position = Get(node, "position");
            FrameId = Long(node, "frameId", -1); ZoneIndex = Int(node, "zoneIndex", -1);
            SourceEntityIndex = Int(node, "sourceEntityIndex", -1); TargetEntityIndex = Int(node, "targetEntityIndex", -1);
            PickupIndex = Int(node, "pickupIndex", -1); ScalarValue = Double(node, "scalarValue", 0);
        }
        public string EventId { get; }
        public string Kind { get; }
        public long FrameId { get; }
        public int ZoneIndex { get; }
        public int SourceEntityIndex { get; }
        public int TargetEntityIndex { get; }
        public int PickupIndex { get; }
        public string SubjectKey { get; }
        public string ItemKey { get; }
        public string Position { get; }
        public double ScalarValue { get; }
        private static string Get(EveUnitySceneNode node, string key) => node.Props.TryGetValue(key, out var value) ? value ?? "" : "";
        private static int Int(EveUnitySceneNode node, string key, int fallback) => int.TryParse(Get(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        private static long Long(EveUnitySceneNode node, string key, long fallback) => long.TryParse(Get(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        private static double Double(EveUnitySceneNode node, string key, double fallback) => double.TryParse(Get(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public sealed class EveUnityFeedbackPresenter
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private bool _primed;
        public event Action<EveUnityFeedbackEvent>? FeedbackAvailable;

        public int Apply(EveUnitySceneProjection projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            var pending = new List<EveUnityFeedbackEvent>();
            Collect(projection.Root, pending);
            if (!_primed)
            {
                foreach (var value in pending) if (!string.IsNullOrWhiteSpace(value.EventId)) _seen.Add(value.EventId);
                _primed = true;
                return 0;
            }
            var emitted = 0;
            foreach (var value in pending)
            {
                if (string.IsNullOrWhiteSpace(value.EventId) || !_seen.Add(value.EventId)) continue;
                FeedbackAvailable?.Invoke(value); emitted++;
            }
            return emitted;
        }

        public void Reset() { _seen.Clear(); _primed = false; }

        private static void Collect(EveUnitySceneNode node, List<EveUnityFeedbackEvent> values)
        {
            if (string.Equals(node.ComponentKind, "feedback.event", StringComparison.Ordinal)) values.Add(new EveUnityFeedbackEvent(node));
            foreach (var child in node.Children) Collect(child, values);
        }
    }
}
