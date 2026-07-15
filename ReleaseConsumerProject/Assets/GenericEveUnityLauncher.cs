using System;
using System.Collections;
using System.IO;
using GameCult.Eve.UnityUIToolkit;
using GameCult.Eve.UnityScene;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class GenericEveUnityLauncher : MonoBehaviour
{
    private EveUnityCultMeshPlayableWorldProvider _provider;
    private EveUnityPlayableWorldClientBootstrap _bootstrap;
    private EveUiToolkitSurfaceLowerer _surfaceLowerer;
    private UIDocument _surfaceDocument;
    private PanelSettings _panelSettings;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartClient()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("EVEUNITY_DISABLE_AUTO_LAUNCHER"), "1", StringComparison.Ordinal))
            return;
        if (FindObjectOfType<GenericEveUnityLauncher>() != null) return;
        new GameObject("Generic EveUnity Client").AddComponent<GenericEveUnityLauncher>();
    }

    private void Awake()
    {
        Application.runInBackground = true;
        var endpoint = Environment.GetEnvironmentVariable("EVEUNITY_RENDEZVOUS_ENDPOINT") ?? "rudp://127.0.0.1:3076";
        var surface = Environment.GetEnvironmentVariable("EVEUNITY_SURFACE_ID") ?? "aetheria.pilot";
        var replica = Path.Combine(Application.persistentDataPath, "eve-unity-client.cc");

        var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.025f, 0.035f, 0.055f);
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 10000f;

        _provider = gameObject.AddComponent<EveUnityCultMeshPlayableWorldProvider>();
        _provider.Configure(endpoint, replica, surfaceId: surface, requiredSurfaceKind: "interactive-world", clientRuntimeId: "eve-unity-interactive");
        _provider.DocumentAvailable += OnSurfaceAvailable;
        _surfaceLowerer = new EveUiToolkitSurfaceLowerer();
        _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        _panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        _panelSettings.referenceResolution = new Vector2Int(1920, 1080);
        _panelSettings.sortingOrder = 20;
        _surfaceDocument = gameObject.AddComponent<UIDocument>();
        _surfaceDocument.panelSettings = _panelSettings;
        _bootstrap = gameObject.AddComponent<EveUnityPlayableWorldClientBootstrap>();
        _bootstrap.ConfigureProvider(_provider);
        StartCoroutine(Connect());
    }

    private IEnumerator Connect()
    {
        while (true)
        {
            var connected = false;
            try
            {
                var presentation = _bootstrap.Mount();
                LowerSurface(_provider.CurrentDocument);
                Debug.Log($"Connected generic Eve client: {_provider.Selection?.ProviderId} / {_provider.Selection?.SurfaceId} / {presentation.ActiveEntities} entities");
                connected = true;
            }
            catch (Exception ex)
            {
                Debug.Log("Waiting for Eve provider: " + ex.Message);
            }
            if (connected) yield break;
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    private void OnSurfaceAvailable(EveUnitySceneProviderSurfaceDocument document)
    {
        LowerSurface(document);
    }

    private void LowerSurface(EveUnitySceneProviderSurfaceDocument document)
    {
        if (_surfaceDocument == null || _surfaceLowerer == null || document == null) return;
        var root = _surfaceDocument.rootVisualElement;
        root.Clear();
        var lowered = _surfaceLowerer.Lower(document.SurfaceDocument, _provider.Submit);
        lowered.style.flexGrow = 1;
        root.Add(lowered);
    }

    private void OnDestroy()
    {
        if (_provider != null) _provider.DocumentAvailable -= OnSurfaceAvailable;
        if (_panelSettings != null) Destroy(_panelSettings);
    }
}
