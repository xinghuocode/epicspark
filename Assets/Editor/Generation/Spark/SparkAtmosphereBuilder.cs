using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SparkCode.Generation
{
    /// <summary>
    /// The "sky light" atmosphere: soft additive god-ray shafts from the dome oculus down to the
    /// island, a faint dome haze, plus slow floating dust motes. Built once in the editor and
    /// fully persisted in the scene (no runtime initialisation required).
    /// </summary>
    public static class SparkAtmosphereBuilder
    {
        const float OculusY = TempleLayout.OculusY;
        const float IslandTopY = TempleLayout.IslandTopY;

        public static void Build(TempleBuildKit kit, EnvironmentInfo env, SparkInfo spark)
        {
            if (kit == null) throw new System.ArgumentNullException("kit");

            var root = new GameObject("SparkAtmosphere");
            root.transform.SetParent(kit.Root, false);

            // --------------------------------------------------------------- materials
            Material shaftMat = kit.LightShaftMat ?? kit.CreateAdditiveMaterial("Temple_LightShaft_Fallback",
                kit.SoftGlow, new Color(1f, 0.91f, 0.72f), 0.5f, 1f);
            Material thinShaftMat = kit.CreateAdditiveMaterial("Temple_LightShaft_Thin", kit.SoftGlow,
                new Color(1f, 0.95f, 0.86f), 0.55f, 1f);
            Material hazeMat = kit.CreateAdditiveMaterial("Temple_LightShaft_Haze", kit.SoftGlow,
                new Color(1f, 0.93f, 0.82f), 0.28f, 1f);
            Material oculusMat = kit.CreateAdditiveMaterial("Temple_OculusGlow", kit.SoftGlow,
                new Color(1f, 0.95f, 0.85f), 0.55f, 1f);
            Material dustMat = kit.DustMat ?? kit.CreateAdditiveMaterial("Temple_Dust_Fallback",
                kit.SoftGlow, new Color(1f, 0.93f, 0.8f), 0.55f, 0.65f);

            // --------------------------------------------------------------- main shaft (oculus -> island)
            Vector3 mainTop = new Vector3(0f, OculusY - 0.9f, 0.12f);
            Vector3 mainBottom = new Vector3(0f, IslandTopY + 0.22f, 0.55f);
            Mesh mainShaft = SparkStarMesh.CreateShaft("Spark_LightShaft_Main", mainTop, mainBottom,
                2.2f, 4.8f, 56, 8, 0.20f, 0.10f,
                new[] { 0.55f, 0.82f, 1.12f, 1.48f },
                new[] { 0.50f, 0.34f, 0.18f, 0.07f });
            CreateAdditiveMeshObject(kit, "LightShaft_Main", mainShaft, shaftMat, root.transform, true, Vector3.one);

            // --------------------------------------------------------------- thinner, slightly tilted companion ray
            Vector3 thinTop = new Vector3(0.12f, OculusY - 1.3f, 0.28f);
            Vector3 thinBottom = new Vector3(-0.42f, IslandTopY + 0.45f, 1.25f);
            Mesh thinShaft = SparkStarMesh.CreateShaft("Spark_LightShaft_Thin", thinTop, thinBottom,
                0.95f, 2.1f, 40, 6, 0.15f, 0.06f,
                new[] { 0.70f, 1.00f, 1.36f },
                new[] { 0.55f, 0.30f, 0.11f });
            CreateAdditiveMeshObject(kit, "LightShaft_Thin", thinShaft, thinShaftMat, root.transform, true, Vector3.one);

            // --------------------------------------------------------------- wide, very faint dome haze
            Vector3 hazeTop = new Vector3(0f, OculusY + 0.6f, 0f);
            Vector3 hazeBottom = new Vector3(0f, IslandTopY + 1.2f, 0.3f);
            Mesh hazeShaft = SparkStarMesh.CreateShaft("Spark_LightShaft_Haze", hazeTop, hazeBottom,
                3.1f, 7.0f, 40, 5, 0.09f, 0.05f,
                new[] { 0.75f, 1.05f },
                new[] { 0.55f, 0.22f });
            CreateAdditiveMeshObject(kit, "LightShaft_Haze", hazeShaft, hazeMat, root.transform, true, Vector3.one);

            // --------------------------------------------------------------- bright oculus disc
            Mesh quad = kit.SaveMesh("Spark_GlowQuad", SparkStarMesh.CreateQuad("Spark_GlowQuad"));
            GameObject oculusGlow = CreateAdditiveMeshObject(kit, "OculusGlow", quad, oculusMat, root.transform, false,
                new Vector3(7.0f, 7.0f, 7.0f));
            oculusGlow.transform.localPosition = new Vector3(0f, OculusY - 0.55f, 0.05f);
            oculusGlow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            // --------------------------------------------------------------- light footprint on the island
            Vector3 islandCentre = spark.Anchor != null ? spark.Anchor.position : env.SparkAnchor.position;
            Material poolMat = kit.CreateAdditiveMaterial("Temple_IslandLightPool", kit.SoftGlow,
                new Color(1f, 0.93f, 0.78f), 0.30f, 1f);
            GameObject islandPool = CreateAdditiveMeshObject(kit, "IslandLightPool", quad, poolMat, root.transform, false,
                new Vector3(4.4f, 4.4f, 4.4f));
            islandPool.transform.localPosition = new Vector3(islandCentre.x, IslandTopY + 0.04f, islandCentre.z + 0.18f);
            islandPool.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            // --------------------------------------------------------------- dust motes (particle system)
            CreateDust(kit, root.transform, dustMat);

            // --------------------------------------------------------------- static dust specks (still frame support)
            Mesh speckMesh = SparkStarMesh.CreateVolumeSpecks("Spark_AtmosphereSpecks", 110,
                6.8f, 1.2f, 22.5f, 0.02f, 0.09f, 20261107);
            CreateAdditiveMeshObject(kit, "AtmosphereSpecks", speckMesh, dustMat, root.transform, true, Vector3.one);

            EditorUtility.SetDirty(root);
        }

        // ------------------------------------------------------------------ helpers

        static GameObject CreateAdditiveMeshObject(TempleBuildKit kit, string name, Mesh mesh, Material mat,
                                                   Transform parent, bool saveMesh, Vector3 scale)
        {
            GameObject go = kit.CreateMeshObject(name, mesh, mat, parent, saveMesh);
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return go;
        }

        static ParticleSystem CreateDust(TempleBuildKit kit, Transform parent, Material dustMat)
        {
            var go = new GameObject("AtmosphereDust");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 11.5f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 16f;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(16f, 30f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.06f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.10f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.97f, 0.9f, 1f), new Color(1f, 0.9f, 0.74f, 1f));
            main.gravityModifier = -0.002f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 340;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 12f;

            var shape = ps.shape;
            shape.enabled = true;
            // Box volume (14 x 22 x 14) centred at y = 11.5 covers y 0.5..22.5, radius ~7
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(14f, 22f, 14f);
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.randomDirectionAmount = 1f;

            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);
            velocity.y = new ParticleSystem.MinMaxCurve(0.03f, 0.14f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.22f;
            noise.frequency = 0.32f;
            noise.scrollSpeed = 0.10f;
            noise.damping = true;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(DustColorGradient());

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, DustSizeCurve());

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.alignment = ParticleSystemRenderSpace.View;
                renderer.sortMode = ParticleSystemSortMode.None;
                renderer.sharedMaterial = dustMat;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sortingFudge = -10f;
            }

            return ps;
        }

        static Gradient DustColorGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.98f, 0.92f), 0f),
                    new GradientColorKey(new Color(1f, 0.95f, 0.85f), 0.5f),
                    new GradientColorKey(new Color(1f, 0.90f, 0.78f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.85f, 0.2f),
                    new GradientAlphaKey(0.7f, 0.75f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        static AnimationCurve DustSizeCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0.4f),
                new Keyframe(0.3f, 1f),
                new Keyframe(0.75f, 0.8f),
                new Keyframe(1f, 0.1f));
        }
    }
}
