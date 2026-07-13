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
            var next = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 0; row < view.EntityCount; row++)
            {
                if (!view.TryReadInt32("entity.index", row, out var entityIndex)) continue;
                if (view.TryReadByte("render.visibility", row, out var visible) && visible == 0) continue;
                if (!view.TryReadVector3("transform.position", row, out var position)) continue;
                view.TryReadFloat("transform.rotation.radians", row, out var rotationRadians);
                view.TryReadFloat("render.scale", row, out var scale);
                view.TryReadUInt32("render.group.id", row, out var groupId);
                var renderGroup = view.RenderGroups.FirstOrDefault(group => group.GroupId == groupId);
                var identity = document.Identities.FirstOrDefault(item => item.Index == entityIndex);
                var entityId = identity?.EntityId ?? $"entity:{entityIndex}";
                next.Add(entityId);
                var assetRef = renderGroup?.MeshAssetRef ?? "";
                _sink.UpsertEntity(
                    new EveUnityPlayableWorldEntity(
                        entityId,
                        entityId,
                        identity?.EntityKind ?? "entity",
                        entityId,
                        "",
                        assetRef,
                        position.x,
                        position.y,
                        position.z,
                        rotationRadians * 57.2957795f,
                        scale > 0f ? scale : renderGroup?.DefaultScale ?? 1f,
                        selectable: true,
                        controllable: false,
                        focusCommand: "",
                        moveCommand: "",
                        targetCommand: "",
                        actionCommand: "",
                        props: new Dictionary<string, string>()),
                    new EveUnityPlayableWorldAssetBinding(
                        assetRef,
                        "entity",
                        string.IsNullOrWhiteSpace(assetRef) ? "unity-generated-placeholder" : "provider-asset-ref"));
            }

            foreach (var entityId in _known.Where(entityId => !next.Contains(entityId)).ToArray())
                _sink.RemoveEntity(entityId);
            _known.Clear();
            foreach (var entityId in next) _known.Add(entityId);
        }
    }
}
