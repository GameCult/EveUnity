using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityFeedbackEffectRenderer : MonoBehaviour
    {
        [SerializeField] private float fallbackLifetimeSeconds = 5f;

        private readonly List<ActiveEffect> _active = new List<ActiveEffect>();
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityGameObjectAssetProvider? _assetProvider;

        public int ActiveEffectCount => _active.Count;

        public void Bind(EveUnityPlayableWorldClientHost host, IEveUnityGameObjectAssetProvider? assetProvider = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (ReferenceEquals(_host, host) && ReferenceEquals(_assetProvider, assetProvider)) return;
            Unbind();
            _host = host;
            _assetProvider = assetProvider;
            _host.FeedbackAvailable += Present;
        }

        public void Unbind()
        {
            if (_host != null) _host.FeedbackAvailable -= Present;
            _host = null;
            _assetProvider = null;
        }

        public void Present(EveUnityFeedbackEvent feedback)
        {
            if (feedback == null) throw new ArgumentNullException(nameof(feedback));
            if (string.IsNullOrWhiteSpace(feedback.Kind)) return;
            var prefab = ResolveRolePrefab("effect.feedback." + feedback.Kind);
            if (prefab == null) return;
            var effect = Instantiate(prefab, transform);
            effect.name = "Eve feedback " + feedback.Kind + " " + feedback.EventId;
            if (TryVector(feedback.Position, out var position)) effect.transform.position = position;
            _active.Add(new ActiveEffect(effect, Time.unscaledTime + Math.Max(0.01f, fallbackLifetimeSeconds)));
        }

        private void Update()
        {
            var now = Time.unscaledTime;
            for (var index = _active.Count - 1; index >= 0; index--)
            {
                var effect = _active[index];
                if (effect.Root != null && now < effect.DestroyAt) continue;
                if (effect.Root != null) Destroy(effect.Root);
                _active.RemoveAt(index);
            }
        }

        private GameObject? ResolveRolePrefab(string role) => _assetProvider?.ResolvePrefab(
            new EveUnityPlayableWorldAssetBinding("", role, "provider-asset-ref"));

        private static bool TryVector(string value, out Vector3 vector)
        {
            vector = Vector3.zero;
            var parts = (value ?? "").Split(',');
            if (parts.Length < 2 ||
                !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return false;
            var y = 0f;
            if (parts.Length >= 3)
            {
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    return false;
            }
            vector = new Vector3(x, y, z);
            return true;
        }

        private void OnDestroy()
        {
            Unbind();
            foreach (var effect in _active)
                if (effect.Root != null) Destroy(effect.Root);
            _active.Clear();
        }

        private readonly struct ActiveEffect
        {
            public ActiveEffect(GameObject root, float destroyAt) { Root = root; DestroyAt = destroyAt; }
            public GameObject Root { get; }
            public float DestroyAt { get; }
        }
    }
}
