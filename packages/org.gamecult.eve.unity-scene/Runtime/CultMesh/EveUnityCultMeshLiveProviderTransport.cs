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
        private readonly Dictionary<string, EveSurfaceDocument> _embeddedSurfaces =
            new Dictionary<string, EveSurfaceDocument>(StringComparer.Ordinal);
        private CultMeshNode? _node;
        private CultMeshSnapshotSession? _snapshot;
        private CultNetDocumentRegistry? _networkRegistry;
        private CultNetShardDescriptor? _replicaShard;
        private CultNetDatabaseSubscriptionClient? _subscriptions;
        private CultNetDatabaseSubscriptionClient? _entitySubscriptions;
        private CultMeshLiveBody? _entityBody;
        private CultNetLiveValue<EveInputCapabilityDocument>? _inputCapability;
        private CultMeshClient? _meshClient;
        private CultMeshContentTransferService? _contentTransfer;
        private CultMeshVerifiedBodyMappingBroker? _assetBodyMappings;
        private EveProviderAdvertisementDocument? _advertisement;
        private EveAdvertisedSurface? _advertisedSurface;
        private EveSurfaceDocument? _baseSurface;
        private CultMeshBodyPublicationResolver? _bodyResolver;
        private EveEntitySoaViewDocument? _latestEntityLayout;
        private CultMeshBodyPublicationDocument? _latestEntityPublication;
        private CultMeshMappedFrameBodyCursor? _mappedEntityFrameCursor;
        private CultMeshBodyDescriptor? _mappedEntityFrameContract;
        private long _lastQueuedEntityViewEpoch = -1;
        private long _lastQueuedEntityViewSequence = -1;
        private long _lastPresentedEntityViewEpoch = -1;
        private long _lastPresentedEntityViewSequence = -1;
        private bool _bootstrapped;
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
            BootstrapIfRequired();
        }

        public void Disconnect()
        {
        }

        public void Refresh()
        {
            EnsureOpen();
            if (_bootstrapped)
            {
                PumpLiveEvents();
                return;
            }

            BootstrapIfRequired();
        }

        private void BootstrapIfRequired()
        {
            if (_bootstrapped)
                return;

            ResolveAdvertisement(forceRefresh: true);
            RefreshSurface();
            RefreshAssetCatalog();
            _bootstrapped = true;
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
            ThrowIfSubscriptionFailed(_entitySubscriptions);
            ThrowIfSubscriptionFailed(_subscriptions);
            while (_liveDocuments.TryDequeue(out var document))
            {
                if (document is PendingEntityGeneration entityGeneration)
                    PublishEntityView(entityGeneration.View, entityGeneration.Publication);
                else if (document is EveFieldsSplatsDocument fields)
                    FieldsSplatsAvailable?.Invoke(fields);
                else if (document is PendingSurfaceDocument surface)
                {
                    if (string.Equals(surface.RecordKey, _advertisedSurface?.RecordRef, StringComparison.Ordinal))
                        PublishBaseSurface(surface.Document);
                    else
                        PublishEmbeddedSurface(surface.RecordKey, surface.Document);
                }
                else if (document is EveInputCapabilityDocument inputCapability)
                    CurrentInputCapability = inputCapability;
                else if (document is EveAssetCatalogDocument assetCatalog)
                    PublishAssetCatalog(assetCatalog);
                else if (document is EveCommandReceiptDocument receipt)
                    PublishReceipt(receipt);
                else if (document is Exception error)
                    throw error;
            }
            PumpMappedEntityFrame();
        }

        private static void ThrowIfSubscriptionFailed(CultNetDatabaseSubscriptionClient? subscription)
        {
            var failure = subscription?.BackgroundFailure;
            if (failure?.IsCompletedSuccessfully == true)
                throw new InvalidOperationException(
                    "The retained CultMesh state subscription failed.",
                    failure.Result);
        }

        private void QueueEntityView(EveEntitySoaViewDocument document)
        {
            TraceHotState($"layout epoch={document.ProducerEpoch} sequence={document.Sequence} identities={document.Identities?.Length ?? 0}");
            _latestEntityLayout = document;
            var publication = _latestEntityPublication;
            if (publication != null && PublicationMatchesLayout(publication, document))
                QueueEntityGeneration(document, publication);
        }

        private void QueueBodyPublication(CultMeshBodyPublicationDocument publication)
        {
            _latestEntityPublication = publication;
            var layout = _latestEntityLayout;
            if (layout == null || layout.Buffers == null || layout.Buffers.Length == 0 ||
                !string.Equals(layout.Buffers[0].BufferId, publication.BodyId, StringComparison.Ordinal))
            {
                TraceHotState($"publication skipped body={publication.BodyId} sequence={publication.Sequence} layout={(layout == null ? "missing" : "incompatible")}");
                return;
            }

            TraceHotState($"publication body={publication.BodyId} epoch={publication.ProducerEpoch} sequence={publication.Sequence} plane={string.Join(",", publication.Representations.Select(value => value.TransportKind))}");

            var generation = PublicationMatchesLayout(publication, layout)
                ? layout
                : MaterializeEntityGeneration(layout, publication.ProducerEpoch, publication.Sequence);
            QueueEntityGeneration(generation, publication);
        }

        private void QueueEntityGeneration(
            EveEntitySoaViewDocument document,
            CultMeshBodyPublicationDocument publication)
        {
            if (document.ProducerEpoch < _lastQueuedEntityViewEpoch ||
                (document.ProducerEpoch == _lastQueuedEntityViewEpoch &&
                 document.Sequence <= _lastQueuedEntityViewSequence))
                return;
            _lastQueuedEntityViewEpoch = document.ProducerEpoch;
            _lastQueuedEntityViewSequence = document.Sequence;
            _liveDocuments.Enqueue(new PendingEntityGeneration(document, publication));
        }

        private static bool PublicationMatchesLayout(
            CultMeshBodyPublicationDocument publication,
            EveEntitySoaViewDocument layout) =>
            publication.ProducerEpoch == layout.ProducerEpoch &&
            publication.Sequence == layout.Sequence;

        private sealed class PendingEntityGeneration
        {
            public PendingEntityGeneration(
                EveEntitySoaViewDocument view,
                CultMeshBodyPublicationDocument publication)
            {
                View = view;
                Publication = publication;
            }

            public EveEntitySoaViewDocument View { get; }
            public CultMeshBodyPublicationDocument Publication { get; }
        }

        private static EveEntitySoaViewDocument MaterializeEntityGeneration(
            EveEntitySoaViewDocument layout,
            long producerEpoch,
            long sequence)
        {
            return new EveEntitySoaViewDocument
            {
                Schema = layout.Schema,
                ProviderId = layout.ProviderId,
                ViewId = layout.ViewId,
                PublishedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                BodySchemaId = layout.BodySchemaId,
                LayoutVersion = layout.LayoutVersion,
                ProducerEpoch = producerEpoch,
                Sequence = sequence,
                Capacity = layout.Capacity,
                Buffers = layout.Buffers,
                Columns = layout.Columns,
                DirtyRanges = (layout.DirtyRanges ?? Array.Empty<EveEntitySoaDirtyRange>())
                    .Select(range => new EveEntitySoaDirtyRange
                    {
                        ColumnId = range.ColumnId,
                        StartIndex = range.StartIndex,
                        Count = range.Count,
                        Sequence = sequence
                    })
                    .ToArray(),
                RenderGroups = layout.RenderGroups,
                Identities = layout.Identities,
                FrameId = sequence
            };
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
            _mappedEntityFrameCursor?.Dispose();
            _mappedEntityFrameCursor = null;
            _mappedEntityFrameContract = null;
            _entityBody?.Dispose();
            _entityBody = null;
            _inputCapability?.Dispose();
            _inputCapability = null;
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
            _baseSurface = null;
            _embeddedSurfaces.Clear();
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
            if (string.IsNullOrWhiteSpace(_advertisement!.ServiceId))
                throw new InvalidOperationException(
                    $"Eve provider '{_advertisement.ProviderId}' has no service-instance identity for body transport.");
            var mappedRoot = Path.GetDirectoryName(_replicaPath) ?? ".";
            var networkBodies = _meshClient!.BodyProvider(
                _advertisement.ProviderId,
                _advertisement.ServiceId,
                new CultMeshSessionBodyProviderOptions { ResponseTimeout = TimeSpan.FromSeconds(2) });
            return new CultMeshBodyPublicationResolver(new CultMeshBodyTransportService(
                new ICultMeshBodyTransportAdapter[]
                {
                    new CultMeshSharedMemoryBodyAdapter(),
                    new CultMeshMappedBodyAdapter(mappedRoot),
                    new CultMeshNetworkBodyAdapter(networkBodies)
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

        private static string NormalizeContentHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("CultMesh content hash is missing.");
            var normalized = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? value.Substring("sha256:".Length)
                : value;
            return normalized.ToLowerInvariant();
        }

        private void PublishEntityView(
            EveEntitySoaViewDocument document,
            CultMeshBodyPublicationDocument publication)
        {
            ResolveAdvertisement();
            if (!string.Equals(document.ProviderId, _advertisement!.ProviderId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException(
                    $"Entity layout provider '{document.ProviderId}' does not match advertised Eve provider '{_advertisement.ProviderId}'.");
            if (document.Buffers == null || document.Buffers.Length == 0)
                throw new InvalidOperationException("Entity layout does not name a primary logical buffer.");
            var handle = BodyPublicationHandle(document);
            handle.Validate(publication);
            var now = DateTimeOffset.UtcNow;
            if (!IsPublicationLive(publication, now))
                return;
            EnsureMappedEntityFrameCursor(publication);
            if (_mappedEntityFrameCursor != null)
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
                _lastPresentedEntityViewEpoch = document.ProducerEpoch;
                _lastPresentedEntityViewSequence = document.Sequence;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private void EnsureMappedEntityFrameCursor(CultMeshBodyPublicationDocument publication)
        {
            var descriptor = (publication.Representations ?? Array.Empty<CultMeshBodyDescriptor>())
                .FirstOrDefault(CultMeshMappedFrameBodyCursor.CanOpen);
            if (descriptor == null)
            {
                _mappedEntityFrameCursor?.Dispose();
                _mappedEntityFrameCursor = null;
                _mappedEntityFrameContract = null;
                return;
            }

            var current = _mappedEntityFrameContract;
            if (current != null &&
                string.Equals(FrameCapabilityIdentity(current.CapabilityToken),
                    FrameCapabilityIdentity(descriptor.CapabilityToken), StringComparison.Ordinal) &&
                string.Equals(current.BodyId, descriptor.BodyId, StringComparison.Ordinal) &&
                string.Equals(current.SchemaId, descriptor.SchemaId, StringComparison.Ordinal) &&
                current.LayoutVersion == descriptor.LayoutVersion &&
                current.Capacity == descriptor.Capacity &&
                current.ProducerEpoch == descriptor.ProducerEpoch &&
                current.ByteSize == descriptor.ByteSize)
                return;

            _mappedEntityFrameCursor?.Dispose();
            _mappedEntityFrameCursor = new CultMeshMappedFrameBodyCursor(descriptor);
            _mappedEntityFrameContract = descriptor;
            TraceHotState(
                $"mapped frame cursor body={descriptor.BodyId} epoch={descriptor.ProducerEpoch} sequence={descriptor.Sequence}");
        }

        private void PumpMappedEntityFrame()
        {
            var cursor = _mappedEntityFrameCursor;
            var layout = _latestEntityLayout;
            if (cursor == null || layout == null || !cursor.TryAcquireLatest(out var lease))
                return;

            var descriptor = lease.Descriptor;
            if (descriptor.ProducerEpoch < _lastPresentedEntityViewEpoch ||
                (descriptor.ProducerEpoch == _lastPresentedEntityViewEpoch &&
                 descriptor.Sequence <= _lastPresentedEntityViewSequence))
            {
                lease.Dispose();
                return;
            }

            var generation = MaterializeEntityGeneration(
                layout,
                descriptor.ProducerEpoch,
                descriptor.Sequence);
            var handler = EntityViewAvailable;
            if (handler == null)
            {
                lease.Dispose();
                return;
            }
            try
            {
                handler(generation, lease);
                _lastPresentedEntityViewEpoch = descriptor.ProducerEpoch;
                _lastPresentedEntityViewSequence = descriptor.Sequence;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static string FrameCapabilityIdentity(string capabilityToken)
        {
            var separator = capabilityToken.LastIndexOf('.');
            return separator > 0 ? capabilityToken.Substring(0, separator) : capabilityToken;
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
            PublishBaseSurface(surface);
        }

        private void PublishBaseSurface(EveSurfaceDocument surface)
        {
            _baseSurface = surface ?? throw new ArgumentNullException(nameof(surface));
            PublishComposedSurface();
            EnsureLiveSubscriptions();
        }

        private void PublishEmbeddedSurface(string recordKey, EveSurfaceDocument surface)
        {
            if (string.IsNullOrWhiteSpace(recordKey))
                throw new InvalidOperationException("An embedded Eve surface update has no record identity.");
            _embeddedSurfaces[recordKey] = surface ?? throw new ArgumentNullException(nameof(surface));
            PublishComposedSurface();
        }

        private void PublishComposedSurface()
        {
            var surface = ComposeSurface(
                _baseSurface ?? throw new InvalidOperationException("The base Eve surface is unavailable."),
                _embeddedSurfaces);
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
        }

        internal static EveSurfaceDocument ComposeSurface(
            EveSurfaceDocument surface,
            IReadOnlyDictionary<string, EveSurfaceDocument> embeddedSurfaces)
        {
            var root = ComposeComponent(surface.Surface.Root, embeddedSurfaces);
            var version = embeddedSurfaces.Values.Select(value => value.Version)
                .DefaultIfEmpty(surface.Version)
                .Append(surface.Version)
                .Max();
            return new EveSurfaceDocument(
                surface.Type,
                surface.Schema,
                surface.ProviderId,
                surface.ProviderKind,
                surface.Title,
                version,
                surface.UpdatedAtUtc,
                new EveSurfaceTree(surface.Surface.Id, root, surface.Surface.Styles),
                surface.Commands);
        }

        private static EveSurfaceComponent ComposeComponent(
            EveSurfaceComponent component,
            IReadOnlyDictionary<string, EveSurfaceDocument> embeddedSurfaces)
        {
            var children = component.Children.Select(child => ComposeComponent(child, embeddedSurfaces)).ToList();
            foreach (var slot in component.EmbeddedDocuments ?? Array.Empty<EveEmbeddedDocumentSlot>())
            {
                if (!string.Equals(slot.SchemaId, EveSurfaceDocument.SchemaId, StringComparison.Ordinal) ||
                    !embeddedSurfaces.TryGetValue(slot.DocumentId, out var embedded))
                    continue;
                children.Add(ComposeComponent(embedded.Surface.Root, embeddedSurfaces));
            }
            return new EveSurfaceComponent(
                component.Id,
                component.Kind,
                component.Props,
                children,
                component.StateBindingRecords,
                component.EmbeddedDocuments,
                component.Layout,
                component.Style);
        }

        private void EnsureLiveSubscriptions()
        {
            if (_subscriptions != null && _entitySubscriptions != null) return;
            _entityBody?.Dispose();
            _entityBody = null;
            _inputCapability?.Dispose();
            _inputCapability = null;
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
                _entityBody?.Dispose();
                _entityBody = null;
                _inputCapability?.Dispose();
                _inputCapability = null;
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
            var fieldRefs = lowered.PlayableWorld == null
                ? Array.Empty<string>()
                : lowered.PlayableWorld.FieldVolumes
                    .Select(field => field.DocumentRef)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (!string.IsNullOrWhiteSpace(entityViewPointer))
            {
                if (string.IsNullOrWhiteSpace(entityBodyId))
                    throw new InvalidOperationException(
                        "The playable world advertises an entity-view pointer without its logical body id.");
                _bodyResolver ??= CreateBodyResolver();
                var initialLayout = _entitySubscriptions.SubscribeAsync(
                        "eve-unity-entity-layout",
                        recordKeys: new[] { entityViewPointer },
                        schemaIds: new[] { EveEntitySoaViewDocument.SchemaId },
                        deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live)
                    .GetAwaiter().GetResult();
                foreach (var layout in initialLayout.OfType<EveEntitySoaViewDocument>())
                    QueueEntityView(layout);

                _entityBody = CultMesh.SubscribeLiveBodyAsync(
                        _entitySubscriptions,
                        _bodyResolver,
                        new CultMeshLiveBodySubscription(
                            "eve-unity-entity-body",
                            _runtimeId,
                            entityBodyId))
                    .GetAwaiter().GetResult();
                if (_entityBody.HasValue)
                    QueueBodyPublication(_entityBody.Current);
                _entityBody.Changed += QueueBodyPublication;
                _entityBody.Removed += () => _liveDocuments.Enqueue(new InvalidOperationException(
                    $"The provider withdrew required entity body '{entityBodyId}'."));

                if (fieldRefs.Length > 0)
                {
                    var initialFields = _entitySubscriptions.SubscribeAsync(
                            "eve-unity-fields",
                            recordKeys: fieldRefs,
                            schemaIds: new[] { EveFieldsSchemas.Splats },
                            deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live)
                        .GetAwaiter().GetResult();
                    foreach (var fields in initialFields.OfType<EveFieldsSplatsDocument>())
                        _liveDocuments.Enqueue(fields);
                }
            }
            else if (fieldRefs.Length > 0)
            {
                var initial = _entitySubscriptions.SubscribeAsync(
                        "eve-unity-fields",
                        recordKeys: fieldRefs,
                        schemaIds: new[] { EveFieldsSchemas.Splats },
                        deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live)
                    .GetAwaiter().GetResult();
                foreach (var fields in initial.OfType<EveFieldsSplatsDocument>())
                    _liveDocuments.Enqueue(fields);
            }
            _subscriptions.SubscribeAsync(
                    "eve-unity-surface",
                    recordKeys: new[] { _advertisedSurface!.RecordRef },
                    schemaIds: new[] { EveSurfaceDocument.SchemaId },
                    includeSnapshot: false)
                .GetAwaiter().GetResult();
            var embeddedSlots = EnumerateEmbeddedDocuments(CurrentSurfaceDocument.SurfaceDocument.Surface.Root)
                .Where(slot => string.Equals(slot.SchemaId, EveSurfaceDocument.SchemaId, StringComparison.Ordinal))
                .Where(slot => !string.IsNullOrWhiteSpace(slot.DocumentId))
                .GroupBy(slot => slot.DocumentId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            for (var index = 0; index < embeddedSlots.Length; index++)
            {
                var slot = embeddedSlots[index];
                var initialEmbedded = _subscriptions.SubscribeAsync(
                        $"eve-unity-embedded-surface-{index}",
                        recordKeys: new[] { slot.DocumentId },
                        schemaIds: new[] { EveSurfaceDocument.SchemaId },
                        deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live)
                    .GetAwaiter().GetResult()
                    .OfType<EveSurfaceDocument>()
                    .FirstOrDefault();
                if (initialEmbedded != null)
                    PublishEmbeddedSurface(slot.DocumentId, initialEmbedded);
            }
            var inputCapabilityRef = FindComponentProp(
                CurrentSurfaceDocument.SurfaceDocument.Surface.Root,
                "inputCapability");
            if (!string.IsNullOrWhiteSpace(inputCapabilityRef))
            {
                _inputCapability = _subscriptions
                    .SubscribeLiveValueAsync<EveInputCapabilityDocument>(
                        "eve-unity-input-capability",
                        inputCapabilityRef)
                    .GetAwaiter().GetResult();
                if (_inputCapability.HasValue)
                    CurrentInputCapability = _inputCapability.Current;
                _inputCapability.Changed += document => _liveDocuments.Enqueue(document);
                _inputCapability.Removed += () => _liveDocuments.Enqueue(new InvalidOperationException(
                    $"The provider withdrew required input capability '{inputCapabilityRef}'."));
            }
            var assetCatalogRef = RequireWorldInteraction().AssetManifestRecordRef;
            if (!string.IsNullOrWhiteSpace(assetCatalogRef))
            {
                _subscriptions.SubscribeAsync(
                        "eve-unity-asset-catalog",
                        recordKeys: new[] { assetCatalogRef },
                        schemaIds: new[] { EveAssetCatalogDocument.SchemaId },
                        includeSnapshot: false)
                    .GetAwaiter().GetResult();
            }
            _subscriptions.SubscribeAsync(
                    "eve-unity-receipts",
                    schemaIds: new[] { EveCommandReceiptDocument.SchemaId },
                    includeSnapshot: false,
                    deliveryMode: CultNetDatabaseSubscriptionDeliveryMode.Live)
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
            TraceHotState($"change subscription={change.SubscriptionId} kind={change.ChangeKind} document={change.Document?.GetType().Name ?? "null"}");
            if (change.Document is EveEntitySoaViewDocument entityView)
                QueueEntityView(entityView);
            else if (change.Document is CultMeshBodyPublicationDocument publication)
                QueueBodyPublication(publication);
            else if (change.Document is EveSurfaceDocument surface)
                _liveDocuments.Enqueue(new PendingSurfaceDocument(change.RecordKey, surface));
            else if (change.Document is EveAssetCatalogDocument or
                     EveCommandReceiptDocument or EveFieldsSplatsDocument)
                _liveDocuments.Enqueue(change.Document);
        }

        private static IEnumerable<EveEmbeddedDocumentSlot> EnumerateEmbeddedDocuments(EveSurfaceComponent component)
        {
            foreach (var slot in component.EmbeddedDocuments ?? Array.Empty<EveEmbeddedDocumentSlot>())
                yield return slot;
            foreach (var child in component.Children ?? Array.Empty<EveSurfaceComponent>())
            foreach (var slot in EnumerateEmbeddedDocuments(child))
                yield return slot;
        }

        private sealed class PendingSurfaceDocument
        {
            public PendingSurfaceDocument(string recordKey, EveSurfaceDocument document)
            {
                RecordKey = recordKey ?? "";
                Document = document ?? throw new ArgumentNullException(nameof(document));
            }

            public string RecordKey { get; }
            public EveSurfaceDocument Document { get; }
        }

        private static void TraceHotState(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_CLIENT_RUDP"), "1", StringComparison.Ordinal))
                Debug.Log($"EveUnity CultMesh hot state: {message}");
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
            if (catalog != null)
                PublishAssetCatalog(catalog);
        }

        private void PublishAssetCatalog(EveAssetCatalogDocument catalog)
        {
            if (catalog.Version == CurrentAssetCatalogVersion)
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
