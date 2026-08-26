using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public interface IEveUnityEntitySoaViewDocumentSource
    {
        event Action<EveEntitySoaViewDocument, ICultMeshBodyReadLease> EntityViewAvailable;
    }

    /// <summary>Projects portable entity SoA generations into the ordinary Unity scene sink.</summary>
    public sealed class EveUnityEntitySoaPresenter
    {
        private readonly IEveUnityPlayableWorldSceneSink _sink;
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);

        public EveUnityEntitySoaPresenter(IEveUnityPlayableWorldSceneSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void Apply(EveEntitySoaViewDocument document, ICultMeshBodyReadLease lease)
        {
            using var view = EveUnityEntitySoaView.Open(document, lease);
            var presented = new List<EveUnityPresentedEntity>();
            var next = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 0; row < view.EntityCount; row++)
            {
                if (!view.TryReadInt32("entity.index", row, out var entityIndex)) continue;
                if (view.TryReadByte("render.visibility", row, out var visible) && visible == 0) continue;
                if (!view.TryReadVector3("transform.position", row, out var position)) continue;
                view.TryReadFloat("transform.rotation.radians", row, out var rotationRadians);
                view.TryReadVector3("transform.velocity", row, out var velocity);
                view.TryReadFloat("physics.body.radius", row, out var radius);
                view.TryReadFloat("render.scale", row, out var scale);
                view.TryReadInt32("render.lod", row, out var lod);
                view.TryReadUInt32("render.group.id", row, out var groupId);
                var renderGroup = view.RenderGroups.FirstOrDefault(group => group.GroupId == groupId);
                var identity = document.Identities.FirstOrDefault(item => item.Index == entityIndex);
                var entityId = identity?.EntityId ?? $"entity:{entityIndex}";
                next.Add(entityId);
                var assetRef = string.IsNullOrWhiteSpace(identity?.AssetRef)
                    ? renderGroup?.MeshAssetRef ?? ""
                    : identity!.AssetRef;
                var scalarState = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var semantic in view.FloatSemantics)
                    if (view.TryReadFloat(semantic, row, out var scalar))
                        scalarState[semantic] = scalar;
                var fact = new EveUnityPresentedEntity(
                    entityIndex,
                    entityId,
                    identity?.EntityKind ?? "entity",
                    string.IsNullOrWhiteSpace(identity?.Label) ? entityId : identity!.Label,
                    identity?.Faction ?? "",
                    position,
                    rotationRadians * 57.2957795f,
                    velocity,
                    radius,
                    scale > 0f ? scale : renderGroup?.DefaultScale ?? 1f,
                    groupId,
                    lod,
                    identity?.Selectable ?? true,
                    identity?.Controllable ?? false,
                    assetRef,
                    scalarState);
                presented.Add(fact);
                if (_sink is IEveUnityEntityGenerationSink)
                    continue;
                _sink.UpsertEntity(
                    fact.ToPlayableWorldEntity(),
                    new EveUnityPlayableWorldAssetBinding(
                        assetRef,
                        "entity",
                        string.IsNullOrWhiteSpace(assetRef) ? "unity-generated-placeholder" : "provider-asset-ref"));
            }

            if (_sink is IEveUnityEntityGenerationSink generationSink)
            {
                generationSink.ApplyGeneration(new EveUnityPresentedEntityGeneration(
                    document.ViewId, document.ProducerEpoch, document.Sequence, presented));
            }
            else
            {
                foreach (var entityId in _known.Where(entityId => !next.Contains(entityId)).ToArray())
                    _sink.RemoveEntity(entityId);
            }
            _known.Clear();
            foreach (var entityId in next) _known.Add(entityId);
        }
    }
}
