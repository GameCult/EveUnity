using System;
using System.Collections.Generic;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityPlayableWorldSceneSink
    {
        void ConfigureWorld(EveUnityPlayableWorldProjection world);

        void UpsertEntity(EveUnityPlayableWorldEntity entity, EveUnityPlayableWorldAssetBinding asset);

        void RemoveEntity(string entityId);
    }

    public interface IEveUnityPlayableWorldAssetResolver
    {
        EveUnityPlayableWorldAssetBinding Resolve(EveUnityPlayableWorldEntity entity);
    }

    public sealed class EveUnityPlayableWorldPresenter
    {
        private readonly IEveUnityPlayableWorldSceneSink _sceneSink;
        private readonly IEveUnityPlayableWorldAssetResolver _assetResolver;
        private readonly HashSet<string> _knownEntityIds = new HashSet<string>(StringComparer.Ordinal);

        public EveUnityPlayableWorldPresenter(
            IEveUnityPlayableWorldSceneSink sceneSink,
            IEveUnityPlayableWorldAssetResolver assetResolver)
        {
            _sceneSink = sceneSink ?? throw new ArgumentNullException(nameof(sceneSink));
            _assetResolver = assetResolver ?? throw new ArgumentNullException(nameof(assetResolver));
        }

        public EveUnityPlayableWorldPresentation Apply(EveUnitySceneProjection projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            if (projection.PlayableWorld == null)
                throw new InvalidOperationException("Unity playable world presentation requires a playableWorld projection.");

            return Apply(projection.PlayableWorld);
        }

        public EveUnityPlayableWorldPresentation Apply(EveUnityPlayableWorldProjection world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var nextEntityIds = new HashSet<string>(StringComparer.Ordinal);
            _sceneSink.ConfigureWorld(world);

            var upserted = 0;
            foreach (var entity in world.Entities)
            {
                if (string.IsNullOrWhiteSpace(entity.EntityId))
                    continue;

                nextEntityIds.Add(entity.EntityId);
                _sceneSink.UpsertEntity(entity, _assetResolver.Resolve(entity));
                upserted++;
            }

            var removed = 0;
            var previousEntityIds = new List<string>(_knownEntityIds);
            foreach (var entityId in previousEntityIds)
            {
                if (nextEntityIds.Contains(entityId))
                    continue;

                _sceneSink.RemoveEntity(entityId);
                _knownEntityIds.Remove(entityId);
                removed++;
            }

            foreach (var entityId in nextEntityIds)
                _knownEntityIds.Add(entityId);

            return new EveUnityPlayableWorldPresentation(
                world.WorldRootId,
                world.PlayerEntityId,
                world.InputProfile,
                world.CameraRig,
                upserted,
                removed,
                _knownEntityIds.Count);
        }
    }

    public sealed class EveUnityPlayableWorldAssetBinding
    {
        public EveUnityPlayableWorldAssetBinding(
            string assetRef,
            string entityKind,
            string presentationKind)
        {
            AssetRef = assetRef ?? "";
            EntityKind = entityKind ?? "";
            PresentationKind = presentationKind ?? "";
        }

        public string AssetRef { get; }

        public string EntityKind { get; }

        public string PresentationKind { get; }
    }

    public sealed class EveUnityPlayableWorldPresentation
    {
        public EveUnityPlayableWorldPresentation(
            string worldRootId,
            string playerEntityId,
            string inputProfile,
            string cameraRig,
            int upsertedEntities,
            int removedEntities,
            int activeEntities)
        {
            WorldRootId = worldRootId ?? "";
            PlayerEntityId = playerEntityId ?? "";
            InputProfile = inputProfile ?? "";
            CameraRig = cameraRig ?? "";
            UpsertedEntities = upsertedEntities;
            RemovedEntities = removedEntities;
            ActiveEntities = activeEntities;
        }

        public string WorldRootId { get; }

        public string PlayerEntityId { get; }

        public string InputProfile { get; }

        public string CameraRig { get; }

        public int UpsertedEntities { get; }

        public int RemovedEntities { get; }

        public int ActiveEntities { get; }
    }

    public sealed class EveUnityAssetRefResolver : IEveUnityPlayableWorldAssetResolver
    {
        public EveUnityPlayableWorldAssetBinding Resolve(EveUnityPlayableWorldEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            return new EveUnityPlayableWorldAssetBinding(
                entity.AssetRef,
                entity.EntityKind,
                string.IsNullOrWhiteSpace(entity.AssetRef) ? "unity-generated-placeholder" : "provider-asset-ref");
        }
    }
}
