using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using GameCult.Eve.Surface;
using UnityEngine;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityEntitySoaView : IDisposable
    {
        private readonly Dictionary<string, MemoryMappedViewAccessor> _buffers =
            new Dictionary<string, MemoryMappedViewAccessor>(StringComparer.Ordinal);
        private readonly Dictionary<string, EveEntitySoaColumn> _columns;

        private EveUnityEntitySoaView(EveEntitySoaViewDocument document)
        {
            Document = document;
            _columns = document.Columns.ToDictionary(column => column.Semantic, StringComparer.Ordinal);
            foreach (var buffer in document.Buffers)
            {
                if (!string.Equals(buffer.Backend, "memory_mapped_file", StringComparison.Ordinal))
                    throw new NotSupportedException($"EveUnity does not support entity SoA backend '{buffer.Backend}'.");
                var mapped = MemoryMappedFile.OpenExisting(buffer.Location, MemoryMappedFileRights.Read);
                _buffers.Add(buffer.BufferId, mapped.CreateViewAccessor(
                    buffer.ByteOffset,
                    buffer.ByteLength,
                    MemoryMappedFileAccess.Read));
                mapped.Dispose();
            }
        }

        public EveEntitySoaViewDocument Document { get; }
        public long Generation => Document.Generation;
        public IReadOnlyList<EveEntityRenderGroup> RenderGroups => Document.RenderGroups;

        public static EveUnityEntitySoaView Open(EveEntitySoaViewDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!string.Equals(document.Schema, EveEntitySoaViewDocument.SchemaId, StringComparison.Ordinal))
                throw new ArgumentException($"Expected {EveEntitySoaViewDocument.SchemaId}, got '{document.Schema}'.", nameof(document));
            return new EveUnityEntitySoaView(document);
        }

        public bool TryReadVector3(string semantic, int index, out Vector3 value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float32", 12, out var column, out var accessor)) return false;
            var offset = column.ByteOffset + (long)index * column.ElementStride;
            value = new Vector3(accessor.ReadSingle(offset), accessor.ReadSingle(offset + 4), accessor.ReadSingle(offset + 8));
            return true;
        }

        public bool TryReadFloat(string semantic, int index, out float value)
        {
            value = default;
            if (!TryColumn(semantic, index, "float32", 4, out var column, out var accessor)) return false;
            value = accessor.ReadSingle(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public bool TryReadInt32(string semantic, int index, out int value)
        {
            value = default;
            if (!TryColumn(semantic, index, "int32", 4, out var column, out var accessor)) return false;
            value = accessor.ReadInt32(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public bool TryReadByte(string semantic, int index, out byte value)
        {
            value = default;
            if (!TryColumn(semantic, index, "uint8", 1, out var column, out var accessor)) return false;
            value = accessor.ReadByte(column.ByteOffset + (long)index * column.ElementStride);
            return true;
        }

        public void Dispose()
        {
            foreach (var accessor in _buffers.Values) accessor.Dispose();
            _buffers.Clear();
        }

        private bool TryColumn(
            string semantic,
            int index,
            string scalarType,
            int minimumStride,
            out EveEntitySoaColumn column,
            out MemoryMappedViewAccessor accessor)
        {
            column = null!;
            accessor = null!;
            if (index < 0 || !_columns.TryGetValue(semantic ?? "", out column)) return false;
            if (index >= column.ElementCount || column.ElementStride < minimumStride) return false;
            if (!string.Equals(column.ScalarType, scalarType, StringComparison.Ordinal)) return false;
            return _buffers.TryGetValue(column.BufferId, out accessor);
        }
    }
}
