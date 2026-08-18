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

        private EveUnityCultMeshLiveProviderTransport? _transport;
        private EveUnitySceneLiveProviderBridge? _bridge;
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
            CultMeshAuthorityTrustPolicy? authorityTrust = null)
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

            ReleaseTransport();
            var navigationEndpoint = target.RendezvousEndpoints
                .FirstOrDefault(endpoint => !string.IsNullOrWhiteSpace(endpoint));
            if (!string.IsNullOrWhiteSpace(navigationEndpoint))
                rendezvousEndpoint = navigationEndpoint;
            verseFilter = target.VerseId;
            providerFilter = target.ProviderId;
            surfaceFilter = target.SurfaceId;
            surfaceKind = string.IsNullOrWhiteSpace(target.SurfaceKind) ? "interactive-world" : target.SurfaceKind;
            await PrepareAsync();
        }

        public void Refresh()
        {
            if (Bridge.IsConnected)
                Bridge.Refresh();
            else
                Bridge.Connect();
        }

        public void Submit(EveSurfaceCommandRequest request)
        {
            Bridge.Submit(request);
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

            Selection = await new EveUnityCultMeshProviderDiscovery(_authorityTrust).DiscoverAsync(
                rendezvousEndpoint,
                providerFilter,
                surfaceFilter,
                surfaceKind,
                verseFilter,
                cancellationToken);
            TraceStartup($"discovery {elapsed.Elapsed.TotalMilliseconds:0.###}ms");
            elapsed.Restart();
            var resolvedCachePath = string.IsNullOrWhiteSpace(cacheDirectory)
                ? Path.Combine(Application.temporaryCachePath, $"eve-unity-{GetInstanceID()}")
                : cacheDirectory;
            var transport = new EveUnityCultMeshLiveProviderTransport(
                resolvedCachePath,
                Selection.RendezvousEndpoint,
                Selection.VerseId,
                Selection.AuthorityRuntimeId,
                Selection.ProviderId,
                Selection.SurfaceId,
                runtimeId,
                authorityTrust: _authorityTrust);
            try
            {
                await transport.PrepareAsync(cancellationToken);
            }
            catch
            {
                transport.Dispose();
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            _transport = transport;
            _transport.EntityViewAvailable += (view, lease) => _pendingEntityViews.Enqueue(new EntityViewLease(view, lease));
            _transport.FieldsSplatsAvailable += fields => _pendingFields.Enqueue(fields);
            _bridge = new EveUnitySceneLiveProviderBridge(_transport);
            _bridge.DocumentAvailable += ForwardDocument;
            _bridge.ReceiptAvailable += ForwardReceipt;
            TraceStartup($"transport-construction {elapsed.Elapsed.TotalMilliseconds:0.###}ms");
        }

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
    }
}
