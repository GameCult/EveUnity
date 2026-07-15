using System;
using System.Globalization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityAimPresentation
    {
        public EveUnityAimPresentation(EveUnitySceneNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            ControlledEntityId = Get(node, "controlledEntityId");
            ConvergenceTargetEntityId = Get(node, "convergenceTargetEntityId");
            ViewDotRole = Get(node, "viewDotRole");
            MinimumConvergenceDistance = Math.Max(0.01f, Number(node, "minimumConvergenceDistance", 50f));
            ViewDotRadius = Math.Max(0.01f, Number(node, "viewDotRadius", 0.8f));
        }

        public string ControlledEntityId { get; }
        public string ConvergenceTargetEntityId { get; }
        public string ViewDotRole { get; }
        public float MinimumConvergenceDistance { get; }
        public float ViewDotRadius { get; }

        public static EveUnityAimPresentation? Find(EveUnitySceneProjection? projection) =>
            projection == null ? null : Find(projection.Root);

        private static EveUnityAimPresentation? Find(EveUnitySceneNode node)
        {
            if (string.Equals(node.ComponentKind, "aim.presentation", StringComparison.Ordinal))
                return new EveUnityAimPresentation(node);
            foreach (var child in node.Children)
            {
                var found = Find(child);
                if (found != null) return found;
            }
            return null;
        }

        private static string Get(EveUnitySceneNode node, string key) =>
            node.Props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static float Number(EveUnitySceneNode node, string key, float fallback) =>
            float.TryParse(Get(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }
}
