using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using SparkCode.Spark;

namespace SparkCode.Generation
{
    /// <summary>
    /// Editor side factory for the golden four-point star "Spark" that floats above the temple island:
    /// procedural star meshes, HDR emissive materials, additive halos, mote particles, a warm point
    /// light, a click collider and the runtime <see cref="SparkVisual"/> / <see cref="SparkClick"/>.
    /// Everything is persisted (meshes/materials as assets, components in the scene) so the built
    /// scene looks final in a still frame.
    /// </summary>
    public static class SparkBuilder
    {
        // Star silhouette / proportions (contract: ~3.6 m wide and tall, ~0.55 m thick)
        const float HalfWidth = 1.74f;
        const float HalfHeight = 1.84f;
        const float HalfThickness = 0.275f;

        public static SparkInfo Build(TempleBuildKit kit, Vector3 position, StartMenuController menu)
        {
            if (kit == null) throw new System.ArgumentNullException("kit");

            // --------------------------------------------------------------- root
            var root = new GameObject("Spark");
            root.transform.SetParent(kit.Root, false);
            root.transform.position = position;
            root.transform.rotation = Quaternion.identity;

            var visualRoot = new GameObject("VisualRoot").transform;
            visualRoot.SetParent(root.transform, false);

            // --------------------------------------------------------------- materials
            Material coreMat = kit.SparkCore;
            Material coreInnerMat = CloneCoreMaterial(kit, kit.SparkCore, "Spark_CoreInner",
                new Color(1f, 0.88f, 0.66f), 7.8f, 3.6f);
            Material glintMat = CloneCoreMaterial(kit, kit.SparkCore, "Spark_CoreGlint",
                new Color(1f, 0.94f, 0.78f), 9.6f, 4.6f);
            Material backMat = CloneCoreMaterial(kit, kit.SparkCore, "Spark_CoreBack",
                new Color(1f, 0.58f, 0.18f), 1.7f, 1.2f);

            Material haloOuterMat = kit.SparkHaloMat != null
                ? kit.SparkHaloMat
                : kit.CreateAdditiveMaterial("Spark_Halo_Outer", kit.SparkHalo, new Color(1f, 0.72f, 0.28f), 2.4f, 0.75f);
            Material haloInnerMat = kit.CreateAdditiveMaterial("Spark_Halo_Inner", kit.SparkHalo,
                new Color(1f, 0.84f, 0.48f), 2.2f, 0.45f);
            Material twinkleMat = kit.CreateAdditiveMaterial("Spark_Twinkle", kit.SparkMote,
                new Color(1f, 0.86f, 0.52f), 1.8f, 0f);

            // --------------------------------------------------------------- star meshes
            Mesh mainMesh = SparkStarMesh.CreateStar("SparkStar_Main", HalfWidth, HalfHeight, HalfThickness,
                0.20f, 1.7f, 0.22f, 0.34f, 0.07f, 128);
            Mesh coreMesh = SparkStarMesh.CreateStar("SparkStar_Core", 0.62f, 0.74f, 0.10f,
                0.22f, 1.5f, 0.10f, 0.50f, 0.10f, 96);
            Mesh glintMesh = SparkStarMesh.CreateStar("SparkStar_Glint", 1.16f, 1.02f, 0.035f,
                0.06f, 2.8f, 0.04f, 0.60f, 0.12f, 96);
            Mesh backMesh = SparkStarMesh.CreateStar("SparkStar_Back", 1.98f, 1.98f, 0.05f,
                0.18f, 1.9f, 0.03f, 0.40f, 0.07f, 96);

            var coreRenderers = new Renderer[4];

            GameObject starMain = CreateStarObject(kit, "SparkStar_Main", mainMesh, coreMat, visualRoot);
            coreRenderers[0] = starMain.GetComponent<Renderer>();

            GameObject starCore = CreateStarObject(kit, "SparkStar_Core", coreMesh, coreInnerMat, visualRoot);
            starCore.transform.localPosition = new Vector3(0f, 0f, 0.36f);
            coreRenderers[1] = starCore.GetComponent<Renderer>();

            GameObject starGlint = CreateStarObject(kit, "SparkStar_Glint", glintMesh, glintMat, visualRoot);
            starGlint.transform.localPosition = new Vector3(0f, 0f, 0.26f);
            starGlint.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            coreRenderers[2] = starGlint.GetComponent<Renderer>();

            GameObject starBack = CreateStarObject(kit, "SparkStar_Back", backMesh, backMat, visualRoot);
            starBack.transform.localPosition = new Vector3(0f, 0f, -0.34f);
            coreRenderers[3] = starBack.GetComponent<Renderer>();

            // --------------------------------------------------------------- static sparkle specks
            Mesh speckMesh = SparkStarMesh.CreateSpecks("Spark_TwinkleSpecks", 88, 1.05f, 2.55f, 0.035f, 0.12f, 0.42f, 20261106);
            GameObject twinkles = CreateMeshObject(kit, "SparkTwinkles", speckMesh, twinkleMat, visualRoot, true, false);

            // --------------------------------------------------------------- additive halos
            Mesh quad = SparkStarMesh.CreateQuad("Spark_HaloQuad");
            quad = kit.SaveMesh("Spark_HaloQuad", quad);

            GameObject haloOuter = CreateMeshObject(kit, "HaloOuter", quad, haloOuterMat, root.transform, false, false);
            haloOuter.transform.localScale = new Vector3(9.2f, 9.2f, 9.2f);
            var haloOuterBillboard = haloOuter.AddComponent<Billboard>();
            haloOuterBillboard.rollSpeed = 2.2f;

            GameObject haloInner = CreateMeshObject(kit, "HaloInner", quad, haloInnerMat, root.transform, false, false);
            haloInner.transform.localScale = new Vector3(6.0f, 6.0f, 6.0f);
            haloInner.transform.localRotation = Quaternion.Euler(0f, 0f, 22f);
            var haloInnerBillboard = haloInner.AddComponent<Billboard>();
            haloInnerBillboard.rollSpeed = -4.5f;
            haloInnerBillboard.baseRoll = 22f;

            // --------------------------------------------------------------- point light
            var lightGo = new GameObject("SparkLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = Vector3.zero;
            var coreLight = lightGo.AddComponent<Light>();
            coreLight.type = LightType.Point;
            coreLight.color = new Color(1f, 0.72f, 0.35f);
            coreLight.intensity = 8f;
            coreLight.range = 28f;
            coreLight.shadows = LightShadows.Soft;
            coreLight.shadowStrength = 0.85f;
            coreLight.shadowBias = 0.02f;
            coreLight.shadowNormalBias = 0.35f;
            coreLight.shadowNearPlane = 0.1f;
            coreLight.renderMode = LightRenderMode.ForcePixel;

            // --------------------------------------------------------------- motes
            ParticleSystem motes = CreateMotes(kit, root.transform);

            // --------------------------------------------------------------- collider + runtime
            var collider = root.AddComponent<SphereCollider>();
            collider.center = Vector3.zero;
            collider.radius = 2.2f;
            collider.isTrigger = false;

            var visual = root.AddComponent<SparkVisual>();
            visual.CoreLight = coreLight;
            visual.VisualRoot = visualRoot;
            visual.HaloTransforms = new[] { haloOuter.transform, haloInner.transform };
            visual.HaloRenderers = new[]
            {
                haloOuter.GetComponent<Renderer>(),
                haloInner.GetComponent<Renderer>(),
                twinkles.GetComponent<Renderer>()
            };
            visual.CoreRenderers = coreRenderers;
            visual.MoteParticles = motes;
            visual.baseLightIntensity = 8f;

            var click = root.AddComponent<SparkClick>();
            click.menu = menu;
            click.visual = visual;

            // --------------------------------------------------------------- result
            return new SparkInfo
            {
                Root = root,
                CoreLight = coreLight,
                MoteParticles = motes,
                Anchor = root.transform
            };
        }

        // ------------------------------------------------------------------ helpers

        static GameObject CreateStarObject(TempleBuildKit kit, string name, Mesh mesh, Material mat, Transform parent)
        {
            GameObject go = CreateMeshObject(kit, name, mesh, mat, parent, true, false);
            return go;
        }

        static GameObject CreateMeshObject(TempleBuildKit kit, string name, Mesh mesh, Material mat, Transform parent,
                                           bool saveMesh, bool castShadows)
        {
            GameObject go = kit.CreateMeshObject(name, mesh, mat, parent, saveMesh);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return go;
        }

        static Material CloneCoreMaterial(TempleBuildKit kit, Material source, string name,
                                          Color emissionColor, float emissionIntensity, float sparkleIntensity)
        {
            if (source == null)
            {
                Debug.LogWarning("[SparkBuilder] SparkCore material missing, using fallback.");
                return null;
            }

            var mat = new Material(source) { name = name };
            mat.SetColor("_EmissionColor", emissionColor);
            mat.SetFloat("_EmissionIntensity", emissionIntensity);
            if (mat.HasProperty("_SparkleIntensity")) mat.SetFloat("_SparkleIntensity", sparkleIntensity);
            if (mat.HasProperty("_FresnelColor")) mat.SetColor("_FresnelColor", Color.Lerp(emissionColor, Color.white, 0.45f));

            string path = TempleBuildKit.MaterialRoot + "/" + name + ".mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mat, path);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static ParticleSystem CreateMotes(TempleBuildKit kit, Transform parent)
        {
            var go = new GameObject("SparkMotes");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 12f;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4.5f, 9.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.16f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.90f, 0.62f, 1f), new Color(1f, 0.66f, 0.22f, 1f));
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 220;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 22f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1.95f;
            shape.radiusThickness = 0.35f;
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.randomDirectionAmount = 1f;

            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.035f, 0.035f);
            velocity.y = new ParticleSystem.MinMaxCurve(0.04f, 0.16f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.035f, 0.035f);
            velocity.radial = new ParticleSystem.MinMaxCurve(0.015f, 0.055f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.18f;
            noise.frequency = 0.55f;
            noise.scrollSpeed = 0.12f;
            noise.damping = true;
            noise.octaveCount = 2;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(MoteColorGradient());

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, MoteSizeCurve());

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.alignment = ParticleSystemRenderSpace.View;
                renderer.sortMode = ParticleSystemSortMode.None;
                renderer.sharedMaterial = kit.SparkMoteMat;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sortingFudge = -8f;
            }

            return ps;
        }

        static Gradient MoteColorGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.96f, 0.78f), 0f),
                    new GradientColorKey(new Color(1f, 0.82f, 0.42f), 0.45f),
                    new GradientColorKey(new Color(1f, 0.58f, 0.20f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.16f),
                    new GradientAlphaKey(0.85f, 0.68f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        static AnimationCurve MoteSizeCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0.35f),
                new Keyframe(0.22f, 1f),
                new Keyframe(0.70f, 0.78f),
                new Keyframe(1f, 0.12f));
            return curve;
        }
    }
}
