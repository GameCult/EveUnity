using System;
using System.Linq;
using GameCult.Eve.Surface;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityInputCapabilitySource
    {
        EveInputCapabilityDocument CurrentInputCapability { get; }
    }

    public sealed class EveUnityPlayableWorldInputDriver : MonoBehaviour
    {
        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool driveInUpdate = true;
        [SerializeField] private string horizontalAxis = "Horizontal";
        [SerializeField] private string verticalAxis = "Vertical";
        [SerializeField] private string actionButton = "Fire1";
        [SerializeField] private string lookHorizontalAxis = "Mouse X";
        [SerializeField] private float defaultLookSensitivityRadians = 0.001f;
        [SerializeField] private float deadZone = 0.01f;
        [SerializeField] private float commandIntervalSeconds = 0.05f;
        [SerializeField] private bool submitZeroOnRelease = true;

        private float _nextCommandAt;
        private bool _wasMoving;
        private float _lookYawRadians;
        private bool _hasLookYaw;
        private float _pendingLookHorizontal;
        private string _lookOwnerKey = "";

        public EveUnityPlayableWorldClientHost? Host
        {
            get => host;
            set
            {
                if (ReferenceEquals(host, value)) return;
                host = value;
                ResetLookState();
            }
        }

        public Transform? CameraTransform
        {
            get => cameraTransform;
            set => cameraTransform = value;
        }

        public EveSurfaceCommandRequest? SubmitMoveVectorInput(
            float horizontal,
            float vertical,
            DateTimeOffset? issuedAt = null)
        {
            var resolvedHost = ResolveHost();
            if (resolvedHost == null || resolvedHost.ActiveWorld == null)
                return null;

            var movement = EveUnityPlayableWorldMoveVector.FromCameraRelativeInput(
                horizontal,
                vertical,
                cameraTransform,
                deadZone);

            if (!movement.HasInput)
            {
                if (!submitZeroOnRelease || !_wasMoving)
                    return null;

                _wasMoving = false;
                return resolvedHost.SubmitMoveVectorIntent(
                    resolvedHost.ActiveWorld.PlayerEntityId,
                    0f,
                    0f,
                    0f,
                    issuedAt);
            }

            _wasMoving = true;
            return resolvedHost.SubmitMoveVectorIntent(
                resolvedHost.ActiveWorld.PlayerEntityId,
                movement.DirectionX,
                movement.DirectionY,
                movement.ScalarValue,
                issuedAt);
        }

        public EveSurfaceCommandRequest? SubmitPrimaryAction(string actionId = "")
        {
            var resolvedHost = ResolveHost();
            if (resolvedHost?.ActiveWorld == null)
                return null;

            var resolvedActionId = string.IsNullOrWhiteSpace(actionId)
                ? ResolvePrimaryActionId(resolvedHost.InputCapability)
                : actionId;
            if (string.IsNullOrWhiteSpace(resolvedActionId))
                return null;

            return resolvedHost.SubmitActionIntent(
                resolvedHost.ActiveWorld.PlayerEntityId,
                resolvedActionId);
        }

        public EveSurfaceCommandRequest? SubmitLookInput(
            float horizontalDelta,
            DateTimeOffset? issuedAt = null)
        {
            var resolvedHost = ResolveHost();
            var world = resolvedHost?.ActiveWorld;
            if (world == null || !string.Equals(world.LookModel, "planar-yaw.v1", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(world.LookCommand) ||
                Mathf.Abs(horizontalDelta) <= 0.0001f)
                return null;

            var ownerKey = $"{resolvedHost!.ConnectionEpoch}\n{world.WorldRootId}\n{world.PlayerEntityId}";
            if (!_hasLookYaw || !string.Equals(_lookOwnerKey, ownerKey, StringComparison.Ordinal))
            {
                _lookYawRadians = ResolveControlledYawRadians(resolvedHost!, world.PlayerEntityId);
                _hasLookYaw = true;
                _lookOwnerKey = ownerKey;
            }
            var sensitivity = Mathf.Abs(world.LookSensitivityRadians) > 0.000001f
                ? world.LookSensitivityRadians
                : defaultLookSensitivityRadians;
            _lookYawRadians += horizontalDelta * sensitivity;
            var direction = EveUnityPlayableWorldLookDirection.FromPlanarYaw(_lookYawRadians);
            return resolvedHost!.SubmitLookDirectionIntent(
                world.PlayerEntityId,
                direction.DirectionX,
                0f,
                direction.DirectionZ,
                issuedAt);
        }

        public void QueueLookInput(float horizontalDelta) => _pendingLookHorizontal += horizontalDelta;

        public EveSurfaceCommandRequest? SubmitPendingLookInput(DateTimeOffset? issuedAt = null)
        {
            var pending = _pendingLookHorizontal;
            _pendingLookHorizontal = 0f;
            return SubmitLookInput(pending, issuedAt);
        }

        public static string ResolvePrimaryActionId(EveInputCapabilityDocument? capability)
        {
            if (capability == null)
                return "";
            var profile = (capability.DefaultProfiles ?? Array.Empty<EveInputProfileDocument>())
                .FirstOrDefault(value => string.Equals(value.DeviceClass, "keyboard-mouse", StringComparison.Ordinal))
                ?? (capability.DefaultProfiles ?? Array.Empty<EveInputProfileDocument>()).FirstOrDefault();
            var binding = (profile?.Bindings ?? Array.Empty<EveInputBindingDocument>())
                .FirstOrDefault(value => (value.Gesture?.Controls ?? Array.Empty<string>())
                    .Any(control => string.Equals(control, "mouse.primary", StringComparison.Ordinal)))
                ?? (profile?.Bindings ?? Array.Empty<EveInputBindingDocument>()).FirstOrDefault(value => value.ActionBar);
            if (binding == null)
                return "";
            var action = (capability.Actions ?? Array.Empty<EveInputActionDocument>())
                .FirstOrDefault(value => string.Equals(value.ActionId, binding.ActionId, StringComparison.Ordinal));
            return action != null && string.Equals(action.Availability, "available", StringComparison.OrdinalIgnoreCase)
                ? action.ActionId
                : "";
        }

        private void Update()
        {
            if (!driveInUpdate)
                return;

            if (!string.IsNullOrWhiteSpace(lookHorizontalAxis))
                QueueLookInput(Input.GetAxisRaw(lookHorizontalAxis));
            if (Time.unscaledTime < _nextCommandAt)
                return;

            _nextCommandAt = Time.unscaledTime + Math.Max(0.01f, commandIntervalSeconds);
            SubmitMoveVectorInput(Input.GetAxisRaw(horizontalAxis), Input.GetAxisRaw(verticalAxis));
            SubmitPendingLookInput();

            if (!string.IsNullOrWhiteSpace(actionButton) && Input.GetButtonDown(actionButton))
                SubmitPrimaryAction();
        }

        private EveUnityPlayableWorldClientHost? ResolveHost()
        {
            if (host != null)
                return host;

            host = GetComponent<EveUnityPlayableWorldClientHost>();
            return host;
        }

        private static float ResolveControlledYawRadians(EveUnityPlayableWorldClientHost host, string entityId)
        {
            foreach (var marker in host.SceneRoot.GetComponentsInChildren<EveUnityPlayableWorldEntityMarker>(true))
                if (string.Equals(marker.EntityId, entityId, StringComparison.Ordinal))
                    return marker.transform.eulerAngles.y * Mathf.Deg2Rad;
            return 0f;
        }

        private void ResetLookState()
        {
            _lookYawRadians = 0f;
            _hasLookYaw = false;
            _pendingLookHorizontal = 0f;
            _lookOwnerKey = "";
        }
    }

    public readonly struct EveUnityPlayableWorldLookDirection
    {
        public EveUnityPlayableWorldLookDirection(float directionX, float directionZ)
        {
            DirectionX = directionX;
            DirectionZ = directionZ;
        }

        public float DirectionX { get; }
        public float DirectionZ { get; }

        public static EveUnityPlayableWorldLookDirection FromPlanarYaw(float yawRadians) =>
            new EveUnityPlayableWorldLookDirection(Mathf.Sin(yawRadians), Mathf.Cos(yawRadians));
    }

    public readonly struct EveUnityPlayableWorldMoveVector
    {
        public EveUnityPlayableWorldMoveVector(
            float directionX,
            float directionY,
            float scalarValue,
            bool hasInput)
        {
            DirectionX = directionX;
            DirectionY = directionY;
            ScalarValue = scalarValue;
            HasInput = hasInput;
        }

        public float DirectionX { get; }

        public float DirectionY { get; }

        public float ScalarValue { get; }

        public bool HasInput { get; }

        public static EveUnityPlayableWorldMoveVector FromCameraRelativeInput(
            float horizontal,
            float vertical,
            Transform? cameraTransform,
            float deadZone = 0.01f)
        {
            var input = new Vector2(horizontal, vertical);
            var magnitude = Mathf.Clamp01(input.magnitude);
            if (magnitude <= Mathf.Max(0f, deadZone))
                return new EveUnityPlayableWorldMoveVector(0f, 0f, 0f, false);

            Vector3 direction;
            if (cameraTransform == null)
            {
                direction = new Vector3(horizontal, 0f, vertical);
            }
            else
            {
                var forward = cameraTransform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude <= 0.0001f)
                    forward = Vector3.forward;
                forward.Normalize();

                var right = cameraTransform.right;
                right.y = 0f;
                if (right.sqrMagnitude <= 0.0001f)
                    right = Vector3.right;
                right.Normalize();

                direction = (right * horizontal) + (forward * vertical);
            }

            if (direction.sqrMagnitude <= 0.0001f)
                return new EveUnityPlayableWorldMoveVector(0f, 0f, 0f, false);

            direction.Normalize();
            return new EveUnityPlayableWorldMoveVector(
                direction.x,
                direction.z,
                magnitude,
                true);
        }
    }
}
