using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityEntitySoaView : IDisposable
    {
        private readonly ICultMeshBodyReadLease _lease;
        private readonly Dictionary<string, EveEntitySoaBuffer> _buffers;
        private readonly Dictionary<string, EveEntitySoaColumn> _columns;

        private EveUnityEntitySoaView(EveEntitySoaViewDocument document, ICultMeshBodyReadLease lease)
        {
            Document = document;
            _lease = lease;
            _buffers = document.Buffers.ToDictionary(buffer => buffer.BufferId, StringComparer.Ordinal);
            _columns = document.Columns.ToDictionary(column => column.Semantic, StringComparer.Ordinal);
        }

        public EveEntitySoaViewDocument Document { get; }
        public long Generation => Document.Sequence;
        public IReadOnlyList<EveEntityRenderGroup> RenderGroups => Document.RenderGroups;
        public int EntityCount => Document.Columns.Length == 0 ? 0 : Document.Columns.Min(column => column.ElementCount);

        public static EveUnityEntitySoaView Open(EveEntitySoaViewDocument document, ICultMeshBodyReadLease lease)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            Validate(document, lease);
            return new EveUnityEntitySoaView(document, lease);
        }

        public bool TryReadVector3(string semantic, int index, out Vector3 value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float3", 12, out var offset)) return false;
            value = new Vector3(_lease.ReadSingle(offset), _lease.ReadSingle(offset + 4), _lease.ReadSingle(offset + 8));
            return true;
        }

        public bool TryReadFloat(string semantic, int index, out float value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float32", 4, out var offset)) return false;
            value = _lease.ReadSingle(offset);
            return true;
        }

        public bool TryReadInt32(string semantic, int index, out int value)
        {
            value = default;
            if (!TryColumn(semantic, index, "int32", 4, out var offset)) return false;
            value = _lease.ReadInt32(offset);
            return true;
        }

        public bool TryReadUInt32(string semantic, int index, out uint value)
        {
            value = default;
            if (!TryColumn(semantic, index, "uint32", 4, out var offset)) return false;
            value = unchecked((uint)_lease.ReadInt32(offset));
            return true;
        }

        public bool TryReadByte(string semantic, int index, out byte value)
        {
            value = default;
            if (!TryColumn(semantic, index, "uint8", 1, out var offset)) return false;
            value = _lease.ReadByte(offset);
            return true;
        }

        public void Dispose() => _lease.Dispose();

        private static void Validate(EveEntitySoaViewDocument document, ICultMeshBodyReadLease lease)
        {
            if (!string.Equals(document.Schema, EveEntitySoaViewDocument.SchemaId, StringComparison.Ordinal))
                throw new ArgumentException($"Expected {EveEntitySoaViewDocument.SchemaId}, got '{document.Schema}'.", nameof(document));
            if (document.Buffers == null || document.Buffers.Length == 0)
                throw new ArgumentException("Entity SoA view requires a primary logical buffer.", nameof(document));
            var descriptor = lease.Descriptor ?? throw new ArgumentException("Body lease has no descriptor.", nameof(lease));
            var primary = document.Buffers[0];
            if (!string.Equals(descriptor.BodyId, primary.BufferId, StringComparison.Ordinal) ||
                !string.Equals(descriptor.SchemaId, document.BodySchemaId, StringComparison.Ordinal) ||
                descriptor.LayoutVersion != document.LayoutVersion ||
                descriptor.ProducerEpoch != document.ProducerEpoch ||
                descriptor.Sequence != document.Sequence ||
                descriptor.Capacity != document.Capacity)
                throw new InvalidOperationException("CultMesh body lease generation does not match the Eve entity layout.");
            if (document.Capacity < 0 || document.Buffers.Any(buffer =>
                    string.IsNullOrWhiteSpace(buffer.BufferId) || buffer.ByteOffset < 0 || buffer.ByteLength < 0 ||
                    buffer.ByteOffset > descriptor.ByteSize - buffer.ByteLength))
                throw new InvalidOperationException("Eve entity layout contains an invalid logical buffer range.");
            var buffers = document.Buffers.ToDictionary(buffer => buffer.BufferId, StringComparer.Ordinal);
            foreach (var column in document.Columns ?? Array.Empty<EveEntitySoaColumn>())
            {
                if (!buffers.TryGetValue(column.BufferId, out var buffer) || column.ByteOffset < 0 ||
                    column.ElementStride <= 0 || column.ElementCount < 0 || column.ElementCount > document.Capacity)
                    throw new InvalidOperationException($"Eve entity column '{column.ColumnId}' has invalid layout metadata.");
                var width = ScalarWidth(column.ScalarType);
                var occupied = column.ElementCount == 0 ? 0 : (long)(column.ElementCount - 1) * column.ElementStride + width;
                if (width == 0 || column.ByteOffset > buffer.ByteLength - occupied)
                    throw new InvalidOperationException($"Eve entity column '{column.ColumnId}' exceeds its logical buffer.");
            }
        }

        private bool TryColumn(string semantic, int index, string scalarType, int minimumStride, out long offset)
        {
            offset = 0;
            if (index < 0 || !_columns.TryGetValue(semantic ?? "", out var column)) return false;
            if (index >= column.ElementCount || column.ElementStride < minimumStride) return false;
            if (!string.Equals(column.ScalarType, scalarType, StringComparison.Ordinal)) return false;
            if (!_buffers.TryGetValue(column.BufferId, out var buffer)) return false;
            offset = buffer.ByteOffset + column.ByteOffset + (long)index * column.ElementStride;
            return true;
        }

        private static int ScalarWidth(string scalarType) => scalarType switch
        {
            "float3" => 12,
            "float32" => 4,
            "int32" => 4,
            "uint32" => 4,
            "uint8" => 1,
            _ => 0
        };
    }
}
