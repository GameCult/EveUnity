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
        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool driveInLateUpdate = true;
        [SerializeField] private float distance = 10f;
        [SerializeField] private float height = 6f;
        [SerializeField] private float yawDegrees = 35f;
        [SerializeField] private float followDamping = 12f;
        private IEveUnityCameraRenderPolicySource? _renderPolicySource;
        private bool _ownsAmbientEnvironment;
        private AmbientMode _previousAmbientMode;
        private Color _previousAmbientLight;

        public EveUnityPlayableWorldClientHost? Host
        {
            get => host;
            set => host = value;
        }

        public Transform? CameraTransform
        {
            get => cameraTransform;
            set => cameraTransform = value;
        }

        public IEveUnityCameraRenderPolicySource? RenderPolicySource
        {
            get => _renderPolicySource;
            set => _renderPolicySource = value;
        }

        public void ReleaseRig() => RestoreAmbientEnvironment();

        public bool ApplyRig(float deltaTime)
        {
            var resolvedHost = ResolveHost();
            var activeWorld = resolvedHost?.ActiveWorld;
            if (resolvedHost == null || activeWorld == null)
            {
                RestoreAmbientEnvironment();
                return false;
            }

            if (activeWorld.CameraRig == "planar.top-down-follow.v1" && !HasValidPlanarContract(activeWorld))
            {
                RestoreAmbientEnvironment();
                return false;
            }
            if (activeWorld.CameraRig == "planar.top-down-follow.v1")
                ApplyAmbientEnvironment(activeWorld);
            else
                RestoreAmbientEnvironment();

            if (!string.IsNullOrWhiteSpace(activeWorld.CameraRig) &&
                activeWorld.CameraRig != "planar.top-down-follow.v1" &&
                activeWorld.CameraRig != "arpg.orbital-follow.v1" &&
                activeWorld.CameraRig != "third-person-orbit")
            {
                return false;
            }

            var camera = cameraTransform != null ? cameraTransform : transform;
            var targetEntityId = string.IsNullOrWhiteSpace(activeWorld.CameraTargetEntityId)
                ? activeWorld.PlayerEntityId
                : activeWorld.CameraTargetEntityId;
            var player = FindEntity(resolvedHost, targetEntityId);
            if (player == null)
                return false;

            var cameraComponent = camera.GetComponent<Camera>();
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

        private void ApplyAmbientEnvironment(EveUnityPlayableWorldProjection world)
        {
            if (!_ownsAmbientEnvironment)
            {
                _previousAmbientMode = RenderSettings.ambientMode;
                _previousAmbientLight = RenderSettings.ambientLight;
                _ownsAmbientEnvironment = true;
            }
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(
                Mathf.Max(0f, world.AmbientLightR * world.AmbientLightIntensity),
                Mathf.Max(0f, world.AmbientLightG * world.AmbientLightIntensity),
                Mathf.Max(0f, world.AmbientLightB * world.AmbientLightIntensity),
                1f);
        }

        private void RestoreAmbientEnvironment()
        {
            if (!_ownsAmbientEnvironment) return;
            RenderSettings.ambientMode = _previousAmbientMode;
            RenderSettings.ambientLight = _previousAmbientLight;
            _ownsAmbientEnvironment = false;
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
