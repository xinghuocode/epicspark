using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Procedural fallback dome (used only if the Blender FBX is unavailable).</summary>
    public static class EnvironmentDomeBuilder
    {
        public static void Build(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Dome", root).transform;
            var steps = new TempleMeshFactory();
            int n = TempleLayout.DomeStepCount;
            float[] r = new float[n + 1];
            float[] y = new float[n + 1];
            for (int k = 0; k <= n; k++)
            {
                float t = (float)k / n;
                r[k] = 10.1f - 7.105f * t - 0.645f * t * t;
                y[k] = 11.9f + 12.6f * t;
            }
            for (int k = 0; k < n; k++)
            {
                steps.AddCylinderWall(y[k], y[k + 1], r[k], r[k], 128, inward: true, outward: false, uvScale: 0.35f);
                steps.AddAnnulus(y[k + 1], r[k + 1], r[k], 128, facingUp: false, uvScale: 0.35f);
            }
            kit.CreateMeshObject("Dome_Steps", steps.Build("Dome_Steps"), kit.Marble, holder);

            var shell = new TempleMeshFactory();
            var profile = new Vector2[n + 2];
            for (int k = 0; k <= n; k++) profile[k] = new Vector2(r[k] + 0.6f, y[k]);
            profile[n + 1] = new Vector2(2.8f, 25f);
            shell.AddLathe(profile, 128, outward: true, uvScale: 0.35f);
            kit.CreateMeshObject("Dome_Shell", shell.Build("Dome_Shell"), kit.Marble, holder);

            var trim = new TempleMeshFactory();
            trim.AddRingBand(9.9f, 10.4f, 11.72f, 11.95f, 128, top: true, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            trim.AddRingBand(2.35f, 2.8f, 24.5f, 25.0f, 128, top: true, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            kit.CreateMeshObject("Dome_Trim", trim.Build("Dome_Trim"), kit.MarbleTrim, holder);

            var gold = new TempleMeshFactory();
            gold.AddTorus(11.94f, 10.2f, 0.03f, 128, 6, 0.5f);
            gold.AddTorus(24.52f, 2.42f, 0.055f, 128, 6, 0.5f);
            kit.CreateMeshObject("Dome_Gold", gold.Build("Dome_Gold"), kit.Gold, holder);
        }
    }
}
