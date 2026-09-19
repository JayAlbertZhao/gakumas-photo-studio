using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

namespace DigitalKotone.ARPhoto.Editor
{
    public static class ARPhotoBuild
    {
        public static void BuildAndroid()
        {
            ConfigureAndroid();
            string output = Environment.GetEnvironmentVariable("AR_PHOTO_APK");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "gakumas-ar-photo.apk"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            const string scenePath = "Assets/Generated/ARPhoto.unity";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Generated"));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException("Could not create the generated AR scene");

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.Development,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Android build failed: " + report.summary.result);
            Debug.Log("AR_PHOTO_APK=" + output);
        }

        private static void ConfigureAndroid()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            PlayerSettings.companyName = "Digital Kotone";
            PlayerSettings.productName = "Gakumas AR Photo";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "org.digital_kotone.arphoto.unity");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.androidIsGame = false;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // ARCore 5.1 on Unity 2022 rejects Vulkan at build time.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.OpenGLES3 });

            XRGeneralSettings settings = GetOrCreateXrSettings(BuildTargetGroup.Android);
            if (!XRPackageMetadataStore.AssignLoader(settings.Manager,
                    "UnityEngine.XR.ARCore.ARCoreLoader", BuildTargetGroup.Android))
                throw new InvalidOperationException("Could not assign the ARCore loader");

            UseExternalToolchain("ANDROID_SDK_ROOT", "AndroidSdkRoot");
            UseExternalToolchain("ANDROID_NDK_ROOT", "AndroidNdkRootR23b");
            UseExternalToolchain("JAVA_HOME", "JdkPath");

            AssetDatabase.SaveAssets();
        }

        private static XRGeneralSettings GetOrCreateXrSettings(BuildTargetGroup targetGroup)
        {
            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(targetGroup);
            if (settings != null && settings.Manager != null) return settings;

            const string directory = "Assets/Generated/XR";
            const string assetPath = directory + "/XRGeneralSettingsPerBuildTarget.asset";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Generated", "XR"));
            var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(assetPath);
            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, assetPath);
            }
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            if (!perTarget.HasManagerSettingsForBuildTarget(targetGroup))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(targetGroup);
            settings = perTarget.SettingsForBuildTarget(targetGroup);
            if (settings == null || settings.Manager == null)
                throw new InvalidOperationException("Could not create XR Management settings for " + targetGroup);
            return settings;
        }

        private static void UseExternalToolchain(string environmentName, string preferenceName)
        {
            string path = Environment.GetEnvironmentVariable(environmentName);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            EditorPrefs.SetString(preferenceName, Path.GetFullPath(path));
        }
    }
}
