using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GenericEveUnityBuild
{
    public static void BuildWindows()
    {
        const string scenePath = "Assets/GenericEveUnity.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Generic EveUnity Client").AddComponent<GenericEveUnityLauncher>();
        EditorSceneManager.SaveScene(scene, scenePath);
        Directory.CreateDirectory("Build/Windows");
        var report = BuildPipeline.BuildPlayer(new[] { scenePath }, "Build/Windows/EveUnity.exe", BuildTarget.StandaloneWindows64, BuildOptions.Development);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new System.InvalidOperationException("Generic EveUnity Windows build failed: " + report.summary.result);
    }
}
