using System;
using GameCult.Eve.Surface;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnitySceneLiveProviderTransport
    {
        string TransportKind { get; }

        string SurfacePointer { get; }

        string AssetManifestPointer { get; }

        EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; }

        EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; }

        event Action<EveUnitySceneProviderSurfaceDocument> SurfaceDocumentAvailable;

        event Action<EveUnityPlayableWorldAssetManifestDocument> AssetManifestDocumentAvailable;

        event Action<EveUnitySceneCommandReceipt> CommandReceiptAvailable;

        void Connect();

        void Disconnect();

        void Refresh();

        void SubmitCommand(EveSurfaceCommandRequest request);
    }

    public abstract class EveUnitySceneLiveProviderTransportBehaviour :
        MonoBehaviour,
        IEveUnitySceneLiveProviderTransport
    {
        public abstract string TransportKind { get; }

        public abstract string SurfacePointer { get; }

        public abstract string AssetManifestPointer { get; }

        public abstract EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; }

        public abstract EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; }

        public abstract event Action<EveUnitySceneProviderSurfaceDocument> SurfaceDocumentAvailable;

        public abstract event Action<EveUnityPlayableWorldAssetManifestDocument> AssetManifestDocumentAvailable;

        public abstract event Action<EveUnitySceneCommandReceipt> CommandReceiptAvailable;

        public abstract void Connect();

        public abstract void Disconnect();

        public abstract void Refresh();

        public abstract void SubmitCommand(EveSurfaceCommandRequest request);
    }

    public sealed class EveUnitySceneLiveProviderBridgeComponent :
        MonoBehaviour,
        IEveUnitySceneProviderSurfaceDocumentSource,
        IEveUnitySceneProviderSurfaceDocumentConnection,
        IEveUnityPlayableWorldAssetManifestDocumentSource,
        IEveUnitySceneCommandSink,
        IEveUnitySceneCommandReceiptSource,
        IEveUnityProviderRefreshSource
    {
        [SerializeField] private MonoBehaviour? transportBehaviour;

        private EveUnitySceneLiveProviderBridge? _bridge;
        private IEveUnitySceneLiveProviderTransport? _configuredTransport;

        public string TransportKind => Bridge.TransportKind;

        public string SurfacePointer => Bridge.SurfacePointer;

        public string AssetManifestPointer => Bridge.AssetManifestPointer;

        public string SinkKind => Bridge.SinkKind;

        public string ManifestRef => Bridge.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument => Bridge.CurrentSurfaceDocument;

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument => Bridge.CurrentAssetManifestDocument;

        EveUnitySceneProviderSurfaceDocument IEveUnitySceneProviderSurfaceDocumentSource.CurrentDocument =>
            Bridge.CurrentSurfaceDocument;

        EveUnityPlayableWorldAssetManifestDocument IEveUnityPlayableWorldAssetManifestDocumentSource.CurrentDocument =>
            Bridge.CurrentAssetManifestDocument;

        public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable
        {
            add { Bridge.DocumentAvailable += value; }
            remove { Bridge.DocumentAvailable -= value; }
        }

        public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable
        {
            add { Bridge.AssetManifestDocumentAvailable += value; }
            remove { Bridge.AssetManifestDocumentAvailable -= value; }
        }

        event Action<EveUnityPlayableWorldAssetManifestDocument> IEveUnityPlayableWorldAssetManifestDocumentSource.DocumentAvailable
        {
            add { Bridge.AssetManifestDocumentAvailable += value; }
            remove { Bridge.AssetManifestDocumentAvailable -= value; }
        }

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable
        {
            add { Bridge.ReceiptAvailable += value; }
            remove { Bridge.ReceiptAvailable -= value; }
        }

        public void Configure(IEveUnitySceneLiveProviderTransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            _bridge?.Dispose();
            _configuredTransport = transport;
            _bridge = new EveUnitySceneLiveProviderBridge(transport);
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

        private void OnDisable()
        {
            Disconnect();
        }

        private void OnDestroy()
        {
            _bridge?.Dispose();
            _bridge = null;
        }

        private EveUnitySceneLiveProviderBridge Bridge
        {
            get
            {
                if (_bridge != null)
                    return _bridge;

                var transport = _configuredTransport ?? transportBehaviour as IEveUnitySceneLiveProviderTransport;
                if (transport == null)
                    throw new InvalidOperationException(
                        "Eve Unity scene live provider bridge requires a transport implementing IEveUnitySceneLiveProviderTransport.");

                _bridge = new EveUnitySceneLiveProviderBridge(transport);
                return _bridge;
            }
        }
    }

    public sealed class EveUnitySceneLiveProviderBridge :
        IEveUnitySceneProviderSurfaceDocumentSource,
        IEveUnitySceneProviderSurfaceDocumentConnection,
        IEveUnityPlayableWorldAssetManifestDocumentSource,
        IEveUnitySceneCommandSink,
        IEveUnitySceneCommandReceiptSource,
        IEveUnityProviderRefreshSource,
        IDisposable
    {
        private readonly IEveUnitySceneLiveProviderTransport _transport;
        private bool _connected;

        public EveUnitySceneLiveProviderBridge(IEveUnitySceneLiveProviderTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            CurrentSurfaceDocument = _transport.CurrentSurfaceDocument;
            CurrentAssetManifestDocument = _transport.CurrentAssetManifestDocument;
        }

        public string TransportKind => _transport.TransportKind;

        public string SurfacePointer => _transport.SurfacePointer;

        public string AssetManifestPointer => _transport.AssetManifestPointer;

        public string SinkKind => _transport.TransportKind;

        public string ManifestRef => CurrentAssetManifestDocument.ManifestRef;

        public EveUnitySceneProviderSurfaceDocument CurrentSurfaceDocument { get; private set; }

        public EveUnityPlayableWorldAssetManifestDocument CurrentAssetManifestDocument { get; private set; }

        public bool IsConnected => _connected;

        EveUnitySceneProviderSurfaceDocument IEveUnitySceneProviderSurfaceDocumentSource.CurrentDocument =>
            CurrentSurfaceDocument;

        EveUnityPlayableWorldAssetManifestDocument IEveUnityPlayableWorldAssetManifestDocumentSource.CurrentDocument =>
            CurrentAssetManifestDocument;

        public event Action<EveUnitySceneProviderSurfaceDocument>? DocumentAvailable;

        public event Action<EveUnityPlayableWorldAssetManifestDocument>? AssetManifestDocumentAvailable;

        event Action<EveUnityPlayableWorldAssetManifestDocument> IEveUnityPlayableWorldAssetManifestDocumentSource.DocumentAvailable
        {
            add { AssetManifestDocumentAvailable += value; }
            remove { AssetManifestDocumentAvailable -= value; }
        }

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable;

        public void Connect()
        {
            if (_connected)
                return;

            _transport.SurfaceDocumentAvailable += OnSurfaceDocumentAvailable;
            _transport.AssetManifestDocumentAvailable += OnAssetManifestDocumentAvailable;
            _transport.CommandReceiptAvailable += OnCommandReceiptAvailable;
            _transport.Connect();
            _connected = true;
        }

        public void Disconnect()
        {
            if (!_connected)
                return;

            _transport.SurfaceDocumentAvailable -= OnSurfaceDocumentAvailable;
            _transport.AssetManifestDocumentAvailable -= OnAssetManifestDocumentAvailable;
            _transport.CommandReceiptAvailable -= OnCommandReceiptAvailable;
            _transport.Disconnect();
            _connected = false;
        }

        public void Refresh()
        {
            _transport.Refresh();
        }

        public void Submit(EveSurfaceCommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            _transport.SubmitCommand(request);
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void OnSurfaceDocumentAvailable(EveUnitySceneProviderSurfaceDocument document)
        {
            CurrentSurfaceDocument = document ?? throw new ArgumentNullException(nameof(document));
            DocumentAvailable?.Invoke(CurrentSurfaceDocument);
        }

        private void OnAssetManifestDocumentAvailable(EveUnityPlayableWorldAssetManifestDocument document)
        {
            CurrentAssetManifestDocument = document ?? throw new ArgumentNullException(nameof(document));
            AssetManifestDocumentAvailable?.Invoke(CurrentAssetManifestDocument);
        }

        private void OnCommandReceiptAvailable(EveUnitySceneCommandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            ReceiptAvailable?.Invoke(receipt);
        }
    }
}
