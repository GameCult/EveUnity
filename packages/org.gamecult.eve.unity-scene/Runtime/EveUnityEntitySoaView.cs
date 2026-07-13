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
        private readonly ICultMeshBodyReadLease _body;
        private readonly Dictionary<string, EveEntitySoaColumn> _columns;

        private EveUnityEntitySoaView(EveEntitySoaViewDocument document, ICultMeshBodyReadLease body)
        {
            Document = document;
            _body = body;
            _columns = document.Columns.ToDictionary(column => column.Semantic, StringComparer.Ordinal);
        }

        public EveEntitySoaViewDocument Document { get; }
        public long Generation => Document.Generation;
        public IReadOnlyList<EveEntityRenderGroup> RenderGroups => Document.RenderGroups;

        public static EveUnityEntitySoaView Open(EveEntitySoaViewDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!string.Equals(document.Schema, EveEntitySoaViewDocument.SchemaId, StringComparison.Ordinal))
                throw new ArgumentException($"Expected {EveEntitySoaViewDocument.SchemaId}, got '{document.Schema}'.", nameof(document));
            if (document.Body == null)
                throw new InvalidOperationException("Eve entity SoA view did not advertise a CultMesh body.");
            var request = new CultMeshBodyValidationRequest
            {
                BodyId = document.Body.BodyId,
                SchemaId = document.Body.SchemaId,
                LayoutVersion = document.Body.LayoutVersion,
                ProducerEpoch = document.Body.ProducerEpoch,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                NowUtc = DateTimeOffset.UtcNow
            };
            var body = new CultMeshSharedMemoryBodyAdapter().OpenReadOnly(document.Body, request);
            return new EveUnityEntitySoaView(document, body);
        }

        public bool TryReadVector3(string semantic, int index, out Vector3 value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float3", 12, out var column)) return false;
            var offset = column.ByteOffset + (long)index * column.ElementStride;
            value = new Vector3(_body.ReadSingle(offset), _body.ReadSingle(offset + 4), _body.ReadSingle(offset + 8));
            return true;
        }

        public bool TryReadFloat(string semantic, int index, out float value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float32", 4, out var column)) return false;
            value = _body.ReadSingle(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public bool TryReadInt32(string semantic, int index, out int value)
        {
            value = default;
            if (!TryColumn(semantic, index, "int32", 4, out var column)) return false;
            value = _body.ReadInt32(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public bool TryReadUInt32(string semantic, int index, out uint value)
        {
            value = default;
            if (!TryColumn(semantic, index, "uint32", 4, out var column)) return false;
            value = unchecked((uint)_body.ReadInt32(column.ByteOffset + (long)index * column.ElementStride));
            return true;
        }

        public int EntityCount => Document.Columns.Length == 0
            ? 0
            : Document.Columns.Min(column => column.ElementCount);

        public bool TryReadByte(string semantic, int index, out byte value)
        {
            value = default;
            if (!TryColumn(semantic, index, "uint8", 1, out var column)) return false;
            value = _body.ReadByte(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public void Dispose()
        {
            _body.Dispose();
        }

        private bool TryColumn(
            string semantic,
            int index,
            string scalarType,
            int minimumStride,
            out EveEntitySoaColumn column)
        {
            column = null!;
            if (index < 0 || !_columns.TryGetValue(semantic ?? "", out column)) return false;
            if (index >= column.ElementCount || column.ElementStride < minimumStride) return false;
            if (!string.Equals(column.ScalarType, scalarType, StringComparison.Ordinal)) return false;
            return DocumentBufferContains(column);
        }

        private bool DocumentBufferContains(EveEntitySoaColumn column)
        {
            var buffer = Document.Buffers.FirstOrDefault(candidate =>
                string.Equals(candidate.BufferId, column.BufferId, StringComparison.Ordinal));
            if (buffer == null || buffer.ByteOffset != 0 || Document.Body == null) return false;
            var columnEnd = column.ByteOffset + (long)column.ElementStride * column.ElementCount;
            return column.ByteOffset >= 0 && columnEnd <= Document.Body.ByteSize;
        }
    }
}
