using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using GameCult.Eve.Surface;
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
        IEveUnityInputCapabilityDocumentSource,
        IEveUnityCameraRenderPolicySource,
        IEveUnityNativeAssetProvider
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
        private Task<EveUnityCultMeshProviderSelection>? _discovery;
        private readonly ConcurrentQueue<EveEntitySoaViewDocument> _pendingEntityViews = new ConcurrentQueue<EveEntitySoaViewDocument>();

        public EveEntitySoaViewDocument? CurrentEntityView { get; private set; }

        public EveInputCapabilityDocument? CurrentInputCapability => _transport?.CurrentInputCapability;

        public event Action<EveEntitySoaViewDocument>? EntityViewAvailable;

        public EveUnityCultMeshProviderSelection? Selection { get; private set; }

        public string SinkKind => Bridge.SinkKind;

        public EveUnitySceneProviderSurfaceDocument CurrentDocument => Bridge.CurrentSurfaceDocument;

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
            if (_bridge != null)
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
            _transport?.PumpLiveEvents();
            DrainEntityViews();
        }

        public void Submit(EveSurfaceCommandRequest request)
        {
            Bridge.Submit(request);
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            EnsureTransport();
            return _transport!.ResolvePrefab(asset);
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            EnsureTransport();
            return _transport!.ResolveAsset(asset, assetType);
        }

        public bool TryGetCameraCullingMask(string viewId, out int cullingMask)
        {
            EnsureTransport();
            return _transport!.TryGetCameraCullingMask(viewId, out cullingMask);
        }

        private void OnDestroy()
        {
            var transportOwnedConnection = _transport != null;
            _bridge?.Dispose();
            _transport?.Dispose();
            _bridge = null;
            _transport = null;
            Selection = null;
            if (!transportOwnedConnection && _discovery?.Status == TaskStatus.RanToCompletion)
                _discovery.Result.Mesh.Dispose();
            _discovery = null;
        }

        private void Update()
        {
            CompleteDiscoveryOnMainThread();
            _transport?.PumpLiveEvents();
            DrainEntityViews();
        }

        private void DrainEntityViews()
        {
            EveEntitySoaViewDocument? latest = null;
            while (_pendingEntityViews.TryDequeue(out var next)) latest = next;
            if (latest == null) return;
            CurrentEntityView = latest;
            EntityViewAvailable?.Invoke(latest);
        }

        private EveUnitySceneLiveProviderBridge Bridge
        {
            get
            {
                EnsureTransport();
                return _bridge!;
            }
        }

        private void EnsureTransport()
        {
            if (_transport != null)
                return;
            if (string.IsNullOrWhiteSpace(rendezvousEndpoint))
                throw new InvalidOperationException("EveUnity requires a CultMesh rendezvous endpoint.");

            if (_discovery == null)
                _discovery = new EveUnityCultMeshProviderDiscovery().DiscoverAsync(
                    rendezvousEndpoint,
                    providerFilter,
                    surfaceFilter,
                    surfaceKind,
                    verseFilter);
            CompleteDiscoveryOnMainThread();
            if (_transport == null)
                throw new InvalidOperationException("EveUnity CultMesh provider discovery is still in progress.");
        }

        private void CompleteDiscoveryOnMainThread()
        {
            if (_transport != null || _discovery == null || !_discovery.IsCompleted)
                return;
            if (_discovery.IsFaulted)
                throw _discovery.Exception?.GetBaseException()
                    ?? new InvalidOperationException("EveUnity CultMesh provider discovery failed.");
            if (_discovery.IsCanceled)
                throw new InvalidOperationException("EveUnity CultMesh provider discovery was cancelled.");

            Selection = _discovery.Result;
            var resolvedReplicaPath = string.IsNullOrWhiteSpace(replicaPath)
                ? Path.Combine(Application.temporaryCachePath, $"eve-unity-{GetInstanceID()}.cc")
                : replicaPath;
            _transport = new EveUnityCultMeshLiveProviderTransport(
                resolvedReplicaPath,
                Selection,
                runtimeId);
            _transport.EntityViewAvailable += view => _pendingEntityViews.Enqueue(view);
            _bridge = new EveUnitySceneLiveProviderBridge(_transport);
        }
    }
}
