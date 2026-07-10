using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityGameObjectAssetProvider
    {
        GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset);
    }

    public sealed class EveUnityGameObjectPlayableWorldSceneSink : IEveUnityPlayableWorldSceneSink
    {
        private readonly Transform _root;
        private readonly IEveUnityGameObjectAssetProvider _assetProvider;
        private readonly Dictionary<string, GameObject> _instances = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public EveUnityGameObjectPlayableWorldSceneSink(
            Transform root,
            IEveUnityGameObjectAssetProvider assetProvider)
        {
            _root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _assetProvider = assetProvider ?? throw new ArgumentNullException(nameof(assetProvider));
        }

        public int ActiveEntityCount => _instances.Count;

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

        private GameObject InstantiateEntity(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset)
        {
            var prefab = _assetProvider.ResolvePrefab(asset);
            GameObject instance;
            if (prefab != null)
            {
                instance = new GameObject(entity.EntityId);
                instance.transform.SetParent(_root, false);
                var visual = UnityEngine.Object.Instantiate(prefab, instance.transform);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            else
            {
                instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            }

            if (instance.transform.parent != _root)
                instance.transform.SetParent(_root, false);

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
            if (entity.Radius > 0f)
                instance.transform.localScale = Vector3.one * entity.Radius;

            var marker = instance.GetComponent<EveUnityPlayableWorldEntityMarker>();
            if (marker == null)
                marker = instance.AddComponent<EveUnityPlayableWorldEntityMarker>();

            marker.Apply(entity, asset);
        }

        private static void DestroyInstance(GameObject instance)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(instance);
            else
                UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    public sealed class EveUnityResourcesAssetProvider : IEveUnityGameObjectAssetProvider
    {
        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var resourcesPath = EveUnityPlayableWorldAssetManifestEntry.NormalizeResourcesPath(asset.AssetRef);
            return string.IsNullOrWhiteSpace(resourcesPath)
                ? null
                : Resources.Load<GameObject>(resourcesPath);
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
