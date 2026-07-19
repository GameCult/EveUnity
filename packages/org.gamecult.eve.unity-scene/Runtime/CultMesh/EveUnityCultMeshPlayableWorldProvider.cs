using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using GameCult.Eve.Surface;
using GameCult.Eve.PluginFields;
using GameCult.Eve.UnityScene.Fields;
using GameCult.Mesh;
using UnityEngine;

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
        IEveUnityInputCapabilitySource
    {
        [SerializeField] private string rendezvousEndpoint = "";
        [SerializeField] private string providerFilter = "";
        [SerializeField] private string surfaceFilter = "";
        [SerializeField] private string verseFilter = "";
        [SerializeField] private string surfaceKind = "interactive-world";
        [SerializeField] private string replicaPath = "";
        [SerializeField] private string runtimeId = "eve-unity";

        private EveUnityCultMeshLiveProviderTransport? _transport;
        private EveUnitySceneLiveProviderBridge? _bridge;
        private Task? _preparation;
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

        public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable
        {
            add { Bridge.DocumentAvailable += value; }
            remove { Bridge.DocumentAvailable -= value; }
        }

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable
        {
            add { Bridge.ReceiptAvailable += value; }
            remove { Bridge.ReceiptAvailable -= value; }
        }

        public void Configure(
            string endpoint,
            string replicaStorePath = "",
            string providerId = "",
            string surfaceId = "",
            string verseId = "",
            string requiredSurfaceKind = "interactive-world",
            string clientRuntimeId = "eve-unity")
        {
            if (_bridge != null || _preparation != null)
                throw new InvalidOperationException("Disconnect the active provider before changing discovery configuration.");

            rendezvousEndpoint = endpoint ?? "";
            replicaPath = replicaStorePath ?? "";
            providerFilter = providerId ?? "";
            surfaceFilter = surfaceId ?? "";
            verseFilter = verseId ?? "";
            surfaceKind = string.IsNullOrWhiteSpace(requiredSurfaceKind) ? "interactive-world" : requiredSurfaceKind;
            runtimeId = string.IsNullOrWhiteSpace(clientRuntimeId) ? "eve-unity" : clientRuntimeId;
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
                _preparation = PrepareTransportAsync();
            return _preparation;
        }

        public void Disconnect()
        {
            _bridge?.Disconnect();
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
            _bridge?.Dispose();
            _transport?.Dispose();
            while (_pendingEntityViews.TryDequeue(out var pending)) pending.Lease.Dispose();
            while (_pendingFields.TryDequeue(out _)) { }
            _bridge = null;
            _transport = null;
            _preparation = null;
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

        private async Task PrepareTransportAsync()
        {
            var elapsed = Stopwatch.StartNew();
            if (_transport != null)
                return;
            if (string.IsNullOrWhiteSpace(rendezvousEndpoint))
                throw new InvalidOperationException("EveUnity requires a CultMesh rendezvous endpoint.");

            Selection = await new EveUnityCultMeshProviderDiscovery().DiscoverAsync(
                rendezvousEndpoint,
                providerFilter,
                surfaceFilter,
                surfaceKind,
                verseFilter);
            TraceStartup($"discovery {elapsed.Elapsed.TotalMilliseconds:0.###}ms");
            elapsed.Restart();
            var resolvedReplicaPath = string.IsNullOrWhiteSpace(replicaPath)
                ? Path.Combine(Application.temporaryCachePath, $"eve-unity-{GetInstanceID()}.cc")
                : replicaPath;
            _transport = new EveUnityCultMeshLiveProviderTransport(
                resolvedReplicaPath,
                Selection.Endpoint,
                Selection.ProviderId,
                Selection.SurfaceId,
                runtimeId);
            _transport.EntityViewAvailable += (view, lease) => _pendingEntityViews.Enqueue(new EntityViewLease(view, lease));
            _transport.FieldsSplatsAvailable += fields => _pendingFields.Enqueue(fields);
            _bridge = new EveUnitySceneLiveProviderBridge(_transport);
            TraceStartup($"transport-construction {elapsed.Elapsed.TotalMilliseconds:0.###}ms");
        }

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
