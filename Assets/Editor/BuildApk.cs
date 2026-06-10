using UnityEditor;
using UnityEngine;

public class BuildApk
{
    public static void Build()
    {
        string[] scenes = { "Assets/RealityLog/Scenes/RealityLogScene.unity" };

        PlayerSettings.Android.keystoreName = "debug.keystore";
        PlayerSettings.Android.keystorePass = "android";
        PlayerSettings.Android.keyaliasName = "androiddebugkey";
        PlayerSettings.Android.keyaliasPass = "android";

        BuildPlayerOptions opts = new BuildPlayerOptions
        {
            scenes           = scenes,
            locationPathName = "kiko.apk",
            target           = BuildTarget.Android,
            options          = BuildOptions.None
        };

        BuildPipeline.BuildPlayer(opts);
    }
}
