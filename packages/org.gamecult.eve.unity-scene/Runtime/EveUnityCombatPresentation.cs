using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCombatPresentation
    {
        public EveUnityCombatPresentation(EveUnitySceneNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            ControlledEntityId = Get(node, "controlledEntityId");
            ControlledEntityIndex = Int(node, "controlledEntityIndex", -1);
            SelectedTargetEntityId = Get(node, "selectedTargetEntityId");
            SelectedTargetEntityIndex = Int(node, "selectedTargetEntityIndex", -1);
            TargetVisible = Bool(node, "targetVisible");
            TargetHostile = Bool(node, "targetHostile");
            ContactInformation = Ratio(node, "contactInformation");
            ShieldRatio = Ratio(node, "shieldRatio");
            HullRatio = Ratio(node, "hullRatio");
            LockProgress = Ratio(node, "lockProgress");
            ReticleRole = Get(node, "reticleRole");
            LockRole = Get(node, "lockRole");
            ShieldMeterRole = Get(node, "shieldMeterRole");
            HullMeterRole = Get(node, "hullMeterRole");
            HitMarkerRole = Get(node, "hitMarkerRole");
            LockDisplayThreshold = Ratio(node, "lockDisplayThreshold", 0.01f);
            HitMarkerDurationSeconds = Math.Max(0.01f, Number(node, "hitMarkerDurationSeconds", 0.25f));
            RadialFillMinimum = Ratio(node, "radialFillMinimum", 0.25f);
            RadialFillMaximum = Ratio(node, "radialFillMaximum", 0.75f);
        }

        public string ControlledEntityId { get; }
        public int ControlledEntityIndex { get; }
        public string SelectedTargetEntityId { get; }
        public int SelectedTargetEntityIndex { get; }
        public bool TargetVisible { get; }
        public bool TargetHostile { get; }
        public float ContactInformation { get; }
        public float ShieldRatio { get; }
        public float HullRatio { get; }
        public float LockProgress { get; }
        public string ReticleRole { get; }
        public string LockRole { get; }
        public string ShieldMeterRole { get; }
        public string HullMeterRole { get; }
        public string HitMarkerRole { get; }
        public float LockDisplayThreshold { get; }
        public float HitMarkerDurationSeconds { get; }
        public float RadialFillMinimum { get; }
        public float RadialFillMaximum { get; }

        public float RadialFill(float ratio) =>
            RadialFillMinimum + (RadialFillMaximum - RadialFillMinimum) * Math.Max(0, Math.Min(1, ratio));

        public bool PresentsHit(EveUnityShotReceipt receipt) =>
            receipt != null &&
            receipt.SourceEntityIndex == ControlledEntityIndex &&
            receipt.TargetEntityIndex == SelectedTargetEntityIndex &&
            receipt.Hit &&
            (receipt.AppliedDamage > 0 || receipt.ShieldAbsorbedDamage > 0);

        public static EveUnityCombatPresentation? Find(EveUnitySceneProjection? projection)
        {
            if (projection == null) return null;
            return Find(projection.Root);
        }

        private static EveUnityCombatPresentation? Find(EveUnitySceneNode node)
        {
            if (string.Equals(node.ComponentKind, "combat.presentation", StringComparison.Ordinal))
                return new EveUnityCombatPresentation(node);
            foreach (var child in node.Children)
            {
                var found = Find(child);
                if (found != null) return found;
            }
            return null;
        }

        private static string Get(EveUnitySceneNode node, string key) =>
            node.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static int Int(EveUnitySceneNode node, string key, int fallback) =>
            int.TryParse(Get(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static bool Bool(EveUnitySceneNode node, string key) =>
            bool.TryParse(Get(node, key), out var value) && value;

        private static float Number(EveUnitySceneNode node, string key, float fallback = 0) =>
            float.TryParse(Get(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static float Ratio(EveUnitySceneNode node, string key, float fallback = 0) =>
            Math.Max(0, Math.Min(1, Number(node, key, fallback)));
    }
}
