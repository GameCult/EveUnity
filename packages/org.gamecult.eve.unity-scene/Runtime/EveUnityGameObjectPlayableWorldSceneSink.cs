using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityGameObjectAssetProvider
    {
        GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset);
    }

    public interface IEveUnityNativeAssetProvider : IEveUnityGameObjectAssetProvider
    {
        UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType);
    }

    public sealed class EveUnityGameObjectPlayableWorldSceneSink :
        IEveUnityPlayableWorldSceneSink,
        IEveUnityEntityGenerationSink,
        IEveUnityPresentedEntityRegistry
    {
        private readonly Transform _root;
        private readonly IEveUnityGameObjectAssetProvider _assetProvider;
        private readonly Dictionary<string, GameObject> _instances = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, EveUnityPresentedEntityHandle> _presentedById =
            new Dictionary<string, EveUnityPresentedEntityHandle>(StringComparer.Ordinal);
        private IReadOnlyDictionary<int, EveUnityPresentedEntityHandle> _presentedByIndex =
            new Dictionary<int, EveUnityPresentedEntityHandle>();

        public EveUnityGameObjectPlayableWorldSceneSink(
            Transform root,
            IEveUnityGameObjectAssetProvider assetProvider)
        {
            _root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _assetProvider = assetProvider ?? throw new ArgumentNullException(nameof(assetProvider));
        }

        public int ActiveEntityCount => _instances.Count;
        public EveUnityPresentedEntityGeneration? CurrentGeneration { get; private set; }

        public bool TryGetByEntityId(string entityId, out EveUnityPresentedEntityHandle handle) =>
            _presentedById.TryGetValue(entityId ?? "", out handle!);

        public bool TryGetBySourceIndex(int sourceIndex, out EveUnityPresentedEntityHandle handle) =>
            _presentedByIndex.TryGetValue(sourceIndex, out handle!);

        public void ConfigureWorld(EveUnityPlayableWorldProjection world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (!string.IsNullOrWhiteSpace(world.WorldRootId))
                _root.name = world.WorldRootId;
        }

        public void UpsertEntity(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrWhiteSpace(entity.EntityId))
                return;

            GameObject instance;
            if (!_instances.TryGetValue(entity.EntityId, out instance) || instance == null)
            {
                instance = InstantiateEntity(entity, asset);
                _instances[entity.EntityId] = instance;
            }
            else
            {
                var marker = instance.GetComponent<EveUnityPlayableWorldEntityMarker>();
                if (marker != null && !string.Equals(marker.AssetRef, asset.AssetRef, StringComparison.Ordinal))
                {
                    DestroyInstance(instance);
                    instance = InstantiateEntity(entity, asset);
                    _instances[entity.EntityId] = instance;
                }
            }

            ApplyEntity(instance, entity, asset);
        }

        public void ApplyGeneration(EveUnityPresentedEntityGeneration generation)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (generation.Entities.Any(entity => string.IsNullOrWhiteSpace(entity.EntityId)))
                throw new InvalidOperationException("Presented entity generations require stable entity ids.");
            var factsById = generation.Entities.ToDictionary(entity => entity.EntityId, StringComparer.Ordinal);
            var factsByIndex = generation.Entities.ToDictionary(entity => entity.SourceIndex);
            GameObject? staging = null;
            var created = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var nextInstances = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            try
            {
                foreach (var fact in generation.Entities)
                {
                    if (_instances.TryGetValue(fact.EntityId, out var existing) && existing != null)
                    {
                        nextInstances.Add(fact.EntityId, existing);
                        continue;
                    }

                    staging ??= new GameObject($"{_root.name}.generation.{generation.ProducerEpoch}.{generation.Sequence}");
                    if (staging.transform.parent == null)
                    {
                        staging.SetActive(false);
                        staging.transform.SetParent(_root, false);
                    }
                    var entity = fact.ToPlayableWorldEntity();
                    var asset = new EveUnityPlayableWorldAssetBinding(
                        fact.AssetRef,
                        fact.EntityKind,
                        string.IsNullOrWhiteSpace(fact.AssetRef) ? "unity-generated-placeholder" : "provider-asset-ref");
                    var instance = InstantiateEntity(entity, asset, staging.transform);
                    created.Add(fact.EntityId, instance);
                    nextInstances.Add(fact.EntityId, instance);
                }

                foreach (var fact in generation.Entities)
                {
                    var entity = fact.ToPlayableWorldEntity();
                    var asset = new EveUnityPlayableWorldAssetBinding(
                        fact.AssetRef,
                        fact.EntityKind,
                        string.IsNullOrWhiteSpace(fact.AssetRef) ? "unity-generated-placeholder" : "provider-asset-ref");
                    ApplyEntity(nextInstances[fact.EntityId], entity, asset);
                }

                var nextById = factsById.ToDictionary(
                    pair => pair.Key,
                    pair => new EveUnityPresentedEntityHandle(pair.Value, nextInstances[pair.Key].transform),
                    StringComparer.Ordinal);
                var nextByIndex = factsByIndex.ToDictionary(
                    pair => pair.Key,
                    pair => nextById[pair.Value.EntityId]);

                foreach (var pair in _instances)
                    if (!nextInstances.ContainsKey(pair.Key) && pair.Value != null)
                        DestroyInstance(pair.Value);
                _instances.Clear();
                foreach (var pair in nextInstances)
                {
                    if (created.ContainsKey(pair.Key))
                    {
                        pair.Value.transform.SetParent(_root, true);
                        pair.Value.SetActive(true);
                    }
                    _instances.Add(pair.Key, pair.Value);
                }
                _presentedById = nextById;
                _presentedByIndex = nextByIndex;
                CurrentGeneration = generation;
            }
            catch
            {
                foreach (var instance in created.Values)
                    if (instance != null) DestroyInstance(instance);
                throw;
            }
            finally
            {
                if (staging != null)
                    DestroyInstance(staging);
            }
        }

        public void RemoveEntity(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId))
                return;

            GameObject instance;
            if (!_instances.TryGetValue(entityId, out instance))
                return;

            _instances.Remove(entityId);
            if (instance != null)
                DestroyInstance(instance);
        }

        private GameObject InstantiateEntity(
            EveUnityPlayableWorldEntity entity,
            EveUnityPlayableWorldAssetBinding asset,
            Transform? parent = null)
        {
            parent ??= _root;
            var prefab = _assetProvider.ResolvePrefab(asset);
            GameObject instance;
            if (prefab != null)
            {
                instance = new GameObject(entity.EntityId);
                instance.transform.SetParent(parent, false);
                var visual = UnityEngine.Object.Instantiate(prefab, instance.transform);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            else
            {
                instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            }

            if (instance.transform.parent != parent)
                instance.transform.SetParent(parent, false);

            return instance;
        }

        private static void ApplyEntity(
            GameObject instance,
            EveUnityPlayableWorldEntity entity,
            EveUnityPlayableWorldAssetBinding asset)
        {
            instance.name = string.IsNullOrWhiteSpace(entity.Label)
                ? entity.EntityId
                : $"{entity.EntityId} ({entity.Label})";
            instance.transform.localPosition = new Vector3(entity.PositionX, entity.PositionY, entity.PositionZ);
            instance.transform.localRotation = Quaternion.Euler(0f, entity.RotationY, 0f);
            if (entity.Radius > 0f &&
                string.Equals(asset.PresentationKind, "unity-generated-placeholder", StringComparison.Ordinal))
                instance.transform.localScale = Vector3.one * entity.Radius;

            var marker = instance.GetComponent<EveUnityPlayableWorldEntityMarker>();
            if (marker == null)
                marker = instance.AddComponent<EveUnityPlayableWorldEntityMarker>();

            marker.Apply(entity, asset);

            if (entity.Props.TryGetValue("presentationState", out var presentationState) &&
                !string.IsNullOrWhiteSpace(presentationState))
            {
                var semanticPresentation = instance.GetComponent<EveUnitySemanticEntityPresentation>();
                if (semanticPresentation == null)
                    semanticPresentation = instance.AddComponent<EveUnitySemanticEntityPresentation>();
                semanticPresentation.Apply(entity.Props);
            }
        }

        private static void DestroyInstance(GameObject instance)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(instance);
            else
                UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    public sealed class EveUnitySemanticEntityPresentation : MonoBehaviour
    {
        private static readonly int Emission = Shader.PropertyToID("_Emission");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock? _properties;
        private Renderer[] _renderers = Array.Empty<Renderer>();
        private string _state = "";
        private float _activePulseSeconds = 1f;
        private float _triggeredPulseSeconds = 0.25f;
        private float _activeEmission = 1f;
        private float _triggeredEmission = 4f;

        public string PresentationState => _state;

        public void Apply(IReadOnlyDictionary<string, string> props)
        {
            props ??= new Dictionary<string, string>(StringComparer.Ordinal);
            _state = Read(props, "presentationState");
            _activePulseSeconds = Positive(ReadFloat(props, "activePulseSeconds", _activePulseSeconds), 1f);
            _triggeredPulseSeconds = Positive(ReadFloat(props, "triggeredPulseSeconds", _triggeredPulseSeconds), 0.25f);
            _activeEmission = Math.Max(0f, ReadFloat(props, "activeEmission", _activeEmission));
            _triggeredEmission = Math.Max(0f, ReadFloat(props, "triggeredEmission", _triggeredEmission));
            _renderers = GetComponentsInChildren<Renderer>(true);
            ApplyAt(Time.time);
        }

        public void ApplyAt(float timeSeconds)
        {
            var triggered = string.Equals(_state, "triggered", StringComparison.Ordinal);
            var active = triggered || string.Equals(_state, "active", StringComparison.Ordinal);
            var period = triggered ? _triggeredPulseSeconds : _activePulseSeconds;
            var peak = triggered ? _triggeredEmission : _activeEmission;
            var envelope = active
                ? Mathf.Lerp(0.1f, 1f, Mathf.Abs(Mathf.Cos(Mathf.PI * timeSeconds / period)))
                : 0f;
            var value = peak * envelope;
            _properties ??= new MaterialPropertyBlock();
            foreach (var renderer in _renderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(_properties);
                _properties.SetFloat(Emission, value);
                _properties.SetColor(EmissionColor, Color.white * value);
                renderer.SetPropertyBlock(_properties);
            }
        }

        private void Update() => ApplyAt(Time.time);

        private static string Read(IReadOnlyDictionary<string, string> props, string key) =>
            props.TryGetValue(key, out var value) ? value ?? "" : "";

        private static float ReadFloat(IReadOnlyDictionary<string, string> props, string key, float fallback) =>
            props.TryGetValue(key, out var value) && float.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;

        private static float Positive(float value, float fallback) => value > 0f ? value : fallback;
    }

    public sealed class EveUnityResourcesAssetProvider : IEveUnityNativeAssetProvider
    {
        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var resourcesPath = EveUnityPlayableWorldAssetManifestEntry.NormalizeResourcesPath(asset.AssetRef);
            return string.IsNullOrWhiteSpace(resourcesPath)
                ? null
                : Resources.Load<GameObject>(resourcesPath);
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            var resourcesPath = EveUnityPlayableWorldAssetManifestEntry.NormalizeResourcesPath(asset.AssetRef);
            return string.IsNullOrWhiteSpace(resourcesPath) ? null : Resources.Load(resourcesPath, assetType);
        }
    }

    public sealed class EveUnityPlayableWorldEntityMarker : MonoBehaviour
    {
        public string EntityId = "";
        public string NodeId = "";
        public string EntityKind = "";
        public string Label = "";
        public string Faction = "";
        public string AssetRef = "";
        public string PresentationKind = "";
        public bool Selectable;
        public bool Controllable;

        public void Apply(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            EntityId = entity.EntityId;
            NodeId = entity.NodeId;
            EntityKind = entity.EntityKind;
            Label = entity.Label;
            Faction = entity.Faction;
            AssetRef = asset.AssetRef;
            PresentationKind = asset.PresentationKind;
            Selectable = entity.Selectable;
            Controllable = entity.Controllable;
        }
    }
}
