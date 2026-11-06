using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Procedural temple generator (fallback). The primary environment is the Blender model imported by
    /// <see cref="BlenderTempleImporter"/>; this fallback keeps the scene buildable without the FBX.
    /// </summary>
    public static class TempleEnvironmentBuilder
    {
        public static EnvironmentInfo Build(TempleBuildKit kit)
        {
            var root = kit.Create("Temple_Procedural");
            var t = root.transform;

            EnvironmentBaseBuilder.Build(kit, t);
            EnvironmentStructureBuilder.Build(kit, t);
            EnvironmentDomeBuilder.Build(kit, t);

            var anchor = kit.Create("SparkAnchor", t);
            anchor.transform.position = TempleLayout.SparkAnchor;

            return new EnvironmentInfo
            {
                Root = t,
                SparkAnchor = anchor.transform,
                CameraTarget = TempleLayout.CameraTarget
            };
        }
    }
}
