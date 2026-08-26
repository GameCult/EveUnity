using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public readonly struct EveUnityShotVector3
    {
        public EveUnityShotVector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    public sealed class EveUnityShotReceipt
    {
        public EveUnityShotReceipt(EveUnitySceneNode node)
        {
            ShotId = Get(node, "shotId");
            FrameId = Long(node, "frameId", -1);
            ZoneIndex = Int(node, "zoneIndex", -1);
            SourceEntityIndex = Int(node, "sourceEntityIndex", -1);
            TargetEntityIndex = Int(node, "targetEntityIndex", -1);
            Hit = Bool(node, "hit");
            Outcome = Get(node, "outcome");
            Origin = Vector(node, "origin");
            Endpoint = Vector(node, "endpoint");
            DurationSeconds = Math.Max(0, Double(node, "presentationDuration", 0));
            PresentationKind = Get(node, "presentationKind");
            ItemKey = Get(node, "itemKey");
            ImpactKind = Get(node, "impactKind");
            PresentationIntensity = Math.Max(0, Double(node, "presentationIntensity", 1));
            AppliedDamage = Math.Max(0, Double(node, "appliedDamage", 0));
            ShieldAbsorbedDamage = Math.Max(0, Double(node, "shieldAbsorbedDamage", 0));
            LockQuality = Math.Max(0, Math.Min(1, Double(node, "lockQuality", 0)));
        }

        public string ShotId { get; }
        public long FrameId { get; }
        public int ZoneIndex { get; }
        public int SourceEntityIndex { get; }
        public int TargetEntityIndex { get; }
        public bool Hit { get; }
        public string Outcome { get; }
        public EveUnityShotVector3 Origin { get; }
        public EveUnityShotVector3 Endpoint { get; }
        public double DurationSeconds { get; }
        public string PresentationKind { get; }
        public string ItemKey { get; }
        public string ImpactKind { get; }
        public double PresentationIntensity { get; }
        public double AppliedDamage { get; }
        public double ShieldAbsorbedDamage { get; }
        public double LockQuality { get; }

        private static string Get(EveUnitySceneNode node, string key) =>
            node.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static int Int(EveUnitySceneNode node, string key, int fallback) =>
            int.TryParse(Get(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static long Long(EveUnitySceneNode node, string key, long fallback) =>
            long.TryParse(Get(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static double Double(EveUnitySceneNode node, string key, double fallback) =>
            double.TryParse(Get(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static bool Bool(EveUnitySceneNode node, string key) =>
            bool.TryParse(Get(node, key), out var value) && value;

        private static EveUnityShotVector3 Vector(EveUnitySceneNode node, string key)
        {
            var parts = Get(node, key).Split(',');
            if (parts.Length < 2) return new EveUnityShotVector3(0, 0, 0);
            var x = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedX) ? parsedX : 0;
            var z = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedZ) ? parsedZ : 0;
            return new EveUnityShotVector3(x, 0, z);
        }
    }

    public sealed class EveUnityShotReceiptPresenter
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private bool _primed;

        public event Action<EveUnityShotReceipt>? ShotAvailable;

        public int Apply(EveUnitySceneProjection projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            var receipts = new List<EveUnityShotReceipt>();
            Collect(projection.Root, receipts);
            if (!_primed)
            {
                foreach (var receipt in receipts)
                    if (!string.IsNullOrWhiteSpace(receipt.ShotId)) _seen.Add(receipt.ShotId);
                _primed = true;
                return 0;
            }

            var emitted = 0;
            foreach (var receipt in receipts)
            {
                if (string.IsNullOrWhiteSpace(receipt.ShotId) || !_seen.Add(receipt.ShotId)) continue;
                ShotAvailable?.Invoke(receipt);
                emitted++;
            }
            return emitted;
        }

        public void Reset()
        {
            _seen.Clear();
            _primed = false;
        }

        private static void Collect(EveUnitySceneNode node, ICollection<EveUnityShotReceipt> receipts)
        {
            if (string.Equals(node.ComponentKind, "shot.receipt", StringComparison.Ordinal))
                receipts.Add(new EveUnityShotReceipt(node));
            foreach (var child in node.Children) Collect(child, receipts);
        }
    }
}
