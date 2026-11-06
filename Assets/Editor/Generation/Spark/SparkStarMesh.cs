using System.Collections.Generic;
using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>
    /// Procedural geometry for the Spark and its god-ray atmosphere.
    /// Everything is generated in the editor and saved as a Mesh asset by SparkBuilder / SparkAtmosphereBuilder.
    /// </summary>
    public static class SparkStarMesh
    {
        // ------------------------------------------------------------------ four point star

        /// <summary>
        /// Builds a double sided, faceted four point star (brand logo silhouette): long tapered points,
        /// a raised diamond core and thin, slightly curved tips.
        /// </summary>
        public static Mesh CreateStar(
            string name,
            float halfWidth,
            float halfHeight,
            float halfThickness,
            float valley,
            float sharpness,
            float bulge,
            float tipThin,
            float bend,
            int segments)
        {
            segments = Mathf.Max(24, segments);

            var outline = new Vector3[segments];
            var radial = new float[segments];
            for (int i = 0; i < segments; i++)
            {
                float ang = Mathf.PI * 2f * i / segments;             // 0 = +X point, 90 = +Y point
                float r = valley + (1f - valley) * Mathf.Pow(Mathf.Abs(Mathf.Cos(2f * ang)), sharpness);
                float bendAng = bend * r * r * r;                      // pinwheel curl of the tips
                outline[i] = new Vector3(Mathf.Cos(ang + bendAng) * halfWidth * r,
                                         Mathf.Sin(ang + bendAng) * halfHeight * r,
                                         0f);
                radial[i] = r;
            }

            int ringCount = 4;
            float[] ringScale = { 0.20f, 0.42f, 0.72f, 1.00f };
            float[] ringBulge = { 0.70f, 0.42f, 0.16f, 0.00f };

            var verts = new List<Vector3>(segments * 32);
            var uvs = new List<Vector2>(segments * 32);
            var tris = new List<int>(segments * 48);
            float invW = 1f / Mathf.Max(0.0001f, 2f * halfWidth);
            float invH = 1f / Mathf.Max(0.0001f, 2f * halfHeight);

            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? 1f : -1f;
                bool front = s == 0;

                // centre apex of the raised diamond core
                Vector3 apex = new Vector3(0f, 0f, side * (halfThickness + bulge));

                // concentric rings, innermost first
                var rings = new Vector3[ringCount][];
                for (int k = 0; k < ringCount; k++)
                {
                    rings[k] = new Vector3[segments];
                    for (int i = 0; i < segments; i++)
                    {
                        float rf = radial[i] * ringScale[k];
                        float thin = ThinAt(rf, valley, tipThin);
                        float z = side * (halfThickness * thin + bulge * ringBulge[k]);
                        rings[k][i] = new Vector3(outline[i].x * ringScale[k], outline[i].y * ringScale[k], z);
                    }
                }

                // apex fan
                for (int i = 0; i < segments; i++)
                {
                    int j = (i + 1) % segments;
                    if (front) AddTriangle(verts, uvs, tris, apex, rings[0][i], rings[0][j], invW, invH);
                    else AddTriangle(verts, uvs, tris, apex, rings[0][j], rings[0][i], invW, invH);
                }

                // concentric bands
                for (int k = 0; k < ringCount - 1; k++)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        int j = (i + 1) % segments;
                        Vector3 a = rings[k][i], b = rings[k][j];
                        Vector3 c = rings[k + 1][i], d = rings[k + 1][j];
                        if (front)
                        {
                            AddTriangle(verts, uvs, tris, a, c, d, invW, invH);
                            AddTriangle(verts, uvs, tris, a, d, b, invW, invH);
                        }
                        else
                        {
                            AddTriangle(verts, uvs, tris, a, d, c, invW, invH);
                            AddTriangle(verts, uvs, tris, a, b, d, invW, invH);
                        }
                    }
                }
            }

            // silhouette rim between the two sides
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                float rf = radial[i];
                float rfj = radial[j];
                float thin = ThinAt(rf, valley, tipThin);
                float thinj = ThinAt(rfj, valley, tipThin);
                Vector3 f0 = new Vector3(outline[i].x, outline[i].y, halfThickness * thin);
                Vector3 f1 = new Vector3(outline[j].x, outline[j].y, halfThickness * thinj);
                Vector3 b0 = new Vector3(f0.x, f0.y, -f0.z);
                Vector3 b1 = new Vector3(f1.x, f1.y, -f1.z);
                AddTriangle(verts, uvs, tris, f0, b0, b1, invW, invH);
                AddTriangle(verts, uvs, tris, f0, b1, f1, invW, invH);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();   // unshared vertices => flat gem facets
            mesh.RecalculateBounds();
            return mesh;
        }

        static float ThinAt(float radialFraction, float valley, float tipThin)
        {
            float tipT = Mathf.Clamp01((radialFraction - valley) / Mathf.Max(0.0001f, 1f - valley));
            return Mathf.Lerp(1f, tipThin, Mathf.Pow(tipT, 1.4f));
        }

        static void AddTriangle(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                Vector3 a, Vector3 b, Vector3 c, float invW, float invH)
        {
            int i0 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            uvs.Add(new Vector2(a.x * invW + 0.5f, a.y * invH + 0.5f));
            uvs.Add(new Vector2(b.x * invW + 0.5f, b.y * invH + 0.5f));
            uvs.Add(new Vector2(c.x * invW + 0.5f, c.y * invH + 0.5f));
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
        }

        // ------------------------------------------------------------------ billboard quad

        /// <summary>Unit quad (1x1) facing +Z, centred on its origin. Scale the transform to size it.</summary>
        public static Mesh CreateQuad(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            });
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            });
            mesh.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ static sparkle specks

        /// <summary>
        /// A single mesh of tiny additive quads spread around the star. They are part of the saved
        /// scene, so the Spark still reads as "covered in small lights" in a plain editor still frame
        /// (particle systems do not simulate outside play mode).
        /// </summary>
        public static Mesh CreateSpecks(string name, int count, float minRadius, float maxRadius,
                                        float minSize, float maxSize, float zSpread, int seed)
        {
            var verts = new List<Vector3>(count * 4);
            var uvs = new List<Vector2>(count * 4);
            var colors = new List<Color>(count * 4);
            var tris = new List<int>(count * 6);
            var rnd = new System.Random(seed);

            for (int i = 0; i < count; i++)
            {
                float ang = (float)(rnd.NextDouble() * Mathf.PI * 2f);
                float rad = Mathf.Lerp(minRadius, maxRadius, Mathf.Sqrt((float)rnd.NextDouble()));
                float size = Mathf.Lerp(minSize, maxSize, (float)rnd.NextDouble());
                float z = Mathf.Lerp(-zSpread, zSpread, (float)rnd.NextDouble());
                float roll = (float)(rnd.NextDouble() * Mathf.PI * 2f);
                var centre = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad, z);
                var rot = Quaternion.Euler(0f, 0f, roll * Mathf.Rad2Deg);

                float alpha = Mathf.Lerp(0.35f, 1f, (float)rnd.NextDouble());
                float warmth = (float)rnd.NextDouble();
                Color col = Color.Lerp(new Color(1f, 0.95f, 0.78f, alpha), new Color(1f, 0.72f, 0.3f, alpha), warmth);

                int i0 = verts.Count;
                verts.Add(centre + rot * new Vector3(-0.5f * size, -0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(0.5f * size, -0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(0.5f * size, 0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(-0.5f * size, 0.5f * size, 0f));
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
                for (int k = 0; k < 4; k++) colors.Add(col);

                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Static dust motes spread through a vertical cylinder (used inside the god ray so the
        /// atmosphere still reads in a plain editor still frame).
        /// </summary>
        public static Mesh CreateVolumeSpecks(string name, int count, float radius, float minY, float maxY,
                                              float minSize, float maxSize, int seed)
        {
            var verts = new List<Vector3>(count * 4);
            var uvs = new List<Vector2>(count * 4);
            var colors = new List<Color>(count * 4);
            var tris = new List<int>(count * 6);
            var rnd = new System.Random(seed);

            for (int i = 0; i < count; i++)
            {
                float ang = (float)(rnd.NextDouble() * Mathf.PI * 2f);
                float rad = radius * Mathf.Sqrt((float)rnd.NextDouble()) * Mathf.Lerp(0.25f, 1f, (float)rnd.NextDouble());
                float y = Mathf.Lerp(minY, maxY, (float)rnd.NextDouble());
                float size = Mathf.Lerp(minSize, maxSize, (float)rnd.NextDouble());
                float roll = (float)(rnd.NextDouble() * Mathf.PI * 2f);
                var centre = new Vector3(Mathf.Cos(ang) * rad, y, Mathf.Sin(ang) * rad);
                var rot = Quaternion.Euler(0f, 0f, roll * Mathf.Rad2Deg);

                float alpha = Mathf.Lerp(0.18f, 0.75f, (float)rnd.NextDouble());
                Color col = new Color(1f, 0.94f, 0.82f, alpha);

                int i0 = verts.Count;
                verts.Add(centre + rot * new Vector3(-0.5f * size, -0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(0.5f * size, -0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(0.5f * size, 0.5f * size, 0f));
                verts.Add(centre + rot * new Vector3(-0.5f * size, 0.5f * size, 0f));
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
                for (int k = 0; k < 4; k++) colors.Add(col);

                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ volumetric light shaft

        /// <summary>
        /// Open, double sided frustum stack used as a cheap volumetric shaft. Colours are baked into
        /// the vertex alpha (bright at the oculus, dissolving near the island); the UVs sit at the
        /// centre of the soft glow texture so the additive tint stays even across the cone.
        /// </summary>
        public static Mesh CreateShaft(
            string name,
            Vector3 topCentre,
            Vector3 bottomCentre,
            float topRadius,
            float bottomRadius,
            int segments,
            int rings,
            float alphaTop,
            float alphaBottom,
            float[] layerRadii,
            float[] layerAlphas)
        {
            segments = Mathf.Max(12, segments);
            rings = Mathf.Max(2, rings);
            if (layerRadii == null || layerRadii.Length == 0) layerRadii = new[] { 1f };
            if (layerAlphas == null || layerAlphas.Length != layerRadii.Length)
            {
                layerAlphas = new float[layerRadii.Length];
                for (int i = 0; i < layerAlphas.Length; i++) layerAlphas[i] = 1f;
            }

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();

            Vector3 axis = bottomCentre - topCentre;

            for (int layer = 0; layer < layerRadii.Length; layer++)
            {
                float radiusMul = layerRadii[layer];
                float alphaMul = layerAlphas[layer];

                var ringVerts = new Vector3[rings + 1][];
                var ringAlpha = new float[rings + 1];

                for (int r = 0; r <= rings; r++)
                {
                    float t = (float)r / rings;
                    Vector3 centre = topCentre + axis * t;
                    float radius = Mathf.Lerp(topRadius, bottomRadius, t) * radiusMul;
                    // fade out just before the island so the beam has no hard bottom edge
                    float bottomFade = 1f - Mathf.SmoothStep(0.78f, 1f, t);
                    ringAlpha[r] = Mathf.Lerp(alphaTop, alphaBottom, t) * alphaMul * bottomFade;
                    ringVerts[r] = new Vector3[segments];
                    for (int i = 0; i < segments; i++)
                    {
                        float ang = Mathf.PI * 2f * i / segments;
                        float radial = 1f + 0.03f * Mathf.Sin(ang * 3f + layer * 1.7f); // subtle organic wobble
                        ringVerts[r][i] = centre + new Vector3(Mathf.Cos(ang) * radius * radial, 0f, Mathf.Sin(ang) * radius * radial);
                    }
                }

                for (int r = 0; r < rings; r++)
                {
                    for (int i = 0; i < segments; i++)
                    {
                        int j = (i + 1) % segments;
                        Vector3 a = ringVerts[r][i], b = ringVerts[r][j];
                        Vector3 c = ringVerts[r + 1][i], d = ringVerts[r + 1][j];
                        float a0 = ringAlpha[r], a1 = ringAlpha[r + 1];
                        int i0 = verts.Count;
                        verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                        uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(new Vector2(0.5f, 0.5f));
                        uvs.Add(new Vector2(0.5f, 0.5f)); uvs.Add(new Vector2(0.5f, 0.5f));
                        Color cw = new Color(1f, 0.94f, 0.8f, 1f);
                        colors.Add(new Color(cw.r, cw.g, cw.b, a0));
                        colors.Add(new Color(cw.r, cw.g, cw.b, a0));
                        colors.Add(new Color(cw.r, cw.g, cw.b, a1));
                        colors.Add(new Color(cw.r, cw.g, cw.b, a1));
                        tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1);
                        tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
                    }
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
