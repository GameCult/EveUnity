using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.PluginFields;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityScene.Fields;
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
            typeof(EveInputCapabilityDocument),
            typeof(EveSurfaceCommandRequest),
            typeof(EveCommandReceiptDocument),
            typeof(EveAssetCatalogDocument),
            typeof(EveEntitySoaViewDocument),
            typeof(EveFieldsSplatsDocument),
            typeof(CultMeshBodyPublicationDocument),
            typeof(CultMeshNetworkBodyDocument),
            typeof(CultMeshCdnArtifactManifest),
            typeof(CultMeshContentTransferStateDocument)
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
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _nativeAssetMetadata =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        private readonly List<AssetBundle> _assetBundles = new List<AssetBundle>();
        private readonly List<ICultMeshBodyReadLease> _assetBodyLeases = new List<ICultMeshBodyReadLease>();
        private readonly Dictionary<string, int> _renderChannelLayers = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<object> _liveDocuments = new ConcurrentQueue<object>();
        private CultMeshNode? _node;
        private CultMeshSnapshotSession? _snapshot;
        private CultNetDocumentRegistry? _networkRegistry;
        private CultNetShardDescriptor? _replicaShard;
        private CultNetDatabaseSubscriptionClient? _subscriptions;
        private CultNetDatabaseSubscriptionClient? _entitySubscriptions;
        private CultMeshClient? _meshClient;
        private CultMeshContentTransferService? _contentTransfer;
        private CultMeshVerifiedBodyMappingBroker? _assetBodyMappings;
        private EveProviderAdvertisementDocument? _advertisement;
        private EveAdvertisedSurface? _advertisedSurface;
        private CultMeshBodyPublicationResolver? _bodyResolver;
        private DateTime _nextReceiptPollUtc;
        private DateTime _nextEntityViewPollUtc;
        private DateTime _nextFieldsPollUtc;
        private long _lastQueuedEntityViewEpoch = -1;
        private long _lastQueuedEntityViewSequence = -1;
        private readonly Dictionary<string, long> _lastFieldsFrameByRecord = new Dictionary<string, long>(StringComparer.Ordinal);
        private Task<LivePollResult>? _livePoll;
        private bool _disposed;

        public EveUnityCultMeshLiveProviderTransport(
            string replicaPath,
            string endpoint,
            string providerId,
            string surfaceId,
            string runtimeId = "eve-unity",
            CultMeshBodyPublicationResolver? bodyResolver = null)
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
            _bodyResolver = bodyResolver;
            CurrentSurfaceDocument = EmptySurfaceDocument();
            CurrentAssetManifestDocument = EmptyAssetManifest();
            CurrentInputCapability = new EveInputCapabilityDocument();
        }

        public string TransportKind => "eve-cultmesh-remote-replica";

        public string SurfacePointer => CurrentSurfaceDocument.SourcePointer;

        public string AssetManifestPointer => CurrentAssetManifestDocument.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

        public EveInputCapabilityDocument CurrentInputCapability { get; private set; }

        public CultMeshBodyTransportKind? CurrentAssetBodyTransportKind { get; private set; }

        public event Action<EveUnitySceneProviderSurfaceDocument>? SurfaceDocumentAvailable;

        public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

        public event Action<EveUnitySceneCommandReceipt>? CommandReceiptAvailable;

        public event Action<EveEntitySoaViewDocument, ICultMeshBodyReadLease>? EntityViewAvailable;

        public event Action<EveFieldsSplatsDocument>? FieldsSplatsAvailable;

        private static CultMeshBodyPublicationHandle BodyPublicationHandle(EveEntitySoaViewDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.Buffers == null || document.Buffers.Length == 0)
                throw new InvalidOperationException("Entity layout does not name a primary logical buffer.");
            return new CultMeshBodyPublicationHandle(
                document.Buffers[0].BufferId,
                document.ProducerEpoch,
                document.Sequence);
        }

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
            ResolveAdvertisement(forceRefresh: true);
            RefreshSurface();
            RefreshInputCapability();
            RefreshAssetCatalog();
        }

        private void RefreshInputCapability()
        {
            var recordRef = FindComponentProp(
                CurrentSurfaceDocument.SurfaceDocument.Surface.Root,
                "inputCapability");
            if (string.IsNullOrWhiteSpace(recordRef))
                throw new InvalidOperationException("The advertised playable world does not publish an input capability record.");
            CurrentInputCapability = _snapshot!
                .FetchDocumentsAsync<EveInputCapabilityDocument>(
                    recordKeys: new[] { recordRef },
                    schemaIds: new[] { EveInputCapabilityDocument.SchemaId })
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"The advertised input capability '{recordRef}' was not available.");
        }

        private static string FindComponentProp(EveSurfaceComponent component, string key)
        {
            var value = component.GetProp(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            foreach (var child in component.Children)
            {
                value = FindComponentProp(child, key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }

        public void PumpLiveEvents()
        {
            CompleteLivePoll();
            while (_liveDocuments.TryDequeue(out var document))
            {
                if (document is EveEntitySoaViewDocument entityView)
                    PublishEntityView(entityView);
                else if (document is EveFieldsSplatsDocument fields)
                    FieldsSplatsAvailable?.Invoke(fields);
                else if (document is EveSurfaceDocument surface)
                    PublishSurface(surface);
                else if (document is EveCommandReceiptDocument receipt)
                    PublishReceipt(receipt);
            }
            ScheduleLivePoll();
        }

        private void CompleteLivePoll()
        {
            if (_livePoll == null || !_livePoll.IsCompleted)
                return;
            var completed = _livePoll;
            _livePoll = null;
            var result = completed.GetAwaiter().GetResult();
            if (result.Error != null)
                throw new InvalidOperationException("EveUnity live provider polling failed.", result.Error);
            foreach (var entityView in result.EntityViews)
                QueueEntityView(entityView);
            foreach (var fields in result.Fields)
            {
                if (_lastFieldsFrameByRecord.TryGetValue(fields.RecordRef, out var frame) && fields.Document.FrameId <= frame)
                    continue;
                _lastFieldsFrameByRecord[fields.RecordRef] = fields.Document.FrameId;
                _liveDocuments.Enqueue(fields.Document);
            }
            foreach (var receipt in result.Receipts)
                _liveDocuments.Enqueue(receipt);
        }

        private void ScheduleLivePoll()
        {
            if (_disposed || _snapshot == null || _livePoll != null)
                return;
            var now = DateTime.UtcNow;
            var pollEntityView = now >= _nextEntityViewPollUtc;
            var pollFields = now >= _nextFieldsPollUtc;
            var pollReceipts = _pendingCommandIds.Count > 0 && now >= _nextReceiptPollUtc;
            if (!pollEntityView && !pollFields && !pollReceipts)
                return;
            if (pollEntityView) _nextEntityViewPollUtc = now.AddMilliseconds(250);
            if (pollFields) _nextFieldsPollUtc = now.AddMilliseconds(250);
            if (pollReceipts) _nextReceiptPollUtc = now.AddMilliseconds(250);

            var world = (pollEntityView || pollFields)
                ? new EveUnitySceneSurfaceLowerer()
                    .Lower(CurrentSurfaceDocument.SurfaceDocument, CurrentSurfaceDocument.AdvertisedSurface)
                    .PlayableWorld
                : null;
            var entityViewRef = pollEntityView ? world?.EntityViewPointerId ?? "" : "";
            var fieldRefs = pollFields && world != null
                ? world.FieldVolumes
                    .Select(field => field.DocumentRef)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
            var receiptKeys = Array.Empty<string>();
            if (pollReceipts)
            {
                var interaction = RequireWorldInteraction();
                if (!string.IsNullOrWhiteSpace(interaction.ReceiptRecordRef))
                    receiptKeys = _pendingCommandIds
                        .Select(commandId => ChildRecordKey(interaction.ReceiptRecordRef, commandId))
                        .ToArray();
            }
            if (string.IsNullOrWhiteSpace(entityViewRef) && fieldRefs.Length == 0 && receiptKeys.Length == 0)
                return;
            _livePoll = PollLiveAsync(entityViewRef, fieldRefs, receiptKeys);
        }

        private async Task<LivePollResult> PollLiveAsync(
            string entityViewRef,
            string[] fieldRefs,
            string[] receiptKeys)
        {
            try
            {
                var entities = string.IsNullOrWhiteSpace(entityViewRef)
                    ? Array.Empty<EveEntitySoaViewDocument>()
                    : (await _snapshot!.FetchDocumentsAsync<EveEntitySoaViewDocument>(
                            recordKeys: new[] { entityViewRef },
                            schemaIds: new[] { EveEntitySoaViewDocument.SchemaId })
                        .ConfigureAwait(false)).ToArray();
                var fields = new List<PolledFields>();
                foreach (var recordRef in fieldRefs)
                {
                    var current = (await _snapshot!.FetchDocumentsAsync<EveFieldsSplatsDocument>(
                            recordKeys: new[] { recordRef },
                            schemaIds: new[] { EveFieldsSchemas.Splats })
                        .ConfigureAwait(false)).FirstOrDefault();
                    if (current != null)
                        fields.Add(new PolledFields(recordRef, current));
                }
                var receipts = receiptKeys.Length == 0
                    ? Array.Empty<EveCommandReceiptDocument>()
                    : (await _snapshot!.FetchDocumentsAsync<EveCommandReceiptDocument>(
                            recordKeys: receiptKeys,
                            schemaIds: new[] { EveCommandReceiptDocument.SchemaId })
                        .ConfigureAwait(false)).ToArray();
                return new LivePollResult(entities, fields.ToArray(), receipts, null);
            }
            catch (Exception error)
            {
                return new LivePollResult(
                    Array.Empty<EveEntitySoaViewDocument>(),
                    Array.Empty<PolledFields>(),
                    Array.Empty<EveCommandReceiptDocument>(),
                    error);
            }
        }

        private void QueueEntityView(EveEntitySoaViewDocument document)
        {
            if (document.ProducerEpoch < _lastQueuedEntityViewEpoch ||
                (document.ProducerEpoch == _lastQueuedEntityViewEpoch &&
                 document.Sequence <= _lastQueuedEntityViewSequence))
                return;
            _lastQueuedEntityViewEpoch = document.ProducerEpoch;
            _lastQueuedEntityViewSequence = document.Sequence;
            _liveDocuments.Enqueue(document);
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
            _disposed = true;
            foreach (var bundle in _assetBundles) bundle.Unload(unloadAllLoadedObjects: false);
            _assetBundles.Clear();
            foreach (var lease in _assetBodyLeases) lease.Dispose();
            _assetBodyLeases.Clear();
            _prefabs.Clear();
            _nativeAssets.Clear();
            _nativeAssetMetadata.Clear();
            _subscriptions?.Dispose();
            _entitySubscriptions?.Dispose();
            _node?.Dispose();
            _snapshot?.Dispose();
            _meshClient?.Dispose();
            _node = null;
            _snapshot = null;
            _subscriptions = null;
            _entitySubscriptions = null;
            _meshClient = null;
            _contentTransfer = null;
            _assetBodyMappings = null;
            _networkRegistry = null;
            _replicaShard = null;
            _pendingCommandIds.Clear();
            _publishedReceiptIds.Clear();
            _lastFieldsFrameByRecord.Clear();
        }

        private sealed class LivePollResult
        {
            public LivePollResult(
                EveEntitySoaViewDocument[] entityViews,
                PolledFields[] fields,
                EveCommandReceiptDocument[] receipts,
                Exception? error)
            {
                EntityViews = entityViews;
                Fields = fields;
                Receipts = receipts;
                Error = error;
            }

            public EveEntitySoaViewDocument[] EntityViews { get; }
            public PolledFields[] Fields { get; }
            public EveCommandReceiptDocument[] Receipts { get; }
            public Exception? Error { get; }
        }

        private sealed class PolledFields
        {
            public PolledFields(string recordRef, EveFieldsSplatsDocument document)
            {
                RecordRef = recordRef;
                Document = document;
            }

            public string RecordRef { get; }
            public EveFieldsSplatsDocument Document { get; }
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

        public bool TryResolveAssetMetadata(
            EveUnityPlayableWorldAssetBinding asset,
            out IReadOnlyDictionary<string, string> metadata)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            return _nativeAssetMetadata.TryGetValue(asset.AssetRef, out metadata!);
        }

        public bool TryGetRenderChannelLayer(string channel, out int layer)
        {
            return _renderChannelLayers.TryGetValue(channel ?? "", out layer);
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
            _snapshot = CultMesh.SnapshotSession(
                _endpoint,
                new CultMeshSnapshotRequestOptions
                {
                    ShardId = RemoteShardId,
                    ShardEpoch = 1,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ResponseTimeout = TimeSpan.FromSeconds(30),
                    MessageIdPrefix = "eve-unity",
                    RudpRuntimeId = _runtimeId,
                    RudpMaxFragmentBytes = 1024
                },
                _networkRegistry);
            _meshClient = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = new[] { _endpoint }
            });
        }

        private void ResolveAdvertisement(bool forceRefresh = false)
        {
            if (!forceRefresh && _advertisement != null && _advertisedSurface != null)
                return;
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

        private CultMeshBodyPublicationResolver CreateBodyResolver()
        {
            var producerIds = RequireAdvertisedBodyProducerIds(_advertisement);
            var mappedRoot = Path.GetDirectoryName(_replicaPath) ?? ".";
            return new CultMeshBodyPublicationResolver(new CultMeshBodyTransportService(
                new ICultMeshBodyTransportAdapter[]
                {
                    new CultMeshSharedMemoryBodyAdapter(),
                    new CultMeshMappedBodyAdapter(mappedRoot),
                    new CultMeshNetworkBodyAdapter(ReadNetworkBody)
                },
                (candidateProducerId, _) => IsAdvertisedBodyProducer(producerIds, candidateProducerId)));
        }

        private static bool IsAdvertisedBodyProducer(
            IReadOnlyList<string> advertisedProducerIds,
            string candidateProducerId) =>
            advertisedProducerIds.Any(producerId =>
                string.Equals(candidateProducerId, producerId, StringComparison.Ordinal));

        private static IReadOnlyList<string> RequireAdvertisedBodyProducerIds(EveProviderAdvertisementDocument? advertisement)
        {
            if (advertisement == null)
                throw new InvalidOperationException("The Eve provider advertisement must be resolved before body transport authorization.");
            var producerIds = advertisement.AuthorizedBodyProducerIds
                .Where(producerId => !string.IsNullOrWhiteSpace(producerId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (producerIds.Length == 0)
                throw new InvalidOperationException(
                    $"Eve provider '{advertisement.ProviderId}' does not advertise an authorized body producer.");
            return producerIds;
        }

        private byte[] ReadNetworkBody(CultMeshBodyDescriptor descriptor)
        {
            var binding = _snapshot!
                .FetchDocumentsAsync<CultMeshNetworkBodyDocument>(
                    recordKeys: new[] { CultMeshNetworkBodyDocument.CreateRecordKey(descriptor.CapabilityToken).Value },
                    schemaIds: new[] { CultMeshBodyPublicationSchemaVersions.NetworkBody })
                .GetAwaiter().GetResult().SingleOrDefault()
                ?? throw new FileNotFoundException("Provider network body capability is missing.", descriptor.CapabilityToken);
            var manifest = _snapshot
                .FetchDocumentsAsync<CultMeshCdnArtifactManifest>(
                    recordKeys: new[] { binding.ManifestRecordKey },
                    schemaIds: new[] { CultMeshCdnSchemaVersions.ArtifactManifest })
                .GetAwaiter().GetResult().SingleOrDefault()
                ?? throw new FileNotFoundException("Provider network body manifest is missing.", binding.ManifestRecordKey);
            ValidateNetworkBody(descriptor, binding, manifest);
            var path = ContentTransfer().FetchAsync(manifest).GetAwaiter().GetResult();
            return File.ReadAllBytes(path);
        }

        private static void ValidateNetworkBody(
            CultMeshBodyDescriptor descriptor,
            CultMeshNetworkBodyDocument binding,
            CultMeshCdnArtifactManifest manifest)
        {
            if (!string.Equals(binding.CapabilityToken, descriptor.CapabilityToken, StringComparison.Ordinal) ||
                !string.Equals(binding.BodyId, descriptor.BodyId, StringComparison.Ordinal) ||
                !string.Equals(binding.SchemaId, descriptor.SchemaId, StringComparison.Ordinal) ||
                binding.LayoutVersion != descriptor.LayoutVersion || binding.ByteSize != descriptor.ByteSize ||
                binding.Capacity != descriptor.Capacity || binding.ProducerEpoch != descriptor.ProducerEpoch ||
                binding.Sequence != descriptor.Sequence || binding.Synchronization != descriptor.Synchronization ||
                binding.LeaseExpiresAtUnixMs != descriptor.LeaseExpiresAtUnixMs ||
                !string.Equals(binding.SemanticHash, descriptor.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body descriptor disagrees with its capability binding.");
            if (!string.Equals(CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value,
                    binding.ManifestRecordKey, StringComparison.Ordinal) ||
                manifest.SizeBytes != binding.ByteSize ||
                !string.Equals(NormalizeContentHash(manifest.ContentHash),
                    binding.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("CultMesh network body manifest disagrees with its capability binding.");
        }

        private static string NormalizeContentHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("CultMesh content hash is missing.");
            var normalized = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? value.Substring("sha256:".Length)
                : value;
            return normalized.ToLowerInvariant();
        }

        private void PublishEntityView(EveEntitySoaViewDocument document)
        {
            ResolveAdvertisement();
            if (!string.Equals(document.ProviderId, _advertisement!.ProviderId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException(
                    $"Entity layout provider '{document.ProviderId}' does not match advertised Eve provider '{_advertisement.ProviderId}'.");
            if (document.Buffers == null || document.Buffers.Length == 0)
                throw new InvalidOperationException("Entity layout does not name a primary logical buffer.");
            var handle = BodyPublicationHandle(document);
            var publicationKey = CultMeshBodyPublicationDocument.CreateLatestRecordKey(handle.BodyId);
            var publication = _node!.Database.Cache.Get<CultMeshBodyPublicationDocument>(publicationKey)
                ?? throw new InvalidOperationException(
                    $"Subscribed CultMesh body publication '{publicationKey.Value}' is missing for generation {handle.Sequence}.");
            handle.Validate(publication);
            var now = DateTimeOffset.UtcNow;
            if (!IsPublicationLive(publication, now))
                return;
            _bodyResolver ??= CreateBodyResolver();
            var lease = _bodyResolver.ResolveReadOnly(publication, new CultMeshBodyValidationRequest
            {
                BodyId = handle.BodyId,
                SchemaId = document.BodySchemaId,
                LayoutVersion = document.LayoutVersion,
                ProducerEpoch = document.ProducerEpoch,
                Sequence = document.Sequence,
                Capacity = document.Capacity,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                NowUtc = now
            });
            var handler = EntityViewAvailable;
            if (handler == null)
            {
                lease.Dispose();
                return;
            }
            try
            {
                handler(document, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static bool IsPublicationLive(
            CultMeshBodyPublicationDocument publication,
            DateTimeOffset now) =>
            publication.LivenessExpiresAtUnixMs > now.ToUnixTimeMilliseconds();

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
            if (_subscriptions != null && _entitySubscriptions != null) return;
            _subscriptions?.Dispose();
            _entitySubscriptions?.Dispose();
            _subscriptions = null;
            _entitySubscriptions = null;
            try
            {
                _subscriptions = CreateSubscriptionClient();
                _entitySubscriptions = CreateSubscriptionClient();
            }
            catch
            {
                _subscriptions?.Dispose();
                _entitySubscriptions?.Dispose();
                _subscriptions = null;
                _entitySubscriptions = null;
                throw;
            }
            _subscriptions.Changed += OnReplicatedDocumentChanged;
            _entitySubscriptions.Changed += OnReplicatedDocumentChanged;

            var lowered = new EveUnitySceneSurfaceLowerer()
                .Lower(CurrentSurfaceDocument.SurfaceDocument, CurrentSurfaceDocument.AdvertisedSurface);
            string entityViewPointer = lowered.PlayableWorld == null
                ? ""
                : lowered.PlayableWorld.EntityViewPointerId;
            string entityBodyId = lowered.PlayableWorld == null
                ? ""
                : lowered.PlayableWorld.EntityBodyId;
            if (!string.IsNullOrWhiteSpace(entityViewPointer))
            {
                if (string.IsNullOrWhiteSpace(entityBodyId))
                    throw new InvalidOperationException(
                        "The playable world advertises an entity-view pointer without its logical body id.");
                _entitySubscriptions.SubscribeAsync(
                        "eve-unity-entity-view",
                        recordKeys: new[]
                        {
                            entityViewPointer,
                            CultMeshBodyPublicationDocument.CreateLatestRecordKey(
                                entityBodyId).Value
                        },
                        schemaIds: new[]
                        {
                            EveEntitySoaViewDocument.SchemaId,
                            CultMeshBodyPublicationSchemaVersions.Publication
                        },
                        consumerRuntimeId: _runtimeId,
                        bodyIds: new[] { entityBodyId },
                        supportedBodyTransports: new[]
                        {
                            CultMeshBodyTransportKind.SharedMemory.ToString(),
                            CultMeshBodyTransportKind.Network.ToString()
                        })
                    .GetAwaiter().GetResult();
                var current = _node!.Database.Cache.Get(new CultRecordKey(entityViewPointer)) as EveEntitySoaViewDocument
                    ?? _snapshot!
                        .FetchDocumentsAsync<EveEntitySoaViewDocument>(
                            recordKeys: new[] { entityViewPointer },
                            schemaIds: new[] { EveEntitySoaViewDocument.SchemaId })
                        .GetAwaiter().GetResult().FirstOrDefault();
                if (current != null)
                    QueueEntityView(current);
            }
            _subscriptions.SubscribeAsync(
                    "eve-unity-surface",
                    recordKeys: new[] { _advertisedSurface!.RecordRef },
                    schemaIds: new[] { EveSurfaceDocument.SchemaId },
                    includeSnapshot: false)
                .GetAwaiter().GetResult();
            _subscriptions.SubscribeAsync(
                    "eve-unity-receipts",
                    schemaIds: new[] { EveCommandReceiptDocument.SchemaId },
                    includeSnapshot: false)
                .GetAwaiter().GetResult();
        }

        private CultNetDatabaseSubscriptionClient CreateSubscriptionClient()
        {
            var endpoint = new Uri(_endpoint);
            var client = CultNetSchemaClients.CreateForEndpoint(_endpoint);
            client.Connect(endpoint.Host, endpoint.Port);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!client.Connected && DateTime.UtcNow < deadline)
                System.Threading.Thread.Sleep(1);
            if (!client.Connected)
            {
                client.Dispose();
                throw new TimeoutException($"Timed out connecting live Eve state subscription to '{_endpoint}'.");
            }
            return new CultNetDatabaseSubscriptionClient(client, _node!.Database.Cache, _networkRegistry!);
        }

        private void OnReplicatedDocumentChanged(CultNetReplicatedDocumentChange change)
        {
            if (change.Document is EveEntitySoaViewDocument entityView)
                QueueEntityView(entityView);
            else if (change.Document is EveSurfaceDocument or EveCommandReceiptDocument)
                _liveDocuments.Enqueue(change.Document);
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
            foreach (var lease in _assetBodyLeases) lease.Dispose();
            _assetBodyLeases.Clear();
            CurrentAssetBodyTransportKind = null;
            _prefabs.Clear();
            _nativeAssets.Clear();
            _nativeAssetMetadata.Clear();
            _renderChannelLayers.Clear();
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
            if (selected.Length == 0)
            {
                var advertisedVariants = catalog.Assets
                    .SelectMany(asset => asset.Variants.Select(variant =>
                        $"{asset.AssetRef}:{variant.RuntimeId}/{variant.Platform}"))
                    .ToArray();
                throw new InvalidOperationException(
                    $"Provider asset catalog '{catalog.CatalogId}' has no unity-scene/{CurrentBundlePlatform()} variant. " +
                    $"Advertised variants: {string.Join(", ", advertisedVariants)}");
            }
            ReadCameraPolicies(selected.Select(selection => selection.Variant!));
            foreach (var group in selected.GroupBy(selection => selection.Variant!.Uri, StringComparer.Ordinal))
            {
                var variant = group.First().Variant!;
                var manifest = _snapshot!.FetchDocumentsAsync<CultMeshCdnArtifactManifest>(
                        recordKeys: new[] { variant.Uri },
                        schemaIds: new[] { CultMeshCdnSchemaVersions.ArtifactManifest })
                    .GetAwaiter()
                    .GetResult();
                var descriptor = manifest.FirstOrDefault();
                if (descriptor == null)
                    throw new InvalidOperationException($"Provider asset bundle manifest '{variant.Uri}' was not available.");
                var bundle = LoadVerifiedBundle(descriptor, variant);
                if (bundle == null)
                    throw new InvalidOperationException($"Unity could not load provider asset bundle '{variant.Uri}'.");
                _assetBundles.Add(bundle);
                var bundleAssetNames = bundle.GetAllAssetNames();
                foreach (var selection in group)
                {
                    var bundleAssetName = ResolveBundleAssetName(bundleAssetNames, selection.Variant!.AssetKey);
                    var value = bundleAssetName == null ? null : bundle.LoadAsset(bundleAssetName);
                    if (value == null)
                        throw new InvalidOperationException(
                            $"Provider asset '{selection.Asset.AssetRef}' advertises missing Unity bundle asset " +
                            $"'{selection.Variant.AssetKey}'. Available assets: " +
                            string.Join(", ", bundleAssetNames));
                    _nativeAssets[selection.Asset.AssetRef] = value;
                    var metadata = MergeAssetMetadata(selection.Asset.Metadata, selection.Variant.Metadata);
                    _nativeAssetMetadata[selection.Asset.AssetRef] = metadata;
                    if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                        !string.IsNullOrWhiteSpace(role))
                    {
                        _nativeAssets[role] = value;
                        _nativeAssetMetadata[role] = metadata;
                    }
                    if (value is GameObject prefab)
                    {
                        _prefabs[selection.Asset.AssetRef] = prefab;
                        if (!string.IsNullOrWhiteSpace(role)) _prefabs[role] = prefab;
                    }
                }
            }

            CurrentAssetCatalogVersion = catalog.Version;
        }

        private static string? ResolveBundleAssetName(IEnumerable<string> bundleAssetNames, string advertisedAssetKey)
        {
            return bundleAssetNames.FirstOrDefault(assetName =>
                string.Equals(assetName, advertisedAssetKey, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyDictionary<string, string> MergeAssetMetadata(
            IReadOnlyDictionary<string, string> assetMetadata,
            IReadOnlyDictionary<string, string> variantMetadata)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in assetMetadata) merged[pair.Key] = pair.Value;
            foreach (var pair in variantMetadata) merged[pair.Key] = pair.Value;
            return merged;
        }

        private void ReadCameraPolicies(IEnumerable<EveAssetVariant> variants)
        {
            _renderChannelLayers.Clear();
            foreach (var variant in variants)
            {
                foreach (var pair in variant.Metadata)
                {
                    const string prefix = "renderChannel.";
                    const string suffix = ".unityLayer";
                    if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                        !pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                        continue;
                    var channel = pair.Key.Substring(prefix.Length, pair.Key.Length - prefix.Length - suffix.Length);
                    if (!string.IsNullOrWhiteSpace(channel) &&
                        int.TryParse(pair.Value, out var layer) && layer >= 0 && layer < 32)
                        _renderChannelLayers[channel] = layer;
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

        private CultMeshContentTransferService ContentTransfer()
        {
            ResolveAdvertisement();
            if (_contentTransfer != null)
                return _contentTransfer;
            if (string.IsNullOrWhiteSpace(_advertisement!.ServiceId))
                throw new InvalidOperationException($"Eve provider '{_advertisement.ProviderId}' has no service-instance identity for content transport.");
            var cacheRoot = Environment.GetEnvironmentVariable("EVEUNITY_ASSET_CACHE_PATH");
            if (string.IsNullOrWhiteSpace(cacheRoot))
                cacheRoot = Path.Combine(Application.persistentDataPath, "EveUnity", "assets");
            _assetBodyMappings = new CultMeshVerifiedBodyMappingBroker(cacheRoot);
            _contentTransfer = new CultMeshContentTransferService(
                _node!.Cache,
                new[]
                {
                    _meshClient!.ContentProvider(
                        _advertisement.ProviderId,
                        _advertisement.ServiceId,
                        new CultMeshSessionContentProviderOptions { ResponseTimeout = TimeSpan.FromSeconds(30) })
                },
                new CultMeshContentTransferOptions(cacheRoot),
                _assetBodyMappings);
            return _contentTransfer;
        }

        private AssetBundle? LoadVerifiedBundle(CultMeshCdnArtifactManifest manifest, EveAssetVariant variant)
        {
            var hash = NormalizeContentHash(variant.ContentHash);
            if (manifest.SizeBytes != variant.SizeBytes)
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' size did not match its catalog.");
            if (!string.Equals(NormalizeContentHash(manifest.ContentHash),
                    hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' hash did not match its catalog.");

            var now = DateTimeOffset.UtcNow;
            var network = NetworkArtifactDescriptor(manifest, now, TimeSpan.FromMinutes(5));
            var mapped = ContentTransfer()
                .FetchMappedContentAsync(manifest, network, now, TimeSpan.FromMinutes(5))
                .GetAwaiter()
                .GetResult();
            var request = new CultMeshBodyValidationRequest
            {
                BodyId = network.BodyId,
                SchemaId = network.SchemaId,
                LayoutVersion = network.LayoutVersion,
                ProducerEpoch = network.ProducerEpoch,
                Sequence = network.Sequence,
                Capacity = network.Capacity,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                NowUtc = now
            };
            var transport = new CultMeshBodyTransportService(
                new ICultMeshBodyTransportAdapter[]
                {
                    new CultMeshMappedBodyAdapter(_assetBodyMappings!),
                    new CultMeshNetworkBodyAdapter(_ => ReadVerifiedArtifactBytes(manifest))
                },
                descriptor => string.Equals(descriptor.BodyId, manifest.ArtifactId, StringComparison.Ordinal) &&
                    string.Equals(descriptor.SemanticHash, hash, StringComparison.Ordinal));
            var negotiated = transport.NegotiateReadOnly(mapped.Descriptor, network, request);
            CurrentAssetBodyTransportKind = negotiated.SelectedTransport;
            _assetBodyLeases.Add(negotiated.Lease);
            if (negotiated.SelectedTransport == CultMeshBodyTransportKind.SharedFileMapping)
                return AssetBundle.LoadFromFile(mapped.VerifiedPath);

            if (negotiated.Lease.Descriptor.ByteSize > int.MaxValue)
                throw new InvalidOperationException("Unity cannot lower a network asset body larger than one managed byte array.");
            var bytes = new byte[checked((int)negotiated.Lease.Descriptor.ByteSize)];
            negotiated.Lease.CopyTo(0, bytes, 0, bytes.Length);
            return AssetBundle.LoadFromMemory(bytes);
        }

        private byte[] ReadVerifiedArtifactBytes(CultMeshCdnArtifactManifest manifest)
        {
            var path = ContentTransfer().FetchAsync(manifest).GetAwaiter().GetResult();
            return File.ReadAllBytes(path);
        }

        private static CultMeshBodyDescriptor NetworkArtifactDescriptor(
            CultMeshCdnArtifactManifest manifest,
            DateTimeOffset now,
            TimeSpan leaseDuration)
        {
            if (manifest.SizeBytes > int.MaxValue)
                throw new InvalidOperationException("CultMesh artifact capacity exceeds the current body descriptor bound.");
            return new CultMeshBodyDescriptor
            {
                BodyId = manifest.ArtifactId,
                SchemaId = "gamecult.mesh.cdn-artifact.v1",
                LayoutVersion = 1,
                ByteSize = manifest.SizeBytes,
                Capacity = checked((int)manifest.SizeBytes),
                ProducerEpoch = 1,
                Sequence = 0,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                Synchronization = CultMeshBodySynchronization.ImmutableSequence,
                LeaseExpiresAtUnixMs = now.Add(leaseDuration).ToUnixTimeMilliseconds(),
                TransportKind = CultMeshBodyTransportKind.Network,
                CapabilityToken = CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value,
                SemanticHash = NormalizeContentHash(manifest.ContentHash)
            };
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
