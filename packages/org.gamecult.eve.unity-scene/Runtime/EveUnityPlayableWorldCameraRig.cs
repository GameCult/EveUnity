using UnityEngine;
using UnityEngine.Rendering;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityCameraRenderPolicySource
    {
        bool TryGetRenderChannelLayer(string channel, out int layer);
    }

    public sealed class EveUnityPlayableWorldCameraRig : MonoBehaviour
    {
        private static EveUnityPlayableWorldCameraRig? _environmentOwner;

        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool driveInLateUpdate = true;
        [SerializeField] private float distance = 10f;
        [SerializeField] private float height = 6f;
        [SerializeField] private float yawDegrees = 35f;
        [SerializeField] private float followDamping = 12f;
        private IEveUnityCameraRenderPolicySource? _renderPolicySource;
        private EveUnityPlayableWorldClientHost? _cameraHost;
        private bool _ownsAmbientEnvironment;
        private AmbientMode _previousAmbientMode;
        private Color _previousAmbientLight;
        private float _previousAmbientIntensity;
        private Material? _previousSkybox;
        private DefaultReflectionMode _previousReflectionMode;
        private Texture? _previousCustomReflection;
        private float _previousReflectionIntensity;
        private Camera? _environmentCamera;
        private CameraClearFlags _previousCameraClearFlags;
        private GameObject? _keyLightObject;

        public EveUnityPlayableWorldClientHost? Host
        {
            get => host;
            set
            {
                if (ReferenceEquals(host, value)) return;
                ReleaseRig();
                host = value;
            }
        }

        public Transform? CameraTransform
        {
            get => cameraTransform;
            set
            {
                if (ReferenceEquals(cameraTransform, value)) return;
                ReleaseRig();
                cameraTransform = value;
            }
        }

        public IEveUnityCameraRenderPolicySource? RenderPolicySource
        {
            get => _renderPolicySource;
            set => _renderPolicySource = value;
        }

        public void ReleaseRig()
        {
            ReleaseCamera();
            RestoreAmbientEnvironment();
        }

        public bool ApplyRig(float deltaTime)
        {
            var resolvedHost = ResolveHost();
            var activeWorld = resolvedHost?.ActiveWorld;
            if (resolvedHost == null || activeWorld == null)
            {
                ReleaseCamera();
                RestoreAmbientEnvironment();
                return false;
            }

            if (activeWorld.CameraRig == "planar.top-down-follow.v1" && !HasValidPlanarContract(activeWorld))
            {
                ReleaseCamera();
                RestoreAmbientEnvironment();
                return false;
            }
            if (!string.IsNullOrWhiteSpace(activeWorld.CameraRig) &&
                activeWorld.CameraRig != "planar.top-down-follow.v1" &&
                activeWorld.CameraRig != "arpg.orbital-follow.v1" &&
                activeWorld.CameraRig != "third-person-orbit")
            {
                ReleaseCamera();
                return false;
            }

            var camera = cameraTransform != null ? cameraTransform : transform;
            var targetEntityId = string.IsNullOrWhiteSpace(activeWorld.CameraTargetEntityId)
                ? activeWorld.PlayerEntityId
                : activeWorld.CameraTargetEntityId;
            var player = FindEntity(resolvedHost, targetEntityId);
            if (player == null)
            {
                ReleaseCamera();
                return false;
            }
            if (!TryResolveEnvironment(resolvedHost, activeWorld, out var skybox, out var reflection))
            {
                ReleaseRig();
                return false;
            }
            if (!resolvedHost.TryClaimWorldCamera(this, camera))
            {
                RestoreAmbientEnvironment();
                return false;
            }
            _cameraHost = resolvedHost;
            if (_environmentOwner != null && !ReferenceEquals(_environmentOwner, this))
            {
                ReleaseCamera();
                return false;
            }
            _environmentOwner = this;

            var cameraComponent = camera.GetComponent<Camera>();
            ApplyAmbientEnvironment(activeWorld, cameraComponent, skybox, reflection);
            ApplyKeyLight(activeWorld);
            if (cameraComponent != null && _renderPolicySource != null)
            {
                var cullingMask = cameraComponent.cullingMask;
                foreach (var channel in activeWorld.ExcludedRenderChannels)
                {
                    if (_renderPolicySource.TryGetRenderChannelLayer(channel, out var layer) &&
                        layer >= 0 && layer < 32)
                        cullingMask &= ~(1 << layer);
                }
                cameraComponent.cullingMask = cullingMask;
            }
            if (activeWorld.CameraRig == "planar.top-down-follow.v1")
                return ApplyPlanarTopDown(activeWorld, camera, cameraComponent, player.position, deltaTime);

            var bounds = CalculateVisualBounds(
                player,
                cameraComponent != null ? cameraComponent.cullingMask : -1);
            var target = bounds?.center ?? player.position;
            var radius = bounds?.extents.magnitude ?? 0f;
            var verticalFov = cameraComponent != null ? cameraComponent.fieldOfView : 60f;
            var framingDistance = radius > 0f
                ? radius / Mathf.Sin(Mathf.Max(1f, verticalFov) * 0.5f * Mathf.Deg2Rad) * 1.15f
                : 0f;
            var resolvedDistance = Mathf.Max(0.1f, distance, framingDistance);
            var resolvedHeight = Mathf.Max(0f, height, radius * 0.45f);
            var orbit = Quaternion.Euler(0f, yawDegrees, 0f) * (Vector3.back * resolvedDistance);
            var desiredPosition = target + orbit + (Vector3.up * resolvedHeight);
            var t = deltaTime <= 0f
                ? 1f
                : 1f - Mathf.Exp(-Mathf.Max(0.01f, followDamping) * deltaTime);

            camera.position = Vector3.Lerp(camera.position, desiredPosition, t);
            camera.LookAt(target);
            return true;
        }

        private static bool ApplyPlanarTopDown(
            EveUnityPlayableWorldProjection world,
            Transform camera,
            Camera? cameraComponent,
            Vector3 target,
            float deltaTime)
        {
            var resolvedDistance = world.CameraDistance;
            var verticalFov = world.CameraVerticalFieldOfViewDegrees;
            if (cameraComponent != null)
            {
                cameraComponent.orthographic = false;
                cameraComponent.lensShift = Vector2.zero;
                cameraComponent.fieldOfView = verticalFov;
                cameraComponent.nearClipPlane = world.CameraNearClipPlane;
                cameraComponent.farClipPlane = world.CameraFarClipPlane;
            }
            var aspect = cameraComponent != null ? cameraComponent.aspect : 16f / 9f;
            var verticalHalfExtent = resolvedDistance * Mathf.Tan(verticalFov * 0.5f * Mathf.Deg2Rad);
            var horizontalHalfExtent = verticalHalfExtent * Mathf.Max(0.01f, aspect);
            var screenOffset = new Vector3(
                (Mathf.Clamp01(world.CameraTargetScreenX) - 0.5f) * 2f * horizontalHalfExtent,
                0f,
                (Mathf.Clamp01(world.CameraTargetScreenY) - 0.5f) * 2f * verticalHalfExtent);
            var desiredPosition = target + (Vector3.up * resolvedDistance) - screenOffset;
            var damping = Mathf.Max(0f, world.CameraPositionDamping);
            var t = deltaTime <= 0f || damping <= 0f
                ? 1f
                : 1f - Mathf.Exp(-damping * deltaTime);
            camera.position = Vector3.Lerp(camera.position, desiredPosition, t);
            camera.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);

            return true;
        }

        private static bool HasValidPlanarContract(EveUnityPlayableWorldProjection world) =>
            world.CameraDistance > 0f &&
            world.CameraVerticalFieldOfViewDegrees > 0f &&
            world.CameraVerticalFieldOfViewDegrees < 180f &&
            world.CameraTargetScreenX >= 0f && world.CameraTargetScreenX <= 1f &&
            world.CameraTargetScreenY >= 0f && world.CameraTargetScreenY <= 1f &&
            world.CameraPositionDamping >= 0f &&
            world.CameraNearClipPlane > 0f &&
            world.CameraFarClipPlane > world.CameraNearClipPlane;

        private static bool TryResolveEnvironment(
            EveUnityPlayableWorldClientHost host,
            EveUnityPlayableWorldProjection world,
            out Material? skybox,
            out Cubemap? reflection)
        {
            skybox = null;
            reflection = null;
            if (!IsFinite(world.AmbientLightR) ||
                !IsFinite(world.AmbientLightG) ||
                !IsFinite(world.AmbientLightB) ||
                !IsFinite(world.AmbientLightIntensity) ||
                !IsFinite(world.ReflectionIntensity) ||
                !IsFinite(world.KeyLightDirectionX) ||
                !IsFinite(world.KeyLightDirectionY) ||
                !IsFinite(world.KeyLightDirectionZ) ||
                !IsFinite(world.KeyLightColorR) ||
                !IsFinite(world.KeyLightColorG) ||
                !IsFinite(world.KeyLightColorB) ||
                !IsFinite(world.KeyLightIntensity))
                return false;
            var assets = host.NativeAssetProvider;
            if (!string.IsNullOrWhiteSpace(world.SkyboxAssetRef))
            {
                skybox = assets?.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(
                        world.SkyboxAssetRef,
                        "",
                        "provider-asset-ref"),
                    typeof(Material)) as Material;
                if (skybox == null || skybox.shader == null || !skybox.shader.isSupported)
                    return false;
            }
            if (!string.IsNullOrWhiteSpace(world.ReflectionAssetRef))
            {
                reflection = assets?.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(
                        world.ReflectionAssetRef,
                        "",
                        "provider-asset-ref"),
                    typeof(Cubemap)) as Cubemap;
                if (reflection == null)
                    return false;
            }
            return true;
        }

        private void ApplyKeyLight(EveUnityPlayableWorldProjection world)
        {
            var direction = new Vector3(
                world.KeyLightDirectionX,
                world.KeyLightDirectionY,
                world.KeyLightDirectionZ);
            if (world.KeyLightIntensity <= 0f || direction.sqrMagnitude <= 0.000001f)
            {
                ReleaseKeyLight();
                return;
            }

            if (_keyLightObject == null)
            {
                _keyLightObject = new GameObject("Eve World Key Light");
                _keyLightObject.transform.SetParent(transform, worldPositionStays: false);
                var created = _keyLightObject.AddComponent<Light>();
                created.type = LightType.Directional;
                created.shadows = LightShadows.None;
            }

            var light = _keyLightObject.GetComponent<Light>();
            light.color = new Color(
                Mathf.Max(0f, world.KeyLightColorR),
                Mathf.Max(0f, world.KeyLightColorG),
                Mathf.Max(0f, world.KeyLightColorB),
                1f);
            light.intensity = Mathf.Max(0f, world.KeyLightIntensity);
            var normalized = direction.normalized;
            var up = Mathf.Abs(Vector3.Dot(normalized, Vector3.up)) > 0.999f
                ? Vector3.forward
                : Vector3.up;
            _keyLightObject.transform.rotation = Quaternion.LookRotation(normalized, up);
            _keyLightObject.SetActive(true);
        }

        private void ReleaseKeyLight()
        {
            if (_keyLightObject == null)
                return;
            _keyLightObject.SetActive(false);
            if (Application.isPlaying)
                Destroy(_keyLightObject);
            else
                DestroyImmediate(_keyLightObject);
            _keyLightObject = null;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void ApplyAmbientEnvironment(
            EveUnityPlayableWorldProjection world,
            Camera? camera,
            Material? skybox,
            Cubemap? reflection)
        {
            var environmentChanged = !ReferenceEquals(RenderSettings.skybox, skybox);
            if (!_ownsAmbientEnvironment)
            {
                _previousAmbientMode = RenderSettings.ambientMode;
                _previousAmbientLight = RenderSettings.ambientLight;
                _previousAmbientIntensity = RenderSettings.ambientIntensity;
                _previousSkybox = RenderSettings.skybox;
                _previousReflectionMode = RenderSettings.defaultReflectionMode;
                _previousCustomReflection = RenderSettings.customReflectionTexture;
                _previousReflectionIntensity = RenderSettings.reflectionIntensity;
                _environmentCamera = camera;
                if (camera != null)
                    _previousCameraClearFlags = camera.clearFlags;
                _ownsAmbientEnvironment = true;
            }
            if (skybox != null)
            {
                RenderSettings.skybox = skybox;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = Mathf.Max(0f, world.AmbientLightIntensity);
                if (camera != null)
                    camera.clearFlags = CameraClearFlags.Skybox;
            }
            else
            {
                RenderSettings.skybox = _previousSkybox;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientIntensity = _previousAmbientIntensity;
                RenderSettings.ambientLight = new Color(
                    Mathf.Max(0f, world.AmbientLightR * world.AmbientLightIntensity),
                    Mathf.Max(0f, world.AmbientLightG * world.AmbientLightIntensity),
                    Mathf.Max(0f, world.AmbientLightB * world.AmbientLightIntensity),
                    1f);
                if (_environmentCamera != null)
                    _environmentCamera.clearFlags = _previousCameraClearFlags;
            }
            if (reflection != null)
            {
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = reflection;
                RenderSettings.reflectionIntensity = Mathf.Max(0f, world.ReflectionIntensity);
            }
            else
            {
                RenderSettings.defaultReflectionMode = _previousReflectionMode;
                RenderSettings.customReflectionTexture = _previousCustomReflection;
                RenderSettings.reflectionIntensity = _previousReflectionIntensity;
            }
            if (environmentChanged)
                DynamicGI.UpdateEnvironment();
        }

        private void RestoreAmbientEnvironment()
        {
            ReleaseKeyLight();
            if (!_ownsAmbientEnvironment) return;
            var environmentChanged = !ReferenceEquals(RenderSettings.skybox, _previousSkybox);
            RenderSettings.ambientMode = _previousAmbientMode;
            RenderSettings.ambientLight = _previousAmbientLight;
            RenderSettings.ambientIntensity = _previousAmbientIntensity;
            RenderSettings.skybox = _previousSkybox;
            RenderSettings.defaultReflectionMode = _previousReflectionMode;
            RenderSettings.customReflectionTexture = _previousCustomReflection;
            RenderSettings.reflectionIntensity = _previousReflectionIntensity;
            if (_environmentCamera != null)
                _environmentCamera.clearFlags = _previousCameraClearFlags;
            _environmentCamera = null;
            _previousSkybox = null;
            _previousCustomReflection = null;
            _ownsAmbientEnvironment = false;
            if (ReferenceEquals(_environmentOwner, this))
                _environmentOwner = null;
            if (environmentChanged)
                DynamicGI.UpdateEnvironment();
        }

        private void ReleaseCamera()
        {
            _cameraHost?.ReleaseWorldCamera(this);
            _cameraHost = null;
        }

        private static Bounds? CalculateVisualBounds(Transform player, int cullingMask)
        {
            Bounds? bounds = null;
            foreach (var root in new[] { player })
            {
                foreach (var visual in root.GetComponentsInChildren<Renderer>(includeInactive: false))
                {
                    if (visual == null || !visual.enabled ||
                        (cullingMask & (1 << visual.gameObject.layer)) == 0)
                        continue;
                    if (visual is LineRenderer || visual is TrailRenderer ||
                        string.Equals(visual.GetType().Name, "ParticleSystemRenderer", System.StringComparison.Ordinal))
                        continue;
                    if (bounds.HasValue)
                    {
                        var combined = bounds.Value;
                        combined.Encapsulate(visual.bounds);
                        bounds = combined;
                    }
                    else
                    {
                        bounds = visual.bounds;
                    }
                }
            }
            return bounds;
        }

        private void LateUpdate()
        {
            if (driveInLateUpdate)
                ApplyRig(Time.deltaTime);
        }

        private void OnDisable() => ReleaseRig();

        private void OnDestroy() => ReleaseRig();

        private EveUnityPlayableWorldClientHost? ResolveHost()
        {
            if (host != null)
                return host;

            host = GetComponent<EveUnityPlayableWorldClientHost>();
            return host;
        }

        private static Transform? FindEntity(
            EveUnityPlayableWorldClientHost host,
            string playerEntityId)
        {
            if (string.IsNullOrWhiteSpace(playerEntityId))
                return null;

            if (host.PresentedEntities?.CurrentGeneration != null)
                return host.PresentedEntities.TryGetByEntityId(playerEntityId, out var presented)
                    ? presented.Transform
                    : null;

            var markers = host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>();
            foreach (var marker in markers)
            {
                if (marker != null && marker.EntityId == playerEntityId)
                    return marker.transform;
            }

            return null;
        }
    }
}
