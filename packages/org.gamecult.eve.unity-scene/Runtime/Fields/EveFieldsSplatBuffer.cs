using System;
using System.Collections.Generic;
using GameCult.Eve.PluginFields;
using UnityEngine;

namespace GameCult.Eve.UnityScene.Fields
{
    public sealed class EveFieldsSplatBuffer : IDisposable
    {
        public const string SplatBufferPropertyName = "_EveFieldsSplats";
        public const string SplatCountPropertyName = "_EveFieldsSplatCount";
        public const string ViewportToClipPropertyName = "_EveFieldsViewportToClip";
        public const string ChannelFilterPropertyName = "_EveFieldsChannelFilter";

        private const int SplatStrideBytes = 96;
        private EveFieldsGpuSplat[] splats = Array.Empty<EveFieldsGpuSplat>();
        private GraphicsBuffer buffer;
        private int capacity;

        public int Count { get; private set; }
        public GraphicsBuffer Buffer => buffer;
        public bool HasGpuBuffer => buffer != null && buffer.IsValid() && Count > 0;

        public bool Upload(IEveFieldsSplatSoa source, int channelFilter = -1, int layerFilter = -1)
        {
            Count = Pack(source, channelFilter, layerFilter);
            EnsureGpuCapacity(Count);
            if (Count <= 0)
                return false;
            buffer.SetData(splats, 0, 0, Count);
            return true;
        }

        private int Pack(IEveFieldsSplatSoa source, int channelFilter, int layerFilter)
        {
            if (source == null || source.Count <= 0)
                return 0;
            EnsureCpuCapacity(source.Count);
            var count = 0;
            for (var index = 0; index < source.Count; index++)
            {
                var channel = Read(source.Channel, index, 0);
                var layer = Read(source.LayerIndex, index, -1);
                if ((channelFilter >= 0 && channel != channelFilter) ||
                    (layerFilter >= 0 && layer != layerFilter))
                    continue;
                var halfExtentX = (float)Read(source.HalfExtentX, index, 0);
                var halfExtentY = (float)Read(source.HalfExtentY, index, 0);
                if (halfExtentX <= 0 || halfExtentY <= 0)
                    continue;
                splats[count++] = new EveFieldsGpuSplat
                {
                    CenterHalfExtent = new Vector4((float)Read(source.CenterX, index, 0), (float)Read(source.CenterY, index, 0), halfExtentX, halfExtentY),
                    RotationChannelFalloff = new Vector4((float)Read(source.RotationCos, index, 1), (float)Read(source.RotationSin, index, 0), channel, Read(source.Falloff, index, EveFieldsSplatFalloffs.Smooth)),
                    LayerSource = new Vector4(layer, Read(source.SourceKind, index, EveFieldsSplatSourceKinds.Constant), (float)Read(source.AnimationSpeed, index, 0), (float)Read(source.SourceFlags, index, 0)),
                    SourceFrequencyPhase = new Vector4((float)Read(source.FrequencyX, index, 1), (float)Read(source.FrequencyY, index, 1), (float)Read(source.PhaseX, index, 0), (float)Read(source.PhaseY, index, 0)),
                    FalloffParameters = new Vector4((float)Read(source.FalloffScale, index, 1), (float)Read(source.FalloffExponent, index, 1), 0, 0),
                    Value = new Vector4((float)Read(source.ValueR, index, 0), (float)Read(source.ValueG, index, 0), (float)Read(source.ValueB, index, 0), (float)Read(source.ValueA, index, 1))
                };
            }
            return count;
        }

        private void EnsureCpuCapacity(int count)
        {
            if (splats.Length >= count) return;
            var next = 1;
            while (next < count) next <<= 1;
            splats = new EveFieldsGpuSplat[next];
        }

        private void EnsureGpuCapacity(int count)
        {
            if (buffer != null && buffer.IsValid() && capacity >= count) return;
            buffer?.Release();
            buffer = null;
            capacity = 0;
            if (count <= 0) return;
            capacity = 1;
            while (capacity < count) capacity <<= 1;
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, SplatStrideBytes);
        }

        public void Dispose()
        {
            buffer?.Release();
            buffer = null;
            capacity = 0;
            Count = 0;
        }

        private static double Read(IReadOnlyList<double> values, int index, double fallback) =>
            values != null && index >= 0 && index < values.Count ? values[index] : fallback;
        private static int Read(IReadOnlyList<int> values, int index, int fallback) =>
            values != null && index >= 0 && index < values.Count ? values[index] : fallback;

        private struct EveFieldsGpuSplat
        {
            public Vector4 CenterHalfExtent;
            public Vector4 RotationChannelFalloff;
            public Vector4 LayerSource;
            public Vector4 SourceFrequencyPhase;
            public Vector4 FalloffParameters;
            public Vector4 Value;
        }
    }
}
