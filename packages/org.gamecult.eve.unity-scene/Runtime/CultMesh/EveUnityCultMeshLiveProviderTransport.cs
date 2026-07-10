using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshLiveProviderTransport :
        IEveUnitySceneLiveProviderTransport,
        IDisposable
    {
        private const string RemoteShardId = "provider";

        private static readonly Type[] WireDocumentTypes =
        {
            typeof(EveProviderAdvertisementDocument),
            typeof(EveSurfaceDocument),
            typeof(EveSurfaceCommandRequest),
            typeof(EveCommandReceiptDocument)
        };

        private readonly string _replicaPath;
        private readonly string _endpoint;
        private readonly string _providerId;
        private readonly string _surfaceId;
        private readonly string _runtimeId;
        private readonly HashSet<string> _pendingCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _publishedReceiptIds = new HashSet<string>(StringComparer.Ordinal);
        private CultMeshNode? _node;
        private CultMeshSnapshotEndpoint? _snapshot;
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
            _node?.Dispose();
            _node = null;
            _snapshot = null;
            _networkRegistry = null;
            _replicaShard = null;
            _pendingCommandIds.Clear();
            _publishedReceiptIds.Clear();
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
                        RudpMaxFragmentBytes = 1200
                    }
                });
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
