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
        private readonly string _cachePath;
        private readonly string _rendezvousEndpoint;
        private readonly CultMeshSessionTarget _target;
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
        private readonly Dictionary<string, SelectedAsset> _assetSelections =
            new Dictionary<string, SelectedAsset>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SelectedAsset>> _bundleSelections =
            new Dictionary<string, List<SelectedAsset>>(StringComparer.Ordinal);
        private readonly Dictionary<string, EveAssetVariant> _bundleVariants =
            new Dictionary<string, EveAssetVariant>(StringComparer.Ordinal);
        private readonly HashSet<string> _loadedBundleUris = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _loadingBundleUris = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ICultMeshBodyReadLease> _assetBodyLeases = new List<ICultMeshBodyReadLease>();
        private readonly Dictionary<string, int> _renderChannelLayers = new Dictionary<string, int>(StringComparer.Ordinal);
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
        private CultMeshClient? _meshClient;
        private bool _liveDocumentsReady;
        private CultCache? _contentState;
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
        private CancellationTokenSource? _realtimeLifetime;
        private Task? _realtimePump;
        private CultMeshRealtimeSession? _realtimeSession;
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
            CultMeshBodyPublicationResolver? bodyResolver = null)
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
            _surfaceId = string.IsNullOrWhiteSpace(surfaceId)
                ? throw new ArgumentException("Surface id must be non-empty.", nameof(surfaceId))
                : surfaceId.Trim();
            _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "eve-unity" : runtimeId.Trim();
            _bodyResolver = bodyResolver;
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

            try
            {
                var elapsed = Stopwatch.StartNew();
                ResolveAdvertisement(forceRefresh: true);
                TraceStartup("advertisement", elapsed);
                RefreshSurface();
                TraceStartup("surface", elapsed);
                RefreshAssetCatalog();
                TraceStartup("asset-catalog-and-bundles", elapsed);
                EnsureLiveDocuments();
                TraceStartup("subscriptions", elapsed);
                _bootstrapped = true;
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is TimeoutException)
            {
                throw new InvalidOperationException(
                    $"CultMesh provider '{_providerId}' bootstrap through target '{_target}' failed.",
                    error);
            }
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
                    PublishAssetCatalog(assetCatalog);
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

            _pendingCommandIds.Add(commandId);
            try
            {
                var recordKey = ChildRecordKey(interaction.CommandRecordRef, commandId);
                _meshClient!.SubmitDocumentAsync(
                        _target,
                        recordKey,
                        request,
                        _runtimeId,
                        "eve-unity")
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                _pendingCommandIds.Remove(commandId);
                throw;
            }
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
            _assetSelections.Clear();
            _bundleSelections.Clear();
            _bundleVariants.Clear();
            _loadedBundleUris.Clear();
            _loadingBundleUris.Clear();
            _mappedEntityFrameCursor?.Dispose();
            _mappedEntityFrameCursor = null;
            _mappedEntityFrameContract = null;
            _realtimeLifetime?.Cancel();
            _realtimeSession?.Dispose();
            _realtimeSession = null;
            if (_realtimePump != null)
            {
                try { _realtimePump.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            _realtimePump = null;
            _realtimeLifetime?.Dispose();
            _realtimeLifetime = null;
            DisposeLiveDocuments();
            _contentState?.Dispose();
            _contentState = null;
            _meshClient?.Dispose();
            _meshClient = null;
            _contentTransfer = null;
            _assetBodyMappings = null;
            _baseSurface = null;
            _embeddedSurfaces.Clear();
            _pendingCommandIds.Clear();
            _publishedReceiptIds.Clear();
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            EnsureAssetLoaded(asset.AssetRef);
            return _prefabs.TryGetValue(asset.AssetRef, out var prefab) ? prefab : null;
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            EnsureAssetLoaded(asset.AssetRef);
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
            if (_meshClient != null)
                return;
            _meshClient = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = new[] { _rendezvousEndpoint },
                RealtimeConnectors = new ICultMeshRealtimeTransportConnector[]
                {
                    new CultMeshNativeQuicRealtimeTransportConnector()
                }
            });
        }

        private void ResolveAdvertisement(bool forceRefresh = false)
        {
            if (!forceRefresh && _advertisement != null && _advertisedSurface != null)
                return;
            using var advertisements = _meshClient!
                .LeaseCollectionAsync<EveProviderAdvertisementDocument>(_target)
                .GetAwaiter()
                .GetResult();
            _advertisement = advertisements.Handle.LatestAsync().GetAwaiter().GetResult().FirstOrDefault(document =>
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

        private void RefreshSurface()
        {
            var surface = _meshClient!
                .ReadAsync<EveSurfaceDocument>(_target, _advertisedSurface!.RecordRef)
                .GetAwaiter()
                .GetResult();
            PublishBaseSurface(surface);
        }

        private void PublishBaseSurface(EveSurfaceDocument surface)
        {
            _baseSurface = surface ?? throw new ArgumentNullException(nameof(surface));
            PublishComposedSurface();
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

        private void EnsureLiveDocuments()
        {
            if (_liveDocumentsReady) return;
            DisposeLiveDocuments();
            try
            {
                OpenLiveDocuments();
                _liveDocumentsReady = true;
            }
            catch
            {
                DisposeLiveDocuments();
                throw;
            }
        }

        private void OpenLiveDocuments()
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
                LeaseDocument<EveEntitySoaViewDocument>(entityViewPointer, QueueEntityView);
                LeaseCollection<CultMeshBodyPublicationDocument>(publication =>
                {
                    if (string.Equals(publication.BodyId, entityBodyId, StringComparison.Ordinal))
                        QueueBodyPublication(publication);
                });
            }
            foreach (var fieldRef in fieldRefs)
                LeaseDocument<EveFieldsSplatsDocument>(fieldRef, fields => _liveDocuments.Enqueue(fields));

            LeaseDocument<EveSurfaceDocument>(
                _advertisedSurface!.RecordRef,
                surface => _liveDocuments.Enqueue(new PendingSurfaceDocument(_advertisedSurface.RecordRef, surface)),
                publishInitial: false);
            var embeddedSlots = EnumerateEmbeddedDocuments(CurrentSurfaceDocument.SurfaceDocument.Surface.Root)
                .Where(slot => string.Equals(slot.SchemaId, EveSurfaceDocument.SchemaId, StringComparison.Ordinal))
                .Where(slot => !string.IsNullOrWhiteSpace(slot.DocumentId))
                .GroupBy(slot => slot.DocumentId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            foreach (var slot in embeddedSlots)
            {
                LeaseDocument<EveSurfaceDocument>(
                    slot.DocumentId,
                    surface => _liveDocuments.Enqueue(new PendingSurfaceDocument(slot.DocumentId, surface)));
            }
            var inputCapabilityRef = FindComponentProp(
                CurrentSurfaceDocument.SurfaceDocument.Surface.Root,
                "inputCapability");
            if (!string.IsNullOrWhiteSpace(inputCapabilityRef))
            {
                LeaseDocument<EveInputCapabilityDocument>(
                    inputCapabilityRef,
                    document => _liveDocuments.Enqueue(document));
            }
            var assetCatalogRef = RequireWorldInteraction().AssetManifestRecordRef;
            if (!string.IsNullOrWhiteSpace(assetCatalogRef))
            {
                LeaseDocument<EveAssetCatalogDocument>(
                    assetCatalogRef,
                    document => _liveDocuments.Enqueue(document),
                    publishInitial: false);
            }
            LeaseCollection<EveCommandReceiptDocument>(
                receipt => _liveDocuments.Enqueue(receipt),
                includeInitialSnapshot: false);
        }

        private void DisposeLiveDocuments()
        {
            foreach (var watch in _documentWatches) watch.Dispose();
            _documentWatches.Clear();
            foreach (var lease in _documentLeases) lease.Dispose();
            _documentLeases.Clear();
            _liveDocumentsReady = false;
        }

        private void LeaseDocument<TDocument>(
            string recordKey,
            Action<TDocument> publish,
            bool publishInitial = true)
            where TDocument : class
        {
            var lease = _meshClient!.LeaseDocumentAsync<TDocument>(_target, recordKey)
                .GetAwaiter().GetResult();
            _documentLeases.Add(lease);
            if (publishInitial) publish(lease.Handle.Latest());
            _documentWatches.Add(lease.Handle.Watch(publish));
        }

        private void LeaseCollection<TDocument>(
            Action<TDocument> publish,
            bool includeInitialSnapshot = true)
            where TDocument : class
        {
            var lease = _meshClient!.LeaseCollectionAsync<TDocument>(_target, includeInitialSnapshot)
                .GetAwaiter().GetResult();
            _documentLeases.Add(lease);
            if (includeInitialSnapshot)
            foreach (var document in lease.Handle.LatestAsync().GetAwaiter().GetResult())
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

            var catalog = _meshClient!
                .ReadAsync<EveAssetCatalogDocument>(_target, interaction.AssetManifestRecordRef)
                .GetAwaiter()
                .GetResult();
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
            _assetSelections.Clear();
            _bundleSelections.Clear();
            _bundleVariants.Clear();
            _loadedBundleUris.Clear();
            _loadingBundleUris.Clear();
            _renderChannelLayers.Clear();
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
            ReadCameraPolicies(selected.Select(selection => selection.Variant!));
            foreach (var selection in selected)
            {
                var variant = selection.Variant!;
                _assetSelections[selection.Asset.AssetRef] = selection;
                _bundleVariants[variant.Uri] = variant;
                if (!_bundleSelections.TryGetValue(variant.Uri, out var group))
                {
                    group = new List<SelectedAsset>();
                    _bundleSelections[variant.Uri] = group;
                }
                group.Add(selection);
                var metadata = MergeAssetMetadata(selection.Asset.Metadata, variant.Metadata);
                _nativeAssetMetadata[selection.Asset.AssetRef] = metadata;
                if (selection.Asset.Metadata.TryGetValue("presentationRole", out var role) &&
                    !string.IsNullOrWhiteSpace(role))
                {
                    _assetSelections[role] = selection;
                    _nativeAssetMetadata[role] = metadata;
                }
            }

            CurrentAssetCatalogVersion = catalog.Version;
        }

        private void EnsureAssetLoaded(string assetRef)
        {
            if (_nativeAssets.ContainsKey(assetRef) || !_assetSelections.TryGetValue(assetRef, out var selection))
                return;
            EnsureBundleLoaded(selection.Variant!.Uri);
        }

        private void EnsureBundleLoaded(string uri)
        {
            if (_loadedBundleUris.Contains(uri)) return;
            if (!_bundleVariants.TryGetValue(uri, out var variant))
                throw new InvalidOperationException($"Provider asset bundle '{uri}' is not present in the selected runtime catalog.");
            if (!_loadingBundleUris.Add(uri))
                throw new InvalidOperationException($"Provider asset bundle dependency cycle includes '{uri}'.");
            try
            {
                if (variant.Metadata.TryGetValue("unity.bundleDependencyUris", out var dependencies))
                foreach (var dependency in dependencies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    EnsureBundleLoaded(dependency);

                var groupElapsed = Stopwatch.StartNew();
                var descriptor = _meshClient!.ReadAsync<CultMeshCdnArtifactManifest>(_target, uri)
                    .GetAwaiter()
                    .GetResult();
                TraceStartup("asset-manifest", groupElapsed);
                var bundle = LoadVerifiedBundle(descriptor, variant);
                TraceStartup("asset-transfer-and-bundle-load", groupElapsed);
                if (bundle == null)
                    throw new InvalidOperationException($"Unity could not load provider asset bundle '{uri}'.");
                _assetBundles.Add(bundle);
                var bundleAssetNames = bundle.GetAllAssetNames();
                foreach (var selection in _bundleSelections[uri])
                {
                    var bundleAssetName = ResolveBundleAssetName(bundleAssetNames, selection.Variant!.AssetKey);
                    var value = bundleAssetName == null
                        ? null
                        : LoadBundleAsset(bundle, bundleAssetName, selection.Asset.AssetKind);
                    if (value == null)
                        throw new InvalidOperationException(
                            $"Provider asset '{selection.Asset.AssetRef}' advertises missing Unity bundle asset " +
                            $"'{selection.Variant.AssetKey}'. Available assets: " + string.Join(", ", bundleAssetNames));
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
                _loadedBundleUris.Add(uri);
                TraceStartup("bundle-assets", groupElapsed);
            }
            finally
            {
                _loadingBundleUris.Remove(uri);
            }
        }

        private static UnityEngine.Object? LoadBundleAsset(AssetBundle bundle, string assetName, string assetKind)
        {
            return string.Equals(assetKind, "sprite", StringComparison.Ordinal)
                ? bundle.LoadAsset<Sprite>(assetName)
                : bundle.LoadAsset(assetName);
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
            _contentState ??= new CultCache(
                CultMesh.CreateCultCacheDocumentRegistry(typeof(CultMeshContentTransferStateDocument)));
            _contentTransfer = new CultMeshContentTransferService(
                _contentState,
                new[]
                {
                    _meshClient!.ContentProvider(
                        _advertisement.ProviderId,
                        _target)
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
            var elapsed = Stopwatch.StartNew();
            var mapped = ContentTransfer()
                .FetchMappedContentAsync(manifest, network, now, TimeSpan.FromMinutes(5))
                .GetAwaiter()
                .GetResult();
            TraceStartup("content-materialization", elapsed);
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
            {
                var bundle = AssetBundle.LoadFromFile(mapped.VerifiedPath);
                TraceStartup("asset-bundle-load-from-file", elapsed);
                return bundle;
            }

            if (negotiated.Lease.Descriptor.ByteSize > int.MaxValue)
                throw new InvalidOperationException("Unity cannot lower a network asset body larger than one managed byte array.");
            var bytes = new byte[checked((int)negotiated.Lease.Descriptor.ByteSize)];
            negotiated.Lease.CopyTo(0, bytes, 0, bytes.Length);
            var loaded = AssetBundle.LoadFromMemory(bytes);
            TraceStartup("asset-bundle-load-from-memory", elapsed);
            return loaded;
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
                receipt.SourceVersion,
                receipt.Navigation == null
                    ? null
                    : new EveUnitySceneNavigationTarget(
                        receipt.Navigation.VerseId,
                        receipt.Navigation.ProviderId,
                        receipt.Navigation.SurfaceId,
                        receipt.Navigation.SurfaceKind,
                        receipt.Navigation.RendezvousEndpoints)));
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
