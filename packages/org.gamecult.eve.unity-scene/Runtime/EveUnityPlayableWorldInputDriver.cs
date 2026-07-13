using System;
using GameCult.Eve.Surface;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPlayableWorldInputDriver : MonoBehaviour
    {
        [SerializeField] private EveUnityPlayableWorldClientHost? host;
        [SerializeField] private Transform? cameraTransform;
        [SerializeField] private bool driveInUpdate = true;
        [SerializeField] private string horizontalAxis = "Horizontal";
        [SerializeField] private string verticalAxis = "Vertical";
        [SerializeField] private string actionButton = "Fire1";
        [SerializeField] private float deadZone = 0.01f;
        [SerializeField] private float commandIntervalSeconds = 0.05f;
        [SerializeField] private bool submitZeroOnRelease = true;

        private float _nextCommandAt;
        private bool _wasMoving;

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

        public EveSurfaceCommandRequest? SubmitMoveVectorInput(
            float horizontal,
            float vertical,
            DateTimeOffset? issuedAt = null)
        {
            var resolvedHost = ResolveHost();
            if (resolvedHost == null || resolvedHost.ActiveWorld == null || !resolvedHost.ActiveWorld.MovementEnabled)
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

        public EveSurfaceCommandRequest? SubmitPrimaryAction(string actionId = "primary")
        {
            var resolvedHost = ResolveHost();
            if (resolvedHost?.ActiveWorld == null)
                return null;

            return resolvedHost.SubmitActionIntent(
                resolvedHost.ActiveWorld.PlayerEntityId,
                string.IsNullOrWhiteSpace(actionId) ? "primary" : actionId);
        }

        private void Update()
        {
            if (!driveInUpdate || Time.unscaledTime < _nextCommandAt)
                return;

            _nextCommandAt = Time.unscaledTime + Math.Max(0.01f, commandIntervalSeconds);
            SubmitMoveVectorInput(Input.GetAxisRaw(horizontalAxis), Input.GetAxisRaw(verticalAxis));

            if (!string.IsNullOrWhiteSpace(actionButton) && Input.GetButtonDown(actionButton))
                SubmitPrimaryAction(actionButton);
        }

        private EveUnityPlayableWorldClientHost? ResolveHost()
        {
            if (host != null)
                return host;

            host = GetComponent<EveUnityPlayableWorldClientHost>();
            return host;
        }
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
