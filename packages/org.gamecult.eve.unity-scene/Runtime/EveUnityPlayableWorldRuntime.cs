using System;
using GameCult.Eve.Surface;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldRuntime : IDisposable
    {
        private readonly EveUnitySceneProviderSurfaceDocumentSource _surfaceSource;
        private readonly EveUnitySceneProviderConnection _connection;
        private readonly EveUnityPlayableWorldLiveClient _client;
        private readonly EveUnityPlayableWorldAssetManifestDocumentSource? _assetManifestSource;
        private readonly IEveUnityEntitySoaViewDocumentSource? _entityViews;
        private readonly EveUnityEntitySoaPresenter _entityPresenter;
        private readonly IEveUnityPresentedEntityRegistry? _presentedEntities;
        private bool _entityViewsConnected;
        private bool _assetManifestConnected;

        public EveUnityPlayableWorldRuntime(
            IEveUnitySceneProviderSurfaceDocumentSource surfaceDocuments,
            IEveUnitySceneCommandSink commandSink,
            IEveUnityPlayableWorldSceneSink sceneSink,
            IEveUnityPlayableWorldAssetManifestDocumentSource? assetManifestDocuments = null,
            IEveUnitySceneCommandReceiptSource? receiptSource = null,
            IEveUnityPlayableWorldAssetResolver? assetResolver = null,
            EveUnityPlayableWorldAssetManifestCache? assetManifests = null)
        {
            if (surfaceDocuments == null) throw new ArgumentNullException(nameof(surfaceDocuments));
            if (commandSink == null) throw new ArgumentNullException(nameof(commandSink));
            if (sceneSink == null) throw new ArgumentNullException(nameof(sceneSink));

            AssetManifests = assetManifests ?? new EveUnityPlayableWorldAssetManifestCache();
            _surfaceSource = new EveUnitySceneProviderSurfaceDocumentSource(surfaceDocuments);
            _entityViews = surfaceDocuments as IEveUnityEntitySoaViewDocumentSource;
            _presentedEntities = sceneSink as IEveUnityPresentedEntityRegistry;
            _entityPresenter = new EveUnityEntitySoaPresenter(sceneSink);
            _connection = new EveUnitySceneProviderConnection(_surfaceSource, commandSink);
            _client = new EveUnityPlayableWorldLiveClient(
                _connection,
                new EveUnityPlayableWorldPresenter(sceneSink, assetResolver ?? new EveUnityAssetRefResolver()),
                receiptSource);

            if (assetManifestDocuments != null)
                _assetManifestSource = new EveUnityPlayableWorldAssetManifestDocumentSource(assetManifestDocuments);
        }

        public EveUnityPlayableWorldAssetManifestCache AssetManifests { get; }

        public IEveUnityGameObjectAssetProvider? GameObjectAssetProvider { get; private set; }
        public IEveUnityPresentedEntityRegistry? PresentedEntities => _presentedEntities;

        public EveUnityPlayableWorldProjection? ActiveWorld => _client.ActiveWorld;

        public EveUnitySceneProjection? ActiveProjection => _client.ActiveProjection;

        public EveUnityPlayableWorldPresentation? LastPresentation => _client.LastPresentation;

        public EveUnitySceneCommandReceipt? LastReceipt => _client.LastReceipt;

        public long ActiveVersion => _client.ActiveVersion;

        public string SourcePointer => _client.SourcePointer;

        public event Action<EveUnitySceneCommandReceipt>? ReceiptAvailable
        {
            add { _client.ReceiptAvailable += value; }
            remove { _client.ReceiptAvailable -= value; }
        }

        public event Action<EveUnityFeedbackEvent>? FeedbackAvailable
        {
            add { _client.FeedbackAvailable += value; }
            remove { _client.FeedbackAvailable -= value; }
        }

        public event Action<EveUnityShotReceipt>? ShotAvailable
        {
            add { _client.ShotAvailable += value; }
            remove { _client.ShotAvailable -= value; }
        }

        public static EveUnityPlayableWorldRuntime CreateForGameObjectScene(
            Transform root,
            IEveUnitySceneProviderSurfaceDocumentSource surfaceDocuments,
            IEveUnitySceneCommandSink commandSink,
            IEveUnityPlayableWorldAssetManifestDocumentSource? assetManifestDocuments = null,
            IEveUnitySceneCommandReceiptSource? receiptSource = null,
            IEveUnityGameObjectAssetProvider? fallbackAssetProvider = null)
        {
            EveUnityPlayableWorldRuntime? runtime = null;
            var assetManifests = new EveUnityPlayableWorldAssetManifestCache();
            var liveAssetProvider = new EveUnityLivePlayableWorldAssetProvider(
                assetManifests,
                () => runtime?.ActiveWorld,
                fallbackAssetProvider ?? new EveUnityResourcesAssetProvider());
            var sceneSink = new EveUnityGameObjectPlayableWorldSceneSink(root, liveAssetProvider);

            runtime = new EveUnityPlayableWorldRuntime(
                surfaceDocuments,
                commandSink,
                sceneSink,
                assetManifestDocuments,
                receiptSource,
                new EveUnityAssetRefResolver(),
                assetManifests);
            runtime.GameObjectAssetProvider = liveAssetProvider;
            return runtime;
        }

        public EveUnityPlayableWorldPresentation Connect()
        {
            EnsureAssetManifestConnected();
            var presentation = _client.Connect();
            EnsureEntityViewsConnected();
            return presentation;
        }

        public EveUnityPlayableWorldPresentation Refresh()
        {
            EnsureAssetManifestConnected();
            return _client.Refresh();
        }

        public EveSurfaceCommandRequest SubmitMoveIntent(
            string entityId,
            float targetX,
            float targetY,
            float targetZ,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitMoveIntent(entityId, targetX, targetY, targetZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitMoveVectorIntent(
            string entityId,
            float directionX,
            float directionY,
            float scalarValue = 1f,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitMoveVectorIntent(entityId, directionX, directionY, scalarValue, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitLookDirectionIntent(
            string entityId,
            float directionX,
            float directionY,
            float directionZ,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitLookDirectionIntent(entityId, directionX, directionY, directionZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitFocusIntent(
            string entityId,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitFocusIntent(entityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitTargetIntent(
            string sourceEntityId,
            string targetEntityId,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitTargetIntent(sourceEntityId, targetEntityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitActionIntent(
            string entityId,
            string actionId,
            DateTimeOffset? issuedAt = null)
        {
            return _client.SubmitActionIntent(entityId, actionId, issuedAt);
        }

        public void Disconnect()
        {
            if (_entityViewsConnected && _entityViews != null)
            {
                _entityViews.EntityViewAvailable -= OnEntityViewAvailable;
                _entityViewsConnected = false;
            }
            _client.Disconnect();

            if (_assetManifestConnected && _assetManifestSource != null)
            {
                AssetManifests.Disconnect(_assetManifestSource);
                _assetManifestSource.Disconnect();
                _assetManifestConnected = false;
            }
        }

        public void Dispose()
        {
            Disconnect();
            _client.Dispose();
            _assetManifestSource?.Dispose();
        }

        private void EnsureAssetManifestConnected()
        {
            if (_assetManifestConnected || _assetManifestSource == null)
                return;

            _assetManifestSource.Connect();
            AssetManifests.Connect(_assetManifestSource);
            _assetManifestConnected = true;
        }

        private void EnsureEntityViewsConnected()
        {
            if (_entityViewsConnected || _entityViews == null) return;
            _entityViews.EntityViewAvailable += OnEntityViewAvailable;
            _entityViewsConnected = true;
        }

        private void OnEntityViewAvailable(EveEntitySoaViewDocument document, GameCult.Mesh.ICultMeshBodyReadLease lease) =>
            _entityPresenter.Apply(document, lease);
    }

    public sealed class EveUnityLivePlayableWorldAssetProvider : IEveUnityNativeAssetProvider
    {
        private readonly EveUnityPlayableWorldAssetManifestCache _assetManifests;
        private readonly Func<EveUnityPlayableWorldProjection?> _activeWorld;
        private readonly IEveUnityGameObjectAssetProvider _fallback;

        public EveUnityLivePlayableWorldAssetProvider(
            EveUnityPlayableWorldAssetManifestCache assetManifests,
            Func<EveUnityPlayableWorldProjection?> activeWorld,
            IEveUnityGameObjectAssetProvider fallback)
        {
            _assetManifests = assetManifests ?? throw new ArgumentNullException(nameof(assetManifests));
            _activeWorld = activeWorld ?? throw new ArgumentNullException(nameof(activeWorld));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public GameObject? ResolvePrefab(EveUnityPlayableWorldAssetBinding asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            var world = _activeWorld();
            var manifest = world == null ? null : _assetManifests.GetForWorld(world);
            if (manifest != null)
            {
                var provider = new EveUnityManifestGameObjectAssetProvider(manifest, _fallback);
                var prefab = provider.ResolvePrefab(asset);
                if (prefab != null)
                    return prefab;
            }

            return _fallback.ResolvePrefab(asset);
        }

        public UnityEngine.Object? ResolveAsset(EveUnityPlayableWorldAssetBinding asset, Type assetType)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            var world = _activeWorld();
            var manifest = world == null ? null : _assetManifests.GetForWorld(world);
            if (manifest != null)
            {
                var provider = new EveUnityManifestGameObjectAssetProvider(manifest, _fallback);
                var value = provider.ResolveAsset(asset, assetType);
                if (value != null) return value;
            }
            return (_fallback as IEveUnityNativeAssetProvider)?.ResolveAsset(asset, assetType);
        }
    }
}
