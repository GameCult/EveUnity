using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityPresentedEntity
    {
        public EveUnityPresentedEntity(
            int sourceIndex,
            string entityId,
            string entityKind,
            string label,
            string faction,
            Vector3 position,
            float rotationYDegrees,
            Vector3 velocity,
            float radius,
            float scale,
            uint renderGroupId,
            int lod,
            bool selectable,
            bool controllable,
            string assetRef,
            IReadOnlyDictionary<string, float>? scalarState = null)
        {
            SourceIndex = sourceIndex;
            EntityId = entityId ?? "";
            EntityKind = entityKind ?? "";
            Label = label ?? "";
            Faction = faction ?? "";
            Position = position;
            RotationYDegrees = rotationYDegrees;
            Velocity = velocity;
            Radius = radius;
            Scale = scale;
            RenderGroupId = renderGroupId;
            Lod = lod;
            Selectable = selectable;
            Controllable = controllable;
            AssetRef = assetRef ?? "";
            ScalarState = scalarState ?? new Dictionary<string, float>(StringComparer.Ordinal);
        }

        public int SourceIndex { get; }
        public string EntityId { get; }
        public string EntityKind { get; }
        public string Label { get; }
        public string Faction { get; }
        public Vector3 Position { get; }
        public float RotationYDegrees { get; }
        public Vector3 Velocity { get; }
        public float Radius { get; }
        public float Scale { get; }
        public uint RenderGroupId { get; }
        public int Lod { get; }
        public bool Selectable { get; }
        public bool Controllable { get; }
        public string AssetRef { get; }
        public IReadOnlyDictionary<string, float> ScalarState { get; }

        public bool TryGetScalar(string semantic, out float value) =>
            ScalarState.TryGetValue(semantic ?? "", out value);

        public EveUnityPlayableWorldEntity ToPlayableWorldEntity() => new EveUnityPlayableWorldEntity(
            EntityId,
            EntityId,
            EntityKind,
            Label,
            Faction,
            AssetRef,
            Position.x,
            Position.y,
            Position.z,
            RotationYDegrees,
            Scale > 0f ? Scale : Radius,
            Selectable,
            Controllable,
            "", "", "", "");
    }

    public sealed class EveUnityPresentedEntityGeneration
    {
        public EveUnityPresentedEntityGeneration(
            string viewId,
            long producerEpoch,
            long sequence,
            IEnumerable<EveUnityPresentedEntity> entities)
        {
            ViewId = viewId ?? "";
            ProducerEpoch = producerEpoch;
            Sequence = sequence;
            Entities = (entities ?? throw new ArgumentNullException(nameof(entities))).ToArray();
        }

        public string ViewId { get; }
        public long ProducerEpoch { get; }
        public long Sequence { get; }
        public IReadOnlyList<EveUnityPresentedEntity> Entities { get; }
    }

    public sealed class EveUnityPresentedEntityHandle
    {
        public EveUnityPresentedEntityHandle(EveUnityPresentedEntity entity, Transform transform)
        {
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
            Transform = transform != null ? transform : throw new ArgumentNullException(nameof(transform));
        }

        public EveUnityPresentedEntity Entity { get; }
        public Transform Transform { get; }
    }

    public interface IEveUnityPresentedEntityRegistry
    {
        EveUnityPresentedEntityGeneration? CurrentGeneration { get; }
        bool TryGetByEntityId(string entityId, out EveUnityPresentedEntityHandle handle);
        bool TryGetBySourceIndex(int sourceIndex, out EveUnityPresentedEntityHandle handle);
    }

    public interface IEveUnityEntityGenerationSink
    {
        void ApplyGeneration(EveUnityPresentedEntityGeneration generation);
    }
}
