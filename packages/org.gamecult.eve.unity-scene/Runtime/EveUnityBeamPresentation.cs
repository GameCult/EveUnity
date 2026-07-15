using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityBeamPresentation
    {
        public const string SourceForwardDirection = "source-forward.v1";

        public EveUnityBeamPresentation(EveUnitySceneNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            Id = node.Id ?? "";
            SourceEntityId = Get(node, "sourceEntityId");
            AssetRole = Get(node, "assetRole");
            DirectionMode = Get(node, "directionMode");
            if (string.IsNullOrWhiteSpace(DirectionMode)) DirectionMode = SourceForwardDirection;
            RenderChannel = Get(node, "renderChannel");
            ActivationActionId = Get(node, "activationActionId");
            Power = NonNegative(node, "power", 0f);
            ActivationThreshold = NonNegative(node, "activationThreshold", 0.01f);
            Radius = NonNegative(node, "radius", 0f);
            MaximumDistance = NonNegative(node, "maximumDistance", 0f);
        }

        public string Id { get; }
        public string SourceEntityId { get; }
        public string AssetRole { get; }
        public string DirectionMode { get; }
        public string RenderChannel { get; }
        public string ActivationActionId { get; }
        public float Power { get; }
        public float ActivationThreshold { get; }
        public float Radius { get; }
        public float MaximumDistance { get; }
        public bool UsesSourceForward =>
            string.Equals(DirectionMode, SourceForwardDirection, StringComparison.Ordinal);

        public static IReadOnlyList<EveUnityBeamPresentation> FindAll(EveUnitySceneProjection? projection)
        {
            var found = new List<EveUnityBeamPresentation>();
            if (projection != null) FindAll(projection.Root, found);
            return found;
        }

        private static void FindAll(EveUnitySceneNode node, ICollection<EveUnityBeamPresentation> found)
        {
            if (string.Equals(node.ComponentKind, "beam.presentation", StringComparison.Ordinal))
                found.Add(new EveUnityBeamPresentation(node));
            foreach (var child in node.Children) FindAll(child, found);
        }

        private static string Get(EveUnitySceneNode node, string key) =>
            node.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static float NonNegative(EveUnitySceneNode node, string key, float fallback)
        {
            if (!float.TryParse(Get(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                float.IsNaN(value) || float.IsInfinity(value))
                return fallback;
            return Math.Max(0f, value);
        }
    }
}
