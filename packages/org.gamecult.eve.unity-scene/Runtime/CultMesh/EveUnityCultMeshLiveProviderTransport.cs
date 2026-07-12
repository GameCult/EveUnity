using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshLiveProviderTransport :
        IEveUnitySceneLiveProviderTransport,
        IEveUnityGameObjectAssetProvider,
        IDisposable
    {
        private const string RemoteShardId = "provider";

        private static readonly Type[] WireDocumentTypes =
        {
            typeof(EveProviderAdvertisementDocument),
            typeof(EveSurfaceDocument),
            typeof(EveSurfaceCommandRequest),
            typeof(EveCommandReceiptDocument),
            typeof(EveAssetCatalogDocument),
            typeof(CultMeshCdnArtifactManifest),
            typeof(CultMeshCdnArtifactChunk)
        };

        private readonly string _replicaPath;
        private readonly string _endpoint;
        private readonly string _providerId;
        private readonly string _surfaceId;
        private readonly string _runtimeId;
        private readonly HashSet<string> _pendingCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _publishedReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityEngine.Object> _nativeAssets = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        private readonly List<AssetBundle> _assetBundles = new List<AssetBundle>();
        private readonly Dictionary<string, int> _cameraCullingMasks = new Dictionary<string, int>(StringComparer.Ordinal);
        private CultMeshNode? _node;
        private CultMeshSnapshotEndpoint? _snapshot;
        private CultMeshSnapshotSession? _assetSession;
        private CultNetDocumentRegistry? _networkRegistry;
        private CultNetShardDescriptor? _replicaShard;
        private EveProviderAdvertisementDocument? _advertisement;
        private EveAdvertisedSurface? _advertisedSurface;

        public EveUnityCultMeshLiveProviderTransport(
            string replicaPath,
            string endpoint,
            string providerId,
            string surfaceId,
            string runtimeId = "eve-unity")
        {
            _replicaPath = string.IsNullOrWhiteSpace(replicaPath)
                ? throw new ArgumentException("Replica path must be non-empty.", nameof(replicaPath))
                : Path.GetFullPath(replicaPath);
            _endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? throw new ArgumentException("CultMesh endpoint must be non-empty.", nameof(endpoint))
                : endpoint.Trim();
            _providerId = string.IsNullOrWhiteSpace(providerId)
                ? throw new ArgumentException("Provider id must be non-empty.", nameof(providerId))
                : providerId.Trim();
            _surfaceId = string.IsNullOrWhiteSpace(surfaceId)
                ? throw new ArgumentException("Surface id must be non-empty.", nameof(surfaceId))
                : surfaceId.Trim();
            _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "eve-unity" : runtimeId.Trim();
            CurrentSurfaceDocument = EmptySurfaceDocument();
            CurrentAssetManifestDocument = EmptyAssetManifest();
        }

        public string TransportKind => "eve-cultmesh-remote-replica";

        public string SurfacePointer => CurrentSurfaceDocument.SourcePointer;

        public string AssetManifestPointer => CurrentAssetManifestDocument.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

        public event Action<EveUnitySceneProviderSurfaceDocument>? SurfaceDocumentAvailable;

        public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

        public event Action<EveUnitySceneCommandReceipt>? CommandReceiptAvailable;

        public void Connect()
        {
            EnsureOpen();
            Refresh();
        }

        public void Disconnect()
        {
        }

        public void Refresh()
        {
            EnsureOpen();
            ResolveAdvertisement();
            RefreshSurface();
            RefreshAssetCatalog();
            RefreshReceipts();
        }

        public void SubmitCommand(EveSurfaceCommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            EnsureOpen();
            ResolveAdvertisement();
            var interaction = RequireWorldInteraction();
            if (string.IsNullOrWhiteSpace(interaction.CommandRecordRef))
                throw new InvalidOperationException("The provider advertisement does not publish a command record reference.");
            if (!string.Equals(request.ProviderId, _providerId, StringComparison.Ordinal) ||
                !string.Equals(request.SurfaceId, _surfaceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The command does not target the connected provider surface.");
            }

            var commandId = request.CommandId;
            if (string.IsNullOrWhiteSpace(commandId))
                throw new InvalidOperationException("Eve command invocations require an idempotency key.");

            var recordKey = ChildRecordKey(interaction.CommandRecordRef, commandId);
            var message = _networkRegistry!.CreateRawDocumentPutMessage(
                $"eve-unity-{commandId}",
                new CultRecordHandle<EveSurfaceCommandRequest>(new CultRecordKey(recordKey)),
                request,
                new CultNetDocumentMessageOptions
                {
                    SourceRuntimeId = _runtimeId,
                    SourceRole = "eve-unity"
                });
            new CultNetSchemaWriteForwarder(new CultNetSchemaWriteForwarderOptions())
                .ForwardPutAsync(_replicaShard!, message)
                .GetAwaiter()
                .GetResult();
            _pendingCommandIds.Add(commandId);
        }

        public void Dispose()
        {
            foreach (var bundle in _assetBundles) bundle.Unload(unloadAllLoadedObjects: false);
            _assetBundles.Clear();
            _prefabs.Clear();
            _nativeAssets.Clear();
            _node?.Dispose();
            _assetSession?.Dispose();
            _node = null;
            _snapshot = null;
            _assetSession = null;
            _networkRegistry = null;
            _replicaShard = null;
            _pendingCommandIds.Clear();
            _publishedReceiptIds.Clear();
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            return _prefabs.TryGetValue(asset.AssetRef, out var prefab) ? prefab : null;
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            return _nativeAssets.TryGetValue(asset.AssetRef, out var value) && assetType.IsInstanceOfType(value)
                ? value : null;
        }

        public bool TryGetCameraCullingMask(string viewId, out int cullingMask)
        {
            return _cameraCullingMasks.TryGetValue(viewId ?? "", out cullingMask);
        }

        private void EnsureOpen()
        {
            if (_node != null)
                return;

            var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(WireDocumentTypes);
            _networkRegistry = CultMesh.CreateCultNetDocumentRegistry(WireDocumentTypes, cacheRegistry);
            _replicaShard = new CultNetShardDescriptor(
                RemoteShardId,
                _providerId,
                epoch: 1,
                isPrimary: false,
                schemaIds: WireDocumentTypes.Select(type => cacheRegistry.GetRequired(type).SchemaId),
                primaryEndpoints: new[] { _endpoint });
            _node = CultMesh.CreateNodeAsync(
                    _replicaPath,
                    new CultMeshNodeOptions
                    {
                        StartServer = false,
                        EnableDurableShardLogs = true,
                        CacheOptions = new CultCacheOpenOptions
                        {
                            Registry = cacheRegistry,
                            PullOnOpen = false,
                            StoreFlushOnDispose = true,
                            UseDirectoryStore = true
                        },
                        DatabaseOptions = new CultNetDatabaseOptions
                        {
                            RuntimeId = _runtimeId,
                            Shards = new[] { _replicaShard },
                            DocumentRegistry = _networkRegistry
                        }
                    })
                .GetAwaiter()
                .GetResult();
            _snapshot = CultMesh.SnapshotEndpoint(
                _endpoint,
                new CultMeshSnapshotEndpointOptions
                {
                    Context = CultMesh.Verse("eve.remote", _runtimeId).Context,
                    DocumentRegistry = _networkRegistry,
                    Request = new CultMeshSnapshotRequestOptions
                    {
                        ShardId = RemoteShardId,
                        ShardEpoch = 1,
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                        ResponseTimeout = TimeSpan.FromSeconds(10),
                        MessageIdPrefix = "eve-unity",
                        RudpRuntimeId = _runtimeId,
                        RudpMaxFragmentBytes = 2048
                    }
                });
            _assetSession = CultMesh.SnapshotSession(
                _endpoint,
                new CultMeshSnapshotRequestOptions
                {
                    ShardId = RemoteShardId,
                    ShardEpoch = 1,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ResponseTimeout = TimeSpan.FromSeconds(10),
                    MessageIdPrefix = "eve-unity-assets",
                    RudpRuntimeId = $"{_runtimeId}.assets",
                    RudpMaxFragmentBytes = 2048
                },
                _networkRegistry);
        }

        private void ResolveAdvertisement()
        {
            var documents = _snapshot!
                .FetchDocumentsAsync<EveProviderAdvertisementDocument>()
                .GetAwaiter()
                .GetResult();
            _advertisement = documents.FirstOrDefault(document =>
                    string.Equals(document.ProviderId, _providerId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Provider '{_providerId}' did not publish an Eve advertisement.");
            _advertisedSurface = _advertisement.Surfaces.FirstOrDefault(surface =>
                    string.Equals(surface.SurfaceId, _surfaceId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Provider '{_providerId}' did not advertise surface '{_surfaceId}'.");
            if (!string.Equals(_advertisedSurface.Transport, "cultmesh-record", StringComparison.Ordinal))
                throw new InvalidOperationException("EveUnity CultMesh transport requires a cultmesh-record surface advertisement.");
            if (string.IsNullOrWhiteSpace(_advertisedSurface.RecordRef))
                throw new InvalidOperationException("The provider surface advertisement does not publish a record reference.");
        }

        private void RefreshSurface()
        {
            var surface = _snapshot!
                .FetchDocumentsAsync<EveSurfaceDocument>(
                    recordKeys: new[] { _advertisedSurface!.RecordRef },
                    schemaIds: new[] { EveSurfaceDocument.SchemaId })
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The advertised Eve surface record was not available.");
            var interaction = RequireWorldInteraction();
            CurrentSurfaceDocument = new EveUnitySceneProviderSurfaceDocument(
                surface,
                new EveUnitySceneProviderSurfaceAdvertisement(
                    _advertisedSurface.SurfaceId,
                    _advertisedSurface.SurfaceKind,
                    new EveUnitySceneWorldInteraction(
                        interaction.ProjectionKind,
                        interaction.CommandBoundary,
                        interaction.ReceiptSchema,
                        interaction.Ownership)),
                _advertisedSurface.RecordRef,
                surface.Version);
            SurfaceDocumentAvailable?.Invoke(CurrentSurfaceDocument);
            AssetManifestDocumentAvailable?.Invoke(CurrentAssetManifestDocument);
        }

        private void RefreshAssetCatalog()
        {
            var interaction = RequireWorldInteraction();
            if (string.IsNullOrWhiteSpace(interaction.AssetManifestRecordRef))
                return;

            var catalog = _snapshot!
                .FetchDocumentsAsync<EveAssetCatalogDocument>(
                    recordKeys: new[] { interaction.AssetManifestRecordRef },
                    schemaIds: new[] { EveAssetCatalogDocument.SchemaId })
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault();
            if (catalog == null || catalog.Version == CurrentAssetCatalogVersion)
                return;

            foreach (var bundle in _assetBundles) bundle.Unload(unloadAllLoadedObjects: false);
            _assetBundles.Clear();
            _prefabs.Clear();
            _nativeAssets.Clear();
            _cameraCullingMasks.Clear();
            var selected = catalog.Assets
                .Select(asset => new
                {
                    Asset = asset,
                    Variant = asset.Variants.FirstOrDefault(variant =>
                        string.Equals(variant.RuntimeId, "unity-scene", StringComparison.Ordinal) &&
                        string.Equals(variant.Platform, CurrentBundlePlatform(), StringComparison.Ordinal))
                })
                .Where(selection => selection.Variant != null)
                .ToArray();
            ReadCameraPolicies(selected.Select(selection => selection.Variant!));
            foreach (var group in selected.GroupBy(selection => selection.Variant!.Uri, StringComparer.Ordinal))
            {
                var variant = group.First().Variant!;
                var manifest = _assetSession!.FetchDocumentsAsync<CultMeshCdnArtifactManifest>(
                        recordKeys: new[] { variant.Uri },
                        schemaIds: new[] { CultMeshCdnSchemaVersions.ArtifactManifest })
                    .GetAwaiter()
                    .GetResult();
                var descriptor = manifest.FirstOrDefault();
                if (descriptor == null)
                    throw new InvalidOperationException($"Provider asset bundle manifest '{variant.Uri}' was not available.");
                var bytes = ReadCachedBundle(descriptor, variant);
                VerifyBundle(bytes, variant);
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                    throw new InvalidOperationException($"Unity could not load provider asset bundle '{variant.Uri}'.");
                _assetBundles.Add(bundle);
                foreach (var selection in group)
                {
                    var value = bundle.LoadAsset(selection.Variant!.AssetKey);
                    if (value == null) continue;
                    _nativeAssets[selection.Asset.AssetRef] = value;
                    if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                        !string.IsNullOrWhiteSpace(role))
                        _nativeAssets[role] = value;
                    if (value is GameObject prefab)
                    {
                        _prefabs[selection.Asset.AssetRef] = prefab;
                        if (!string.IsNullOrWhiteSpace(role)) _prefabs[role] = prefab;
                    }
                }
            }

            CurrentAssetCatalogVersion = catalog.Version;
        }

        private void ReadCameraPolicies(IEnumerable<EveAssetVariant> variants)
        {
            _cameraCullingMasks.Clear();
            foreach (var variant in variants)
            {
                foreach (var pair in variant.Metadata)
                {
                    const string prefix = "view.";
                    const string suffix = ".excludeUnityLayers";
                    if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                        !pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                        continue;
                    var viewId = pair.Key.Substring(prefix.Length, pair.Key.Length - prefix.Length - suffix.Length);
                    var mask = -1;
                    foreach (var token in (pair.Value ?? "").Split(','))
                    {
                        if (int.TryParse(token.Trim(), out var layer) && layer >= 0 && layer < 32)
                            mask &= ~(1 << layer);
                    }
                    _cameraCullingMasks[viewId] = mask;
                }
            }
        }

        private long CurrentAssetCatalogVersion { get; set; } = -1;

        private static string CurrentBundlePlatform()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ||
                   Application.platform == RuntimePlatform.WindowsPlayer
                ? "StandaloneWindows64"
                : Application.platform.ToString();
        }

        private byte[] ReadBundle(CultMeshCdnArtifactManifest manifest)
        {
            var bytes = new byte[checked((int)manifest.SizeBytes)];
            var references = manifest.Chunks.OrderBy(chunk => chunk.Offset).ToArray();
            for (var index = 0; index < references.Length; index += 2)
            {
                var batch = references.Skip(index).Take(2).ToArray();
                var chunks = _assetSession!
                    .FetchDocumentsAsync<CultMeshCdnArtifactChunk>(
                        recordKeys: batch.Select(reference => reference.RecordKey).ToArray(),
                        schemaIds: new[] { CultMeshCdnSchemaVersions.ArtifactChunk })
                    .GetAwaiter()
                    .GetResult();
                foreach (var reference in batch)
                {
                    var chunk = chunks.FirstOrDefault(candidate =>
                        string.Equals(candidate.ChunkHash, reference.ChunkHash, StringComparison.Ordinal));
                    if (chunk == null)
                        throw new InvalidOperationException($"Provider asset chunk '{reference.RecordKey}' was not available.");
                    if (chunk.SizeBytes != reference.SizeBytes || chunk.Payload.Length != reference.SizeBytes)
                        throw new InvalidDataException($"Provider asset chunk '{reference.RecordKey}' size did not match its manifest.");
                    Buffer.BlockCopy(chunk.Payload, 0, bytes, checked((int)reference.Offset), reference.SizeBytes);
                }
            }
            return bytes;
        }

        private byte[] ReadCachedBundle(CultMeshCdnArtifactManifest manifest, EveAssetVariant variant)
        {
            var cacheRoot = Environment.GetEnvironmentVariable("EVEUNITY_ASSET_CACHE_PATH");
            if (string.IsNullOrWhiteSpace(cacheRoot))
                cacheRoot = Path.Combine(Application.persistentDataPath, "EveUnity", "assets");
            Directory.CreateDirectory(cacheRoot);
            var hash = variant.ContentHash.StartsWith("sha256:", StringComparison.Ordinal)
                ? variant.ContentHash.Substring("sha256:".Length)
                : variant.ContentHash;
            var cachePath = Path.Combine(cacheRoot, hash + ".bundle");
            if (File.Exists(cachePath))
            {
                var cached = File.ReadAllBytes(cachePath);
                VerifyBundle(cached, variant);
                return cached;
            }

            var bytes = ReadBundle(manifest);
            VerifyBundle(bytes, variant);
            var temporaryPath = cachePath + ".tmp";
            File.WriteAllBytes(temporaryPath, bytes);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(temporaryPath, cachePath);
            return bytes;
        }

        private static void VerifyBundle(byte[] bytes, EveAssetVariant variant)
        {
            if (bytes.LongLength != variant.SizeBytes)
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' size did not match its catalog.");
            byte[] digest;
            using (var sha256 = SHA256.Create())
                digest = sha256.ComputeHash(bytes);
            var actual = "sha256:" + string.Concat(digest.Select(value => value.ToString("x2")));
            if (!string.Equals(actual, variant.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' hash did not match its catalog.");
        }

        private void RefreshReceipts()
        {
            if (_pendingCommandIds.Count == 0)
                return;

            var receipts = _snapshot!
                .FetchDocumentsAsync<EveCommandReceiptDocument>()
                .GetAwaiter()
                .GetResult();
            foreach (var receipt in receipts
                         .Where(receipt => _pendingCommandIds.Contains(receipt.CommandId))
                         .OrderBy(receipt => receipt.SourceVersion))
            {
                if (!_publishedReceiptIds.Add(receipt.ReceiptId))
                    continue;

                CommandReceiptAvailable?.Invoke(new EveUnitySceneCommandReceipt(
                    receipt.ReceiptId,
                    receipt.Command,
                    receipt.CommandId,
                    receipt.State,
                    receipt.OwnerRepo,
                    receipt.Authority,
                    receipt.Schema,
                    receipt.ProviderId,
                    receipt.SurfaceId,
                    receipt.Message,
                    DateTimeOffset.TryParse(receipt.IssuedAtUtc, out var issuedAt) ? issuedAt : null,
                    receipt.SourceVersion));
                if (string.Equals(receipt.State, "accepted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(receipt.State, "denied", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(receipt.State, "reconciled", StringComparison.OrdinalIgnoreCase))
                {
                    _pendingCommandIds.Remove(receipt.CommandId);
                }
            }
        }

        private EveWorldInteractionAdvertisement RequireWorldInteraction()
        {
            return _advertisedSurface?.WorldInteraction
                ?? throw new InvalidOperationException("The advertised surface has no interactive-world contract.");
        }

        private EveUnitySceneProviderSurfaceDocument EmptySurfaceDocument()
        {
            var surface = new EveSurfaceDocument(
                _providerId,
                "",
                "",
                0,
                "",
                new EveSurfaceTree(
                    _surfaceId,
                    new EveSurfaceComponent(
                        "root",
                        "surface",
                        new Dictionary<string, string>(),
                        Array.Empty<EveSurfaceComponent>()),
                    Array.Empty<EveStyleToken>()),
                Array.Empty<EveCommandTemplate>());
            return new EveUnitySceneProviderSurfaceDocument(
                surface,
                new EveUnitySceneProviderSurfaceAdvertisement(
                    _surfaceId,
                    "interactive-world",
                    new EveUnitySceneWorldInteraction("", "", EveCommandReceiptDocument.SchemaId, "")),
                "",
                0);
        }

        private EveUnityPlayableWorldAssetManifestDocument EmptyAssetManifest()
        {
            return new EveUnityPlayableWorldAssetManifestDocument(
                "",
                Array.Empty<EveUnityPlayableWorldAssetManifestDocumentEntry>(),
                _providerId);
        }

        private static string ChildRecordKey(string parent, string child)
        {
            return $"{parent.TrimEnd(':', '/')}:{child}";
        }
    }
}
