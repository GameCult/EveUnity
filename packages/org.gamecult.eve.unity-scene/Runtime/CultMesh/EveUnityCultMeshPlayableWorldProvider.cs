using System;
using System.IO;
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
        IEveUnityGameObjectAssetProvider
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
            Bridge.Connect();
            Bridge.Refresh();
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

        private void OnDestroy()
        {
            _bridge?.Dispose();
            _transport?.Dispose();
            _bridge = null;
            _transport = null;
            Selection = null;
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

            Selection = new EveUnityCultMeshProviderDiscovery().Discover(
                rendezvousEndpoint,
                providerFilter,
                surfaceFilter,
                surfaceKind,
                verseFilter);
            var resolvedReplicaPath = string.IsNullOrWhiteSpace(replicaPath)
                ? Path.Combine(Application.temporaryCachePath, $"eve-unity-{GetInstanceID()}.cc")
                : replicaPath;
            _transport = new EveUnityCultMeshLiveProviderTransport(
                resolvedReplicaPath,
                Selection.Endpoint,
                Selection.ProviderId,
                Selection.SurfaceId,
                runtimeId);
            _bridge = new EveUnitySceneLiveProviderBridge(_transport);
        }
    }
}
