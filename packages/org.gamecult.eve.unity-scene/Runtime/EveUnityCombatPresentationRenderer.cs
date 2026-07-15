using System;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCombatPresentationRenderer : MonoBehaviour
    {
        [SerializeField] private Color reticleColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        [SerializeField] private Color lockColor = new Color(1f, 0.7f, 0.15f, 1f);
        [SerializeField] private Color shieldColor = new Color(0.15f, 0.65f, 1f, 1f);
        [SerializeField] private Color hullColor = new Color(1f, 0.35f, 0.2f, 1f);
        [SerializeField] private Color hitColor = Color.white;
        [SerializeField] private float width = 0.12f;

        private EveUnityPlayableWorldClientHost? _host;
        private Transform? _visualRoot;
        private LineRenderer? _reticle;
        private LineRenderer? _lock;
        private LineRenderer? _shield;
        private LineRenderer? _hull;
        private LineRenderer? _hitA;
        private LineRenderer? _hitB;
        private Material? _material;
        private float _hitUntil;
        private EveUnityCombatPresentation? _lastPresentation;
        private Vector3 _lastCenter;
        private Vector3 _lastRight;
        private Vector3 _lastUp;
        private float _lastRadius;
        private bool _hasLastTarget;

        public EveUnityCombatPresentation? Current { get; private set; }
        public bool ReticleVisible => _reticle != null && _reticle.enabled;
        public bool LockVisible => _lock != null && _lock.enabled;
        public bool HitMarkerVisible => _hitA != null && _hitA.enabled;

        public void Bind(EveUnityPlayableWorldClientHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (ReferenceEquals(_host, host)) return;
            Unbind();
            _host = host;
            _host.ShotAvailable += OnShotAvailable;
        }

        public void Unbind()
        {
            if (_host != null) _host.ShotAvailable -= OnShotAvailable;
            _host = null;
            Current = null;
            SetVisible(false);
        }

        public void RefreshNow()
        {
            Current = EveUnityCombatPresentation.Find(_host?.ActiveProjection);
            if (Current == null || string.IsNullOrWhiteSpace(Current.SelectedTargetEntityId))
            {
                PresentRetainedHit();
                return;
            }

            var marker = FindMarker(Current.SelectedTargetEntityId);
            if (marker == null)
            {
                PresentRetainedHit();
                return;
            }

            _lastPresentation = Current;
            EnsureVisuals();
            PresentationAxes(out var right, out var up, out var towardCamera, out var cullingMask);
            var visualBounds = VisualBounds(marker.transform, cullingMask);
            var center = visualBounds?.center ?? (marker.transform.position + Vector3.up * 0.25f);
            var radius = Math.Max(0.8f, visualBounds?.extents.magnitude ?? marker.transform.lossyScale.magnitude * 0.8f);
            center += towardCamera * 0.1f;
            _lastCenter = center;
            _lastRight = right;
            _lastUp = up;
            _lastRadius = radius;
            _hasLastTarget = true;
            Arc(_reticle!, center, right, up, radius * 1.35f, 1f, 0f);
            _reticle!.enabled = Current.TargetVisible;

            var lockVisible = Current.TargetVisible && Current.TargetHostile &&
                Current.LockProgress > Current.LockDisplayThreshold;
            var noise = (1f - Current.LockProgress) * radius * 0.2f;
            var frequency = Mathf.Lerp(0.01f, 10f, Mathf.Pow(Current.LockProgress, 4f));
            var noisyCenter = center +
                right * ((Mathf.PerlinNoise(Time.unscaledTime * frequency, 0.37f) - 0.5f) * noise) +
                up * ((Mathf.PerlinNoise(0.73f, Time.unscaledTime * frequency) - 0.5f) * noise);
            var spin = Time.unscaledTime * Mathf.Lerp(5f, 720f, Current.LockProgress * Current.LockProgress);
            Arc(_lock!, noisyCenter, right, up, radius * 1.55f, Current.LockProgress, spin);
            _lock!.enabled = lockVisible;

            Arc(_shield!, center, right, up, radius * 1.78f, Current.RadialFill(Current.ShieldRatio), -90f);
            Arc(_hull!, center, right, up, radius * 1.98f, Current.RadialFill(Current.HullRatio), -90f);
            _shield!.enabled = Current.TargetVisible;
            _hull!.enabled = Current.TargetVisible;

            var hitVisible = Time.unscaledTime < _hitUntil;
            Cross(_hitA!, _hitB!, center, right, up, radius * 0.55f);
            _hitA!.enabled = hitVisible;
            _hitB!.enabled = hitVisible;
        }

        private void PresentRetainedHit()
        {
            if (!_hasLastTarget || Time.unscaledTime >= _hitUntil)
            {
                SetVisible(false);
                return;
            }
            EnsureVisuals();
            _reticle!.enabled = _lock!.enabled = _shield!.enabled = _hull!.enabled = false;
            Cross(_hitA!, _hitB!, _lastCenter, _lastRight, _lastUp, _lastRadius * 0.55f);
            _hitA!.enabled = _hitB!.enabled = true;
        }

        private void LateUpdate() => RefreshNow();

        private void OnShotAvailable(EveUnityShotReceipt receipt)
        {
            Current = EveUnityCombatPresentation.Find(_host?.ActiveProjection);
            var presentation = Current;
            if (presentation?.PresentsHit(receipt) != true)
                presentation = _lastPresentation;
            if (presentation?.PresentsHit(receipt) == true)
                _hitUntil = Time.unscaledTime + presentation.HitMarkerDurationSeconds;
        }

        private EveUnityPlayableWorldEntityMarker? FindMarker(string entityId)
        {
            if (_host == null) return null;
            foreach (var marker in _host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true))
                if (string.Equals(marker.EntityId, entityId, StringComparison.Ordinal)) return marker;
            return null;
        }

        private void EnsureVisuals()
        {
            if (_visualRoot != null) return;
            var root = new GameObject("Eve combat presentation");
            root.transform.SetParent(transform, false);
            _visualRoot = root.transform;
            _reticle = Line("Selected target reticle", reticleColor, width);
            _lock = Line("Target lock", lockColor, width * 1.15f);
            _shield = Line("Target shield", shieldColor, width * 0.75f);
            _hull = Line("Target hull", hullColor, width * 0.75f);
            _hitA = Line("Hit marker A", hitColor, width * 1.3f);
            _hitB = Line("Hit marker B", hitColor, width * 1.3f);
        }

        private LineRenderer Line(string name, Color color, float lineWidth)
        {
            var child = new GameObject(name);
            child.transform.SetParent(_visualRoot, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = false;
            line.material = SharedMaterial();
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = Math.Max(0.01f, lineWidth);
            line.numCapVertices = 2;
            return line;
        }

        private Material SharedMaterial()
        {
            if (_material != null) return _material;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("EveUnity requires an unlit shader for combat presentation.");
            _material = new Material(shader) { name = "EveUnity combat presentation" };
            return _material;
        }

        private static void PresentationAxes(out Vector3 right, out Vector3 up, out Vector3 towardCamera, out int cullingMask)
        {
            Camera? selected = Camera.main;
            if (selected == null)
            {
                foreach (var candidate in Camera.allCameras)
                {
                    if (!candidate.isActiveAndEnabled || candidate.orthographic)
                        continue;
                    selected = candidate;
                    break;
                }
            }
            right = selected == null ? Vector3.right : selected.transform.right;
            up = selected == null ? Vector3.up : selected.transform.up;
            towardCamera = selected == null ? Vector3.back : -selected.transform.forward;
            cullingMask = selected == null ? -1 : selected.cullingMask;
        }

        private static Bounds? VisualBounds(Transform root, int cullingMask)
        {
            Bounds? bounds = null;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: false))
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                if ((cullingMask & (1 << renderer.gameObject.layer)) == 0)
                    continue;
                if (renderer is LineRenderer || renderer is TrailRenderer ||
                    string.Equals(renderer.GetType().Name, "ParticleSystemRenderer", StringComparison.Ordinal))
                    continue;
                if (bounds.HasValue)
                {
                    var combined = bounds.Value;
                    combined.Encapsulate(renderer.bounds);
                    bounds = combined;
                }
                else
                {
                    bounds = renderer.bounds;
                }
            }
            return bounds;
        }

        private static void Arc(LineRenderer line, Vector3 center, Vector3 right, Vector3 up, float radius, float fill, float rotationDegrees)
        {
            fill = Mathf.Clamp01(fill);
            var segments = Math.Max(2, Mathf.CeilToInt(64 * fill) + 1);
            line.positionCount = segments;
            for (var index = 0; index < segments; index++)
            {
                var progress = segments <= 1 ? 0 : index / (float)(segments - 1);
                var angle = (rotationDegrees + progress * 360f * fill) * Mathf.Deg2Rad;
                line.SetPosition(index, center + right * (Mathf.Cos(angle) * radius) + up * (Mathf.Sin(angle) * radius));
            }
        }

        private static void Cross(LineRenderer a, LineRenderer b, Vector3 center, Vector3 right, Vector3 up, float radius)
        {
            a.positionCount = b.positionCount = 2;
            a.SetPosition(0, center - right * radius - up * radius);
            a.SetPosition(1, center + right * radius + up * radius);
            b.SetPosition(0, center - right * radius + up * radius);
            b.SetPosition(1, center + right * radius - up * radius);
        }

        private void SetVisible(bool visible)
        {
            if (_reticle != null) _reticle.enabled = visible;
            if (_lock != null) _lock.enabled = visible;
            if (_shield != null) _shield.enabled = visible;
            if (_hull != null) _hull.enabled = visible;
            if (_hitA != null) _hitA.enabled = visible && Time.unscaledTime < _hitUntil;
            if (_hitB != null) _hitB.enabled = visible && Time.unscaledTime < _hitUntil;
        }

        private void OnDestroy()
        {
            Unbind();
            if (_material != null) Destroy(_material);
        }
    }
}
