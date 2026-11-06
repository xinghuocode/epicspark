using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SparkCode.Generation
{
    /// <summary>Creates the URP post-processing volume used by the start scene.</summary>
    public static class PostFxBuilder
    {
        public const string ProfilePath = "Assets/Generated/Profiles/TemplePost.asset";

        public static Volume Build()
        {
            TempleBuildKit.EnsureAssetFolder("Assets/Generated/Profiles");
            AssetDatabase.DeleteAsset(ProfilePath);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "TemplePost";
            AssetDatabase.CreateAsset(profile, ProfilePath);

            var tonemapping = Add<Tonemapping>(profile);
            tonemapping.mode.Override(TonemappingMode.ACES);

            var color = Add<ColorAdjustments>(profile);
            color.postExposure.Override(0.12f);
            color.contrast.Override(14f);
            color.saturation.Override(8f);
            color.colorFilter.Override(new Color(1f, 0.985f, 0.95f, 1f));

            var whiteBalance = Add<WhiteBalance>(profile);
            whiteBalance.temperature.Override(4f);
            whiteBalance.tint.Override(1f);

            var bloom = Add<Bloom>(profile);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(1.25f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.86f, 0.62f, 1f));
            bloom.highQualityFiltering.Override(true);
            bloom.skipIterations.Override(1);

            var vignette = Add<Vignette>(profile);
            vignette.intensity.Override(0.3f);
            vignette.smoothness.Override(0.42f);
            vignette.color.Override(new Color(0.03f, 0.03f, 0.04f, 1f));

            var dof = Add<DepthOfField>(profile);
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(13.5f);
            dof.aperture.Override(4.2f);
            dof.focalLength.Override(58f);
            dof.bladeCount.Override(7);

            var grain = Add<FilmGrain>(profile);
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.08f);
            grain.response.Override(0.65f);

            var ca = Add<ChromaticAberration>(profile);
            ca.intensity.Override(0.055f);

            var distortion = Add<LensDistortion>(profile);
            distortion.intensity.Override(-0.045f);
            distortion.scale.Override(1.0f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var go = new GameObject("Global Volume (Temple Post)");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.weight = 1f;
            volume.sharedProfile = profile;
            return volume;
        }

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            T component = profile.Add<T>(true);
            if (!AssetDatabase.Contains(component))
            {
                AssetDatabase.AddObjectToAsset(component, profile);
            }
            return component;
        }
    }
}
