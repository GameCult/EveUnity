using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;
using GameCult.Eve.UnityScene.Fields;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldClientHost : MonoBehaviour
    {
        [SerializeField] private Transform? sceneRoot;
        [SerializeField] private MonoBehaviour? providerSurfaceDocuments;
        [SerializeField] private MonoBehaviour? commandSink;
        [SerializeField] private MonoBehaviour? assetManifestDocuments;
        [SerializeField] private MonoBehaviour? receiptSource;
        [SerializeField] private MonoBehaviour? fallbackAssetProvider;
        [SerializeField] private bool connectOnEnable;
        [SerializeField] private bool refreshInUpdate;
        [SerializeField] private float refreshIntervalSeconds = 1f;
        [SerializeField] private bool renderShotTrajectories = true;

        private float _nextRefreshAt;
        private object? _cameraOwner;

        public EveUnityPlayableWorldRuntime? Runtime { get; private set; }

        public EveUnityPlayableWorldProjection? ActiveWorld => Runtime?.ActiveWorld;

        public EveUnitySceneProjection? ActiveProjection => Runtime?.ActiveProjection;

        public EveUnityPlayableWorldPresentation? LastPresentation => Runtime?.LastPresentation;

        public EveUnitySceneCommandReceipt? LastReceipt => Runtime?.LastReceipt;
        public IEveUnityPresentedEntityRegistry? PresentedEntities => Runtime?.PresentedEntities;

        public EveInputCapabilityDocument? InputCapability =>
            (providerSurfaceDocuments as IEveUnityInputCapabilitySource)?.CurrentInputCapability;

        public long ActiveVersion => Runtime?.ActiveVersion ?? 0;
        public int ConnectionEpoch { get; private set; }
        public Transform? ActiveCameraTransform { get; private set; }
        public IEveUnityNativeAssetProvider? NativeAssetProvider =>
            Runtime?.GameObjectAssetProvider as IEveUnityNativeAssetProvider;

        public event Action<EveUnityFeedbackEvent>? FeedbackAvailable;
        public event Action<EveUnityShotReceipt>? ShotAvailable;

        public Transform SceneRoot => sceneRoot == null ? transform : sceneRoot;

        public bool ConnectOnEnable
        {
            get => connectOnEnable;
            set => connectOnEnable = value;
        }

        public void Configure(
            Transform? sceneRoot,
            MonoBehaviour providerSurfaceDocuments,
            MonoBehaviour commandSink,
            MonoBehaviour? assetManifestDocuments = null,
            MonoBehaviour? receiptSource = null,
            MonoBehaviour? fallbackAssetProvider = null)
        {
            this.sceneRoot = sceneRoot;
            this.providerSurfaceDocuments = providerSurfaceDocuments;
            this.commandSink = commandSink;
            this.assetManifestDocuments = assetManifestDocuments;
            this.receiptSource = receiptSource;
            this.fallbackAssetProvider = fallbackAssetProvider;
        }

        public EveUnityPlayableWorldPresentation Connect()
        {
            Disconnect();
            RefreshProviderSources();

            Runtime = EveUnityPlayableWorldRuntime.CreateForGameObjectScene(
                SceneRoot,
                Required<IEveUnitySceneProviderSurfaceDocumentSource>(
                    providerSurfaceDocuments,
                    nameof(providerSurfaceDocuments)),
                Required<IEveUnitySceneCommandSink>(
                    commandSink,
                    nameof(commandSink)),
                Optional<IEveUnityPlayableWorldAssetManifestDocumentSource>(assetManifestDocuments),
                Optional<IEveUnitySceneCommandReceiptSource>(receiptSource),
                Optional<IEveUnityGameObjectAssetProvider>(fallbackAssetProvider));
            Runtime.FeedbackAvailable += OnFeedbackAvailable;
            Runtime.ShotAvailable += OnShotAvailable;
            if (renderShotTrajectories)
            {
                var trajectories = GetComponent<EveUnityShotTrajectoryRenderer>();
                if (trajectories == null) trajectories = gameObject.AddComponent<EveUnityShotTrajectoryRenderer>();
                trajectories.Bind(this, Runtime.GameObjectAssetProvider);
            }
            var feedbackEffects = GetComponent<EveUnityFeedbackEffectRenderer>();
            if (feedbackEffects == null) feedbackEffects = gameObject.AddComponent<EveUnityFeedbackEffectRenderer>();
            feedbackEffects.Bind(this, Runtime.GameObjectAssetProvider);
            var combatPresentation = GetComponent<EveUnityCombatPresentationRenderer>();
            if (combatPresentation == null) combatPresentation = gameObject.AddComponent<EveUnityCombatPresentationRenderer>();
            combatPresentation.Bind(this);
            var aimPresentation = GetComponent<EveUnityAimPresentationRenderer>();
            if (aimPresentation == null) aimPresentation = gameObject.AddComponent<EveUnityAimPresentationRenderer>();
            aimPresentation.Bind(this);
            var beamPresentation = GetComponent<EveUnityBeamPresentationRenderer>();
            if (beamPresentation == null) beamPresentation = gameObject.AddComponent<EveUnityBeamPresentationRenderer>();
            beamPresentation.Bind(this, Runtime.GameObjectAssetProvider);
            var thermal = GetComponent<EveUnityThermalPresenter>();
            var thermalHud = GetComponent<EveUnityThermalHudSink>();
            if (thermalHud == null) thermalHud = gameObject.AddComponent<EveUnityThermalHudSink>();
            if (thermal == null) thermal = gameObject.AddComponent<EveUnityThermalPresenter>();
            thermal.Bind(this, Runtime.GameObjectAssetProvider as IEveUnityNativeAssetProvider);
            var fieldVolume = GetComponent<EveUnityFieldsVolumeRenderer>();
            if (fieldVolume == null) fieldVolume = gameObject.AddComponent<EveUnityFieldsVolumeRenderer>();
            fieldVolume.Bind(this, providerSurfaceDocuments as IEveUnityFieldsSplatsDocumentSource);

            var presentation = Runtime.Connect();
            ConnectionEpoch++;
            _nextRefreshAt = Time.unscaledTime + Math.Max(0.01f, refreshIntervalSeconds);
            return presentation;
        }

        internal bool TryClaimWorldCamera(object owner, Transform camera)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (_cameraOwner != null && !ReferenceEquals(_cameraOwner, owner))
                return false;
            _cameraOwner = owner;
            ActiveCameraTransform = camera;
            return true;
        }

        internal void ReleaseWorldCamera(object owner)
        {
            if (!ReferenceEquals(_cameraOwner, owner)) return;
            _cameraOwner = null;
            ActiveCameraTransform = null;
        }

        public EveUnityPlayableWorldPresentation Refresh()
        {
            RefreshProviderSources();
            if (Runtime == null)
                return Connect();

            return Runtime.Refresh();
        }

        public EveSurfaceCommandRequest SubmitMoveIntent(
            string entityId,
            float targetX,
            float targetY,
            float targetZ,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitMoveIntent(entityId, targetX, targetY, targetZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitMoveVectorIntent(
            string entityId,
            float directionX,
            float directionY,
            float scalarValue = 1f,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitMoveVectorIntent(entityId, directionX, directionY, scalarValue, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitLookDirectionIntent(
            string entityId,
            float directionX,
            float directionY,
            float directionZ,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitLookDirectionIntent(entityId, directionX, directionY, directionZ, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitFocusIntent(
            string entityId,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitFocusIntent(entityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitTargetIntent(
            string sourceEntityId,
            string targetEntityId,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitTargetIntent(sourceEntityId, targetEntityId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitActionIntent(
            string entityId,
            string actionId,
            DateTimeOffset? issuedAt = null)
        {
            return RequireRuntime().SubmitActionIntent(entityId, actionId, issuedAt);
        }

        public EveSurfaceCommandRequest SubmitAdvertisedActionIntent(
            string entityId,
            string actionId,
            DateTimeOffset? issuedAt = null)
        {
            var action = EveUnityAdvertisedInputAction.Resolve(InputCapability, actionId);
            return RequireRuntime().SubmitCommandIntent(action.Operation, action.BuildPayload(entityId), issuedAt);
        }

        public EveSurfaceCommandRequest SubmitAdvertisedActionValueIntent(
            string entityId,
            string actionId,
            float inputValue,
            DateTimeOffset? issuedAt = null)
        {
            var action = EveUnityAdvertisedInputAction.Resolve(InputCapability, actionId);
            return RequireRuntime().SubmitCommandIntent(
                action.Operation,
                action.BuildPayload(entityId, inputValue),
                issuedAt);
        }

        public void Disconnect()
        {
            if (_cameraOwner is EveUnityPlayableWorldCameraRig rig)
                rig.ReleaseRig();
            if (Runtime != null)
            {
                Runtime.FeedbackAvailable -= OnFeedbackAvailable;
                Runtime.ShotAvailable -= OnShotAvailable;
            }
            Runtime?.Dispose();
            Runtime = null;
            _cameraOwner = null;
            ActiveCameraTransform = null;
        }

        private void OnFeedbackAvailable(EveUnityFeedbackEvent value) => FeedbackAvailable?.Invoke(value);
        private void OnShotAvailable(EveUnityShotReceipt value) => ShotAvailable?.Invoke(value);

        private void OnEnable()
        {
            if (connectOnEnable)
                Connect();
        }

        private void Update()
        {
            if (!refreshInUpdate || Runtime == null)
                return;

            if (Time.unscaledTime < _nextRefreshAt)
                return;

            _nextRefreshAt = Time.unscaledTime + Math.Max(0.01f, refreshIntervalSeconds);
            Refresh();
        }

        private void OnDisable()
        {
            Disconnect();
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        private EveUnityPlayableWorldRuntime RequireRuntime()
        {
            if (Runtime == null)
                throw new InvalidOperationException("Eve Unity playable world client host is not connected.");

            return Runtime;
        }

        private void RefreshProviderSources()
        {
            var refreshed = new HashSet<MonoBehaviour>();
            RefreshProvider(providerSurfaceDocuments, refreshed);
            RefreshProvider(assetManifestDocuments, refreshed);
            RefreshProvider(receiptSource, refreshed);
            RefreshProvider(commandSink, refreshed);
        }

        private static void RefreshProvider(MonoBehaviour? behaviour, ISet<MonoBehaviour> refreshed)
        {
            if (behaviour is IEveUnityProviderRefreshSource refreshSource && refreshed.Add(behaviour))
                refreshSource.Refresh();
        }

        private static T Required<T>(MonoBehaviour? behaviour, string fieldName) where T : class
        {
            var resolved = behaviour as T;
            if (resolved == null)
                throw new InvalidOperationException(
                    $"Eve Unity playable world client host requires '{fieldName}' to implement {typeof(T).Name}.");

            return resolved;
        }

        private static T? Optional<T>(MonoBehaviour? behaviour) where T : class
        {
            return behaviour as T;
        }
    }
}
