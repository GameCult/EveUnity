using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    internal interface IEveUnityRenderPassSource
    {
        int RenderOrder { get; }
        bool TryEnqueuePass(Camera camera, ScriptableRenderer renderer);
    }

    internal static class EveUnityRenderPassRegistry
    {
        private static readonly List<IEveUnityRenderPassSource> Sources =
            new List<IEveUnityRenderPassSource>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => Sources.Clear();

        internal static void Register(IEveUnityRenderPassSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (Sources.Contains(source)) return;
            Sources.Add(source);
            Sources.Sort(static (left, right) => left.RenderOrder.CompareTo(right.RenderOrder));
        }

        internal static void Unregister(IEveUnityRenderPassSource source) => Sources.Remove(source);

        internal static void Enqueue(Camera camera, ScriptableRenderer renderer)
        {
            for (var index = 0; index < Sources.Count; index++)
                Sources[index].TryEnqueuePass(camera, renderer);
        }
    }

    /// <summary>
    /// URP-owned scheduling point for generic Eve world effects.
    /// Add this feature to the Universal Renderer used by Eve cameras.
    /// Provider surfaces supply effect programs, assets, and parameters; this feature only lowers them.
    /// </summary>
    public sealed class EveUnityRendererFeature : ScriptableRendererFeature
    {
        public override void Create()
        {
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (camera == null || renderingData.cameraData.cameraType != CameraType.Game) return;
            EveUnityRenderPassRegistry.Enqueue(camera, renderer);
        }
    }
}
