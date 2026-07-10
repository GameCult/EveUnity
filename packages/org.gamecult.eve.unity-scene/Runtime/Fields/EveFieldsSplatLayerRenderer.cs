using System;
using System.Collections.Generic;
using GameCult.Eve.PluginFields;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GameCult.Eve.UnityScene.Fields
{
    public sealed class EveFieldsSplatLayerRenderer : MonoBehaviour
    {
        [SerializeField] private EveFieldsSplatRasterizer rasterizer;
        private readonly Dictionary<string, RenderTexture> textures = new Dictionary<string, RenderTexture>(StringComparer.Ordinal);

        public bool TryGetTexture(string layerKey, out RenderTexture texture) =>
            textures.TryGetValue(layerKey ?? "", out texture);

        public void Render(IEveFieldsSplatsDocument document, IReadOnlyList<EveFieldsSplatLayerTarget> targets, int width, int height)
        {
            if (document == null || targets == null) return;
            ResolveRasterizer();
            if (rasterizer == null) return;
            var layers = document.Layers;
            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                var layer = layers[layerIndex];
                if (layer == null || string.IsNullOrWhiteSpace(layer.LayerKey)) continue;
                for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    var target = targets[targetIndex];
                    if (target == null || !target.Enabled || !string.Equals(target.LayerKey, layer.LayerKey, StringComparison.Ordinal)) continue;
                    var texture = EnsureTexture(target, layer, width, height);
                    rasterizer.RenderLayerToTarget(document, texture, layerIndex, MaterialPass(layer.BlendMode), ClearColor(layer));
                    target.TargetTexture = texture;
                    textures[layer.LayerKey] = texture;
                }
            }
        }

        private void ResolveRasterizer()
        {
            if (rasterizer != null) return;
            rasterizer = GetComponent<EveFieldsSplatRasterizer>();
            if (rasterizer == null) rasterizer = gameObject.AddComponent<EveFieldsSplatRasterizer>();
        }

        private static RenderTexture EnsureTexture(EveFieldsSplatLayerTarget target, IEveFieldsSplatLayer layer, int width, int height)
        {
            var targetWidth = Mathf.Max(1, Mathf.RoundToInt(width * Mathf.Max(0.01f, target.WidthScale)));
            var targetHeight = Mathf.Max(1, Mathf.RoundToInt(height * Mathf.Max(0.01f, target.HeightScale)));
            var formatName = string.IsNullOrWhiteSpace(target.GraphicsFormatOverride) ? layer.GraphicsFormat : target.GraphicsFormatOverride;
            var format = Enum.TryParse(formatName, out GraphicsFormat parsed) ? parsed : GraphicsFormat.R16_SFloat;
            var existing = target.TargetTexture;
            if (existing != null && existing.width == targetWidth && existing.height == targetHeight && existing.graphicsFormat == format) return existing;
            if (existing != null) { existing.Release(); Destroy(existing); }
            var descriptor = new RenderTextureDescriptor(targetWidth, targetHeight)
            {
                depthBufferBits = 0,
                graphicsFormat = format,
                msaaSamples = 1,
                sRGB = false,
                useMipMap = target.UseMipMaps,
                autoGenerateMips = target.UseMipMaps
            };
            var texture = new RenderTexture(descriptor)
            {
                name = $"Eve Fields {layer.LayerKey}",
                filterMode = target.FilterMode,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();
            return texture;
        }

        private static int MaterialPass(string blendMode) =>
            blendMode == EveFieldsSplatBlendModes.Max ? 1 : blendMode == EveFieldsSplatBlendModes.Alpha ? 2 : 0;

        private static Color ClearColor(IEveFieldsSplatLayer layer) =>
            new Color((float)layer.ClearR, (float)layer.ClearG, (float)layer.ClearB, (float)layer.ClearA);

        private void OnDisable()
        {
            foreach (var texture in textures.Values)
            {
                if (texture == null) continue;
                texture.Release();
                Destroy(texture);
            }
            textures.Clear();
        }
    }

    [Serializable]
    public sealed class EveFieldsSplatLayerTarget
    {
        public bool Enabled = true;
        public string LayerKey = "";
        public string GraphicsFormatOverride = "";
        public float WidthScale = 1f;
        public float HeightScale = 1f;
        public bool UseMipMaps;
        public FilterMode FilterMode = FilterMode.Bilinear;
        [NonSerialized] public RenderTexture TargetTexture;
    }
}
