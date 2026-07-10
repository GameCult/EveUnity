using GameCult.Eve.PluginFields;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GameCult.Eve.UnityScene.Fields
{
    public class EveFieldsSplatRasterizer : MonoBehaviour
    {
        [SerializeField] private Material splatMaterial;
        [SerializeField] private RenderTexture targetTexture;
        [SerializeField] private GraphicsFormat targetFormat = GraphicsFormat.R16_SFloat;
        [SerializeField] private int width = 512;
        [SerializeField] private int height = 512;
        [SerializeField] private int channelFilter = -1;
        [SerializeField] private int layerFilter = -1;
        [SerializeField] private int materialPass;
        [SerializeField] private Color clearColor = Color.clear;
        [SerializeField] private bool clearBeforeDraw = true;

        private readonly EveFieldsSplatBuffer splatBuffer = new EveFieldsSplatBuffer();
        private CommandBuffer commandBuffer;
        private RenderTexture ownedTargetTexture;
        private Material ownedSplatMaterial;
        private static readonly int SplatBufferId = Shader.PropertyToID(EveFieldsSplatBuffer.SplatBufferPropertyName);
        private static readonly int SplatCountId = Shader.PropertyToID(EveFieldsSplatBuffer.SplatCountPropertyName);
        private static readonly int ViewportToClipId = Shader.PropertyToID(EveFieldsSplatBuffer.ViewportToClipPropertyName);
        private static readonly int ChannelFilterId = Shader.PropertyToID(EveFieldsSplatBuffer.ChannelFilterPropertyName);

        public RenderTexture TargetTexture => targetTexture != null ? targetTexture : ownedTargetTexture;
        public int LastDrawnCount { get; private set; }
        public int ChannelFilter { get => channelFilter; set => channelFilter = value; }
        public int LayerFilter { get => layerFilter; set => layerFilter = value; }

        public RenderTexture Render(IEveFieldsSplatsDocument document, int overrideWidth = 0, int overrideHeight = 0)
        {
            if (document == null) { LastDrawnCount = 0; return TargetTexture; }
            var output = EnsureTarget(overrideWidth, overrideHeight);
            var material = ResolveMaterial();
            if (output == null || material == null) { LastDrawnCount = 0; return output; }
            splatBuffer.Upload(document.Splats, channelFilter, layerFilter);
            LastDrawnCount = splatBuffer.Count;
            if (!splatBuffer.HasGpuBuffer) { if (clearBeforeDraw) Clear(output); return output; }
            commandBuffer ??= new CommandBuffer { name = "Eve Fields Splats" };
            commandBuffer.Clear();
            commandBuffer.SetRenderTarget(output);
            if (clearBeforeDraw) commandBuffer.ClearRenderTarget(false, true, clearColor);
            commandBuffer.SetGlobalBuffer(SplatBufferId, splatBuffer.Buffer);
            commandBuffer.SetGlobalInt(SplatCountId, splatBuffer.Count);
            commandBuffer.SetGlobalInt(ChannelFilterId, channelFilter);
            commandBuffer.SetGlobalMatrix(ViewportToClipId, BuildViewportToClip(document.Viewport));
            commandBuffer.DrawProcedural(Matrix4x4.identity, material, Mathf.Max(0, materialPass), MeshTopology.Triangles, 6, splatBuffer.Count);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            return output;
        }

        public RenderTexture RenderLayerToTarget(IEveFieldsSplatsDocument document, RenderTexture output, int overrideLayerFilter, int overrideMaterialPass, Color overrideClearColor)
        {
            var oldTarget = targetTexture; var oldLayer = layerFilter; var oldPass = materialPass; var oldClear = clearColor;
            targetTexture = output; layerFilter = overrideLayerFilter; materialPass = overrideMaterialPass; clearColor = overrideClearColor;
            try { return Render(document, output != null ? output.width : 0, output != null ? output.height : 0); }
            finally { targetTexture = oldTarget; layerFilter = oldLayer; materialPass = oldPass; clearColor = oldClear; }
        }

        private Material ResolveMaterial()
        {
            if (splatMaterial != null) return splatMaterial;
            if (ownedSplatMaterial != null) return ownedSplatMaterial;
            var shader = Shader.Find("Eve/Fields/Splats");
            if (shader == null) return null;
            ownedSplatMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
            return ownedSplatMaterial;
        }

        private RenderTexture EnsureTarget(int overrideWidth, int overrideHeight)
        {
            if (targetTexture != null) return targetTexture;
            var targetWidth = Mathf.Max(1, overrideWidth > 0 ? overrideWidth : width);
            var targetHeight = Mathf.Max(1, overrideHeight > 0 ? overrideHeight : height);
            if (ownedTargetTexture != null && ownedTargetTexture.width == targetWidth && ownedTargetTexture.height == targetHeight && ownedTargetTexture.graphicsFormat == targetFormat) return ownedTargetTexture;
            ReleaseOwnedTarget();
            var descriptor = new RenderTextureDescriptor(targetWidth, targetHeight) { depthBufferBits = 0, graphicsFormat = targetFormat, msaaSamples = 1, sRGB = false, useMipMap = false, autoGenerateMips = false };
            ownedTargetTexture = new RenderTexture(descriptor) { name = "Eve Fields Splats", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            ownedTargetTexture.Create();
            return ownedTargetTexture;
        }

        private void Clear(RenderTexture output)
        {
            commandBuffer ??= new CommandBuffer { name = "Eve Fields Splats" };
            commandBuffer.Clear(); commandBuffer.SetRenderTarget(output); commandBuffer.ClearRenderTarget(false, true, clearColor); Graphics.ExecuteCommandBuffer(commandBuffer);
        }

        private static Matrix4x4 BuildViewportToClip(IEveFieldsViewport viewport)
        {
            var minX = (float)System.Math.Min(viewport?.MinX ?? 0, viewport?.MaxX ?? 0);
            var minY = (float)System.Math.Min(viewport?.MinY ?? 0, viewport?.MaxY ?? 0);
            var maxX = (float)System.Math.Max(viewport?.MinX ?? 0, viewport?.MaxX ?? 0);
            var maxY = (float)System.Math.Max(viewport?.MinY ?? 0, viewport?.MaxY ?? 0);
            var spanX = Mathf.Max(0.0001f, maxX - minX); var spanY = Mathf.Max(0.0001f, maxY - minY);
            return new Matrix4x4(new Vector4(2f / spanX, 0, 0, 0), new Vector4(0, 2f / spanY, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(-(maxX + minX) / spanX, -(maxY + minY) / spanY, 0, 1));
        }

        private void OnDisable()
        {
            splatBuffer.Dispose(); commandBuffer?.Release(); commandBuffer = null;
            if (ownedSplatMaterial != null) { Destroy(ownedSplatMaterial); ownedSplatMaterial = null; }
            ReleaseOwnedTarget(); LastDrawnCount = 0;
        }

        private void ReleaseOwnedTarget()
        {
            if (ownedTargetTexture == null) return;
            ownedTargetTexture.Release(); Destroy(ownedTargetTexture); ownedTargetTexture = null;
        }
    }
}
