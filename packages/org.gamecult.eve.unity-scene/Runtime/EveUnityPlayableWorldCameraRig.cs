using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldCameraRig : MonoBehaviour
    {
        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool driveInLateUpdate = true;
        [SerializeField] private float distance = 10f;
        [SerializeField] private float height = 6f;
        [SerializeField] private float yawDegrees = 35f;
        [SerializeField] private float followDamping = 12f;

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
            var player = FindPlayerMarker(resolvedHost, activeWorld.PlayerEntityId);
            if (player == null)
                return false;

            var target = player.transform.position;
            var orbit = Quaternion.Euler(0f, yawDegrees, 0f) * (Vector3.back * Mathf.Max(0.1f, distance));
            var desiredPosition = target + orbit + (Vector3.up * Mathf.Max(0f, height));
            var t = deltaTime <= 0f
                ? 1f
                : 1f - Mathf.Exp(-Mathf.Max(0.01f, followDamping) * deltaTime);

            camera.position = Vector3.Lerp(camera.position, desiredPosition, t);
            camera.LookAt(target);
            return true;
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

        private static EveUnityPlayableWorldEntityMarker? FindPlayerMarker(
            EveUnityPlayableWorldClientHost host,
            string playerEntityId)
        {
            if (string.IsNullOrWhiteSpace(playerEntityId))
                return null;

            var markers = host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>();
            foreach (var marker in markers)
            {
                if (marker != null && marker.EntityId == playerEntityId)
                    return marker;
            }

            return null;
        }
    }
}
