using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Eve.PluginFields;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityScene.Fields;
using GameCult.Mesh;
using GameCult.Mesh.Quic.Native;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshLiveProviderTransport :
        IEveUnitySceneLiveProviderTransport,
        IEveUnityGameObjectAssetProvider,
        IDisposable
    {
        private const int AssetPresentationAttemptLimit = 3;

        private readonly string _cachePath;
        private readonly string _rendezvousEndpoint;
        private readonly CultMeshSessionTarget _target;
        private readonly string _providerId;
        private readonly string _surfaceId;
        private readonly string _runtimeId;
        private readonly CultMeshAuthorityTrustPolicy _authorityTrust;
        private readonly CultMeshAuthorityTrustPolicy _crossTargetAuthorityTrust;
        private readonly Dictionary<string, EveSurfaceCommandRequest> _pendingCommands =
            new Dictionary<string, EveSurfaceCommandRequest>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<ReceiptSubscription>> _receiptSubscriptions =
            new Dictionary<string, Task<ReceiptSubscription>>(StringComparer.Ordinal);
        private readonly object _presentationFinalityGate = new object();
        private readonly Dictionary<string, EveCommandReceiptDocument> _deferredTerminalReceipts =
            new Dictionary<string, EveCommandReceiptDocument>(StringComparer.Ordinal);
        private readonly HashSet<string> _continuousCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<object> _liveDocuments = new ConcurrentQueue<object>();
        private readonly object _realtimeFrameGate = new object();
        private readonly Dictionary<string, CultMeshRealtimeFrame> _latestRealtimeFrames =
            new Dictionary<string, CultMeshRealtimeFrame>(StringComparer.Ordinal);
        private readonly HashSet<string> _queuedRealtimeFrameBodies =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, EveSurfaceDocument> _embeddedSurfaces =
            new Dictionary<string, EveSurfaceDocument>(StringComparer.Ordinal);
        private readonly List<IDisposable> _documentLeases = new List<IDisposable>();
        private readonly List<IDisposable> _documentWatches = new List<IDisposable>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _assetGenerationGate = new SemaphoreSlim(1, 1);
        private CultMeshClient? _meshClient;
        private bool _liveDocumentsReady;
        private CultCache? _contentState;
        private AssetGeneration _assetGeneration;
        private EveProviderAdvertisementDocument? _advertisement;
        private EveAdvertisedSurface? _advertisedSurface;
        private EveSurfaceDocument? _baseSurface;
        private CultMeshBodyPublicationResolver? _bodyResolver;
        private EveEntitySoaViewDocument? _latestEntityLayout;
        private CultMeshBodyPublicationDocument? _latestEntityPublication;
        private CultMeshMappedFrameBodyCursor? _mappedEntityFrameCursor;
        private CultMeshBodyDescriptor? _mappedEntityFrameContract;
        private CancellationTokenSource? _realtimeLifetime;
        private CancellationTokenSource? _surfaceAssetCandidateLifetime;
        private Task? _realtimePump;
        private CultMeshRealtimeSession? _realtimeSession;
        private EveUnityCultMeshCommandOutbox? _commandOutbox;
        private PendingAssetCatalogUpdate? _pendingAssetCatalog;
        private bool _assetCatalogUpdateRunning;
        private long _surfaceAssetGeneration;
        private long _highestObservedBaseSurfaceVersion = -1;
        private long _activeBaseSurfaceCandidateVersion = -1;
        private long _activeBaseSurfaceCandidateGeneration = -1;
        private int _lastAssetPresentationAttemptCount;
        private long _mountedBaseSurfaceVersion;
        private long _presentationBarrierVersion = -1;
        private long _lastQueuedEntityViewEpoch = -1;
        private long _lastQueuedEntityViewSequence = -1;
        private long _lastPresentedEntityViewEpoch = -1;
        private long _lastPresentedEntityViewSequence = -1;
        private bool _bootstrapped;
        private bool _disposed;

        public EveUnityCultMeshLiveProviderTransport(
            string cachePath,
            string rendezvousEndpoint,
            string verseId,
            string authorityRuntimeId,
            string providerId,
            string surfaceId,
            string runtimeId = "eve-unity",
            CultMeshBodyPublicationResolver? bodyResolver = null,
            CultMeshAuthorityTrustPolicy? authorityTrust = null,
            CultMeshAuthorityTrustPolicy? crossTargetAuthorityTrust = null)
        {
            _cachePath = string.IsNullOrWhiteSpace(cachePath)
                ? throw new ArgumentException("Cache path must be non-empty.", nameof(cachePath))
                : Path.GetFullPath(cachePath);
            _rendezvousEndpoint = string.IsNullOrWhiteSpace(rendezvousEndpoint)
                ? throw new ArgumentException("CultMesh rendezvous endpoint must be non-empty.", nameof(rendezvousEndpoint))
                : rendezvousEndpoint.Trim();
            _target = new CultMeshSessionTarget(verseId, authorityRuntimeId);
            _providerId = string.IsNullOrWhiteSpace(providerId)
                ? throw new ArgumentException("Provider id must be non-empty.", nameof(providerId))
                : providerId.Trim();
            _assetGeneration = new AssetGeneration(
                new AssetSource(_target, _providerId, "", Array.Empty<string>()),
                sourceIdentity: "",
                catalogVersion: -1,
                meshClient: null);
            _surfaceId = string.IsNullOrWhiteSpace(surfaceId)
                ? throw new ArgumentException("Surface id must be non-empty.", nameof(surfaceId))
                : surfaceId.Trim();
            _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "eve-unity" : runtimeId.Trim();
            _bodyResolver = bodyResolver;
            _authorityTrust = authorityTrust ?? new CultMeshAuthorityTrustPolicy(
                CultMeshAuthorityTrustMode.AuthenticatedRemote);
            _crossTargetAuthorityTrust = crossTargetAuthorityTrust ?? _authorityTrust;
            CurrentSurfaceDocument = EmptySurfaceDocument();
            CurrentAssetManifestDocument = EmptyAssetManifest();
            CurrentInputCapability = new EveInputCapabilityDocument();
        }

        public string TransportKind => "eve-cultmesh-identity-session";

        public string SurfacePointer => CurrentSurfaceDocument.SourcePointer;

        public string AssetManifestPointer => CurrentAssetManifestDocument.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

        public EveInputCapabilityDocument CurrentInputCapability { get; private set; }

        public CultMeshBodyTransportKind? CurrentAssetBodyTransportKind => _assetGeneration.BodyTransportKind;

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
            EnsurePrepared();
        }

        public void Disconnect()
        {
        }

        public void Refresh()
        {
            EnsurePrepared();
            PumpLiveEvents();
        }

        public async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            if (_bootstrapped)
                return;

            try
            {
                EnsureOpen();
                var elapsed = Stopwatch.StartNew();
                await ResolveAdvertisementAsync(forceRefresh: true, cancellationToken);
                TraceStartup("advertisement", elapsed);
                await RefreshSurfaceAsync(cancellationToken);
                TraceStartup("surface", elapsed);
                await RefreshAssetCatalogAsync(cancellationToken);
                TraceStartup("asset-catalog-and-bundles", elapsed);
                await EnsureLiveDocumentsAsync(cancellationToken);
                TraceStartup("subscriptions", elapsed);
                _commandOutbox = new EveUnityCultMeshCommandOutbox(
                    SendCommandAsync,
                    ContinuousCommandKey,
                    ForgetPendingCommand);
                _bootstrapped = true;
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is TimeoutException)
            {
                throw new InvalidOperationException(
                    $"CultMesh provider '{_providerId}' bootstrap through target '{_target}' failed.",
                    error);
            }
        }

        private void EnsurePrepared()
        {
            if (!_bootstrapped)
                throw new InvalidOperationException(
                    "The EveUnity CultMesh transport is not prepared. Await PrepareAsync before mounting or connecting it.");
        }

        private static void TraceStartup(string phase, Stopwatch elapsed)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("EVEUNITY_TRACE_STARTUP_PHASES"), "1", StringComparison.Ordinal))
                Debug.Log($"EveUnity startup phase {phase} took {elapsed.Elapsed.TotalMilliseconds:0.###}ms.");
            elapsed.Restart();
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
            while (_liveDocuments.TryDequeue(out var document))
            {
                if (document is PendingEntityGeneration entityGeneration)
                    PublishEntityView(entityGeneration.View, entityGeneration.Publication);
                else if (document is PendingRealtimeEntityFrame realtimeFrame)
                    PublishPendingRealtimeEntityFrame(realtimeFrame.BodyId);
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
                    QueueAssetCatalogUpdate(CurrentAssetSourceIdentity, assetCatalog);
                else if (document is EveCommandReceiptDocument receipt)
                    PublishReceipt(receipt);
                else if (document is Exception error)
                    throw error;
            }
            PumpMappedEntityFrame();
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
            EnsurePrepared();
            EnsurePresentationWritable();
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

            lock (_pendingCommands)
            {
                if (_pendingCommands.ContainsKey(commandId))
                    throw new InvalidOperationException($"Eve command '{commandId}' is already pending.");
                _pendingCommands.Add(commandId, request);
            }
            try { _commandOutbox!.Enqueue(request); }
            catch
            {
                ForgetPendingCommand(commandId);
                throw;
            }
        }

        private Task SendCommandAsync(EveSurfaceCommandRequest request, CancellationToken cancellationToken)
        {
            return SendCommandWithReceiptLeaseAsync(request, cancellationToken);
        }

        private async Task SendCommandWithReceiptLeaseAsync(
            EveSurfaceCommandRequest request,
            CancellationToken cancellationToken)
        {
            await EnsureReceiptSubscriptionAsync(request.CommandId, cancellationToken).ConfigureAwait(false);
            var interaction = RequireWorldInteraction();
            var recordKey = ChildRecordKey(interaction.CommandRecordRef, request.CommandId);
            await _meshClient!.SubmitDocumentAsync(
                    _target,
                    recordKey,
                    request,
                    _runtimeId,
                    "eve-unity",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private string? ContinuousCommandKey(EveSurfaceCommandRequest request)
        {
            if (!request.PayloadFields.TryGetValue("commandId", out var commandId) ||
                !_continuousCommandIds.Contains(commandId))
                return null;
            request.PayloadFields.TryGetValue("entityId", out var entityId);
            return commandId + "\u001f" + (entityId ?? "");
        }

        private void ForgetPendingCommand(string commandId)
        {
            Task<ReceiptSubscription>? subscription = null;
            lock (_pendingCommands)
            {
                _pendingCommands.Remove(commandId);
                if (_receiptSubscriptions.TryGetValue(commandId, out subscription))
                    _receiptSubscriptions.Remove(commandId);
            }
            if (subscription != null) DisposeReceiptSubscription(subscription);
        }

        public void Dispose()
        {
            _disposed = true;
            _lifetime.Cancel();
            _commandOutbox?.Dispose();
            _commandOutbox = null;
            _assetGeneration.Dispose();
            _mappedEntityFrameCursor?.Dispose();
            _mappedEntityFrameCursor = null;
            _mappedEntityFrameContract = null;
            _realtimeLifetime?.Cancel();
            _surfaceAssetCandidateLifetime?.Cancel();
            _surfaceAssetCandidateLifetime?.Dispose();
            _surfaceAssetCandidateLifetime = null;
            _realtimeSession?.Dispose();
            _realtimeSession = null;
            _realtimePump = null;
            _realtimeLifetime?.Dispose();
            _realtimeLifetime = null;
            DisposeLiveDocuments();
            _contentState?.Dispose();
            _contentState = null;
            _meshClient?.Dispose();
            _meshClient = null;
            _baseSurface = null;
            _embeddedSurfaces.Clear();
            lock (_pendingCommands)
            {
                _pendingCommands.Clear();
                foreach (var subscription in _receiptSubscriptions.Values)
                    DisposeReceiptSubscription(subscription);
                _receiptSubscriptions.Clear();
            }
            lock (_presentationFinalityGate)
            {
                _deferredTerminalReceipts.Clear();
                _presentationBarrierVersion = -1;
            }
            _lifetime.Dispose();
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            EnsureAssetPrepared(asset.AssetRef);
            return _assetGeneration.Prefabs.TryGetValue(asset.AssetRef, out var prefab) ? prefab : null;
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            EnsureAssetPrepared(asset.AssetRef);
            return _assetGeneration.NativeAssets.TryGetValue(asset.AssetRef, out var value) && assetType.IsInstanceOfType(value)
                ? value : null;
        }

        private void EnsureAssetPrepared(string assetRef)
        {
            EnsurePrepared();
            if (_assetGeneration.AssetSelections.ContainsKey(assetRef) &&
                !_assetGeneration.NativeAssets.ContainsKey(assetRef))
                throw new InvalidOperationException(
                    $"Provider asset '{assetRef}' was not materialized during asynchronous preparation.");
        }

        public bool TryResolveAssetMetadata(
            EveUnityPlayableWorldAssetBinding asset,
            out IReadOnlyDictionary<string, string> metadata)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            return _assetGeneration.NativeAssetMetadata.TryGetValue(asset.AssetRef, out metadata!);
        }

        public bool TryGetRenderChannelLayer(string channel, out int layer)
        {
            return _assetGeneration.RenderChannelLayers.TryGetValue(channel ?? "", out layer);
        }

        private void EnsureOpen()
        {
            if (_meshClient != null)
                return;
            _meshClient = CreateMeshClient(new[] { _rendezvousEndpoint }, _authorityTrust);
        }

        private CultMeshClient CreateMeshClient(
            IReadOnlyList<string> rendezvousEndpoints,
            CultMeshAuthorityTrustPolicy authorityTrust)
        {
            return new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = rendezvousEndpoints,
                Discovery = EveUnityCultMeshConnectivity.Discovery(),
                Sessions = new CultMeshSessionManagerOptions
                {
                    Trust = authorityTrust ?? throw new ArgumentNullException(nameof(authorityTrust))
                },
                Connectors = EveUnityCultMeshConnectivity.SchemaConnectors(),
                ContentConnectors = EveUnityCultMeshConnectivity.ContentConnectors(),
                RealtimeConnectors = new ICultMeshRealtimeTransportConnector[]
                {
                    new CultMeshNativeQuicRealtimeTransportConnector()
                }
            });
        }

        private CultMeshClient AssetMeshClient(AssetGeneration? generation = null) =>
            (generation ?? _assetGeneration).MeshClient ??
            _meshClient ?? throw new InvalidOperationException("The CultMesh client is not open.");

        private async Task ResolveAdvertisementAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (!forceRefresh && _advertisement != null && _advertisedSurface != null)
                return;
            using var lease = await _meshClient!
                .LeaseCollectionAsync<EveProviderAdvertisementDocument>(_target, cancellationToken);
            var documents = await lease.Handle.LatestAsync();
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
            var mappedRoot = _cachePath;
            var networkBodies = _meshClient!.BodyProvider(
                _advertisement.ProviderId,
                _target,
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
            EnsurePrepared();
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
            EnsureRealtimeEntityState();
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
            try
            {
                _mappedEntityFrameCursor = new CultMeshMappedFrameBodyCursor(descriptor);
                _mappedEntityFrameContract = descriptor;
                TraceHotState(
                    $"mapped frame cursor body={descriptor.BodyId} epoch={descriptor.ProducerEpoch} sequence={descriptor.Sequence}");
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is InvalidOperationException)
            {
                _mappedEntityFrameCursor = null;
                _mappedEntityFrameContract = null;
                TraceHotState($"mapped frame unavailable; selecting remote realtime plane: {error.Message}");
            }
        }

        private void EnsureRealtimeEntityState()
        {
            if (_realtimePump != null)
                return;
            if (_advertisement == null || string.IsNullOrWhiteSpace(_advertisement.VerseId))
                throw new InvalidOperationException(
                    $"Eve provider '{_providerId}' does not advertise the Verse identity required for realtime state.");
            _realtimeLifetime = new CancellationTokenSource();
            if (!string.Equals(_advertisement.VerseId, _target.VerseId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Eve provider '{_providerId}' advertisement Verse '{_advertisement.VerseId}' does not match selected Verse '{_target.VerseId}'.");
            _realtimePump = Task.Run(() => PumpRealtimeEntityStateAsync(_realtimeLifetime.Token));
        }

        private async Task PumpRealtimeEntityStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                _realtimeSession = await _meshClient!
                    .ConnectRealtimeAsync(_target, cancellationToken)
                    .ConfigureAwait(false);
                TraceHotState($"realtime state connected transport={_realtimeSession.TransportId} target={_target}");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await _realtimeSession.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    EnqueueRealtimeEntityFrame(frame);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Exception error)
            {
                _liveDocuments.Enqueue(error);
            }
        }

        private void EnqueueRealtimeEntityFrame(CultMeshRealtimeFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            lock (_realtimeFrameGate)
            {
                if (_latestRealtimeFrames.TryGetValue(frame.BodyId, out var current) &&
                    (frame.ProducerEpoch < current.ProducerEpoch ||
                     (frame.ProducerEpoch == current.ProducerEpoch && frame.Sequence <= current.Sequence)))
                    return;
                _latestRealtimeFrames[frame.BodyId] = frame;
                if (_queuedRealtimeFrameBodies.Add(frame.BodyId))
                    _liveDocuments.Enqueue(new PendingRealtimeEntityFrame(frame.BodyId));
            }
        }

        private void PublishPendingRealtimeEntityFrame(string bodyId)
        {
            CultMeshRealtimeFrame? frame;
            lock (_realtimeFrameGate)
            {
                _queuedRealtimeFrameBodies.Remove(bodyId);
                if (!_latestRealtimeFrames.TryGetValue(bodyId, out frame))
                    return;
                _latestRealtimeFrames.Remove(bodyId);
            }
            PublishRealtimeEntityFrame(frame);
        }

        private void PublishRealtimeEntityFrame(CultMeshRealtimeFrame frame)
        {
            var layout = _latestEntityLayout
                ?? throw new InvalidOperationException("Realtime entity state arrived before its Eve SoA layout.");
            var publication = _latestEntityPublication
                ?? throw new InvalidOperationException("Realtime entity state arrived before its body publication.");
            if (!string.Equals(frame.BodyId, publication.BodyId, StringComparison.Ordinal) ||
                !string.Equals(frame.SchemaId, layout.BodySchemaId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Realtime entity state does not match the advertised Eve body contract.");
            if (frame.Delivery != CultMeshRealtimeDelivery.LatestOnly)
                throw new InvalidDataException("Playable entity state requires latest-only QUIC delivery.");
            if (frame.ProducerEpoch < _lastPresentedEntityViewEpoch ||
                (frame.ProducerEpoch == _lastPresentedEntityViewEpoch &&
                 frame.Sequence <= _lastPresentedEntityViewSequence))
                return;
            var representation = (publication.Representations ?? Array.Empty<CultMeshBodyDescriptor>())
                .FirstOrDefault(value =>
                    string.Equals(value.BodyId, frame.BodyId, StringComparison.Ordinal) &&
                    string.Equals(value.SchemaId, frame.SchemaId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("Realtime entity state has no advertised body representation.");
            if (frame.Payload.Length != representation.ByteSize)
                throw new InvalidDataException(
                    $"Realtime entity body size {frame.Payload.Length} does not match advertised size {representation.ByteSize}.");

            var generation = MaterializeEntityGeneration(layout, frame.ProducerEpoch, frame.Sequence);
            var lease = new RealtimeEntityBodyLease(
                new CultMeshBodyDescriptor
                {
                    BodyId = frame.BodyId,
                    SchemaId = frame.SchemaId,
                    LayoutVersion = layout.LayoutVersion,
                    ByteSize = frame.Payload.Length,
                    Capacity = layout.Capacity,
                    ProducerEpoch = frame.ProducerEpoch,
                    Sequence = frame.Sequence,
                    AccessMode = CultMeshBodyAccessMode.ReadOnly,
                    Synchronization = CultMeshBodySynchronization.EpochSequence,
                    LeaseExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
                    TransportKind = CultMeshBodyTransportKind.Network,
                    CapabilityToken = "authenticated-quic",
                    SemanticHash = ""
                },
                frame.Payload.ToArray());
            var handler = EntityViewAvailable;
            if (handler == null)
            {
                lease.Dispose();
                return;
            }
            try
            {
                handler(generation, lease);
                _lastPresentedEntityViewEpoch = frame.ProducerEpoch;
                _lastPresentedEntityViewSequence = frame.Sequence;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private sealed class PendingRealtimeEntityFrame
        {
            public PendingRealtimeEntityFrame(string bodyId) =>
                BodyId = string.IsNullOrWhiteSpace(bodyId)
                    ? throw new ArgumentException("Body id must be non-empty.", nameof(bodyId))
                    : bodyId;

            public string BodyId { get; }
        }

        private sealed class RealtimeEntityBodyLease : ICultMeshBodyReadLease
        {
            private byte[] _bytes;

            public RealtimeEntityBodyLease(CultMeshBodyDescriptor descriptor, byte[] bytes)
            {
                Descriptor = descriptor;
                _bytes = bytes;
            }

            public CultMeshBodyDescriptor Descriptor { get; }
            public CultMeshBodyTransportKind TransportKind => CultMeshBodyTransportKind.Network;
            public byte ReadByte(long offset) { Validate(offset, 1); return _bytes[(int)offset]; }
            public int ReadInt32(long offset) { Validate(offset, 4); return BitConverter.ToInt32(_bytes, (int)offset); }
            public long ReadInt64(long offset) { Validate(offset, 8); return BitConverter.ToInt64(_bytes, (int)offset); }
            public float ReadSingle(long offset) { Validate(offset, 4); return BitConverter.ToSingle(_bytes, (int)offset); }
            public double ReadDouble(long offset) { Validate(offset, 8); return BitConverter.ToDouble(_bytes, (int)offset); }

            public int CopyTo(long offset, byte[] destination, int destinationOffset, int count)
            {
                Validate(offset, count);
                Buffer.BlockCopy(_bytes, (int)offset, destination, destinationOffset, count);
                return count;
            }

            public void Dispose() => _bytes = Array.Empty<byte>();

            private void Validate(long offset, int count)
            {
                if (offset < 0 || count < 0 || offset > _bytes.LongLength - count)
                    throw new ArgumentOutOfRangeException(nameof(offset));
            }
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

        private async Task RefreshSurfaceAsync(CancellationToken cancellationToken)
        {
            var surface = await _meshClient!
                .ReadAsync<EveSurfaceDocument>(_target, _advertisedSurface!.RecordRef, cancellationToken);
            PublishBaseSurface(surface);
        }

        private void PublishBaseSurface(EveSurfaceDocument surface)
        {
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (!TryBeginBaseSurfaceCandidate(surface.Version, out var generation))
                return;
            CancellationToken candidateToken;
            lock (_presentationFinalityGate)
            {
                _surfaceAssetCandidateLifetime?.Cancel();
                _surfaceAssetCandidateLifetime?.Dispose();
                _surfaceAssetCandidateLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                candidateToken = _surfaceAssetCandidateLifetime.Token;
            }
            if (!_bootstrapped)
            {
                _baseSurface = surface;
                PublishComposedSurface(surface.Version);
                return;
            }

            var source = ResolveAssetSource(surface);
            if (string.Equals(source.Identity, CurrentAssetSourceIdentity, StringComparison.Ordinal) &&
                (source.RequiredCatalogVersion <= 0 || source.RequiredCatalogVersion == CurrentAssetCatalogVersion))
            {
                _baseSurface = surface;
                PublishComposedSurface(surface.Version);
                return;
            }

            // A surface and its provider-qualified asset catalog are one presentation
            // candidate. Keep the previous surface mounted until the candidate assets
            // have resolved and preloaded; never lower a remote Verse surface against
            // the previous Verse's catalog merely because the surface record arrived first.
            BeginPresentationTransition(surface.Version);
            _ = RefreshAssetsForSurfaceAsync(surface, source, generation, candidateToken);
        }

        private bool TryBeginBaseSurfaceCandidate(long version, out long generation)
        {
            lock (_presentationFinalityGate)
            {
                generation = -1;
                if (version < _highestObservedBaseSurfaceVersion ||
                    version <= _mountedBaseSurfaceVersion ||
                    (version == _highestObservedBaseSurfaceVersion &&
                     _activeBaseSurfaceCandidateVersion == version))
                    return false;
                _highestObservedBaseSurfaceVersion = Math.Max(_highestObservedBaseSurfaceVersion, version);
                generation = Interlocked.Increment(ref _surfaceAssetGeneration);
                _activeBaseSurfaceCandidateVersion = version;
                _activeBaseSurfaceCandidateGeneration = generation;
                return true;
            }
        }

        private void RetireFailedBaseSurfaceCandidate(long version, long generation)
        {
            lock (_presentationFinalityGate)
            {
                if (_activeBaseSurfaceCandidateVersion != version ||
                    _activeBaseSurfaceCandidateGeneration != generation)
                    return;
                _activeBaseSurfaceCandidateVersion = -1;
                _activeBaseSurfaceCandidateGeneration = -1;
            }
        }

        private void PublishEmbeddedSurface(string recordKey, EveSurfaceDocument surface)
        {
            if (string.IsNullOrWhiteSpace(recordKey))
                throw new InvalidOperationException("An embedded Eve surface update has no record identity.");
            _embeddedSurfaces[recordKey] = surface ?? throw new ArgumentNullException(nameof(surface));
            PublishComposedSurface();
        }

        private void PublishComposedSurface(long? committedBaseVersion = null)
        {
            ApplyPreparedSurface(PrepareComposedSurface(
                _baseSurface ?? throw new InvalidOperationException("The base Eve surface is unavailable.")),
                committedBaseVersion);
        }

        private PreparedSurface PrepareComposedSurface(EveSurfaceDocument baseSurface)
        {
            var surface = ComposeSurface(baseSurface, _embeddedSurfaces);
            var interaction = RequireWorldInteraction();
            var document = new EveUnitySceneProviderSurfaceDocument(
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
            var lowered = new EveUnitySceneSurfaceLowerer()
                .Lower(document.SurfaceDocument, document.AdvertisedSurface);
            var continuousCommands = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(lowered.PlayableWorld?.MovementCommand))
                continuousCommands.Add(lowered.PlayableWorld.MovementCommand);
            if (!string.IsNullOrWhiteSpace(lowered.PlayableWorld?.LookCommand))
                continuousCommands.Add(lowered.PlayableWorld.LookCommand);
            return new PreparedSurface(document, continuousCommands);
        }

        private void ApplyPreparedSurface(PreparedSurface prepared, long? committedBaseVersion = null)
        {
            CurrentSurfaceDocument = prepared.Document;
            _continuousCommandIds.Clear();
            foreach (var command in prepared.ContinuousCommands)
                _continuousCommandIds.Add(command);
            SurfaceDocumentAvailable?.Invoke(CurrentSurfaceDocument);
            if (committedBaseVersion.HasValue)
                CompletePresentationTransition(committedBaseVersion.Value);
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

        private async Task EnsureLiveDocumentsAsync(CancellationToken cancellationToken)
        {
            if (_liveDocumentsReady) return;
            DisposeLiveDocuments();
            try
            {
                await OpenLiveDocumentsAsync(cancellationToken);
                _liveDocumentsReady = true;
            }
            catch
            {
                DisposeLiveDocuments();
                throw;
            }
        }

        private async Task OpenLiveDocumentsAsync(CancellationToken cancellationToken)
        {
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
                await LeaseDocumentAsync<EveEntitySoaViewDocument>(entityViewPointer, QueueEntityView, cancellationToken);
                await LeaseCollectionAsync<CultMeshBodyPublicationDocument>(publication =>
                {
                    if (string.Equals(publication.BodyId, entityBodyId, StringComparison.Ordinal))
                        QueueBodyPublication(publication);
                }, cancellationToken);
            }
            foreach (var fieldRef in fieldRefs)
                await LeaseDocumentAsync<EveFieldsSplatsDocument>(
                    fieldRef,
                    fields => _liveDocuments.Enqueue(fields),
                    cancellationToken);

            await LeaseDocumentAsync<EveSurfaceDocument>(
                _advertisedSurface!.RecordRef,
                surface => _liveDocuments.Enqueue(new PendingSurfaceDocument(_advertisedSurface.RecordRef, surface)),
                cancellationToken,
                publishInitial: false);
            var embeddedSlots = EnumerateEmbeddedDocuments(CurrentSurfaceDocument.SurfaceDocument.Surface.Root)
                .Where(slot => string.Equals(slot.SchemaId, EveSurfaceDocument.SchemaId, StringComparison.Ordinal))
                .Where(slot => !string.IsNullOrWhiteSpace(slot.DocumentId))
                .GroupBy(slot => slot.DocumentId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            foreach (var slot in embeddedSlots)
            {
                await LeaseDocumentAsync<EveSurfaceDocument>(
                    slot.DocumentId,
                    surface => _liveDocuments.Enqueue(new PendingSurfaceDocument(slot.DocumentId, surface)),
                    cancellationToken);
            }
            var inputCapabilityRef = FindComponentProp(
                CurrentSurfaceDocument.SurfaceDocument.Surface.Root,
                "inputCapability");
            if (!string.IsNullOrWhiteSpace(inputCapabilityRef))
            {
                await LeaseDocumentAsync<EveInputCapabilityDocument>(
                    inputCapabilityRef,
                    document => _liveDocuments.Enqueue(document),
                    cancellationToken);
            }
            var interaction = RequireWorldInteraction();
            if (!string.Equals(interaction.ReceiptSchema, EveCommandReceiptDocument.SchemaId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The provider advertises receipt schema '{interaction.ReceiptSchema}', expected '{EveCommandReceiptDocument.SchemaId}'.");
            if (string.IsNullOrWhiteSpace(interaction.ReceiptRecordRef))
                throw new InvalidOperationException("The provider advertisement does not publish a receipt record reference.");
        }

        private Task<ReceiptSubscription> EnsureReceiptSubscriptionAsync(
            string commandId,
            CancellationToken cancellationToken)
        {
            Task<ReceiptSubscription> subscription;
            lock (_pendingCommands)
            {
                if (_receiptSubscriptions.TryGetValue(commandId, out subscription!)) return subscription;
                subscription = OpenReceiptSubscriptionAsync(commandId, cancellationToken);
                _receiptSubscriptions.Add(commandId, subscription);
            }
            return RemoveFailedReceiptSubscriptionAsync(commandId, subscription);
        }

        private async Task<ReceiptSubscription> RemoveFailedReceiptSubscriptionAsync(
            string commandId,
            Task<ReceiptSubscription> subscription)
        {
            try { return await subscription.ConfigureAwait(false); }
            catch
            {
                lock (_pendingCommands)
                {
                    if (_receiptSubscriptions.TryGetValue(commandId, out var current) &&
                        ReferenceEquals(current, subscription))
                        _receiptSubscriptions.Remove(commandId);
                }
                throw;
            }
        }

        private async Task<ReceiptSubscription> OpenReceiptSubscriptionAsync(
            string commandId,
            CancellationToken cancellationToken)
        {
            var recordKey = ChildRecordKey(RequireWorldInteraction().ReceiptRecordRef, commandId);
            var lease = await _meshClient!.LeaseDocumentAsync<EveCommandReceiptDocument>(
                    _target,
                    recordKey,
                    cancellationToken)
                .ConfigureAwait(false);
            IDisposable? watch = null;
            try
            {
                watch = lease.Handle.Watch(receipt => QueueReceipt(commandId, receipt));
                try { QueueReceipt(commandId, await lease.Handle.LatestAsync().ConfigureAwait(false)); }
                catch (KeyNotFoundException) { }
                return new ReceiptSubscription(lease, watch);
            }
            catch
            {
                watch?.Dispose();
                lease.Dispose();
                throw;
            }
        }

        private void QueueReceipt(string expectedCommandId, EveCommandReceiptDocument receipt)
        {
            if (receipt == null) return;
            lock (_pendingCommands)
            {
                if (!_pendingCommands.TryGetValue(expectedCommandId, out var request) ||
                    !ReceiptMatches(request, receipt))
                    return;
            }
            _liveDocuments.Enqueue(receipt);
        }

        private static void DisposeReceiptSubscription(Task<ReceiptSubscription> subscription)
        {
            if (subscription.Status == TaskStatus.RanToCompletion)
                subscription.Result.Dispose();
            else
                _ = subscription.ContinueWith(
                    completed => { if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose(); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private void DisposeLiveDocuments()
        {
            foreach (var watch in _documentWatches) watch.Dispose();
            _documentWatches.Clear();
            foreach (var lease in _documentLeases) lease.Dispose();
            _documentLeases.Clear();
            _liveDocumentsReady = false;
        }

        private async Task LeaseDocumentAsync<TDocument>(
            string recordKey,
            Action<TDocument> publish,
            CancellationToken cancellationToken,
            bool publishInitial = true)
            where TDocument : class
        {
            var lease = await _meshClient!.LeaseDocumentAsync<TDocument>(_target, recordKey, cancellationToken);
            _documentLeases.Add(lease);
            if (publishInitial) publish(await lease.Handle.LatestAsync());
            _documentWatches.Add(lease.Handle.Watch(publish));
        }

        private async Task LeaseCollectionAsync<TDocument>(
            Action<TDocument> publish,
            CancellationToken cancellationToken,
            bool includeInitialSnapshot = true)
            where TDocument : class
        {
            var lease = await _meshClient!.LeaseCollectionAsync<TDocument>(
                _target,
                includeInitialSnapshot,
                cancellationToken);
            _documentLeases.Add(lease);
            if (includeInitialSnapshot)
            foreach (var document in await lease.Handle.LatestAsync())
                publish(document);
            _documentWatches.Add(lease.Handle.WatchChanges(change =>
            {
                if (change.Document != null) publish(change.Document);
            }));
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

        private sealed class PreparedSurface
        {
            public PreparedSurface(
                EveUnitySceneProviderSurfaceDocument document,
                IReadOnlyCollection<string> continuousCommands)
            {
                Document = document;
                ContinuousCommands = continuousCommands;
            }

            public EveUnitySceneProviderSurfaceDocument Document { get; }
            public IReadOnlyCollection<string> ContinuousCommands { get; }
        }

        private static void TraceHotState(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_CLIENT_RUDP"), "1", StringComparison.Ordinal))
                Debug.Log($"EveUnity CultMesh hot state: {message}");
        }

        private async Task RefreshAssetCatalogAsync(CancellationToken cancellationToken)
        {
            var source = ResolveAssetSource(
                _baseSurface ?? throw new InvalidOperationException("The provider surface must be available before its assets are resolved."));
            if (string.IsNullOrWhiteSpace(source.ManifestRecordRef))
                return;

            var candidate = await BuildAssetGenerationAsync(source, cancellationToken);
            CommitAssetGeneration(candidate);
        }

        private async Task RefreshAssetsForSurfaceAsync(
            EveSurfaceDocument surface,
            AssetSource source,
            long generation,
            CancellationToken cancellationToken)
        {
            Exception? finalError = null;
            Interlocked.Exchange(ref _lastAssetPresentationAttemptCount, 0);
            for (var attempt = 1; attempt <= AssetPresentationAttemptLimit; attempt++)
            {
                Interlocked.Exchange(ref _lastAssetPresentationAttemptCount, attempt);
                try
                {
                    if (generation != Interlocked.Read(ref _surfaceAssetGeneration))
                        return;
                    await _assetGenerationGate.WaitAsync(cancellationToken);
                    try
                    {
                        if (generation != Interlocked.Read(ref _surfaceAssetGeneration))
                            return;
                        var candidate = await BuildAssetGenerationAsync(source, cancellationToken);
                        if (generation != Interlocked.Read(ref _surfaceAssetGeneration))
                        {
                            candidate.Dispose();
                            return;
                        }
                        CommitAssetGeneration(candidate, surface);
                        return;
                    }
                    finally
                    {
                        _assetGenerationGate.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    finalError = error;
                    if (generation != Interlocked.Read(ref _surfaceAssetGeneration))
                        return;
                    if (attempt < AssetPresentationAttemptLimit)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }
            }
            RetireFailedBaseSurfaceCandidate(surface.Version, generation);
            _liveDocuments.Enqueue(finalError ?? new InvalidOperationException(
                $"Eve surface {surface.Version} asset preparation exhausted its retry policy."));
        }

        private AssetSource ResolveAssetSource(EveSurfaceDocument surface)
        {
            var world = FindWorldScene(surface.Surface.Root);
            var manifestRecordRef = world?.GetProp("assetManifest") ?? "";
            if (string.IsNullOrWhiteSpace(manifestRecordRef))
                manifestRecordRef = RequireWorldInteraction().AssetManifestRecordRef;
            var providerId = world?.GetProp("assetProviderId") ?? "";
            var verseId = world?.GetProp("assetVerseId") ?? "";
            var authorityRuntimeId = world?.GetProp("assetAuthorityRuntimeId") ?? "";
            var requiredCatalogVersion = long.TryParse(
                    world?.GetProp("assetCatalogVersion"),
                    out var parsedCatalogVersion)
                ? Math.Max(0, parsedCatalogVersion)
                : 0;
            var rendezvousEndpoints = ParseEndpointList(world?.GetProp("assetRendezvousEndpoints") ?? "");
            var hasQualifiedSource = !string.IsNullOrWhiteSpace(providerId) ||
                !string.IsNullOrWhiteSpace(verseId) ||
                !string.IsNullOrWhiteSpace(authorityRuntimeId);
            if (!hasQualifiedSource)
                return new AssetSource(
                    _target,
                    _providerId,
                    manifestRecordRef,
                    Array.Empty<string>(),
                    requiredCatalogVersion);
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(verseId) ||
                string.IsNullOrWhiteSpace(authorityRuntimeId))
                throw new InvalidOperationException(
                    "A provider-qualified Eve asset source requires assetProviderId, assetVerseId, and assetAuthorityRuntimeId.");
            return new AssetSource(
                new CultMeshSessionTarget(verseId, authorityRuntimeId),
                providerId,
                manifestRecordRef,
                rendezvousEndpoints,
                requiredCatalogVersion);
        }

        private async Task<AssetGeneration> BuildAssetGenerationAsync(
            AssetSource source,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var crossTarget = IsCrossTarget(source);
                var endpoints = AssetRendezvousEndpoints(source, crossTarget);
                var candidate = new AssetGeneration(
                    source,
                    source.Identity,
                    catalogVersion: -1,
                    crossTarget || source.RendezvousEndpoints.Count > 0
                        ? CreateMeshClient(
                            endpoints,
                            AssetAuthorityTrust(source))
                        : null);
                try
                {
                    var catalog = await AttachAssetCatalogSubscriptionAsync(
                        candidate,
                        source.RequiredCatalogVersion,
                        cancellationToken);
                    ConfigureAssetCatalog(candidate, catalog);
                    ReuseCompatibleBundles(_assetGeneration, candidate);
                    await PreloadAssetsAsync(candidate, cancellationToken);
                    var latest = await candidate.CatalogLease!.Handle.LatestAsync();
                    candidate.ObserveCatalog(latest);
                    if (latest.Version != candidate.CatalogVersion)
                    {
                        candidate.Dispose();
                        continue;
                    }
                    return candidate;
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }
            }
        }

        private bool IsCrossTarget(AssetSource source) =>
            !string.Equals(source.Target.VerseId, _target.VerseId, StringComparison.Ordinal) ||
            !string.Equals(source.Target.AuthorityRuntimeId, _target.AuthorityRuntimeId, StringComparison.Ordinal);

        private CultMeshAuthorityTrustPolicy AssetAuthorityTrust(AssetSource source) =>
            IsCrossTarget(source) ? _crossTargetAuthorityTrust : _authorityTrust;

        private IReadOnlyList<string> AssetRendezvousEndpoints(AssetSource source, bool crossTarget)
        {
            if (source.RendezvousEndpoints.Count > 0)
                return source.RendezvousEndpoints;
            if (crossTarget)
                throw new InvalidOperationException(
                    $"Cross-target asset source '{source.Target.VerseId}/{source.Target.AuthorityRuntimeId}' " +
                    "must advertise at least one Odin rendezvous endpoint.");
            return new[] { _rendezvousEndpoint };
        }

        private async Task<EveAssetCatalogDocument> AttachAssetCatalogSubscriptionAsync(
            AssetGeneration candidate,
            long requiredCatalogVersion,
            CancellationToken cancellationToken)
        {
            var requiredCatalog = new TaskCompletionSource<EveAssetCatalogDocument>();
            var lease = await AssetMeshClient(candidate)
                .LeaseDocumentAsync<EveAssetCatalogDocument>(
                    candidate.Source.Target,
                    candidate.Source.ManifestRecordRef,
                    cancellationToken);
            IDisposable? watch = null;
            try
            {
                watch = lease.Handle.Watch(catalog =>
                {
                    var pending = candidate.ObserveCatalog(catalog);
                    if (requiredCatalogVersion > 0 && catalog.Version == requiredCatalogVersion)
                        requiredCatalog.TrySetResult(catalog);
                    if (pending != null && requiredCatalogVersion <= 0)
                        QueueAssetCatalogUpdate(candidate.SourceIdentity, pending);
                });
                candidate.CatalogWatch = watch;
                candidate.CatalogLease = lease;
                var catalog = await lease.Handle.LatestAsync();
                candidate.ObserveCatalog(catalog);
                if (requiredCatalogVersion <= 0 || catalog.Version == requiredCatalogVersion)
                    return catalog;
                using (cancellationToken.Register(() => requiredCatalog.TrySetCanceled()))
                    return await requiredCatalog.Task;
            }
            catch
            {
                watch?.Dispose();
                lease.Dispose();
                throw;
            }
        }

        private void CommitAssetGeneration(AssetGeneration candidate, EveSurfaceDocument? surface = null)
        {
            PreparedSurface prepared;
            try
            {
                prepared = PrepareComposedSurface(surface ?? _baseSurface ??
                    throw new InvalidOperationException("The base Eve surface is unavailable."));
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
            var previous = _assetGeneration;
            previous.TransferSharedBundlesTo(candidate);
            _assetGeneration = candidate;
            var pendingCatalog = candidate.ActivateCatalogObservation();
            if (pendingCatalog != null && candidate.Source.RequiredCatalogVersion <= 0)
                QueueAssetCatalogUpdate(candidate.SourceIdentity, pendingCatalog);
            if (surface != null)
                _baseSurface = surface;
            try
            {
                ApplyPreparedSurface(prepared, surface?.Version);
            }
            finally
            {
                previous.Dispose();
            }
        }

        private static void ReuseCompatibleBundles(AssetGeneration current, AssetGeneration candidate)
        {
            foreach (var pair in candidate.BundleVariants)
            {
                if (!current.LoadedBundlesByUri.TryGetValue(pair.Key, out var bundle) ||
                    !current.BundleVariants.TryGetValue(pair.Key, out var existing) ||
                    !string.Equals(existing.ContentHash, pair.Value.ContentHash, StringComparison.Ordinal) ||
                    existing.SizeBytes != pair.Value.SizeBytes)
                    continue;
                var selections = candidate.BundleSelections[pair.Key];
                if (selections.Any(selection =>
                        !current.NativeAssets.ContainsKey(selection.Asset.AssetRef)))
                    continue;

                candidate.AssetBundles.Add(bundle);
                candidate.BorrowedAssetBundles.Add(bundle);
                candidate.LoadedBundlesByUri[pair.Key] = bundle;
                candidate.LoadedBundleUris.Add(pair.Key);
                foreach (var selection in selections)
                {
                    var value = current.NativeAssets[selection.Asset.AssetRef];
                    candidate.NativeAssets[selection.Asset.AssetRef] = value;
                    if (value is GameObject prefab)
                        candidate.Prefabs[selection.Asset.AssetRef] = prefab;
                    if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                        !string.IsNullOrWhiteSpace(role))
                    {
                        candidate.NativeAssets[role] = value;
                        if (value is GameObject rolePrefab)
                            candidate.Prefabs[role] = rolePrefab;
                    }
                }
            }
        }

        private static EveSurfaceComponent? FindWorldScene(EveSurfaceComponent component)
        {
            if (string.Equals(component.Kind, "world.scene3d", StringComparison.Ordinal) ||
                string.Equals(component.Kind, "world", StringComparison.Ordinal))
                return component;
            foreach (var child in component.Children ?? Array.Empty<EveSurfaceComponent>())
            {
                var found = FindWorldScene(child);
                if (found != null) return found;
            }
            return null;
        }

        private static IReadOnlyList<string> ParseEndpointList(string value) =>
            (value ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(endpoint => endpoint.Trim())
                .Where(endpoint => endpoint.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private static void ConfigureAssetCatalog(AssetGeneration candidate, EveAssetCatalogDocument catalog)
        {
            var selected = catalog.Assets
                .Select(asset => new SelectedAsset(
                    asset,
                    asset.Variants.FirstOrDefault(variant =>
                        string.Equals(variant.RuntimeId, "unity-scene", StringComparison.Ordinal) &&
                        string.Equals(variant.Platform, CurrentBundlePlatform(), StringComparison.Ordinal))))
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
            candidate.SetCatalogVersion(catalog.Version);
            ReadCameraPolicies(candidate, selected.Select(selection => selection.Variant!));
            foreach (var selection in selected)
            {
                var variant = selection.Variant!;
                candidate.AssetSelections[selection.Asset.AssetRef] = selection;
                candidate.BundleVariants[variant.Uri] = variant;
                if (!candidate.BundleSelections.TryGetValue(variant.Uri, out var group))
                {
                    group = new List<SelectedAsset>();
                    candidate.BundleSelections[variant.Uri] = group;
                }
                group.Add(selection);
                var metadata = MergeAssetMetadata(selection.Asset.Metadata, variant.Metadata);
                candidate.NativeAssetMetadata[selection.Asset.AssetRef] = metadata;
                if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                    !string.IsNullOrWhiteSpace(role))
                {
                    candidate.AssetSelections[role] = selection;
                    candidate.NativeAssetMetadata[role] = metadata;
                }
            }
        }

        private void QueueAssetCatalogUpdate(string sourceIdentity, EveAssetCatalogDocument catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (_assetGeneration.Source.RequiredCatalogVersion > 0)
            {
                _pendingAssetCatalog = null;
                return;
            }
            _pendingAssetCatalog = new PendingAssetCatalogUpdate(
                sourceIdentity,
                catalog);
            if (_assetCatalogUpdateRunning) return;
            _assetCatalogUpdateRunning = true;
            _ = DrainAssetCatalogUpdatesAsync();
        }

        private async Task DrainAssetCatalogUpdatesAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var update = _pendingAssetCatalog;
                    _pendingAssetCatalog = null;
                    if (update == null) break;
                    if (_assetGeneration.Source.RequiredCatalogVersion > 0)
                        continue;
                    if (!string.Equals(update.SourceIdentity, CurrentAssetSourceIdentity, StringComparison.Ordinal))
                        continue;
                    if (update.Catalog.Version == CurrentAssetCatalogVersion)
                        continue;
                    await _assetGenerationGate.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (!string.Equals(update.SourceIdentity, CurrentAssetSourceIdentity, StringComparison.Ordinal) ||
                            update.Catalog.Version == CurrentAssetCatalogVersion ||
                            _assetGeneration.Source.RequiredCatalogVersion > 0)
                            continue;
                        var currentSource = _assetGeneration.Source;
                        var source = new AssetSource(
                            currentSource.Target,
                            currentSource.ProviderId,
                            currentSource.ManifestRecordRef,
                            currentSource.RendezvousEndpoints,
                            0);
                        var candidate = await BuildAssetGenerationAsync(source, _lifetime.Token);
                        if (!string.Equals(update.SourceIdentity, CurrentAssetSourceIdentity, StringComparison.Ordinal) ||
                            _assetGeneration.Source.RequiredCatalogVersion > 0)
                        {
                            candidate.Dispose();
                            continue;
                        }
                        CommitAssetGeneration(candidate);
                    }
                    finally
                    {
                        _assetGenerationGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _liveDocuments.Enqueue(error);
            }
            finally
            {
                _assetCatalogUpdateRunning = false;
                if (_pendingAssetCatalog != null && !_lifetime.IsCancellationRequested)
                    QueueAssetCatalogUpdate(_pendingAssetCatalog.SourceIdentity, _pendingAssetCatalog.Catalog);
            }
        }

        private async Task PreloadAssetsAsync(AssetGeneration candidate, CancellationToken cancellationToken)
        {
            foreach (var uri in candidate.BundleVariants.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray())
                await EnsureBundleLoadedAsync(candidate, uri, cancellationToken);
        }

        private async Task EnsureBundleLoadedAsync(
            AssetGeneration candidate,
            string uri,
            CancellationToken cancellationToken)
        {
            if (candidate.LoadedBundleUris.Contains(uri)) return;
            if (!candidate.BundleVariants.TryGetValue(uri, out var variant))
                throw new InvalidOperationException($"Provider asset bundle '{uri}' is not present in the selected runtime catalog.");
            if (!candidate.LoadingBundleUris.Add(uri))
                throw new InvalidOperationException($"Provider asset bundle dependency cycle includes '{uri}'.");
            try
            {
                if (variant.Metadata.TryGetValue("unity.bundleDependencyUris", out var dependencies))
                foreach (var dependency in dependencies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    await EnsureBundleLoadedAsync(candidate, dependency, cancellationToken);

                var groupElapsed = Stopwatch.StartNew();
                var descriptor = await AssetMeshClient(candidate)
                    .ReadAsync<CultMeshCdnArtifactManifest>(candidate.Source.Target, uri, cancellationToken);
                TraceStartup("asset-manifest", groupElapsed);
                var bundle = await LoadVerifiedBundleAsync(candidate, descriptor, variant, cancellationToken);
                TraceStartup("asset-transfer-and-bundle-load", groupElapsed);
                if (bundle == null)
                    throw new InvalidOperationException($"Unity could not load provider asset bundle '{uri}'.");
                candidate.AssetBundles.Add(bundle);
                candidate.LoadedBundlesByUri[uri] = bundle;
                var bundleAssetNames = bundle.GetAllAssetNames();
                foreach (var selection in candidate.BundleSelections[uri])
                {
                    var bundleAssetName = ResolveBundleAssetName(bundleAssetNames, selection.Variant!.AssetKey);
                    var value = bundleAssetName == null
                        ? null
                        : await LoadBundleAssetAsync(bundle, bundleAssetName, selection.Asset.AssetKind, cancellationToken);
                    if (value == null)
                        throw new InvalidOperationException(
                            $"Provider asset '{selection.Asset.AssetRef}' advertises missing Unity bundle asset " +
                            $"'{selection.Variant.AssetKey}'. Available assets: " + string.Join(", ", bundleAssetNames));
                    candidate.NativeAssets[selection.Asset.AssetRef] = value;
                    if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                        !string.IsNullOrWhiteSpace(role))
                        candidate.NativeAssets[role] = value;
                    if (value is GameObject prefab)
                    {
                        candidate.Prefabs[selection.Asset.AssetRef] = prefab;
                        if (!string.IsNullOrWhiteSpace(role)) candidate.Prefabs[role] = prefab;
                    }
                }
                candidate.LoadedBundleUris.Add(uri);
                TraceStartup("bundle-assets", groupElapsed);
            }
            finally
            {
                candidate.LoadingBundleUris.Remove(uri);
            }
        }

        private static async Task<UnityEngine.Object?> LoadBundleAssetAsync(
            AssetBundle bundle,
            string assetName,
            string assetKind,
            CancellationToken cancellationToken)
        {
            var request = string.Equals(assetKind, "sprite", StringComparison.Ordinal)
                ? bundle.LoadAssetAsync<Sprite>(assetName)
                : bundle.LoadAssetAsync(assetName);
            await AwaitUnityAsync(request, cancellationToken);
            return request.asset;
        }

        private sealed class SelectedAsset
        {
            public SelectedAsset(EveAssetCatalogEntry asset, EveAssetVariant? variant)
            {
                Asset = asset;
                Variant = variant;
            }

            public EveAssetCatalogEntry Asset { get; }
            public EveAssetVariant? Variant { get; }
        }

        private sealed class AssetGeneration : IDisposable
        {
            private readonly object _catalogObservationGate = new object();
            private EveAssetCatalogDocument? _latestObservedCatalog;
            private bool _catalogObservationActive;

            public AssetGeneration(
                AssetSource source,
                string sourceIdentity,
                long catalogVersion,
                CultMeshClient? meshClient)
            {
                Source = source;
                SourceIdentity = sourceIdentity ?? "";
                CatalogVersion = catalogVersion;
                MeshClient = meshClient;
            }

            public AssetSource Source { get; }
            public string SourceIdentity { get; }
            public long CatalogVersion { get; private set; }
            public CultMeshClient? MeshClient { get; }
            public CultMeshDocumentLease<EveAssetCatalogDocument>? CatalogLease { get; set; }
            public IDisposable? CatalogWatch { get; set; }
            public CultMeshContentTransferService? ContentTransfer { get; set; }
            public CultMeshVerifiedBodyMappingBroker? BodyMappings { get; set; }
            public CultMeshBodyTransportKind? BodyTransportKind { get; set; }
            public Dictionary<string, GameObject> Prefabs { get; } = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            public Dictionary<string, UnityEngine.Object> NativeAssets { get; } = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            public Dictionary<string, IReadOnlyDictionary<string, string>> NativeAssetMetadata { get; } =
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            public List<AssetBundle> AssetBundles { get; } = new List<AssetBundle>();
            public HashSet<AssetBundle> BorrowedAssetBundles { get; } = new HashSet<AssetBundle>();
            public Dictionary<string, AssetBundle> LoadedBundlesByUri { get; } =
                new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            public Dictionary<string, SelectedAsset> AssetSelections { get; } =
                new Dictionary<string, SelectedAsset>(StringComparer.Ordinal);
            public Dictionary<string, List<SelectedAsset>> BundleSelections { get; } =
                new Dictionary<string, List<SelectedAsset>>(StringComparer.Ordinal);
            public Dictionary<string, EveAssetVariant> BundleVariants { get; } =
                new Dictionary<string, EveAssetVariant>(StringComparer.Ordinal);
            public HashSet<string> LoadedBundleUris { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> LoadingBundleUris { get; } = new HashSet<string>(StringComparer.Ordinal);
            public List<ICultMeshBodyReadLease> BodyLeases { get; } = new List<ICultMeshBodyReadLease>();
            public Dictionary<string, int> RenderChannelLayers { get; } =
                new Dictionary<string, int>(StringComparer.Ordinal);

            public void TransferSharedBundlesTo(AssetGeneration next)
            {
                foreach (var bundle in next.BorrowedAssetBundles)
                    AssetBundles.Remove(bundle);
                next.BorrowedAssetBundles.Clear();
            }

            public void SetCatalogVersion(long catalogVersion)
            {
                if (CatalogVersion >= 0 && CatalogVersion != catalogVersion)
                    throw new InvalidOperationException("An asset generation cannot change its catalog version after configuration.");
                CatalogVersion = catalogVersion;
            }

            public EveAssetCatalogDocument? ObserveCatalog(EveAssetCatalogDocument catalog)
            {
                lock (_catalogObservationGate)
                {
                    _latestObservedCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
                    return _catalogObservationActive && catalog.Version != CatalogVersion ? catalog : null;
                }
            }

            public EveAssetCatalogDocument? ActivateCatalogObservation()
            {
                lock (_catalogObservationGate)
                {
                    _catalogObservationActive = true;
                    return _latestObservedCatalog != null && _latestObservedCatalog.Version != CatalogVersion
                        ? _latestObservedCatalog
                        : null;
                }
            }

            public void Dispose()
            {
                CatalogWatch?.Dispose();
                CatalogWatch = null;
                CatalogLease?.Dispose();
                CatalogLease = null;
                MeshClient?.Dispose();
                foreach (var bundle in AssetBundles)
                    if (!BorrowedAssetBundles.Contains(bundle))
                        bundle.Unload(unloadAllLoadedObjects: false);
                AssetBundles.Clear();
                BorrowedAssetBundles.Clear();
                foreach (var lease in BodyLeases)
                    lease.Dispose();
                BodyLeases.Clear();
                Prefabs.Clear();
                NativeAssets.Clear();
                NativeAssetMetadata.Clear();
                AssetSelections.Clear();
                BundleSelections.Clear();
                BundleVariants.Clear();
                LoadedBundleUris.Clear();
                LoadingBundleUris.Clear();
                LoadedBundlesByUri.Clear();
                RenderChannelLayers.Clear();
            }
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

        private static void ReadCameraPolicies(AssetGeneration candidate, IEnumerable<EveAssetVariant> variants)
        {
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
                        candidate.RenderChannelLayers[channel] = layer;
                }
            }
        }

        private long CurrentAssetCatalogVersion => _assetGeneration.CatalogVersion;

        private string CurrentAssetSourceIdentity => _assetGeneration.SourceIdentity;

        private static string CurrentBundlePlatform()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ||
                   Application.platform == RuntimePlatform.WindowsPlayer
                ? "StandaloneWindows64"
                : Application.platform.ToString();
        }

        private CultMeshContentTransferService ContentTransfer(AssetGeneration candidate)
        {
            if (candidate.ContentTransfer != null)
                return candidate.ContentTransfer;
            if (_advertisement == null)
                throw new InvalidOperationException("The Eve provider advertisement must be prepared before content transfer.");
            if (string.IsNullOrWhiteSpace(_advertisement!.ServiceId))
                throw new InvalidOperationException($"Eve provider '{_advertisement.ProviderId}' has no service-instance identity for content transport.");
            var cacheRoot = Environment.GetEnvironmentVariable("EVEUNITY_ASSET_CACHE_PATH");
            if (string.IsNullOrWhiteSpace(cacheRoot))
                cacheRoot = Path.Combine(Application.persistentDataPath, "EveUnity", "assets");
            candidate.BodyMappings = new CultMeshVerifiedBodyMappingBroker(cacheRoot);
            _contentState ??= new CultCache(
                CultMesh.CreateCultCacheDocumentRegistry(typeof(CultMeshContentTransferStateDocument)));
            candidate.ContentTransfer = new CultMeshContentTransferService(
                _contentState,
                new[]
                {
                    AssetMeshClient(candidate).ContentProvider(
                        candidate.Source.ProviderId,
                        candidate.Source.Target)
                },
                new CultMeshContentTransferOptions(cacheRoot),
                candidate.BodyMappings);
            return candidate.ContentTransfer;
        }

        private async Task<AssetBundle?> LoadVerifiedBundleAsync(
            AssetGeneration candidate,
            CultMeshCdnArtifactManifest manifest,
            EveAssetVariant variant,
            CancellationToken cancellationToken)
        {
            var hash = NormalizeContentHash(variant.ContentHash);
            if (manifest.SizeBytes != variant.SizeBytes)
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' size did not match its catalog.");
            if (!string.Equals(NormalizeContentHash(manifest.ContentHash),
                    hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Provider asset bundle '{variant.Uri}' hash did not match its catalog.");

            var now = DateTimeOffset.UtcNow;
            var network = NetworkArtifactDescriptor(manifest, now, TimeSpan.FromMinutes(5));
            var elapsed = Stopwatch.StartNew();
            var mapped = await ContentTransfer(candidate)
                .FetchMappedContentAsync(manifest, network, now, TimeSpan.FromMinutes(5), cancellationToken);
            TraceStartup("content-materialization", elapsed);
            var validationRequest = new CultMeshBodyValidationRequest
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
                    new CultMeshMappedBodyAdapter(candidate.BodyMappings!),
                    new CultMeshNetworkBodyAdapter(_ => File.ReadAllBytes(mapped.VerifiedPath))
                },
                descriptor => string.Equals(descriptor.BodyId, manifest.ArtifactId, StringComparison.Ordinal) &&
                    string.Equals(descriptor.SemanticHash, hash, StringComparison.Ordinal));
            var negotiated = transport.NegotiateReadOnly(mapped.Descriptor, network, validationRequest);
            candidate.BodyTransportKind = negotiated.SelectedTransport;
            candidate.BodyLeases.Add(negotiated.Lease);
            var bundleRequest = AssetBundle.LoadFromFileAsync(mapped.VerifiedPath);
            await AwaitUnityAsync(bundleRequest, cancellationToken);
            TraceStartup("asset-bundle-load-from-file", elapsed);
            return bundleRequest.assetBundle;
        }

        private static async Task AwaitUnityAsync(AsyncOperation operation, CancellationToken cancellationToken)
        {
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
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
            var terminal = IsTerminalReceiptState(receipt.State);
            lock (_pendingCommands)
            {
                if (!_pendingCommands.TryGetValue(receipt.CommandId, out var request) ||
                    !ReceiptMatches(request, receipt))
                    return;
            }
            if (terminal)
            {
                _commandOutbox?.Acknowledge(receipt.CommandId);
                if (DeferTerminalReceiptUntilPresentation(receipt))
                    return;
            }
            FinalizeReceipt(receipt, terminal);
        }

        private void EnsurePresentationWritable()
        {
            lock (_presentationFinalityGate)
            {
                if (_presentationBarrierVersion > _mountedBaseSurfaceVersion)
                    throw new InvalidOperationException(
                        $"The Eve presentation is awaiting provider surface version {_presentationBarrierVersion}; " +
                        $"mounted base version {_mountedBaseSurfaceVersion} is read-only until that generation commits.");
            }
        }

        private void BeginPresentationTransition(long sourceVersion)
        {
            lock (_presentationFinalityGate)
            {
                if (sourceVersion <= _mountedBaseSurfaceVersion)
                    return;
                _presentationBarrierVersion = Math.Max(_presentationBarrierVersion, sourceVersion);
            }
        }

        private bool DeferTerminalReceiptUntilPresentation(EveCommandReceiptDocument receipt)
        {
            if (receipt.PresentationSurfaceVersion <= 0)
                return false;
            lock (_presentationFinalityGate)
            {
                if (receipt.PresentationSurfaceVersion <= _mountedBaseSurfaceVersion)
                    return false;
                _presentationBarrierVersion = Math.Max(
                    _presentationBarrierVersion,
                    receipt.PresentationSurfaceVersion);
                _deferredTerminalReceipts[receipt.CommandId] = receipt;
                return true;
            }
        }

        private void CompletePresentationTransition(long committedVersion)
        {
            EveCommandReceiptDocument[] ready;
            lock (_presentationFinalityGate)
            {
                _mountedBaseSurfaceVersion = Math.Max(_mountedBaseSurfaceVersion, committedVersion);
                if (_activeBaseSurfaceCandidateVersion == committedVersion)
                {
                    _activeBaseSurfaceCandidateVersion = -1;
                    _activeBaseSurfaceCandidateGeneration = -1;
                }
                ready = _deferredTerminalReceipts.Values
                    .Where(receipt => receipt.PresentationSurfaceVersion <= _mountedBaseSurfaceVersion)
                    .ToArray();
                foreach (var receipt in ready)
                    _deferredTerminalReceipts.Remove(receipt.CommandId);
                if (_presentationBarrierVersion <= _mountedBaseSurfaceVersion)
                    _presentationBarrierVersion = -1;
            }
            foreach (var receipt in ready)
                FinalizeReceipt(receipt, terminal: true);
        }

        private long MountedBaseSurfaceVersion
        {
            get
            {
                lock (_presentationFinalityGate)
                    return _mountedBaseSurfaceVersion;
            }
        }

        private void FinalizeReceipt(EveCommandReceiptDocument receipt, bool terminal)
        {
            if (terminal)
            {
                lock (_pendingCommands)
                {
                    if (!_pendingCommands.ContainsKey(receipt.CommandId))
                        return;
                    _pendingCommands.Remove(receipt.CommandId);
                }
                ForgetPendingCommand(receipt.CommandId);
            }
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
                receipt.SourceVersion,
                receipt.Navigation == null
                    ? null
                    : new EveUnitySceneNavigationTarget(
                        receipt.Navigation.VerseId,
                        receipt.Navigation.ProviderId,
                        receipt.Navigation.SurfaceId,
                        receipt.Navigation.SurfaceKind,
                        receipt.Navigation.RendezvousEndpoints,
                        receipt.Navigation.AuthorityRuntimeId)));
        }

        private sealed class AssetSource
        {
            public AssetSource(
                CultMeshSessionTarget target,
                string providerId,
                string manifestRecordRef,
                IReadOnlyList<string> rendezvousEndpoints)
                : this(target, providerId, manifestRecordRef, rendezvousEndpoints, 0)
            {
            }

            public AssetSource(
                CultMeshSessionTarget target,
                string providerId,
                string manifestRecordRef,
                IReadOnlyList<string> rendezvousEndpoints,
                long requiredCatalogVersion)
            {
                Target = target;
                ProviderId = providerId ?? "";
                ManifestRecordRef = manifestRecordRef ?? "";
                RendezvousEndpoints = rendezvousEndpoints ?? Array.Empty<string>();
                RequiredCatalogVersion = Math.Max(0, requiredCatalogVersion);
                Identity = Target.VerseId + "\u001f" + Target.AuthorityRuntimeId + "\u001f" +
                    ProviderId + "\u001f" + ManifestRecordRef + "\u001f" +
                    string.Join("\u001e", RendezvousEndpoints);
            }

            public CultMeshSessionTarget Target { get; }
            public string ProviderId { get; }
            public string ManifestRecordRef { get; }
            public IReadOnlyList<string> RendezvousEndpoints { get; }
            public long RequiredCatalogVersion { get; }
            public string Identity { get; }
        }

        private sealed class PendingAssetCatalogUpdate
        {
            public PendingAssetCatalogUpdate(string sourceIdentity, EveAssetCatalogDocument catalog)
            {
                SourceIdentity = sourceIdentity ?? "";
                Catalog = catalog;
            }

            public string SourceIdentity { get; }
            public EveAssetCatalogDocument Catalog { get; }
        }

        private bool ReceiptMatches(EveSurfaceCommandRequest request, EveCommandReceiptDocument receipt) =>
            string.Equals(receipt.Schema, EveCommandReceiptDocument.SchemaId, StringComparison.Ordinal) &&
            string.Equals(receipt.CommandId, request.CommandId, StringComparison.Ordinal) &&
            string.Equals(receipt.Command, request.Command, StringComparison.Ordinal) &&
            string.Equals(receipt.ProviderId, request.ProviderId, StringComparison.Ordinal) &&
            string.Equals(receipt.SurfaceId, request.SurfaceId, StringComparison.Ordinal) &&
            string.Equals(receipt.Authority, _target.AuthorityRuntimeId, StringComparison.Ordinal) &&
            string.Equals(receipt.InvocationHash, EveCommandInvocationHash.Compute(request), StringComparison.Ordinal) &&
            IsKnownReceiptState(receipt.State);

        private static bool IsKnownReceiptState(string state) =>
            string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase) ||
            IsTerminalReceiptState(state);

        private static bool IsTerminalReceiptState(string state) =>
            string.Equals(state, "accepted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "denied", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "reconciled", StringComparison.OrdinalIgnoreCase);

        private sealed class ReceiptSubscription : IDisposable
        {
            private IDisposable? _lease;
            private IDisposable? _watch;

            public ReceiptSubscription(IDisposable lease, IDisposable watch)
            {
                _lease = lease;
                _watch = watch;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _watch, null)?.Dispose();
                Interlocked.Exchange(ref _lease, null)?.Dispose();
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
