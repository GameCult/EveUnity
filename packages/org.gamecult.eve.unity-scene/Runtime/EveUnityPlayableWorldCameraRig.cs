using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityCameraRenderPolicySource
    {
        bool TryGetCameraCullingMask(string viewId, out int cullingMask);
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

        public bool ApplyRig(float deltaTime)
        {
            var resolvedHost = ResolveHost();
            var activeWorld = resolvedHost?.ActiveWorld;
            if (resolvedHost == null || activeWorld == null)
                return false;

            if (!string.IsNullOrWhiteSpace(activeWorld.CameraRig) &&
                activeWorld.CameraRig != "arpg.orbital-follow.v1" &&
                activeWorld.CameraRig != "third-person-orbit")
            {
                return false;
            }

            var camera = cameraTransform != null ? cameraTransform : transform;
            var targetMarker = FindEntityMarker(resolvedHost, activeWorld.CameraTargetEntityId);
            if (targetMarker == null)
                return false;

            var cameraComponent = camera.GetComponent<Camera>();
            if (cameraComponent != null &&
                _renderPolicySource != null &&
                _renderPolicySource.TryGetCameraCullingMask(activeWorld.ViewId, out var cullingMask))
            {
                cameraComponent.cullingMask = cullingMask;
            }
            var bounds = CalculateVisualBounds(targetMarker);
            var target = bounds?.center ?? targetMarker.transform.position;
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

        private static Bounds? CalculateVisualBounds(EveUnityPlayableWorldEntityMarker target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(includeInactive: false);
            Bounds? bounds = null;
            foreach (var visual in renderers)
            {
                if (visual == null || !visual.enabled)
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
            return bounds;
        }

        private void LateUpdate()
        {
            if (driveInLateUpdate)
                ApplyRig(Time.deltaTime);
        }

        private EveUnityPlayableWorldClientHost? ResolveHost()
        {
            if (host != null)
                return host;

            host = GetComponent<EveUnityPlayableWorldClientHost>();
            return host;
        }

        private static EveUnityPlayableWorldEntityMarker? FindEntityMarker(
            EveUnityPlayableWorldClientHost host,
            string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId))
                return null;

            var markers = host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(includeInactive: true);
            foreach (var marker in markers)
            {
                if (marker != null && marker.EntityId == entityId)
                    return marker;
            }

            return null;
        }
    }
}
