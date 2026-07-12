using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldAssetManifest
    {
        private readonly Dictionary<string, EveUnityPlayableWorldAssetManifestEntry> _byAssetRef =
            new Dictionary<string, EveUnityPlayableWorldAssetManifestEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, EveUnityPlayableWorldAssetManifestEntry> _byEntityKind =
            new Dictionary<string, EveUnityPlayableWorldAssetManifestEntry>(StringComparer.Ordinal);

        public EveUnityPlayableWorldAssetManifest(
            string manifestRef,
            IReadOnlyList<EveUnityPlayableWorldAssetManifestEntry> entries)
        {
            ManifestRef = manifestRef ?? "";
            Entries = entries ?? Array.Empty<EveUnityPlayableWorldAssetManifestEntry>();

            foreach (var entry in Entries)
            {
                if (entry == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(entry.AssetRef))
                    _byAssetRef[entry.AssetRef] = entry;
                if (!string.IsNullOrWhiteSpace(entry.EntityKind))
                    _byEntityKind[entry.EntityKind] = entry;
            }
        }

        public string ManifestRef { get; }

        public IReadOnlyList<EveUnityPlayableWorldAssetManifestEntry> Entries { get; }

        public static EveUnityPlayableWorldAssetManifest FromDocument(EveUnityPlayableWorldAssetManifestDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var entries = new List<EveUnityPlayableWorldAssetManifestEntry>();
            foreach (var entry in document.Entries)
            {
                if (entry == null)
                    continue;

                entries.Add(new EveUnityPlayableWorldAssetManifestEntry(
                    entry.AssetRef,
                    entry.EntityKind,
                    entry.ResourcesPath,
                    entry.PrefabKey,
                    entry.PresentationKind));
            }

            return new EveUnityPlayableWorldAssetManifest(document.ManifestRef, entries);
        }

        public EveUnityPlayableWorldAssetManifestEntry? Find(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            EveUnityPlayableWorldAssetManifestEntry entry;
            if (!string.IsNullOrWhiteSpace(asset.AssetRef) && _byAssetRef.TryGetValue(asset.AssetRef, out entry))
                return entry;
            if (!string.IsNullOrWhiteSpace(asset.EntityKind) && _byEntityKind.TryGetValue(asset.EntityKind, out entry))
                return entry;
            return null;
        }

        public EveUnityPlayableWorldAssetManifestEntry? FindByPresentationRole(string presentationRole)
        {
            if (string.IsNullOrWhiteSpace(presentationRole)) return null;
            return _byEntityKind.TryGetValue(presentationRole, out var entry) ? entry : null;
        }
    }

    public interface IEveUnityPlayableWorldAssetManifestSource
    {
        string ManifestRef { get; }

        EveUnityPlayableWorldAssetManifest CurrentManifest { get; }

        event Action<EveUnityPlayableWorldAssetManifest> ManifestAvailable;
    }

    public sealed class EveUnityPlayableWorldAssetManifestCache
    {
        private readonly Dictionary<string, EveUnityPlayableWorldAssetManifest> _manifests =
            new Dictionary<string, EveUnityPlayableWorldAssetManifest>(StringComparer.Ordinal);
        private readonly Dictionary<IEveUnityPlayableWorldAssetManifestSource, Action<EveUnityPlayableWorldAssetManifest>> _subscriptions =
            new Dictionary<IEveUnityPlayableWorldAssetManifestSource, Action<EveUnityPlayableWorldAssetManifest>>();

        public int Count => _manifests.Count;

        public void Add(EveUnityPlayableWorldAssetManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrWhiteSpace(manifest.ManifestRef))
                throw new ArgumentException("Asset manifest ref is required.", nameof(manifest));

            _manifests[manifest.ManifestRef] = manifest;
        }

        public void Connect(IEveUnityPlayableWorldAssetManifestSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (_subscriptions.ContainsKey(source))
                return;

            Add(source.CurrentManifest);
            Action<EveUnityPlayableWorldAssetManifest> handler = Add;
            source.ManifestAvailable += handler;
            _subscriptions[source] = handler;
        }

        public void Disconnect(IEveUnityPlayableWorldAssetManifestSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Action<EveUnityPlayableWorldAssetManifest> handler;
            if (!_subscriptions.TryGetValue(source, out handler))
                return;

            source.ManifestAvailable -= handler;
            _subscriptions.Remove(source);
        }

        public bool TryGet(string manifestRef, out EveUnityPlayableWorldAssetManifest? manifest)
        {
            if (string.IsNullOrWhiteSpace(manifestRef))
            {
                manifest = null;
                return false;
            }

            return _manifests.TryGetValue(manifestRef, out manifest);
        }

        public EveUnityPlayableWorldAssetManifest? GetForWorld(EveUnityPlayableWorldProjection world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            EveUnityPlayableWorldAssetManifest? manifest;
            return TryGet(world.AssetManifest, out manifest) ? manifest : null;
        }

        public IEveUnityGameObjectAssetProvider CreateGameObjectAssetProvider(
            EveUnityPlayableWorldProjection world,
            IEveUnityGameObjectAssetProvider fallback)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (fallback == null) throw new ArgumentNullException(nameof(fallback));

            var manifest = GetForWorld(world);
            return manifest == null
                ? fallback
                : new EveUnityManifestGameObjectAssetProvider(manifest, fallback);
        }
    }

    public interface IEveUnityPlayableWorldAssetManifestDocumentSource
    {
        string ManifestRef { get; }

        EveUnityPlayableWorldAssetManifestDocument CurrentDocument { get; }

        event Action<EveUnityPlayableWorldAssetManifestDocument> DocumentAvailable;
    }

    public sealed class EveUnityPlayableWorldAssetManifestDocumentSource : IEveUnityPlayableWorldAssetManifestSource, IDisposable
    {
        private readonly IEveUnityPlayableWorldAssetManifestDocumentSource _documentSource;
        private bool _connected;

        public EveUnityPlayableWorldAssetManifestDocumentSource(
            IEveUnityPlayableWorldAssetManifestDocumentSource documentSource)
        {
            _documentSource = documentSource ?? throw new ArgumentNullException(nameof(documentSource));
            CurrentManifest = EveUnityPlayableWorldAssetManifest.FromDocument(_documentSource.CurrentDocument);
        }

        public string ManifestRef => CurrentManifest.ManifestRef;

        public EveUnityPlayableWorldAssetManifest CurrentManifest { get; private set; }

        public event Action<EveUnityPlayableWorldAssetManifest>? ManifestAvailable;

        public void Connect()
        {
            if (_connected)
                return;

            _documentSource.DocumentAvailable += OnDocumentAvailable;
            _connected = true;
        }

        public void Disconnect()
        {
            if (!_connected)
                return;

            _documentSource.DocumentAvailable -= OnDocumentAvailable;
            _connected = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void OnDocumentAvailable(EveUnityPlayableWorldAssetManifestDocument document)
        {
            CurrentManifest = EveUnityPlayableWorldAssetManifest.FromDocument(document);
            ManifestAvailable?.Invoke(CurrentManifest);
        }
    }

    public sealed class EveUnityPlayableWorldAssetManifestDocument
    {
        public const string SchemaId = "gamecult.eve.unity_playable_world_asset_manifest.v1";

        public EveUnityPlayableWorldAssetManifestDocument(
            string manifestRef,
            IReadOnlyList<EveUnityPlayableWorldAssetManifestDocumentEntry> entries,
            string providerId = "",
            string schema = SchemaId)
        {
            ManifestRef = manifestRef ?? "";
            Entries = entries ?? Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>();
            ProviderId = providerId ?? "";
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
        }

        public string Schema { get; }

        public string ManifestRef { get; }

        public string ProviderId { get; }

        public IReadOnlyList<EveUnityPlayableWorldAssetManifestDocumentEntry> Entries { get; }
    }

    public sealed class EveUnityPlayableWorldAssetManifestDocumentEntry
    {
        public EveUnityPlayableWorldAssetManifestDocumentEntry(
            string assetRef,
            string entityKind,
            string resourcesPath,
            string prefabKey,
            string presentationKind = "provider-asset-ref")
        {
            AssetRef = assetRef ?? "";
            EntityKind = entityKind ?? "";
            ResourcesPath = resourcesPath ?? "";
            PrefabKey = prefabKey ?? "";
            PresentationKind = presentationKind ?? "";
        }

        public string AssetRef { get; }

        public string EntityKind { get; }

        public string ResourcesPath { get; }

        public string PrefabKey { get; }

        public string PresentationKind { get; }
    }

    public sealed class EveUnityPlayableWorldAssetManifestEntry
    {
        public EveUnityPlayableWorldAssetManifestEntry(
            string assetRef,
            string entityKind,
            string resourcesPath,
            string prefabKey,
            string presentationKind = "provider-asset-ref")
        {
            AssetRef = assetRef ?? "";
            EntityKind = entityKind ?? "";
            ResourcesPath = NormalizeResourcesPath(resourcesPath);
            PrefabKey = prefabKey ?? "";
            PresentationKind = presentationKind ?? "";
        }

        public string AssetRef { get; }

        public string EntityKind { get; }

        public string ResourcesPath { get; }

        public string PrefabKey { get; }

        public string PresentationKind { get; }

        public static string NormalizeResourcesPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            var normalized = path.Trim();
            foreach (var prefix in new[] { "resources://", "resource://", "Resources/" })
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(prefix.Length);
                    break;
                }
            }

            if (normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - ".prefab".Length);

            return normalized;
        }
    }

    public sealed class EveUnityManifestGameObjectAssetProvider : IEveUnityNativeAssetProvider
    {
        private readonly EveUnityPlayableWorldAssetManifest _manifest;
        private readonly IEveUnityGameObjectAssetProvider? _fallback;

        public EveUnityManifestGameObjectAssetProvider(
            EveUnityPlayableWorldAssetManifest manifest,
            IEveUnityGameObjectAssetProvider? fallback = null)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _fallback = fallback;
        }

        public string ManifestRef => _manifest.ManifestRef;

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            var entry = _manifest.Find(asset);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.ResourcesPath))
            {
                var prefab = Resources.Load<GameObject>(entry.ResourcesPath);
                if (prefab != null)
                    return prefab;
            }

            return _fallback?.ResolvePrefab(asset);
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            var entry = _manifest.Find(asset) ?? _manifest.FindByPresentationRole(asset.AssetRef);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.ResourcesPath))
            {
                var value = Resources.Load(entry.ResourcesPath, assetType);
                if (value != null) return value;
            }
            return (_fallback as IEveUnityNativeAssetProvider)?.ResolveAsset(asset, assetType);
        }
    }
}
