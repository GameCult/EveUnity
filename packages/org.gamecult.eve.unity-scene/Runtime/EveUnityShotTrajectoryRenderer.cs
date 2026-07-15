using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityShotTrajectoryRenderer : MonoBehaviour
    {
        [SerializeField] private float width = 0.2f;
        [SerializeField] private float minimumDurationSeconds = 0.03f;
        [SerializeField] private float lingerSeconds = 0.08f;
        [SerializeField] private Color hitColor = new Color(1f, 0.35f, 0.12f, 1f);
        [SerializeField] private Color missColor = new Color(0.35f, 0.8f, 1f, 0.8f);

        private readonly List<ActiveTrajectory> _active = new List<ActiveTrajectory>();
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityGameObjectAssetProvider? _assetProvider;
        private Material? _material;

        public int ActiveTrajectoryCount => _active.Count;
        public int ActiveFallbackTrajectoryCount => _active.Count(trajectory => trajectory.Line != null);

        public void Bind(EveUnityPlayableWorldClientHost host, IEveUnityGameObjectAssetProvider? assetProvider = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (ReferenceEquals(_host, host) && ReferenceEquals(_assetProvider, assetProvider)) return;
            Unbind();
            _host = host;
            _assetProvider = assetProvider;
            _host.ShotAvailable += Present;
        }

        public void Unbind()
        {
            if (_host != null) _host.ShotAvailable -= Present;
            _host = null;
            _assetProvider = null;
        }

        public void Present(EveUnityShotReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var shot = new GameObject("Eve shot " + receipt.ShotId);
            shot.transform.SetParent(transform, false);
            var origin = Vector(receipt.Origin);
            var endpoint = Vector(receipt.Endpoint);
            var effectPrefab = ResolveEffectPrefab(receipt);
            LineRenderer? line = null;
            if (effectPrefab != null)
            {
                var effect = Instantiate(effectPrefab, shot.transform);
                effect.transform.position = origin;
                var direction = endpoint - origin;
                if (direction.sqrMagnitude > 0.0001f) effect.transform.rotation = Quaternion.LookRotation(direction.normalized);
                var lightning = effect.GetComponentInChildren<LightningCompute>();
                if (lightning != null)
                {
                    lightning.StartPosition = origin;
                    lightning.EndPosition = endpoint;
                    lightning.Animate = true;
                    lightning.StartAnimation();
                }
            }
            else
            {
                line = shot.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                var intensityScale = Mathf.Clamp(Mathf.Sqrt((float)receipt.PresentationIntensity), 0.5f, 4f);
                line.startWidth = Math.Max(0.01f, width * intensityScale);
                line.endWidth = Math.Max(0.01f, width * 0.5f * intensityScale);
                line.material = SharedMaterial();
                line.startColor = line.endColor = receipt.Hit ? hitColor : missColor;
                line.SetPosition(0, origin);
            }
            var impactPrefab = ResolveRolePrefab("effect.impact." + receipt.ImpactKind);
            var travels = string.Equals(receipt.PresentationKind, "bolt", StringComparison.Ordinal) ||
                string.Equals(receipt.PresentationKind, "guided", StringComparison.Ordinal);
            if (line != null)
                line.SetPosition(1, travels ? origin : endpoint);
            _active.Add(new ActiveTrajectory(
                shot,
                line,
                origin,
                endpoint,
                Time.unscaledTime,
                Math.Max(minimumDurationSeconds, (float)receipt.DurationSeconds),
                travels,
                impactPrefab));
        }

        private void Update()
        {
            var now = Time.unscaledTime;
            for (var index = _active.Count - 1; index >= 0; index--)
            {
                var trajectory = _active[index];
                var progress = Mathf.Clamp01((now - trajectory.StartedAt) / trajectory.Duration);
                if (trajectory.Travels && trajectory.Line != null)
                    trajectory.Line.SetPosition(1, Vector3.Lerp(trajectory.Origin, trajectory.Endpoint, progress));
                if (progress >= 1 && !trajectory.ImpactPresented)
                {
                    trajectory.ImpactPresented = true;
                    if (trajectory.ImpactPrefab != null)
                    {
                        var impact = Instantiate(trajectory.ImpactPrefab, trajectory.Root.transform);
                        impact.transform.position = trajectory.Endpoint;
                    }
                }
                if (progress < 1 || now < trajectory.StartedAt + trajectory.Duration + lingerSeconds) continue;
                Destroy(trajectory.Root);
                _active.RemoveAt(index);
            }
        }

        private Material SharedMaterial()
        {
            if (_material != null) return _material;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("EveUnity requires an unlit shader for shot trajectories.");
            _material = new Material(shader) { name = "EveUnity shot trajectory" };
            return _material;
        }

        private GameObject? ResolveEffectPrefab(EveUnityShotReceipt receipt)
        {
            if (!string.IsNullOrWhiteSpace(receipt.ItemKey))
            {
                var itemPrefab = ResolveRolePrefab("effect.shot.item." + receipt.ItemKey);
                if (itemPrefab != null) return itemPrefab;
            }
            return ResolveRolePrefab("effect.shot." + receipt.PresentationKind);
        }

        private GameObject? ResolveRolePrefab(string role) => _assetProvider?.ResolvePrefab(
            new EveUnityPlayableWorldAssetBinding("", role, "provider-asset-ref"));

        private void OnDestroy()
        {
            Unbind();
            foreach (var trajectory in _active)
                if (trajectory.Root != null) Destroy(trajectory.Root);
            _active.Clear();
            if (_material != null) Destroy(_material);
        }

        private static Vector3 Vector(EveUnityShotVector3 value) =>
            new Vector3((float)value.X, (float)value.Y, (float)value.Z);

        private sealed class ActiveTrajectory
        {
            public ActiveTrajectory(GameObject root, LineRenderer? line, Vector3 origin, Vector3 endpoint, float startedAt, float duration, bool travels, GameObject? impactPrefab)
            {
                Root = root;
                Line = line;
                Origin = origin;
                Endpoint = endpoint;
                StartedAt = startedAt;
                Duration = duration;
                Travels = travels;
                ImpactPrefab = impactPrefab;
            }

            public GameObject Root { get; }
            public LineRenderer? Line { get; }
            public Vector3 Origin { get; }
            public Vector3 Endpoint { get; }
            public float StartedAt { get; }
            public float Duration { get; }
            public bool Travels { get; }
            public GameObject? ImpactPrefab { get; }
            public bool ImpactPresented { get; set; }
        }
    }
}
