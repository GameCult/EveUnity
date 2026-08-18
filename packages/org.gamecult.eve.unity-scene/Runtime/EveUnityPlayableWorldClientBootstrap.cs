using System;
using System.Collections;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldClientBootstrap : MonoBehaviour
    {
        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private MonoBehaviour? provider;
        [SerializeField] private MonoBehaviour? providerSurfaceDocuments;
        [SerializeField] private MonoBehaviour? commandSink;
        [SerializeField] private MonoBehaviour? assetManifestDocuments;
        [SerializeField] private MonoBehaviour? receiptSource;
        [SerializeField] private MonoBehaviour? fallbackAssetProvider;
        [SerializeField] private Transform? sceneRoot;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool createSceneRootIfMissing = true;
        [SerializeField] private bool attachInputDriver = true;
        [SerializeField] private bool attachCameraRig = true;
        [SerializeField] private bool connectOnEnable;

        public EveUnityPlayableWorldClientHost? Host => host;

        public Transform? SceneRoot => sceneRoot;

        public Transform? CameraTransform => cameraTransform;

        public EveUnityPlayableWorldPresentation? LastPresentation { get; private set; }

        public EveUnitySceneNavigationFailure? LastNavigationFailure { get; private set; }

        public event Action<EveUnitySceneNavigationFailure>? NavigationFailed;

        private IEveUnitySceneCommandReceiptSource? _navigationReceiptSource;
        private bool _navigationInProgress;

        public void ConfigureProvider(MonoBehaviour providerComponent)
        {
            UnbindNavigation();
            provider = providerComponent != null ? providerComponent : throw new ArgumentNullException(nameof(providerComponent));
            providerSurfaceDocuments = provider;
            commandSink = provider;
            assetManifestDocuments = provider;
            receiptSource = provider;
            BindNavigation();
        }

        private void OnDestroy() => UnbindNavigation();

        private void BindNavigation()
        {
            if (provider is not IEveUnityNavigableProvider || provider is not IEveUnitySceneCommandReceiptSource source)
                return;
            _navigationReceiptSource = source;
            source.ReceiptAvailable += OnReceiptAvailable;
        }

        private void UnbindNavigation()
        {
            if (_navigationReceiptSource != null)
                _navigationReceiptSource.ReceiptAvailable -= OnReceiptAvailable;
            _navigationReceiptSource = null;
        }

        private void OnReceiptAvailable(EveUnitySceneCommandReceipt receipt)
        {
            if (_navigationInProgress || receipt.Navigation == null)
                return;
            if (!string.Equals(receipt.State, "accepted", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(receipt.State, "reconciled", StringComparison.OrdinalIgnoreCase))
                return;
            StartCoroutine(NavigateAsync(receipt.Navigation));
        }

        private IEnumerator NavigateAsync(EveUnitySceneNavigationTarget target)
        {
            if (provider is not IEveUnityNavigableProvider navigable)
                yield break;
            _navigationInProgress = true;
            var navigation = navigable.NavigateAsync(target);
            while (!navigation.IsCompleted)
                yield return null;
            try
            {
                navigation.GetAwaiter().GetResult();
                LastNavigationFailure = null;
                Mount();
            }
            catch (Exception error)
            {
                var failure = new EveUnitySceneNavigationFailure(target, error.Message, DateTimeOffset.UtcNow);
                LastNavigationFailure = failure;
                NavigationFailed?.Invoke(failure);
                Debug.LogError($"Eve provider navigation failed; the current surface remains mounted. {error}");
            }
            finally
            {
                _navigationInProgress = false;
            }
        }

        public EveUnityPlayableWorldPresentation Mount()
        {
            var resolvedHost = ResolveHost();
            var resolvedProvider = provider;
            var surfaceDocuments = ResolveBehaviour<IEveUnitySceneProviderSurfaceDocumentSource>(
                providerSurfaceDocuments,
                resolvedProvider,
                nameof(providerSurfaceDocuments));
            var resolvedCommandSink = ResolveBehaviour<IEveUnitySceneCommandSink>(
                commandSink,
                resolvedProvider,
                nameof(commandSink));
            var resolvedAssetDocuments = ResolveOptionalBehaviour<IEveUnityPlayableWorldAssetManifestDocumentSource>(
                assetManifestDocuments,
                resolvedProvider);
            var resolvedReceiptSource = ResolveOptionalBehaviour<IEveUnitySceneCommandReceiptSource>(
                receiptSource,
                resolvedProvider);
            var resolvedFallbackAssetProvider = ResolveOptionalBehaviour<IEveUnityGameObjectAssetProvider>(
                fallbackAssetProvider,
                resolvedProvider);

            sceneRoot = ResolveSceneRoot();
            resolvedHost.ConnectOnEnable = false;
            resolvedHost.Configure(
                sceneRoot,
                surfaceDocuments,
                resolvedCommandSink,
                resolvedAssetDocuments,
                resolvedReceiptSource,
                resolvedFallbackAssetProvider);

            if (attachInputDriver)
                ConfigureInputDriver(resolvedHost);

            if (attachCameraRig)
                ConfigureCameraRig(resolvedHost);

            LastPresentation = resolvedHost.Connect();
            return LastPresentation;
        }

        private void Awake()
        {
            if (host != null && provider != null)
                host.ConnectOnEnable = false;
        }

        private void OnEnable()
        {
            if (connectOnEnable)
                Mount();
        }

        private EveUnityPlayableWorldClientHost ResolveHost()
        {
            if (host != null)
                return host;

            host = GetComponent<EveUnityPlayableWorldClientHost>();
            if (host == null)
                host = gameObject.AddComponent<EveUnityPlayableWorldClientHost>();

            return host;
        }

        private Transform ResolveSceneRoot()
        {
            if (sceneRoot != null)
                return sceneRoot;

            if (!createSceneRootIfMissing)
                return transform;

            var root = new GameObject("Eve Unity Playable World Root");
            root.transform.SetParent(transform, false);
            sceneRoot = root.transform;
            return sceneRoot;
        }

        private Transform ResolveCameraTransform()
        {
            if (cameraTransform != null)
                return cameraTransform;

            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                cameraTransform = mainCamera.transform;
                return cameraTransform;
            }

            var cameraObject = new GameObject("Eve Unity Playable World Camera");
            cameraObject.transform.SetParent(transform, false);
            cameraObject.AddComponent<Camera>();
            cameraTransform = cameraObject.transform;
            return cameraTransform;
        }

        private void ConfigureInputDriver(EveUnityPlayableWorldClientHost resolvedHost)
        {
            var driver = GetComponent<EveUnityPlayableWorldInputDriver>();
            if (driver == null)
                driver = gameObject.AddComponent<EveUnityPlayableWorldInputDriver>();

            driver.Host = resolvedHost;
            driver.CameraTransform = ResolveCameraTransform();
        }

        private void ConfigureCameraRig(EveUnityPlayableWorldClientHost resolvedHost)
        {
            var rig = GetComponent<EveUnityPlayableWorldCameraRig>();
            if (rig == null)
                rig = gameObject.AddComponent<EveUnityPlayableWorldCameraRig>();

            rig.Host = resolvedHost;
            rig.CameraTransform = ResolveCameraTransform();
            rig.RenderPolicySource = provider as IEveUnityCameraRenderPolicySource;
        }

        private MonoBehaviour ResolveBehaviour<T>(
            MonoBehaviour? explicitComponent,
            MonoBehaviour? providerComponent,
            string fieldName) where T : class
        {
            var resolved = ResolveOptionalBehaviour<T>(explicitComponent, providerComponent);
            if (resolved != null)
                return resolved;

            throw new InvalidOperationException(
                $"Eve Unity playable world bootstrap requires '{fieldName}' or provider to implement {typeof(T).Name}.");
        }

        private MonoBehaviour? ResolveOptionalBehaviour<T>(
            MonoBehaviour? explicitComponent,
            MonoBehaviour? providerComponent) where T : class
        {
            if (explicitComponent is T)
                return explicitComponent;

            if (providerComponent is T)
                return providerComponent;

            var components = GetComponents<MonoBehaviour>();
            foreach (var component in components)
            {
                if (component is T)
                    return component;
            }

            return null;
        }
    }

    public sealed record EveUnitySceneNavigationFailure(
        EveUnitySceneNavigationTarget Target,
        string Diagnostic,
        DateTimeOffset FailedAtUtc);
}
