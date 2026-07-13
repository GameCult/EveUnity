using System;
using GameCult.Caching;
using MessagePack;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.entity_soa_view", SchemaId)]
    [MessagePackObject]
    public sealed class EveEntitySoaViewDocument
    {
        public const string SchemaId = "gamecult.eve.entity_soa_view.v1";

        [Key(0)] public string Schema { get; set; } = SchemaId;
        [Key(1)] public string ProviderId { get; set; } = "";
        [Key(2)] public string ViewId { get; set; } = "";
        [Key(3)] public long FrameId { get; set; }
        [Key(4)] public long Generation { get; set; }
        [Key(5)] public string PublishedAtUtc { get; set; } = "";
        [Key(6)] public string Backend { get; set; } = "cultcache";
        [Key(7)] public string SynchronizationMode { get; set; } = "immutable_frame";
        [Key(8)] public EveEntitySoaBuffer[] Buffers { get; set; } = Array.Empty<EveEntitySoaBuffer>();
        [Key(9)] public EveEntitySoaColumn[] Columns { get; set; } = Array.Empty<EveEntitySoaColumn>();
        [Key(10)] public EveEntitySoaDirtyRange[] DirtyRanges { get; set; } = Array.Empty<EveEntitySoaDirtyRange>();
        [Key(11)] public EveEntityRenderGroup[] RenderGroups { get; set; } = Array.Empty<EveEntityRenderGroup>();
        [Key(12)] public EveEntitySoaIdentity[] Identities { get; set; } = Array.Empty<EveEntitySoaIdentity>();
        [Key(13)] public CultMeshBodyDescriptor? Body { get; set; }
    }

    [MessagePackObject]
    public sealed class EveEntitySoaIdentity
    {
        [Key(0)] public int EntityIndex { get; set; } = -1;
        [Key(1)] public string EntityId { get; set; } = "";
        [Key(2)] public string Kind { get; set; } = "";
        [Key(3)] public string Label { get; set; } = "";
        [Key(4)] public string Faction { get; set; } = "";
        [Key(5)] public bool Selectable { get; set; } = true;
        [Key(6)] public bool Controllable { get; set; }
        [Key(7)] public string AssetRef { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class EveEntitySoaBuffer
    {
        [Key(0)] public string BufferId { get; set; } = "";
        [Key(1)] public string Backend { get; set; } = "cultcache";
        [Key(2)] public string Location { get; set; } = "";
        [Key(3)] public long ByteOffset { get; set; }
        [Key(4)] public long ByteLength { get; set; }
        [Key(5)] public long Generation { get; set; }
    }

    [MessagePackObject]
    public sealed class EveEntitySoaColumn
    {
        [Key(0)] public string ColumnId { get; set; } = "";
        [Key(1)] public string Semantic { get; set; } = "";
        [Key(2)] public string BufferId { get; set; } = "";
        [Key(3)] public string ScalarType { get; set; } = "";
        [Key(4)] public long ByteOffset { get; set; }
        [Key(5)] public int ElementStride { get; set; }
        [Key(6)] public int ElementCount { get; set; }
        [Key(7)] public string Unit { get; set; } = "";
        [Key(8)] public string CoordinateSpace { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class EveEntitySoaDirtyRange
    {
        [Key(0)] public string ColumnId { get; set; } = "";
        [Key(1)] public int StartIndex { get; set; }
        [Key(2)] public int Count { get; set; }
        [Key(3)] public long Generation { get; set; }
    }

    [MessagePackObject]
    public sealed class EveEntityRenderGroup
    {
        [Key(0)] public int GroupId { get; set; }
        [Key(1)] public string MeshAssetRef { get; set; } = "";
        [Key(2)] public string MaterialAssetRef { get; set; } = "";
        [Key(3)] public int SubMeshIndex { get; set; }
        [Key(4)] public int Layer { get; set; }
        [Key(5)] public int InstanceCount { get; set; }
        [Key(6)] public float DefaultScale { get; set; } = 1f;
        [Key(7)] public float BoundsCenterX { get; set; }
        [Key(8)] public float BoundsCenterY { get; set; }
        [Key(9)] public float BoundsCenterZ { get; set; }
        [Key(10)] public float BoundsSizeX { get; set; }
        [Key(11)] public float BoundsSizeY { get; set; }
        [Key(12)] public float BoundsSizeZ { get; set; }
        [Key(13)] public string ShadowMode { get; set; } = "on";
        [Key(14)] public bool ReceiveShadows { get; set; } = true;
        [Key(15)] public int Lod { get; set; } = -1;
    }
}
