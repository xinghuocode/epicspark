using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Floor, inlays, ring pool, island and altar.</summary>
    public static class EnvironmentBaseBuilder
    {
        public static void Build(TempleBuildKit kit, Transform root)
        {
            BuildFloor(kit, root);
            BuildPool(kit, root);
            BuildIsland(kit, root);
        }

        static void BuildFloor(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Floor", root).transform;

            var floor = new TempleMeshFactory();
            floor.AddAnnulus(TempleLayout.FloorY, 7.35f, TempleLayout.PlazaRadius, 144, facingUp: true, uvScale: 1f / 8f);
            var floorGo = kit.CreateMeshObject("Floor_Marble", floor.Build("Floor_Marble"), kit.MarbleFloor, holder);
            kit.AddMeshCollider(floorGo);

            // raised rim around the outer circular edge, just inside the wall
            var edge = new TempleMeshFactory();
            edge.AddRingBand(20.55f, 21.2f, -0.02f, 0.16f, 128, top: true, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            kit.CreateMeshObject("Floor_EdgeRing", edge.Build("Floor_EdgeRing"), kit.MarbleTrim, holder);

            // concentric stone inlay bands
            var bands = new TempleMeshFactory();
            float[] radii = { 8.6f, 13f, 17.5f };
            foreach (float r in radii)
            {
                bands.AddAnnulus(0.006f, r - 0.13f, r + 0.13f, 128, facingUp: true, uvScale: 0.5f);
            }
            kit.CreateMeshObject("Floor_InlayStone", bands.Build("Floor_InlayStone"), kit.MarbleTrim, holder);

            // gold lines
            var gold = new TempleMeshFactory();
            foreach (float r in radii)
            {
                gold.AddTorus(0.02f, r, 0.032f, 128, 6, 0.5f);
            }
            gold.AddTorus(0.02f, 20.55f, 0.035f, 128, 6, 0.5f);
            var goldGo = kit.CreateMeshObject("Floor_GoldInlay", gold.Build("Floor_GoldInlay"), kit.Gold, holder);
            goldGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static void BuildPool(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Pool", root).transform;

            // water annulus
            var water = new TempleMeshFactory();
            water.AddAnnulus(TempleLayout.WaterY, TempleLayout.PoolInnerRadius + 0.02f, TempleLayout.PoolOuterRadius - 0.02f, 160, facingUp: true, uvScale: 0.32f);
            var waterGo = kit.CreateMeshObject("Pool_Water", water.Build("Pool_Water"), kit.Water, holder);
            var waterRenderer = waterGo.GetComponent<MeshRenderer>();
            waterRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            waterRenderer.receiveShadows = false;

            // basin shell: floor + inner/outer walls
            var basin = new TempleMeshFactory();
            basin.AddAnnulus(TempleLayout.BasinFloorY, TempleLayout.PoolInnerRadius, 7.35f, 128, facingUp: true, uvScale: 0.5f);
            basin.AddCylinderWall(TempleLayout.BasinFloorY, TempleLayout.WaterY, 7.2f, 7.2f, 128, inward: true, outward: false, uvScale: 0.5f);
            basin.AddCylinderWall(TempleLayout.BasinFloorY, TempleLayout.WaterY, TempleLayout.PoolInnerRadius, TempleLayout.PoolInnerRadius, 128, inward: false, outward: true, uvScale: 0.5f);
            kit.CreateMeshObject("Pool_Basin", basin.Build("Pool_Basin"), kit.MarbleBasin, holder);

            // stone rim, slightly overhanging the water
            var rim = new TempleMeshFactory();
            rim.AddRingBand(TempleLayout.RimInnerRadius, TempleLayout.RimOuterRadius, TempleLayout.WaterY - 0.05f, TempleLayout.RimTopY, 160,
                top: true, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            kit.CreateMeshObject("Pool_Rim", rim.Build("Pool_Rim"), kit.MarbleTrim, holder);

            // gold trims around the rim
            var gold = new TempleMeshFactory();
            gold.AddTorus(TempleLayout.RimTopY + 0.012f, TempleLayout.RimOuterRadius - 0.035f, 0.035f, 160, 6, 0.5f);
            gold.AddTorus(TempleLayout.RimTopY + 0.012f, TempleLayout.RimInnerRadius + 0.035f, 0.03f, 160, 6, 0.5f);
            kit.CreateMeshObject("Pool_RimGold", gold.Build("Pool_RimGold"), kit.Gold, holder);

            // dark wet ring right below the rim inside the water
            var wet = new TempleMeshFactory();
            wet.AddCylinderWall(TempleLayout.WaterY - 0.35f, TempleLayout.WaterY, TempleLayout.RimInnerRadius, TempleLayout.RimInnerRadius, 128, inward: true, outward: false, uvScale: 0.5f);
            kit.CreateMeshObject("Pool_WetWall", wet.Build("Pool_WetWall"), kit.MarbleBasin, holder);
        }

        static void BuildIsland(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Pool_Island", root).transform;

            // three marble steps descending into the water
            var body = new TempleMeshFactory();
            body.AddCylinderWall(TempleLayout.BasinFloorY, 0.05f, 3.3f, 3.3f, 96, inward: false, outward: true, uvScale: 0.5f);
            body.AddAnnulus(0.05f, 3.0f, 3.3f, 96, facingUp: true, uvScale: 0.5f);
            body.AddCylinderWall(0.05f, 0.30f, 3.0f, 3.0f, 96, inward: false, outward: true, uvScale: 0.5f);
            body.AddAnnulus(0.30f, TempleLayout.IslandRadius, 3.0f, 96, facingUp: true, uvScale: 0.5f);
            body.AddCylinderWall(0.30f, TempleLayout.IslandTopY, TempleLayout.IslandRadius, TempleLayout.IslandRadius, 96, inward: false, outward: true, uvScale: 0.5f);
            kit.CreateMeshObject("Island_Steps", body.Build("Island_Steps"), kit.MarbleTrim, holder);

            var top = new TempleMeshFactory();
            top.AddDisc(TempleLayout.IslandTopY, TempleLayout.IslandRadius, 96, facingUp: true, uvScale: 0.5f);
            kit.CreateMeshObject("Island_Top", top.Build("Island_Top"), kit.MarbleFloor, holder);

            // low altar below the spark
            var altar = new TempleMeshFactory();
            altar.AddCylinderWall(TempleLayout.IslandTopY, 0.9f, 1.05f, 1.02f, 96, inward: false, outward: true, uvScale: 0.5f);
            altar.AddDisc(0.9f, 1.02f, 96, facingUp: true, uvScale: 0.5f);
            kit.CreateMeshObject("Island_Altar", altar.Build("Island_Altar"), kit.Marble, holder);

            var gold = new TempleMeshFactory();
            gold.AddTorus(0.9f, 1.05f, 0.045f, 96, 6, 0.5f);
            gold.AddTorus(TempleLayout.IslandTopY + 0.005f, TempleLayout.IslandRadius - 0.06f, 0.025f, 96, 6, 0.5f);
            kit.CreateMeshObject("Island_Gold", gold.Build("Island_Gold"), kit.Gold, holder);
        }
    }
}

