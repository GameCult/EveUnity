using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using GameCult.Eve.Surface;
using GameCult.Eve.PluginFields;
using GameCult.Eve.UnityScene.Fields;
using GameCult.Mesh;
using UnityEngine;
using UnityEngine.Serialization;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityCultMeshPlayableWorldProvider :
        MonoBehaviour,
        IEveUnitySceneProviderSurfaceDocumentSource,
        IEveUnitySceneProviderSurfaceDocumentConnection,
        IEveUnitySceneCommandSink,
        IEveUnitySceneCommandReceiptSource,
        IEveUnityProviderRefreshSource,
        IEveUnityEntitySoaViewDocumentSource,
        IEveUnityFieldsSplatsDocumentSource,
        IEveUnityCameraRenderPolicySource,
        IEveUnityNativeAssetProvider,
        IEveUnityNativeAssetMetadataProvider,
        IEveUnityInputCapabilitySource,
        IEveUnityNavigableProvider
    {
        [SerializeField] private string rendezvousEndpoint = "";
        [SerializeField] private string providerFilter = "";
        [SerializeField] private string surfaceFilter = "";
        [SerializeField] private string verseFilter = "";
        [SerializeField] private string surfaceKind = "interactive-world";
        [FormerlySerializedAs("replicaPath")]
        [SerializeField] private string cacheDirectory = "";
        [SerializeField] private string runtimeId = "eve-unity";
        private CultMeshAuthorityTrustPolicy _authorityTrust = new CultMeshAuthorityTrustPolicy(
            CultMeshAuthorityTrustMode.AuthenticatedRemote);
        private CultMeshAuthorityTrustPolicy _navigationAuthorityTrust = new CultMeshAuthorityTrustPolicy(
            CultMeshAuthorityTrustMode.AuthenticatedRemote);

        private EveUnityCultMeshLiveProviderTransport? _transport;
        private EveUnitySceneLiveProviderBridge? _bridge;
        private PreviousProvider? _previousProvider;
        private Task? _preparation;
        private CancellationTokenSource? _preparationLifetime;
        private readonly ConcurrentQueue<EntityViewLease> _pendingEntityViews = new ConcurrentQueue<EntityViewLease>();
        private readonly ConcurrentQueue<EveFieldsSplatsDocument> _pendingFields = new ConcurrentQueue<EveFieldsSplatsDocument>();

        public event Action<EveEntitySoaViewDocument, ICultMeshBodyReadLease>? EntityViewAvailable;

        public event Action<EveFieldsSplatsDocument>? FieldsSplatsAvailable;

        public EveUnityCultMeshProviderSelection? Selection { get; private set; }

        public string SinkKind => Bridge.SinkKind;

        public EveUnitySceneProviderSurfaceDocument CurrentDocument => Bridge.CurrentSurfaceDocument;

        public EveInputCapabilityDocument CurrentInputCapability
        {
            get
            {
                RequirePrepared();
                return _transport!.CurrentInputCapability;
            }
        }

        public CultMeshBodyTransportKind? CurrentAssetBodyTransportKind
        {
            get
            {
                RequirePrepared();
                return _transport!.CurrentAssetBodyTransportKind;
            }
        }

        public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable;

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;

        public void Configure(
            string endpoint,
            string localCacheDirectory = "",
            string providerId = "",
            string surfaceId = "",
            string verseId = "",
            string requiredSurfaceKind = "interactive-world",
            string clientRuntimeId = "eve-unity",
            CultMeshAuthorityTrustPolicy? authorityTrust = null,
            CultMeshAuthorityTrustPolicy? navigationAuthorityTrust = null)
        {
            if (_bridge != null || _preparation != null)
                throw new InvalidOperationException("Disconnect the active provider before changing discovery configuration.");

            rendezvousEndpoint = endpoint ?? "";
            cacheDirectory = localCacheDirectory ?? "";
            providerFilter = providerId ?? "";
            surfaceFilter = surfaceId ?? "";
            verseFilter = verseId ?? "";
            surfaceKind = string.IsNullOrWhiteSpace(requiredSurfaceKind) ? "interactive-world" : requiredSurfaceKind;
            runtimeId = string.IsNullOrWhiteSpace(clientRuntimeId) ? "eve-unity" : clientRuntimeId;
            _authorityTrust = authorityTrust ?? new CultMeshAuthorityTrustPolicy(
                CultMeshAuthorityTrustMode.AuthenticatedRemote);
            _navigationAuthorityTrust = navigationAuthorityTrust ?? _authorityTrust;
        }

        public void Connect()
        {
            Bridge.Connect();
        }

        public Task PrepareAsync()
        {
            if (_bridge != null)
                return Task.CompletedTask;
            if (_preparation == null || _preparation.IsCanceled || _preparation.IsFaulted)
            {
                _preparationLifetime?.Dispose();
                _preparationLifetime = new CancellationTokenSource();
                _preparation = PrepareTransportAsync(_preparationLifetime.Token);
            }
            return _preparation;
        }

        public void Disconnect()
        {
            _bridge?.Disconnect();
        }

        public async Task NavigateAsync(EveUnitySceneNavigationTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(target.VerseId))
                throw new ArgumentException("Eve provider navigation requires a stable Verse identity.", nameof(target));
            if (string.IsNullOrWhiteSpace(target.SurfaceId))
                throw new ArgumentException("Eve provider navigation requires a surface identity.", nameof(target));

            var endpoints = (target.RendezvousEndpoints ?? Array.Empty<string>())
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Select(endpoint => endpoint.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (endpoints.Length == 0 && !string.IsNullOrWhiteSpace(rendezvousEndpoint))
                endpoints = new[] { rendezvousEndpoint };
            if (endpoints.Length == 0)
                throw new InvalidOperationException("Eve provider navigation requires at least one Odin rendezvous endpoint.");

            var requiredSurfaceKind = string.IsNullOrWhiteSpace(target.SurfaceKind)
                ? "interactive-world"
                : target.SurfaceKind;
            var prepared = await PrepareCandidateAsync(
                endpoints,
                target.ProviderId,
                target.SurfaceId,
                requiredSurfaceKind,
                target.VerseId,
                _navigationAuthorityTrust,
                CancellationToken.None).ConfigureAwait(false);

            StagePrepared(prepared);
            rendezvousEndpoint = prepared.Selection.RendezvousEndpoint;
            verseFilter = target.VerseId;
            providerFilter = target.ProviderId;
            surfaceFilter = target.SurfaceId;
            surfaceKind = requiredSurfaceKind;
            _authorityTrust = _navigationAuthorityTrust;
            _preparation = Task.CompletedTask;
        }

        public void CommitNavigation()
        {
            var previous = _previousProvider;
            if (previous == null) return;
            _previousProvider = null;
            if (previous.Bridge != null)
                previous.Bridge.ReceiptAvailable -= ForwardReceipt;
            previous.Bridge?.Dispose();
            previous.Transport?.Dispose();
        }

        public void RollbackNavigation()
        {
            var previous = _previousProvider;
            if (previous == null) return;
            _previousProvider = null;
            if (previous.Bridge != null)
                previous.Bridge.ReceiptAvailable -= ForwardReceipt;
            DetachPrepared(_transport, _bridge);
            _bridge?.Dispose();
            _transport?.Dispose();
            while (_pendingEntityViews.TryDequeue(out var pending)) pending.Lease.Dispose();
            while (_pendingFields.TryDequeue(out _)) { }

            Selection = previous.Selection;
            _transport = previous.Transport;
            _bridge = previous.Bridge;
            rendezvousEndpoint = previous.RendezvousEndpoint;
            verseFilter = previous.VerseFilter;
            providerFilter = previous.ProviderFilter;
            surfaceFilter = previous.SurfaceFilter;
            surfaceKind = previous.SurfaceKind;
            _authorityTrust = previous.AuthorityTrust;
            AttachPrepared(_transport, _bridge);
            _preparation = Task.CompletedTask;
        }

        public void Refresh()
        {
            var activeBridge = _previousProvider?.Bridge ?? Bridge;
            if (activeBridge.IsConnected)
                activeBridge.Refresh();
            else
                activeBridge.Connect();
        }

        public void Submit(EveSurfaceCommandRequest request)
        {
            // Navigation preparation is not a command-routing commit.  The mounted
            // presentation continues to target its old provider until the visual
            // candidate has been lowered and accepted by the bootstrap.
            (_previousProvider?.Bridge ?? Bridge).Submit(request);
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            RequirePrepared();
            return _transport!.ResolvePrefab(asset);
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            RequirePrepared();
            return _transport!.ResolveAsset(asset, assetType);
        }

        public bool TryResolveAssetMetadata(
            EveUnityPlayableWorldAssetBinding asset,
            out IReadOnlyDictionary<string, string> metadata)
        {
            RequirePrepared();
            return _transport!.TryResolveAssetMetadata(asset, out metadata);
        }

        public bool TryGetRenderChannelLayer(string channel, out int layer)
        {
            RequirePrepared();
            return _transport!.TryGetRenderChannelLayer(channel, out layer);
        }

        private void OnDestroy()
        {
            ReleaseTransport();
        }

        private void ReleaseTransport()
        {
            _preparationLifetime?.Cancel();
            if (_bridge != null)
            {
                _bridge.DocumentAvailable -= ForwardDocument;
                _bridge.ReceiptAvailable -= ForwardReceipt;
            }
            _bridge?.Dispose();
            _transport?.Dispose();
            _previousProvider?.Bridge?.Dispose();
            _previousProvider?.Transport?.Dispose();
            _previousProvider = null;
            while (_pendingEntityViews.TryDequeue(out var pending)) pending.Lease.Dispose();
            while (_pendingFields.TryDequeue(out _)) { }
            _bridge = null;
            _transport = null;
            _preparation = null;
            _preparationLifetime?.Dispose();
            _preparationLifetime = null;
            Selection = null;
        }

        private void Update()
        {
            _transport?.PumpLiveEvents();
            EveFieldsSplatsDocument? latestFields = null;
            while (_pendingFields.TryDequeue(out var fields))
                latestFields = fields;
            if (latestFields != null)
                FieldsSplatsAvailable?.Invoke(latestFields);
            EntityViewLease? latest = null;
            while (_pendingEntityViews.TryDequeue(out var next))
            {
                latest?.Lease.Dispose();
                latest = next;
            }
            if (latest == null) return;
            var handler = EntityViewAvailable;
            if (handler == null) latest.Lease.Dispose();
            else handler(latest.Document, latest.Lease);
        }

        private EveUnitySceneLiveProviderBridge Bridge
        {
            get
            {
                RequirePrepared();
                return _bridge!;
            }
        }

        private async Task PrepareTransportAsync(CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            if (_transport != null)
                return;
            if (string.IsNullOrWhiteSpace(rendezvousEndpoint))
                throw new InvalidOperationException("EveUnity requires a CultMesh rendezvous endpoint.");

            var prepared = await PrepareCandidateAsync(
                new[] { rendezvousEndpoint },
                providerFilter,
                surfaceFilter,
                surfaceKind,
                verseFilter,
                _authorityTrust,
                cancellationToken).ConfigureAwait(false);
            TraceStartup($"discovery and transport preparation {elapsed.Elapsed.TotalMilliseconds:0.###}ms");
            AdoptPrepared(prepared);
        }

        private async Task<PreparedProvider> PrepareCandidateAsync(
            IReadOnlyList<string> endpoints,
            string requiredProviderId,
            string requiredSurfaceId,
            string requiredSurfaceKind,
            string requiredVerseId,
            CultMeshAuthorityTrustPolicy trust,
            CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var selection = await new EveUnityCultMeshProviderDiscovery(trust).DiscoverAsync(
                        endpoint,
                        requiredProviderId,
                        requiredSurfaceId,
                        requiredSurfaceKind,
                        requiredVerseId,
                        cancellationToken).ConfigureAwait(false);
                    var resolvedCachePath = string.IsNullOrWhiteSpace(cacheDirectory)
                        ? Path.Combine(Application.temporaryCachePath, $"eve-unity-{GetInstanceID()}")
                        : cacheDirectory;
                    var transport = new EveUnityCultMeshLiveProviderTransport(
                        resolvedCachePath,
                        selection.RendezvousEndpoint,
                        selection.VerseId,
                        selection.AuthorityRuntimeId,
                        selection.ProviderId,
                        selection.SurfaceId,
                        runtimeId,
                        authorityTrust: trust);
                    try
                    {
                        await transport.PrepareAsync(cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        return new PreparedProvider(selection, transport, new EveUnitySceneLiveProviderBridge(transport));
                    }
                    catch
                    {
                        transport.Dispose();
                        throw;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add($"{endpoint}: {error.Message}");
                }
            }

            throw new InvalidOperationException(
                $"No configured Odin endpoint prepared Verse '{requiredVerseId}' surface '{requiredSurfaceId}'. " +
                string.Join(" | ", failures));
        }

        private void AdoptPrepared(PreparedProvider prepared)
        {
            if (_previousProvider != null)
                throw new InvalidOperationException("A staged Eve provider navigation must be committed or rolled back first.");
            var oldBridge = _bridge;
            var oldTransport = _transport;
            DetachPrepared(oldTransport, oldBridge);

            Selection = prepared.Selection;
            _transport = prepared.Transport;
            _bridge = prepared.Bridge;
            AttachPrepared(_transport, _bridge);

            while (_pendingEntityViews.TryDequeue(out var pending)) pending.Lease.Dispose();
            while (_pendingFields.TryDequeue(out _)) { }
            oldBridge?.Dispose();
            oldTransport?.Dispose();
        }

        private void StagePrepared(PreparedProvider prepared)
        {
            if (_previousProvider != null)
            {
                prepared.Bridge.Dispose();
                prepared.Transport.Dispose();
                throw new InvalidOperationException("A staged Eve provider navigation must be committed or rolled back first.");
            }

            _previousProvider = new PreviousProvider(
                Selection,
                _transport,
                _bridge,
                rendezvousEndpoint,
                verseFilter,
                providerFilter,
                surfaceFilter,
                surfaceKind,
                _authorityTrust);
            DetachPrepared(_transport, _bridge);
            if (_previousProvider.Bridge != null)
                _previousProvider.Bridge.ReceiptAvailable += ForwardReceipt;
            Selection = prepared.Selection;
            _transport = prepared.Transport;
            _bridge = prepared.Bridge;
            AttachPrepared(_transport, _bridge);
            while (_pendingEntityViews.TryDequeue(out var pending)) pending.Lease.Dispose();
            while (_pendingFields.TryDequeue(out _)) { }
        }

        private void AttachPrepared(
            EveUnityCultMeshLiveProviderTransport? transport,
            EveUnitySceneLiveProviderBridge? bridge)
        {
            if (transport != null)
            {
                transport.EntityViewAvailable += QueueEntityView;
                transport.FieldsSplatsAvailable += QueueFields;
            }
            if (bridge != null)
            {
                bridge.DocumentAvailable += ForwardDocument;
                bridge.ReceiptAvailable += ForwardReceipt;
            }
        }

        private void DetachPrepared(
            EveUnityCultMeshLiveProviderTransport? transport,
            EveUnitySceneLiveProviderBridge? bridge)
        {
            if (transport != null)
            {
                transport.EntityViewAvailable -= QueueEntityView;
                transport.FieldsSplatsAvailable -= QueueFields;
            }
            if (bridge == null) return;
            bridge.DocumentAvailable -= ForwardDocument;
            bridge.ReceiptAvailable -= ForwardReceipt;
        }

        private void QueueEntityView(EveEntitySoaViewDocument view, ICultMeshBodyReadLease lease) =>
            _pendingEntityViews.Enqueue(new EntityViewLease(view, lease));

        private void QueueFields(EveFieldsSplatsDocument fields) => _pendingFields.Enqueue(fields);

        private void ForwardDocument(EveUnitySceneProviderSurfaceDocument document) =>
            DocumentAvailable?.Invoke(document);

        private void ForwardReceipt(EveUnitySceneCommandReceipt receipt) =>
            ReceiptAvailable?.Invoke(receipt);

        private static void TraceStartup(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("EVEUNITY_TRACE_STARTUP_PHASES"), "1", StringComparison.Ordinal))
                Debug.Log($"EveUnity startup phase {message}.");
        }

        private void RequirePrepared()
        {
            if (_transport == null || _bridge == null)
                throw new InvalidOperationException(
                    "The EveUnity CultMesh provider is not prepared. Await PrepareAsync before mounting the playable world.");
        }

        private sealed class EntityViewLease
        {
            public EntityViewLease(EveEntitySoaViewDocument document, ICultMeshBodyReadLease lease)
            {
                Document = document;
                Lease = lease;
            }

            public EveEntitySoaViewDocument Document { get; }
            public ICultMeshBodyReadLease Lease { get; }
        }

        private sealed record PreparedProvider(
            EveUnityCultMeshProviderSelection Selection,
            EveUnityCultMeshLiveProviderTransport Transport,
            EveUnitySceneLiveProviderBridge Bridge);

        private sealed record PreviousProvider(
            EveUnityCultMeshProviderSelection? Selection,
            EveUnityCultMeshLiveProviderTransport? Transport,
            EveUnitySceneLiveProviderBridge? Bridge,
            string RendezvousEndpoint,
            string VerseFilter,
            string ProviderFilter,
            string SurfaceFilter,
            string SurfaceKind,
            CultMeshAuthorityTrustPolicy AuthorityTrust);
    }
}
