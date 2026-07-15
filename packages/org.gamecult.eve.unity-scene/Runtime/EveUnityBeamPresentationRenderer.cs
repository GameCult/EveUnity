using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityBeamPresentationRenderer : MonoBehaviour
    {
        private readonly Dictionary<string, ActiveBeam> _active = new Dictionary<string, ActiveBeam>(StringComparer.Ordinal);
        private EveUnityPlayableWorldClientHost? _host;
        private IEveUnityGameObjectAssetProvider? _assetProvider;

        public int ActiveBeamCount => _active.Count;
        public int ActiveParticleSystemCount
        {
            get
            {
                var count = 0;
                foreach (var beam in _active.Values)
                    if (beam.Root != null)
                        count += beam.Root.GetComponentsInChildren<ParticleSystem>(true).Length;
                return count;
            }
        }

        public void Bind(
            EveUnityPlayableWorldClientHost host,
            IEveUnityGameObjectAssetProvider? assetProvider = null)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            _assetProvider = assetProvider;
        }

        public void RefreshNow()
        {
            Apply(
                EveUnityBeamPresentation.FindAll(_host?.ActiveProjection),
                _host?.PresentedEntities,
                _assetProvider);
        }

        public void Apply(
            IReadOnlyList<EveUnityBeamPresentation> presentations,
            IEveUnityPresentedEntityRegistry? entities,
            IEveUnityGameObjectAssetProvider? assetProvider)
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var presentation in presentations ?? Array.Empty<EveUnityBeamPresentation>())
            {
                if (string.IsNullOrWhiteSpace(presentation.Id) ||
                    string.IsNullOrWhiteSpace(presentation.SourceEntityId) ||
                    string.IsNullOrWhiteSpace(presentation.AssetRole) ||
                    !presentation.UsesSourceForward || entities == null ||
                    !entities.TryGetByEntityId(presentation.SourceEntityId, out var source))
                    continue;

                retained.Add(presentation.Id);
                if (!_active.TryGetValue(presentation.Id, out var beam) ||
                    !string.Equals(beam.SourceEntityId, presentation.SourceEntityId, StringComparison.Ordinal) ||
                    !string.Equals(beam.AssetRole, presentation.AssetRole, StringComparison.Ordinal) ||
                    beam.Root == null)
                {
                    Remove(presentation.Id);
                    var prefab = assetProvider?.ResolvePrefab(new EveUnityPlayableWorldAssetBinding(
                        presentation.AssetRole, presentation.AssetRole, "provider-asset-ref"));
                    if (prefab == null) continue;
                    var root = Instantiate(prefab, source.Transform, false);
                    root.name = "Eve beam " + presentation.Id;
                    beam = new ActiveBeam(root, presentation.SourceEntityId, presentation.AssetRole);
                    _active[presentation.Id] = beam;
                }
                else if (beam.Root.transform.parent != source.Transform)
                {
                    beam.Root.transform.SetParent(source.Transform, false);
                }

                var visiblePower = presentation.Power > presentation.ActivationThreshold
                    ? presentation.Power
                    : 0f;
                foreach (var particles in beam.Root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var emission = particles.emission;
                    emission.rateOverTimeMultiplier = visiblePower;
                }
                beam.Power = visiblePower;
            }

            var stale = new List<string>();
            foreach (var id in _active.Keys)
                if (!retained.Contains(id)) stale.Add(id);
            foreach (var id in stale) Remove(id);
        }

        public bool TryGetPower(string presentationId, out float power)
        {
            if (_active.TryGetValue(presentationId ?? "", out var beam) && beam.Root != null)
            {
                power = beam.Power;
                return true;
            }
            power = 0f;
            return false;
        }

        private void LateUpdate() => RefreshNow();

        private void Remove(string id)
        {
            if (!_active.TryGetValue(id, out var beam)) return;
            _active.Remove(id);
            if (beam.Root != null)
            {
                if (Application.isPlaying) Destroy(beam.Root);
                else DestroyImmediate(beam.Root);
            }
        }

        private void OnDestroy()
        {
            foreach (var beam in _active.Values)
                if (beam.Root != null)
                {
                    if (Application.isPlaying) Destroy(beam.Root);
                    else DestroyImmediate(beam.Root);
                }
            _active.Clear();
            _host = null;
            _assetProvider = null;
        }

        private sealed class ActiveBeam
        {
            public ActiveBeam(GameObject root, string sourceEntityId, string assetRole)
            {
                Root = root;
                SourceEntityId = sourceEntityId;
                AssetRole = assetRole;
            }

            public GameObject Root { get; }
            public string SourceEntityId { get; }
            public string AssetRole { get; }
            public float Power { get; set; }
        }
    }
}
