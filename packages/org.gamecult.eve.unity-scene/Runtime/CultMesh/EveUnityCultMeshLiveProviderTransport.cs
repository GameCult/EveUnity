using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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
            typeof(EveEntitySoaViewDocument),
            typeof(EveInputCapabilityDocument),
            typeof(CultMeshCdnArtifactManifest),
            typeof(CultMeshCdnArtifactChunk)
        };

        private readonly EveUnityCultMeshProviderSelection _selection;
        private readonly CultMeshClient _mesh;
        private readonly string _endpointId;
        private readonly string _providerId;
        private readonly string _surfaceId;
        private readonly string _runtimeId;
        private CultMeshSession? _documentSession;
        private readonly HashSet<string> _pendingCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _publishedReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityEngine.Object> _nativeAssets = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        private readonly List<AssetBundle> _assetBundles = new List<AssetBundle>();
        private readonly Dictionary<string, int> _cameraCullingMasks = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<object> _liveDocuments = new ConcurrentQueue<object>();
        private CultNetDocumentRegistry? _networkRegistry;
        private readonly List<IDisposable> _liveWatches = new List<IDisposable>();
        private bool _liveSubscriptionsOpen;
        private EveProviderAdvertisementDocument? _advertisement;
        private EveAdvertisedSurface? _advertisedSurface;
        private string _assetCatalogPointer = "";

        public EveUnityCultMeshLiveProviderTransport(
            string replicaPath,
            EveUnityCultMeshProviderSelection selection,
            string runtimeId = "eve-unity")
        {
            _ = replicaPath;
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _mesh = selection.Mesh;
            _endpointId = selection.EndpointId;
            _providerId = selection.ProviderId;
            _surfaceId = selection.SurfaceId;
            _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "eve-unity" : runtimeId.Trim();
            CurrentSurfaceDocument = EmptySurfaceDocument();
            CurrentAssetManifestDocument = EmptyAssetManifest();
        }

        public string TransportKind => "eve-cultmesh-remote-replica";

        public string SurfacePointer => CurrentSurfaceDocument.SourcePointer;

        public string AssetManifestPointer => CurrentAssetManifestDocument.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

        public EveInputCapabilityDocument? CurrentInputCapability { get; private set; }

        public event Action<EveUnitySceneProviderSurfaceDocument>? SurfaceDocumentAvailable;

        public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

        public event Action<EveUnitySceneCommandReceipt>? CommandReceiptAvailable;

        public event Action<EveEntitySoaViewDocument>? EntityViewAvailable;

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
        }

        public void PumpLiveEvents()
        {
            while (_liveDocuments.TryDequeue(out var document))
            {
                if (document is EveEntitySoaViewDocument entityView)
                    EntityViewAvailable?.Invoke(entityView);
                else if (document is EveSurfaceDocument surface)
                    PublishSurface(surface);
                else if (document is EveCommandReceiptDocument receipt)
                    PublishReceipt(receipt);
                else if (document is EveInputCapabilityDocument inputCapability)
                    CurrentInputCapability = inputCapability;
                else if (document is EveAssetCatalogDocument assetCatalog)
                    ApplyAssetCatalog(assetCatalog);
            }
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
            message.ShardId = RemoteShardId;
            message.ShardEpoch = 1;
            _documentSession!.SendCultNet(message);
            _pendingCommandIds.Add(commandId);
        }

        public void Dispose()
        {
            foreach (var bundle in _assetBundles) bundle.Unload(unloadAllLoadedObjects: false);
            _assetBundles.Clear();
            _prefabs.Clear();
            _nativeAssets.Clear();
            foreach (var watch in _liveWatches) watch.Dispose();
            _liveWatches.Clear();
            _mesh.Dispose();
            _documentSession = null;
            _networkRegistry = null;
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
            if (_documentSession != null)
                return;

            var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(WireDocumentTypes);
            _networkRegistry = CultMesh.CreateCultNetDocumentRegistry(WireDocumentTypes, cacheRegistry);
            _documentSession = _mesh.ConnectAsync(_endpointId, CultMeshProtocols.Documents)
                .GetAwaiter().GetResult();
        }

        private void ResolveAdvertisement()
        {
            _advertisement = _selection.Surface.Provider;
            _advertisedSurface = _selection.Surface.Advertisement;
        }

        private void RefreshSurface()
        {
            var surface = _selection.Surface.Surface.LatestAsync().GetAwaiter().GetResult();
            PublishSurface(surface);
        }

        private void PublishSurface(EveSurfaceDocument surface)
        {
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
            EnsureLiveSubscriptions();
        }

        private void EnsureLiveSubscriptions()
        {
            if (_liveSubscriptionsOpen) return;
            _liveSubscriptionsOpen = true;
            _liveWatches.Add(_selection.Surface.Surface.Watch(surface => _liveDocuments.Enqueue(surface)));
            var lowered = new EveUnitySceneSurfaceLowerer()
                .Lower(CurrentSurfaceDocument.SurfaceDocument, CurrentSurfaceDocument.AdvertisedSurface);
            string entityViewPointer = lowered.PlayableWorld == null
                ? ""
                : lowered.PlayableWorld.EntityViewPointerId;
            if (!string.IsNullOrWhiteSpace(entityViewPointer))
            {
                var entities = _mesh.DocumentAsync<EveEntitySoaViewDocument>(_endpointId, entityViewPointer)
                    .GetAwaiter().GetResult();
                _liveDocuments.Enqueue(entities.LatestAsync().GetAwaiter().GetResult());
                _liveWatches.Add(entities.Watch(view => _liveDocuments.Enqueue(view)));
            }
            var inputCapabilityPointer = lowered.PlayableWorld?.InputCapabilityPointerId ?? "";
            if (!string.IsNullOrWhiteSpace(inputCapabilityPointer))
            {
                var inputs = _mesh.DocumentAsync<EveInputCapabilityDocument>(_endpointId, inputCapabilityPointer)
                    .GetAwaiter().GetResult();
                CurrentInputCapability = inputs.LatestAsync().GetAwaiter().GetResult();
                _liveWatches.Add(inputs.Watch(document => _liveDocuments.Enqueue(document)));
            }
            var receipts = _mesh.CollectionAsync<EveCommandReceiptDocument>(_endpointId).GetAwaiter().GetResult();
            _liveWatches.Add(receipts.WatchChanges(change =>
            {
                if (change.Document != null) _liveDocuments.Enqueue(change.Document);
            }));
        }

        private void RefreshAssetCatalog()
        {
            var interaction = RequireWorldInteraction();
            if (string.IsNullOrWhiteSpace(interaction.AssetManifestRecordRef))
                return;

            if (string.Equals(_assetCatalogPointer, interaction.AssetManifestRecordRef, StringComparison.Ordinal))
                return;

            _assetCatalogPointer = interaction.AssetManifestRecordRef;
            var document = _mesh.DocumentAsync<EveAssetCatalogDocument>(_endpointId, _assetCatalogPointer)
                .GetAwaiter().GetResult();
            ApplyAssetCatalog(document.LatestAsync().GetAwaiter().GetResult());
            _liveWatches.Add(document.Watch(catalog => _liveDocuments.Enqueue(catalog)));
        }

        private void ApplyAssetCatalog(EveAssetCatalogDocument catalog)
        {
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
                var descriptor = _mesh.ReadAsync<CultMeshCdnArtifactManifest>(_endpointId, variant.Uri)
                    .GetAwaiter().GetResult();
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
            var batches = references.Select((reference, index) => new { reference, index })
                .GroupBy(value => value.index / 2)
                .Select(group => group.Select(value => value.reference).ToArray())
                .ToArray();
            using var concurrency = new SemaphoreSlim(2, 2);
            var downloads = batches.Select(async batch =>
            {
                await concurrency.WaitAsync().ConfigureAwait(false);
                try
                {
                    var chunks = await _mesh.ReadManyAsync<CultMeshCdnArtifactChunk>(
                            _endpointId,
                            batch.Select(reference => reference.RecordKey).ToArray())
                        .ConfigureAwait(false);
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
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();
            Task.WhenAll(downloads).GetAwaiter().GetResult();
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

        private void PublishReceipt(EveCommandReceiptDocument receipt)
        {
            if (!_pendingCommandIds.Contains(receipt.CommandId) || !_publishedReceiptIds.Add(receipt.ReceiptId))
                return;
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
                _pendingCommandIds.Remove(receipt.CommandId);
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
