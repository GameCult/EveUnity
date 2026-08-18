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
        private StagedPresentation? _ownedPresentation;

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

        private void OnDestroy()
        {
            UnbindNavigation();
            _ownedPresentation?.Dispose();
            _ownedPresentation = null;
        }

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
            StagedPresentation? staged = null;
            PresentationSwap? swap = null;
            while (!navigation.IsCompleted)
                yield return null;
            try
            {
                navigation.GetAwaiter().GetResult();
                staged = PrepareStagedPresentation();
                navigable.CommitNavigation();
                swap = ActivateStagedPresentation(staged);
                navigable.FinalizeNavigation();
                swap.Complete();
                swap = null;
                staged = null;
                LastNavigationFailure = null;
            }
            catch (Exception error)
            {
                var providerRestored = false;
                try
                {
                    navigable.RollbackNavigation();
                    providerRestored = true;
                }
                catch (Exception rollbackError)
                {
                    Debug.LogError($"Eve provider route rollback failed; the previous presentation will remain inactive. {rollbackError}");
                }
                if (providerRestored)
                {
                    try
                    {
                        swap?.Rollback();
                    }
                    catch (Exception rollbackError)
                    {
                        Debug.LogError($"Eve previous presentation rollback failed after provider restoration. {rollbackError}");
                    }
                }
                try
                {
                    staged?.Dispose();
                }
                catch (Exception cleanupError)
                {
                    Debug.LogWarning($"Eve rejected presentation cleanup failed. {cleanupError}");
                }
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

        private StagedPresentation PrepareStagedPresentation()
        {
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

            var stagingObject = new GameObject("Eve Unity Candidate Presentation");
            stagingObject.SetActive(false);
            stagingObject.transform.SetParent(transform, false);
            var stagingRootObject = new GameObject("Eve Unity Candidate Scene Root");
            stagingRootObject.transform.SetParent(stagingObject.transform, false);
            var stagingHost = stagingObject.AddComponent<EveUnityPlayableWorldClientHost>();
            stagingHost.ConnectOnEnable = false;
            try
            {
                stagingHost.Configure(
                    stagingRootObject.transform,
                    surfaceDocuments,
                    resolvedCommandSink,
                    resolvedAssetDocuments,
                    resolvedReceiptSource,
                    resolvedFallbackAssetProvider);
                var presentation = stagingHost.Connect();
                return new StagedPresentation(stagingObject, stagingRootObject.transform, stagingHost, presentation);
            }
            catch
            {
                stagingHost.Disconnect();
                DestroyObject(stagingObject);
                throw;
            }
        }

        private PresentationSwap ActivateStagedPresentation(StagedPresentation staged)
        {
            var previousHost = host;
            var previousRoot = sceneRoot;
            var previousPresentation = LastPresentation;
            var previousRootWasActive = previousRoot == null || previousRoot.gameObject.activeSelf;
            var input = GetComponent<EveUnityPlayableWorldInputDriver>();
            var previousInputHost = input?.Host;
            var previousInputCamera = input?.CameraTransform;
            var rig = GetComponent<EveUnityPlayableWorldCameraRig>();
            var previousRigHost = rig?.Host;
            var previousRigCamera = rig?.CameraTransform;
            var previousRigPolicy = rig?.RenderPolicySource;

            try
            {
                staged.GameObject.SetActive(true);
                if (attachInputDriver)
                    ConfigureInputDriver(staged.Host);
                if (attachCameraRig)
                    ConfigureCameraRig(staged.Host);
                host = staged.Host;
                sceneRoot = staged.SceneRoot;
                LastPresentation = staged.Presentation;
                if (previousRoot != null && !ReferenceEquals(previousRoot, transform) &&
                    !ReferenceEquals(previousRoot, staged.SceneRoot))
                    previousRoot.gameObject.SetActive(false);
                return new PresentationSwap(
                    this,
                    staged,
                    _ownedPresentation,
                    previousHost,
                    previousRoot,
                    previousPresentation,
                    previousRootWasActive,
                    input,
                    previousInputHost,
                    previousInputCamera,
                    rig,
                    previousRigHost,
                    previousRigCamera,
                    previousRigPolicy);
            }
            catch
            {
                if (input != null)
                {
                    input.Host = previousInputHost;
                    input.CameraTransform = previousInputCamera;
                }
                if (rig != null)
                {
                    rig.Host = previousRigHost;
                    rig.CameraTransform = previousRigCamera;
                    rig.RenderPolicySource = previousRigPolicy;
                }
                host = previousHost;
                sceneRoot = previousRoot;
                LastPresentation = previousPresentation;
                if (previousRoot != null)
                    previousRoot.gameObject.SetActive(previousRootWasActive);
                throw;
            }
        }

        private static void DestroyObject(UnityEngine.Object value)
        {
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }

        private sealed class StagedPresentation : IDisposable
        {
            public StagedPresentation(
                GameObject gameObject,
                Transform sceneRoot,
                EveUnityPlayableWorldClientHost host,
                EveUnityPlayableWorldPresentation presentation)
            {
                GameObject = gameObject;
                SceneRoot = sceneRoot;
                Host = host;
                Presentation = presentation;
            }

            public GameObject GameObject { get; }
            public Transform SceneRoot { get; }
            public EveUnityPlayableWorldClientHost Host { get; }
            public EveUnityPlayableWorldPresentation Presentation { get; }

            public void Dispose()
            {
                try
                {
                    Host.Disconnect();
                }
                catch (Exception error)
                {
                    Debug.LogWarning($"Eve presentation disconnect failed during cleanup: {error}");
                }
                finally
                {
                    DestroyObject(GameObject);
                }
            }
        }

        private sealed class PresentationSwap
        {
            private readonly EveUnityPlayableWorldClientBootstrap _owner;
            private readonly StagedPresentation _candidate;
            private readonly StagedPresentation? _previousOwnedPresentation;
            private readonly EveUnityPlayableWorldClientHost? _previousHost;
            private readonly Transform? _previousRoot;
            private readonly EveUnityPlayableWorldPresentation? _previousPresentation;
            private readonly bool _previousRootWasActive;
            private readonly EveUnityPlayableWorldInputDriver? _input;
            private readonly EveUnityPlayableWorldClientHost? _previousInputHost;
            private readonly Transform? _previousInputCamera;
            private readonly EveUnityPlayableWorldCameraRig? _rig;
            private readonly EveUnityPlayableWorldClientHost? _previousRigHost;
            private readonly Transform? _previousRigCamera;
            private readonly IEveUnityCameraRenderPolicySource? _previousRigPolicy;
            private bool _settled;

            public PresentationSwap(
                EveUnityPlayableWorldClientBootstrap owner,
                StagedPresentation candidate,
                StagedPresentation? previousOwnedPresentation,
                EveUnityPlayableWorldClientHost? previousHost,
                Transform? previousRoot,
                EveUnityPlayableWorldPresentation? previousPresentation,
                bool previousRootWasActive,
                EveUnityPlayableWorldInputDriver? input,
                EveUnityPlayableWorldClientHost? previousInputHost,
                Transform? previousInputCamera,
                EveUnityPlayableWorldCameraRig? rig,
                EveUnityPlayableWorldClientHost? previousRigHost,
                Transform? previousRigCamera,
                IEveUnityCameraRenderPolicySource? previousRigPolicy)
            {
                _owner = owner;
                _candidate = candidate;
                _previousOwnedPresentation = previousOwnedPresentation;
                _previousHost = previousHost;
                _previousRoot = previousRoot;
                _previousPresentation = previousPresentation;
                _previousRootWasActive = previousRootWasActive;
                _input = input;
                _previousInputHost = previousInputHost;
                _previousInputCamera = previousInputCamera;
                _rig = rig;
                _previousRigHost = previousRigHost;
                _previousRigCamera = previousRigCamera;
                _previousRigPolicy = previousRigPolicy;
            }

            public void Complete()
            {
                if (_settled) return;
                _settled = true;
                _owner._ownedPresentation = _candidate;
                if (_previousOwnedPresentation != null &&
                    !ReferenceEquals(_previousOwnedPresentation, _candidate))
                {
                    _previousOwnedPresentation.Dispose();
                    return;
                }
                if (_previousHost == null || ReferenceEquals(_previousHost, _candidate.Host)) return;
                try
                {
                    _previousHost.Disconnect();
                }
                catch (Exception error)
                {
                    Debug.LogWarning($"Eve previous presentation disconnect failed after navigation commit: {error}");
                }
            }

            public void Rollback()
            {
                if (_settled) return;
                _settled = true;
                _owner.host = _previousHost;
                _owner.sceneRoot = _previousRoot;
                _owner.LastPresentation = _previousPresentation;
                if (_input != null)
                {
                    _input.Host = _previousInputHost;
                    _input.CameraTransform = _previousInputCamera;
                }
                if (_rig != null)
                {
                    _rig.Host = _previousRigHost;
                    _rig.CameraTransform = _previousRigCamera;
                    _rig.RenderPolicySource = _previousRigPolicy;
                }
                if (_previousRoot != null)
                    _previousRoot.gameObject.SetActive(_previousRootWasActive);
                _candidate.GameObject.SetActive(false);
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
