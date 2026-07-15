using System;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityAimPresentationRenderer : MonoBehaviour
    {
        [SerializeField] private Color viewDotColor = new Color(0.35f, 0.95f, 1f, 0.95f);
        [SerializeField] private float width = 0.1f;

        private EveUnityPlayableWorldClientHost? _host;
        private LineRenderer? _ring;
        private LineRenderer? _horizontal;
        private LineRenderer? _vertical;
        private Material? _material;

        public EveUnityAimPresentation? Current { get; private set; }
        public bool ViewDotVisible => _ring != null && _ring.enabled;
        public Vector3 ViewDotPosition { get; private set; }

        public void Bind(EveUnityPlayableWorldClientHost host)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
        }

        public void RefreshNow()
        {
            Current = EveUnityAimPresentation.Find(_host?.ActiveProjection);
            if (Current == null || string.IsNullOrWhiteSpace(Current.ControlledEntityId))
            {
                SetVisible(false);
                return;
            }

            var controlled = FindMarker(Current.ControlledEntityId);
            if (controlled == null)
            {
                SetVisible(false);
                return;
            }

            var distance = Current.MinimumConvergenceDistance;
            var target = FindMarker(Current.ConvergenceTargetEntityId);
            if (target != null)
                distance = Mathf.Max(distance, Vector3.Distance(controlled.transform.position, target.transform.position));
            var forward = controlled.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            ViewDotPosition = controlled.transform.position + forward * distance;

            EnsureVisuals();
            var camera = _host?.ActiveCameraTransform;
            if (camera == null)
            {
                SetVisible(false);
                return;
            }
            var right = camera.right;
            var up = camera.up;
            var center = ViewDotPosition - camera.forward * 0.1f;
            Ring(_ring!, center, right, up, Current.ViewDotRadius);
            Segment(_horizontal!, center, right, Current.ViewDotRadius * 1.6f);
            Segment(_vertical!, center, up, Current.ViewDotRadius * 1.6f);
            SetVisible(true);
        }

        private void LateUpdate() => RefreshNow();

        private EveUnityPlayableWorldEntityMarker? FindMarker(string entityId)
        {
            if (_host == null || string.IsNullOrWhiteSpace(entityId)) return null;
            foreach (var marker in _host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true))
                if (string.Equals(marker.EntityId, entityId, StringComparison.Ordinal)) return marker;
            return null;
        }

        private void EnsureVisuals()
        {
            if (_ring != null) return;
            var root = new GameObject("Eve aim presentation");
            root.transform.SetParent(transform, false);
            _ring = Line(root.transform, "View direction ring");
            _horizontal = Line(root.transform, "View direction horizontal");
            _vertical = Line(root.transform, "View direction vertical");
        }

        private LineRenderer Line(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.material = SharedMaterial();
            line.startColor = line.endColor = viewDotColor;
            line.startWidth = line.endWidth = Mathf.Max(0.01f, width);
            line.numCapVertices = 2;
            return line;
        }

        private Material SharedMaterial()
        {
            if (_material != null) return _material;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("EveUnity requires an unlit shader for aim presentation.");
            return _material = new Material(shader) { name = "EveUnity aim presentation" };
        }

        private static void Ring(LineRenderer line, Vector3 center, Vector3 right, Vector3 up, float radius)
        {
            const int segments = 33;
            line.positionCount = segments;
            for (var index = 0; index < segments; index++)
            {
                var angle = index / (float)(segments - 1) * Mathf.PI * 2f;
                line.SetPosition(index, center + right * (Mathf.Cos(angle) * radius) + up * (Mathf.Sin(angle) * radius));
            }
        }

        private static void Segment(LineRenderer line, Vector3 center, Vector3 axis, float radius)
        {
            line.positionCount = 2;
            line.SetPosition(0, center - axis * radius);
            line.SetPosition(1, center + axis * radius);
        }

        private void SetVisible(bool visible)
        {
            if (_ring != null) _ring.enabled = visible;
            if (_horizontal != null) _horizontal.enabled = visible;
            if (_vertical != null) _vertical.enabled = visible;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
