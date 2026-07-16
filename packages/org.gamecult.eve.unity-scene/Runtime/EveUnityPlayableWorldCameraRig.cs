using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
        private GameObject? _postProcessVolumeObject;
        private Volume? _postProcessVolume;
        private UniversalAdditionalCameraData? _postProcessCameraData;
        private bool _createdPostProcessCameraData;
        private bool _previousRenderPostProcessing;
        private AntialiasingMode _previousAntialiasing;
        private TemporalAA.Settings _previousTaaSettings;

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

            if ((activeWorld.CameraRig == "planar.top-down-follow.v1" ||
                 activeWorld.CameraRig == "perspective.entity-forward-follow.v1") &&
                !HasValidFramedFollowContract(activeWorld))
            {
                ReleaseCamera();
                RestoreAmbientEnvironment();
                return false;
            }
            if (!string.IsNullOrWhiteSpace(activeWorld.CameraRig) &&
                activeWorld.CameraRig != "planar.top-down-follow.v1" &&
                activeWorld.CameraRig != "perspective.entity-forward-follow.v1" &&
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
            if (!TryResolveEnvironment(resolvedHost, activeWorld, out var skybox, out var reflection, out var postProcessProfile))
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
            if (postProcessProfile != null && cameraComponent == null)
            {
                ReleaseRig();
                return false;
            }
            ApplyAmbientEnvironment(activeWorld, cameraComponent, skybox, reflection, postProcessProfile);
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
            if (activeWorld.CameraRig == "perspective.entity-forward-follow.v1")
                return ApplyEntityForwardFollow(activeWorld, resolvedHost, camera, cameraComponent, player, deltaTime);

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

        private static bool ApplyEntityForwardFollow(
            EveUnityPlayableWorldProjection world,
            EveUnityPlayableWorldClientHost host,
            Transform camera,
            Camera? cameraComponent,
            Transform target,
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

            var forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();
            var rotation = Quaternion.LookRotation(forward, Vector3.up);
            var aspect = cameraComponent != null ? cameraComponent.aspect : 16f / 9f;
            var verticalHalfExtent = resolvedDistance * Mathf.Tan(verticalFov * 0.5f * Mathf.Deg2Rad);
            var horizontalHalfExtent = verticalHalfExtent * Mathf.Max(0.01f, aspect);
            var horizontalOffset = (Mathf.Clamp01(world.CameraTargetScreenX) - 0.5f) * 2f * horizontalHalfExtent;
            var verticalOffset = (Mathf.Clamp01(world.CameraTargetScreenY) - 0.5f) * 2f * verticalHalfExtent;
            if (string.Equals(world.CameraLookAt, "aim.convergence-point.v1", System.StringComparison.Ordinal))
            {
                var aim = EveUnityAimPresentation.Find(host.ActiveProjection);
                if (aim == null || !aim.TryResolveViewPoint(host, out var aimPoint)) return false;
                var targetToAim = aimPoint - target.position;
                var targetToAimDistanceSquared = targetToAim.sqrMagnitude;
                var screenOffsetSquared = horizontalOffset * horizontalOffset + verticalOffset * verticalOffset;
                if (targetToAimDistanceSquared <= screenOffsetSquared + 0.0001f) return false;
                var targetToAimDirection = targetToAim.normalized;
                var opticalDepthDelta = Mathf.Sqrt(targetToAimDistanceSquared - screenOffsetSquared);
                var cameraLocalTargetToAim = new Vector3(
                    -horizontalOffset,
                    -verticalOffset,
                    opticalDepthDelta);
                rotation = Quaternion.LookRotation(targetToAimDirection, Vector3.up) *
                           Quaternion.FromToRotation(cameraLocalTargetToAim.normalized, Vector3.forward);
            }
            var right = rotation * Vector3.right;
            var up = rotation * Vector3.up;
            var opticalForward = rotation * Vector3.forward;
            var desiredPosition = target.position -
                                  (opticalForward * resolvedDistance) -
                                  (right * horizontalOffset) -
                                  (up * verticalOffset);
            var damping = Mathf.Max(0f, world.CameraPositionDamping);
            var t = deltaTime <= 0f || damping <= 0f
                ? 1f
                : 1f - Mathf.Exp(-damping * deltaTime);
            camera.position = Vector3.Lerp(camera.position, desiredPosition, t);
            camera.rotation = rotation;
            return true;
        }

        private static bool HasValidFramedFollowContract(EveUnityPlayableWorldProjection world) =>
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
            out Cubemap? reflection,
            out VolumeProfile? postProcessProfile)
        {
            skybox = null;
            reflection = null;
            postProcessProfile = null;
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
                !IsFinite(world.KeyLightIntensity) ||
                !IsFinite(world.TemporalHistoryBlend) ||
                !IsFinite(world.TemporalJitterScale) ||
                !IsFinite(world.TemporalSharpening) ||
                (!string.IsNullOrWhiteSpace(world.CameraReconstruction) &&
                 !string.Equals(world.CameraReconstruction, "temporal-reprojection.v1", System.StringComparison.Ordinal)) ||
                (string.Equals(world.CameraReconstruction, "temporal-reprojection.v1", System.StringComparison.Ordinal) &&
                 (world.TemporalHistoryBlend < 0f || world.TemporalHistoryBlend > 1f ||
                  world.TemporalJitterScale < 0f || world.TemporalJitterScale > 1f ||
                  world.TemporalSharpening < 0f || world.TemporalSharpening > 1f ||
                  !IsTemporalQuality(world.TemporalQuality))))
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
            if (!string.IsNullOrWhiteSpace(world.PostProcessProfileAssetRef))
            {
                postProcessProfile = assets?.ResolveAsset(
                    new EveUnityPlayableWorldAssetBinding(
                        world.PostProcessProfileAssetRef,
                        "",
                        "provider-asset-ref"),
                    typeof(VolumeProfile)) as VolumeProfile;
                if (postProcessProfile == null)
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
            Cubemap? reflection,
            VolumeProfile? postProcessProfile)
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
            ApplyCameraRendering(camera, postProcessProfile, world);
        }

        private void ApplyCameraRendering(
            Camera? camera,
            VolumeProfile? profile,
            EveUnityPlayableWorldProjection world)
        {
            var usesTemporalReprojection = string.Equals(
                world.CameraReconstruction,
                "temporal-reprojection.v1",
                System.StringComparison.Ordinal);
            if (profile == null && !usesTemporalReprojection)
            {
                RestorePostProcess();
                return;
            }
            if (camera == null)
                return;
            if (_postProcessCameraData != null && !ReferenceEquals(_postProcessCameraData.gameObject, camera.gameObject))
                RestorePostProcess();
            if (_postProcessCameraData == null)
            {
                _postProcessCameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                _createdPostProcessCameraData = _postProcessCameraData == null;
                if (_postProcessCameraData == null)
                    _postProcessCameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                _previousRenderPostProcessing = _postProcessCameraData.renderPostProcessing;
                _previousAntialiasing = _postProcessCameraData.antialiasing;
                _previousTaaSettings = _postProcessCameraData.taaSettings;
            }
            _postProcessCameraData.renderPostProcessing = profile != null || _previousRenderPostProcessing;
            if (usesTemporalReprojection)
            {
                _postProcessCameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                ref var settings = ref _postProcessCameraData.taaSettings;
                settings = TemporalAA.Settings.Create();
                settings.quality = ParseTemporalQuality(world.TemporalQuality);
                settings.baseBlendFactor = world.TemporalHistoryBlend;
                settings.jitterScale = world.TemporalJitterScale;
                settings.contrastAdaptiveSharpening = world.TemporalSharpening;
            }
            else
            {
                _postProcessCameraData.antialiasing = _previousAntialiasing;
                _postProcessCameraData.taaSettings = _previousTaaSettings;
            }
            if (profile != null)
            {
                if (_postProcessVolumeObject == null)
                {
                    _postProcessVolumeObject = new GameObject("Eve World Post Process");
                    _postProcessVolumeObject.transform.SetParent(transform, worldPositionStays: false);
                    _postProcessVolume = _postProcessVolumeObject.AddComponent<Volume>();
                    _postProcessVolume.isGlobal = true;
                    _postProcessVolume.priority = 1000f;
                    _postProcessVolume.weight = 1f;
                }
                _postProcessVolume!.sharedProfile = profile;
                _postProcessVolumeObject.SetActive(true);
            }
            else if (_postProcessVolumeObject != null)
            {
                _postProcessVolumeObject.SetActive(false);
            }
        }

        private static bool IsTemporalQuality(string quality) =>
            quality == "very-low" || quality == "low" || quality == "medium" ||
            quality == "high" || quality == "very-high";

        private static TemporalAAQuality ParseTemporalQuality(string quality)
        {
            switch (quality)
            {
                case "very-low": return TemporalAAQuality.VeryLow;
                case "low": return TemporalAAQuality.Low;
                case "medium": return TemporalAAQuality.Medium;
                case "very-high": return TemporalAAQuality.VeryHigh;
                default: return TemporalAAQuality.High;
            }
        }

        private void RestorePostProcess()
        {
            if (_postProcessCameraData != null)
            {
                if (_createdPostProcessCameraData)
                {
                    if (Application.isPlaying)
                        Destroy(_postProcessCameraData);
                    else
                        DestroyImmediate(_postProcessCameraData);
                }
                else
                {
                    _postProcessCameraData.renderPostProcessing = _previousRenderPostProcessing;
                    _postProcessCameraData.antialiasing = _previousAntialiasing;
                    _postProcessCameraData.taaSettings = _previousTaaSettings;
                }
            }
            _postProcessCameraData = null;
            _createdPostProcessCameraData = false;
            if (_postProcessVolumeObject != null)
            {
                if (Application.isPlaying)
                    Destroy(_postProcessVolumeObject);
                else
                    DestroyImmediate(_postProcessVolumeObject);
            }
            _postProcessVolumeObject = null;
            _postProcessVolume = null;
        }

        private void RestoreAmbientEnvironment()
        {
            ReleaseKeyLight();
            RestorePostProcess();
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
