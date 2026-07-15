using System;
using System.Globalization;
using UnityEngine;

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

        public bool TryResolveViewPoint(EveUnityPlayableWorldClientHost? host, out Vector3 viewPoint)
        {
            viewPoint = default;
            if (host == null || string.IsNullOrWhiteSpace(ControlledEntityId)) return false;
            var controlled = FindMarker(host, ControlledEntityId);
            if (controlled == null) return false;
            var distance = MinimumConvergenceDistance;
            var target = FindMarker(host, ConvergenceTargetEntityId);
            if (target != null)
                distance = Mathf.Max(distance, Vector3.Distance(controlled.transform.position, target.transform.position));
            var forward = controlled.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            viewPoint = controlled.transform.position + forward * distance;
            return true;
        }

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

        private static EveUnityPlayableWorldEntityMarker? FindMarker(
            EveUnityPlayableWorldClientHost host,
            string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId)) return null;
            foreach (var marker in host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true))
                if (string.Equals(marker.EntityId, entityId, StringComparison.Ordinal)) return marker;
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
