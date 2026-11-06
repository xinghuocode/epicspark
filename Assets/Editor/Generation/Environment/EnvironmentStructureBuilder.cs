using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Colonnades, entablatures, perimeter wall, pilasters and coffered ceiling.</summary>
    public static class EnvironmentStructureBuilder
    {
        const float InnerColumnH = 10.6f;
        const float InnerColumnRing = 10.4f;
        const int InnerCount = 16;
        const float OuterColumnH = 15.6f;
        const float OuterColumnRing = 18.0f;
        const int OuterCount = 24;
        const float CeilingY = 16.2f;

        public static void Build(TempleBuildKit kit, Transform root)
        {
            BuildColumns(kit, root);
            BuildEntablatures(kit, root);
            BuildWalls(kit, root);
            BuildCeiling(kit, root);
        }

        // ------------------------------------------------------------------ columns
        static void BuildColumns(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Columns", root).transform;

            var innerMesh = BuildColumnMesh("Column_Inner", InnerColumnH, 0.52f, 0.43f);
            var outerMesh = BuildColumnMesh("Column_Outer", OuterColumnH, 0.72f, 0.60f);
            innerMesh = kit.SaveMesh("Column_Inner", innerMesh);
            outerMesh = kit.SaveMesh("Column_Outer", outerMesh);

            var innerRoot = kit.Create("Columns_Inner", holder).transform;
            for (int i = 0; i < InnerCount; i++)
            {
                float a = Mathf.PI * 2f * i / InnerCount;
                var go = kit.CreateMeshObject("InnerColumn_" + i, innerMesh, kit.Marble, innerRoot, saveMesh: false);
                go.transform.position = new Vector3(Mathf.Cos(a) * InnerColumnRing, 0f, Mathf.Sin(a) * InnerColumnRing);
                go.transform.rotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
            }

            var outerRoot = kit.Create("Columns_Outer", holder).transform;
            for (int i = 0; i < OuterCount; i++)
            {
                float a = Mathf.PI * 2f * i / OuterCount;
                var go = kit.CreateMeshObject("OuterColumn_" + i, outerMesh, kit.Marble, outerRoot, saveMesh: false);
                go.transform.position = new Vector3(Mathf.Cos(a) * OuterColumnRing, 0f, Mathf.Sin(a) * OuterColumnRing);
                go.transform.rotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
            }
        }

        static Mesh BuildColumnMesh(string name, float height, float shaftBottom, float shaftTop)
        {
            var f = new TempleMeshFactory();
            float h = height;

            f.AddBox(new Vector3(0f, 0.015f * h, 0f), new Vector3(0.145f * h, 0.03f * h, 0.145f * h), 0.6f);
            f.AddBox(new Vector3(0f, 0.04f * h, 0f), new Vector3(0.12f * h, 0.02f * h, 0.12f * h), 0.6f);
            f.AddTorus(0.058f * h, 0.062f * h, 0.012f * h, 48, 6, 0.6f);
            f.AddTorus(0.074f * h, 0.056f * h, 0.008f * h, 48, 6, 0.6f);

            // fluted shaft (slight taper)
            float y0 = 0.085f * h;
            float y1 = 0.86f * h;
            int rings = 16;
            int segments = 48;
            const int flutes = 24;
            for (int r = 0; r < rings; r++)
            {
                float t0 = (float)r / rings;
                float t1 = (float)(r + 1) / rings;
                float ya = Mathf.Lerp(y0, y1, t0);
                float yb = Mathf.Lerp(y0, y1, t1);
                float ra = Mathf.Lerp(shaftBottom, shaftTop, t0);
                float rb = Mathf.Lerp(shaftBottom, shaftTop, t1);
                for (int i = 0; i < segments; i++)
                {
                    float a0 = Mathf.PI * 2f * i / segments;
                    float a1 = Mathf.PI * 2f * (i + 1) / segments;
                    float m0 = Flute(a0, flutes);
                    float m1 = Flute(a1, flutes);
                    Vector3 b0 = new Vector3(Mathf.Cos(a0) * ra * m0, ya, Mathf.Sin(a0) * ra * m0);
                    Vector3 b1 = new Vector3(Mathf.Cos(a1) * ra * m1, ya, Mathf.Sin(a1) * ra * m1);
                    Vector3 t0v = new Vector3(Mathf.Cos(a0) * rb * m0, yb, Mathf.Sin(a0) * rb * m0);
                    Vector3 t1v = new Vector3(Mathf.Cos(a1) * rb * m1, yb, Mathf.Sin(a1) * rb * m1);
                    Vector3 n = new Vector3(Mathf.Cos((a0 + a1) * 0.5f), 0f, Mathf.Sin((a0 + a1) * 0.5f));
                    f.AddQuad(b0, t0v, t1v, b1, n,
                        new Vector2(i * 0.12f, ya), new Vector2(i * 0.12f, yb),
                        new Vector2((i + 1) * 0.12f, yb), new Vector2((i + 1) * 0.12f, ya));
                }
            }

            f.AddTorus(0.866f * h, 0.044f * h, 0.007f * h, 48, 6, 0.6f);

            // echinus + abacus
            f.AddCylinderWall(0.87f * h, 0.94f * h, 0.042f * h, 0.072f * h, 48, inward: false, outward: true, uvScale: 0.6f);
            f.AddDisc(0.94f * h, 0.072f * h, 48, facingUp: true, uvScale: 0.6f);
            f.AddBox(new Vector3(0f, 0.9625f * h, 0f), new Vector3(0.15f * h, 0.075f * h, 0.15f * h), 0.6f);
            f.AddBox(new Vector3(0f, 0.91f * h, 0f), new Vector3(0.105f * h, 0.02f * h, 0.105f * h), 0.6f);

            return f.Build(name);
        }

        static float Flute(float angle, int count)
        {
            float groove = Mathf.Pow(0.5f + 0.5f * Mathf.Cos(angle * count), 3f);
            return 1f - 0.05f * groove;
        }

        // ------------------------------------------------------------------ entablature
        static void BuildEntablatures(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Entablature", root).transform;

            // inner: architrave / frieze / cornice, supported by the 16 columns
            var inner = new TempleMeshFactory();
            inner.AddRingBand(9.95f, 10.9f, InnerColumnH, 10.98f, 144, top: true, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            inner.AddRingBand(9.88f, 10.8f, 10.98f, 11.45f, 144, top: true, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            inner.AddRingBand(9.68f, 11.05f, 11.45f, 11.9f, 144, top: true, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            AddDentils(inner, 10.86f, 11.46f, 0.16f, 64, 0.16f);
            kit.CreateMeshObject("Entablature_Inner", inner.Build("Entablature_Inner"), kit.Marble, holder);

            var innerGold = new TempleMeshFactory();
            innerGold.AddTorus(InnerColumnH + 0.01f, 10.9f, 0.025f, 144, 6, 0.5f);
            innerGold.AddTorus(11.44f, 11.02f, 0.022f, 144, 6, 0.5f);
            kit.CreateMeshObject("Entablature_InnerGold", innerGold.Build("Entablature_InnerGold"), kit.Gold, holder);

            // outer: thinner architrave under the ceiling
            var outer = new TempleMeshFactory();
            outer.AddRingBand(17.3f, 18.95f, OuterColumnH, 15.83f, 180, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            outer.AddRingBand(17.22f, 18.85f, 15.83f, 16.04f, 180, top: false, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            outer.AddRingBand(17.05f, 19.15f, 16.04f, CeilingY, 180, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            AddDentils(outer, 18.9f, 16.08f, 0.15f, 80, 0.14f);
            kit.CreateMeshObject("Entablature_Outer", outer.Build("Entablature_Outer"), kit.Marble, holder);

            var outerGold = new TempleMeshFactory();
            outerGold.AddTorus(OuterColumnH + 0.01f, 18.93f, 0.025f, 180, 6, 0.5f);
            outerGold.AddTorus(16.03f, 19.1f, 0.022f, 180, 6, 0.5f);
            kit.CreateMeshObject("Entablature_OuterGold", outerGold.Build("Entablature_OuterGold"), kit.Gold, holder);
        }

        static void AddDentils(TempleMeshFactory f, float radius, float y, float size, int count, float depth)
        {
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.PI * 2f * i / count;
                Vector3 radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 center = radial * radius + Vector3.up * y;
                f.AddBox(center, new Vector3(size * 1.1f, size * 1.35f, depth), Quaternion.LookRotation(radial), 0.8f);
            }
        }

        // ------------------------------------------------------------------ walls
        static void BuildWalls(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Walls", root).transform;

            var wall = new TempleMeshFactory();
            float doorHalf = TempleLayout.DoorHalfAngle;
            float doorHeight = TempleLayout.DoorHeight;
            float rIn = 20.95f, rOut = 21.85f;

            // four full-height segments between the four doors
            float[,] spans = { { 6.5f, 83.5f }, { 96.5f, 173.5f }, { 186.5f, 263.5f }, { 276.5f, 353.5f } };
            for (int i = 0; i < 4; i++)
            {
                wall.AddArcBand(rIn, rOut, 0f, 17.4f, spans[i, 0], spans[i, 1], 40, top: false, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            }
            // lintels above the doors (bottom face = door soffit)
            for (int i = 0; i < 4; i++)
            {
                float c = i * 90f;
                wall.AddArcBand(rIn, rOut, doorHeight, 17.4f, c - doorHalf, c + doorHalf, 8, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            }
            // wall crown and base
            wall.AddRingBand(rIn, rOut, 17.0f, 17.4f, 192, top: true, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            wall.AddRingBand(20.8f, rOut, 0.0f, 0.35f, 192, top: true, bottom: false, inner: true, outer: true, uvScale: 0.5f);
            kit.CreateMeshObject("Walls_Marble", wall.Build("Walls_Marble"), kit.Marble, holder);

            // door thresholds and frames
            var trim = new TempleMeshFactory();
            var gold = new TempleMeshFactory();
            for (int i = 0; i < 4; i++)
            {
                float c = i * 90f;
                float a0 = (c - doorHalf) * Mathf.Deg2Rad;
                float a1 = (c + doorHalf) * Mathf.Deg2Rad;
                Vector3 rd = new Vector3(Mathf.Cos(c * Mathf.Deg2Rad), 0f, Mathf.Sin(c * Mathf.Deg2Rad));

                // threshold
                trim.AddBox(rd * 21.15f + Vector3.up * 0.06f, new Vector3(4.6f, 0.12f, 1.0f), Quaternion.LookRotation(rd), 0.7f);

                // vertical frames at both jambs, on the inner face
                for (int s = -1; s <= 1; s += 2)
                {
                    float ang = c * Mathf.Deg2Rad + s * (doorHalf - 0.35f) * Mathf.Deg2Rad;
                    Vector3 ar = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                    Vector3 tang = new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                    trim.AddBox(ar * 20.92f + Vector3.up * (doorHeight * 0.5f), new Vector3(0.36f, doorHeight, 0.22f), Quaternion.LookRotation(ar), 0.7f);
                    gold.AddBox(ar * 20.80f + Vector3.up * (doorHeight * 0.5f), new Vector3(0.06f, doorHeight, 0.06f), Quaternion.LookRotation(ar), 0.8f);
                    gold.AddBox(tang * 0.0f + ar * 20.80f + Vector3.up * 0.05f, new Vector3(0.06f, 0.08f, 0.06f), Quaternion.LookRotation(ar), 0.8f);
                }

                // lintel frame
                trim.AddBox(rd * 20.92f + Vector3.up * (doorHeight + 0.2f), new Vector3(4.7f, 0.42f, 0.25f), Quaternion.LookRotation(rd), 0.7f);
                gold.AddBox(rd * 20.79f + Vector3.up * (doorHeight + 0.02f), new Vector3(4.35f, 0.07f, 0.06f), Quaternion.LookRotation(rd), 0.8f);
            }
            kit.CreateMeshObject("Walls_DoorFrames", trim.Build("Walls_DoorFrames"), kit.MarbleTrim, holder);
            kit.CreateMeshObject("Walls_DoorGold", gold.Build("Walls_DoorGold"), kit.Gold, holder);

            // pilasters on the inner wall, aligned with the outer columns, skipped at doors
            var pil = new TempleMeshFactory();
            for (int i = 0; i < OuterCount; i++)
            {
                float deg = i * 360f / OuterCount;
                if (IsNearDoor(deg, 12f)) continue;
                float a = deg * Mathf.Deg2Rad;
                Vector3 radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var rot = Quaternion.LookRotation(radial);
                pil.AddBox(radial * 20.78f + Vector3.up * 8.0f, new Vector3(0.62f, 15.4f, 0.32f), rot, 0.5f);
                pil.AddBox(radial * 20.70f + Vector3.up * 15.55f, new Vector3(0.86f, 0.42f, 0.48f), rot, 0.5f);
                pil.AddBox(radial * 20.72f + Vector3.up * 0.55f, new Vector3(0.82f, 0.55f, 0.44f), rot, 0.5f);
            }
            kit.CreateMeshObject("Walls_Pilasters", pil.Build("Walls_Pilasters"), kit.MarbleTrim, holder);

            // narrow gold line running around the wall at the base of the crown
            var wallGold = new TempleMeshFactory();
            wallGold.AddTorus(17.02f, 20.96f, 0.028f, 192, 6, 0.5f);
            kit.CreateMeshObject("Walls_GoldLine", wallGold.Build("Walls_GoldLine"), kit.Gold, holder);
        }

        static bool IsNearDoor(float degrees, float margin)
        {
            for (int i = 0; i < 4; i++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(degrees, i * 90f));
                if (d < TempleLayout.DoorHalfAngle + margin) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ ceiling
        static void BuildCeiling(TempleBuildKit kit, Transform root)
        {
            var holder = kit.Create("Ceiling", root).transform;

            var panels = new TempleMeshFactory();
            panels.AddAnnulus(CeilingY, 7.62f, 21.82f, 200, facingUp: false, uvScale: 0.5f);
            kit.CreateMeshObject("Ceiling_Panels", panels.Build("Ceiling_Panels"), kit.MarbleDark, holder);

            var beams = new TempleMeshFactory();
            // radial beams
            for (int i = 0; i < 24; i++)
            {
                float a = Mathf.PI * 2f * i / 24;
                Vector3 radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                beams.AddBox(radial * 14.7f + Vector3.up * 16.02f, new Vector3(0.34f, 0.36f, 13.6f), Quaternion.LookRotation(radial), 0.5f);
            }
            // concentric beams
            float[] rings = { 10.2f, 13.0f, 16.0f, 19.0f };
            foreach (float r in rings)
            {
                beams.AddRingBand(r - 0.17f, r + 0.17f, 15.84f, CeilingY, 160, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            }
            // inner trim where the ceiling meets the dome
            beams.AddRingBand(7.42f, 7.78f, 15.62f, CeilingY, 160, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            // outer trim against the wall
            beams.AddRingBand(20.9f, 21.9f, 15.55f, CeilingY, 192, top: false, bottom: true, inner: true, outer: true, uvScale: 0.5f);
            kit.CreateMeshObject("Ceiling_Beams", beams.Build("Ceiling_Beams"), kit.MarbleDark, holder);

            var gold = new TempleMeshFactory();
            foreach (float r in rings)
            {
                gold.AddTorus(15.85f, r, 0.022f, 160, 6, 0.5f);
            }
            gold.AddTorus(15.63f, 7.6f, 0.028f, 160, 6, 0.5f);
            kit.CreateMeshObject("Ceiling_Gold", gold.Build("Ceiling_Gold"), kit.Gold, holder);
        }
    }
}

