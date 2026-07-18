using System.Linq;
using GameCult.Eve.UnityScene;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class GenericEveUnityRenderPipeline
{
    private const string PostProcessDataGuid = "41439944d30ece34e96484bdb6645b55";
    private const string RendererPath = "Assets/GenericEveUnityRenderer.asset";
    private const string PipelinePath = "Assets/GenericEveUnityRenderPipeline.asset";

    public static void Configure()
    {
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (renderer == null)
        {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, RendererPath);
        }

        renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
            AssetDatabase.GUIDToAssetPath(PostProcessDataGuid));
        if (!renderer.rendererFeatures.OfType<EveUnityRendererFeature>().Any())
        {
            var feature = ScriptableObject.CreateInstance<EveUnityRendererFeature>();
            feature.name = "Eve Unity World Effects";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
        }
        EditorUtility.SetDirty(renderer);

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, PipelinePath);
        }

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
    }
}
