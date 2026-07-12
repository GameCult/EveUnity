using System;
using System.Collections.Generic;
using System.Globalization;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnitySceneClientSession
    {
        public const string RuntimeClientId = "unity-scene";

        private readonly EveUnitySceneSurfaceLowerer _lowerer;
        private EveUnitySceneProviderSurfaceSnapshot? _activeSnapshot;

        public EveUnitySceneClientSession()
            : this(new EveUnitySceneSurfaceLowerer())
        {
        }

        public EveUnitySceneClientSession(EveUnitySceneSurfaceLowerer lowerer)
        {
            _lowerer = lowerer ?? throw new ArgumentNullException(nameof(lowerer));
        }

        public EveUnitySceneProjection? ActiveProjection { get; private set; }

        public string ActiveSourcePointer => _activeSnapshot?.SourcePointer ?? "";

        public long ActiveVersion => _activeSnapshot?.Version ?? 0;

        public EveUnitySceneProjection Connect(EveUnitySceneProviderSurfaceSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var projection = _lowerer.Lower(snapshot.Document, snapshot.AdvertisedSurface);
            _activeSnapshot = snapshot;
            ActiveProjection = projection;
            return projection;
        }

        public EveUnitySceneProjection ApplySnapshot(EveUnitySceneProviderSurfaceSnapshot snapshot)
        {
            return Connect(snapshot);
        }

        public EveSurfaceCommandRequest CreateMoveIntent(
            string entityId,
            float targetX,
            float targetY,
            float targetZ,
            DateTimeOffset? issuedAt = null)
        {
            var playableWorld = RequirePlayableWorld();
            return CreatePlayableWorldIntent(
                playableWorld.MovementCommand,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["entityId"] = entityId ?? "",
                    ["targetPosition"] = FormatVector3(targetX, targetY, targetZ)
                },
                issuedAt);
        }

        public EveSurfaceCommandRequest CreateMoveVectorIntent(
            string entityId,
            float directionX,
            float directionY,
            float scalarValue = 1f,
            DateTimeOffset? issuedAt = null)
        {
            var playableWorld = RequirePlayableWorld();
            return CreatePlayableWorldIntent(
                playableWorld.MovementCommand,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["entityId"] = entityId ?? "",
                    ["directionX"] = FormatFloat(directionX),
                    ["directionY"] = FormatFloat(directionY),
                    ["scalarValue"] = FormatFloat(scalarValue)
                },
                issuedAt);
        }

        public EveSurfaceCommandRequest CreateFocusIntent(
            string entityId,
            DateTimeOffset? issuedAt = null)
        {
            var playableWorld = RequirePlayableWorld();
            return CreatePlayableWorldIntent(
                playableWorld.FocusCommand,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["entityId"] = entityId ?? ""
                },
                issuedAt);
        }

        public EveSurfaceCommandRequest CreateTargetIntent(
            string sourceEntityId,
            string targetEntityId,
            DateTimeOffset? issuedAt = null)
        {
            var playableWorld = RequirePlayableWorld();
            return CreatePlayableWorldIntent(
                playableWorld.TargetCommand,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceEntityId"] = sourceEntityId ?? "",
                    ["targetEntityId"] = targetEntityId ?? ""
                },
                issuedAt);
        }

        public EveSurfaceCommandRequest CreateActionIntent(
            string entityId,
            EveInputActionDocument action,
            DateTimeOffset? issuedAt = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var payload = new Dictionary<string, string>(action.Payload ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["entityId"] = entityId ?? "",
                ["actionId"] = action.ActionId ?? ""
            };
            return CreatePlayableWorldIntent(
                action.Operation,
                payload,
                issuedAt);
        }

        public EveSurfaceCommandRequest CreatePlayableWorldIntent(
            string commandId,
            IReadOnlyDictionary<string, string>? payload = null,
            DateTimeOffset? issuedAt = null)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                throw new InvalidOperationException("Playable world command id is missing from the active surface.");

            var snapshot = RequireSnapshot();
            var projection = RequireProjection();
            var envelopePayload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commandId"] = commandId
            };

            if (payload != null)
            {
                foreach (var entry in payload)
                    envelopePayload[entry.Key] = entry.Value;
            }

            return _lowerer.CreateCommandIntent(
                snapshot.Document,
                snapshot.AdvertisedSurface,
                projection.CommandBoundary,
                envelopePayload,
                issuedAt);
        }

        private EveUnityPlayableWorldProjection RequirePlayableWorld()
        {
            var projection = RequireProjection();
            if (projection.PlayableWorld == null)
                throw new InvalidOperationException("The active Unity scene surface does not expose a playable world projection.");
            return projection.PlayableWorld;
        }

        private EveUnitySceneProjection RequireProjection()
        {
            if (ActiveProjection == null)
                throw new InvalidOperationException("No provider surface snapshot has been connected to the Unity scene client session.");
            return ActiveProjection;
        }

        private EveUnitySceneProviderSurfaceSnapshot RequireSnapshot()
        {
            if (_activeSnapshot == null)
                throw new InvalidOperationException("No provider surface snapshot has been connected to the Unity scene client session.");
            return _activeSnapshot;
        }

        private static string FormatVector3(float x, float y, float z)
        {
            return string.Join(
                ",",
                x.ToString("R", CultureInfo.InvariantCulture),
                y.ToString("R", CultureInfo.InvariantCulture),
                z.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    public sealed class EveUnitySceneProviderSurfaceSnapshot
    {
        public EveUnitySceneProviderSurfaceSnapshot(
            EveSurfaceDocument document,
            EveUnitySceneProviderSurfaceAdvertisement advertisedSurface,
            string sourcePointer,
            long version = 0)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            AdvertisedSurface = advertisedSurface ?? throw new ArgumentNullException(nameof(advertisedSurface));
            SourcePointer = sourcePointer ?? "";
            Version = version;
        }

        public EveSurfaceDocument Document { get; }

        public EveUnitySceneProviderSurfaceAdvertisement AdvertisedSurface { get; }

        public string SourcePointer { get; }

        public long Version { get; }
    }
}
