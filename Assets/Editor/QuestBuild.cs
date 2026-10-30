using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class QuestBuild
{
    public static void BuildAndRun()
    {
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        string directory = Path.Combine("Builds", "Quest");
        Directory.CreateDirectory(directory);
        string apk = Path.GetFullPath(Path.Combine(directory, "Test.apk"));
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.AutoRunPlayer
        };
        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(options);
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError("Quest build failed: " + ex);
            EditorApplication.Exit(1);
            return;
        }
        if (report.summary.result != BuildResult.Succeeded)
        {
            UnityEngine.Debug.LogError("Quest build failed: " + report.summary.result + " errors=" + report.summary.totalErrors);
            EditorApplication.Exit(1);
            return;
        }
        UnityEngine.Debug.Log("Quest build succeeded: " + apk);
        EditorApplication.Exit(0);
    }
}
