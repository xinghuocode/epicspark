using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SparkCode.Generation
{
    /// <summary>
    /// Small procedural mesh builder. Winding is fixed automatically against each surface's desired normal,
    /// then smooth normals are recalculated (duplicate vertices are used where hard edges are wanted).
    /// </summary>
    public class TempleMeshFactory
    {
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        public int VertexCount => _v.Count;
        public int TriangleCount => _t.Count / 3;

        public void Clear()
        {
            _v.Clear();
            _uv.Clear();
            _t.Clear();
        }

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            int i = _v.Count;
            Vector3 cross = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(cross, normal) < 0f)
            {
                // swap b/c to face `normal`
                Vector3 tv = b; b = c; c = tv;
                Vector2 tu = ub; ub = uc; uc = tu;
            }
            _v.Add(a); _v.Add(b); _v.Add(c);
            _uv.Add(ua); _uv.Add(ub); _uv.Add(uc);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            AddTriangle(a, b, c, normal, ua, ub, uc);
            AddTriangle(a, c, d, normal, ua, uc, ud);
        }

        public void AddBox(Vector3 center, Vector3 size, Quaternion rot, float uvScale = 0.5f)
        {
            Vector3 h = size * 0.5f;
            Vector3[] p = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                p[i] = center + rot * new Vector3(((i & 1) == 0 ? -h.x : h.x), ((i & 2) == 0 ? -h.y : h.y), ((i & 4) == 0 ? -h.z : h.z));
            }
            // local axes in world space
            Vector3 ax = rot * Vector3.right;
            Vector3 ay = rot * Vector3.up;
            Vector3 az = rot * Vector3.forward;
            float su = (size.z + size.y) * uvScale;
            float sv = (size.x + size.y) * uvScale;

            // +X
            AddQuad(p[1], p[3], p[7], p[5], ax, new Vector2(0, 0), new Vector2(size.z * uvScale, 0), new Vector2(size.z * uvScale, size.y * uvScale), new Vector2(0, size.y * uvScale));
            // -X
            AddQuad(p[0], p[4], p[6], p[2], -ax, new Vector2(0, 0), new Vector2(size.z * uvScale, 0), new Vector2(size.z * uvScale, size.y * uvScale), new Vector2(0, size.y * uvScale));
            // +Y
            AddQuad(p[2], p[6], p[7], p[3], ay, new Vector2(0, 0), new Vector2(0, size.z * uvScale), new Vector2(size.x * uvScale, size.z * uvScale), new Vector2(size.x * uvScale, 0));
            // -Y
            AddQuad(p[0], p[1], p[5], p[4], -ay, new Vector2(0, 0), new Vector2(0, size.z * uvScale), new Vector2(size.x * uvScale, size.z * uvScale), new Vector2(size.x * uvScale, 0));
            // +Z
            AddQuad(p[4], p[5], p[7], p[6], az, new Vector2(0, 0), new Vector2(size.x * uvScale, 0), new Vector2(size.x * uvScale, size.y * uvScale), new Vector2(0, size.y * uvScale));
            // -Z
            AddQuad(p[0], p[2], p[3], p[1], -az, new Vector2(0, 0), new Vector2(size.x * uvScale, 0), new Vector2(size.x * uvScale, size.y * uvScale), new Vector2(0, size.y * uvScale));
        }

        public void AddBox(Vector3 center, Vector3 size, float uvScale = 0.5f)
        {
            AddBox(center, size, Quaternion.identity, uvScale);
        }

        public void AddDisc(float y, float radius, int segments, bool facingUp, float uvScale = 0.5f)
        {
            Vector3 normal = facingUp ? Vector3.up : Vector3.down;
            Vector3 c = new Vector3(0f, y, 0f);
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                Vector3 p0 = c + new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                Vector3 p1 = c + new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);
                Vector2 uv0 = new Vector2(p0.x, p0.z) * uvScale;
                Vector2 uv1 = new Vector2(p1.x, p1.z) * uvScale;
                AddTriangle(c, p0, p1, normal, new Vector2(c.x, c.z) * uvScale, uv0, uv1);
            }
        }

        public void AddAnnulus(float y, float inner, float outer, int segments, bool facingUp, float uvScale = 0.5f)
        {
            Vector3 normal = facingUp ? Vector3.up : Vector3.down;
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                Vector3 i0 = new Vector3(Mathf.Cos(a0) * inner, y, Mathf.Sin(a0) * inner);
                Vector3 i1 = new Vector3(Mathf.Cos(a1) * inner, y, Mathf.Sin(a1) * inner);
                Vector3 o0 = new Vector3(Mathf.Cos(a0) * outer, y, Mathf.Sin(a0) * outer);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * outer, y, Mathf.Sin(a1) * outer);
                AddQuad(i0, i1, o1, o0, normal,
                    new Vector2(i0.x, i0.z) * uvScale, new Vector2(i1.x, i1.z) * uvScale,
                    new Vector2(o1.x, o1.z) * uvScale, new Vector2(o0.x, o0.z) * uvScale);
            }
        }

        /// <summary>Vertical cylindrical band between y0..y1. Inward/outward select which visible sides to emit.</summary>
        public void AddCylinderWall(float y0, float y1, float rBottom, float rTop, int segments, bool inward, bool outward, float uvScale = 0.5f)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                float cos0 = Mathf.Cos(a0), sin0 = Mathf.Sin(a0);
                float cos1 = Mathf.Cos(a1), sin1 = Mathf.Sin(a1);
                Vector3 b0 = new Vector3(cos0 * rBottom, y0, sin0 * rBottom);
                Vector3 b1 = new Vector3(cos1 * rBottom, y0, sin1 * rBottom);
                Vector3 t0 = new Vector3(cos0 * rTop, y1, sin0 * rTop);
                Vector3 t1 = new Vector3(cos1 * rTop, y1, sin1 * rTop);
                Vector3 midN = new Vector3((cos0 + cos1) * 0.5f, 0f, (sin0 + sin1) * 0.5f);
                float circumference = Mathf.PI * 2f * ((rBottom + rTop) * 0.5f);
                float u0 = circumference * i / segments * uvScale;
                float u1 = circumference * (i + 1) / segments * uvScale;
                if (outward) AddQuad(b0, t0, t1, b1, midN, new Vector2(u0, y0 * uvScale), new Vector2(u0, y1 * uvScale), new Vector2(u1, y1 * uvScale), new Vector2(u1, y0 * uvScale));
                if (inward) AddQuad(b0, b1, t1, t0, -midN, new Vector2(u0, y0 * uvScale), new Vector2(u1, y0 * uvScale), new Vector2(u1, y1 * uvScale), new Vector2(u0, y1 * uvScale));
            }
        }

        /// <summary>Solid ring band (rectangular cross-section) with selectable faces.</summary>
        public void AddRingBand(float rInner, float rOuter, float y0, float y1, int segments, bool top, bool bottom, bool inner, bool outer, float uvScale = 0.5f)
        {
            AddCylinderWall(y0, y1, rInner, rInner, segments, inward: true, outward: false, uvScale);
            AddCylinderWall(y0, y1, rOuter, rOuter, segments, inward: false, outward: true, uvScale);
            if (top) AddAnnulus(y1, rInner, rOuter, segments, facingUp: true, uvScale);
            if (bottom) AddAnnulus(y0, rInner, rOuter, segments, facingUp: false, uvScale);
        }

        /// <summary>Lathe surface from a radial profile. Profile points are (radius, height) in order from bottom to top; normals face outward/up.</summary>
        public void AddLathe(Vector2[] profile, int segments, bool outward = true, float uvScale = 0.5f)
        {
            for (int k = 0; k < profile.Length - 1; k++)
            {
                float r0 = profile[k].x, y0 = profile[k].y;
                float r1 = profile[k + 1].x, y1 = profile[k + 1].y;
                float dr = r1 - r0, dy = y1 - y0;
                Vector2 n2 = new Vector2(dy, -dr).normalized; // outward/up
                for (int i = 0; i < segments; i++)
                {
                    float a0 = Mathf.PI * 2f * i / segments;
                    float a1 = Mathf.PI * 2f * (i + 1) / segments;
                    float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0);
                    float c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                    Vector3 b0 = new Vector3(c0 * r0, y0, s0 * r0);
                    Vector3 b1 = new Vector3(c1 * r0, y0, s1 * r0);
                    Vector3 t0 = new Vector3(c0 * r1, y1, s0 * r1);
                    Vector3 t1 = new Vector3(c1 * r1, y1, s1 * r1);
                    Vector3 midN = new Vector3((c0 + c1) * 0.5f * n2.x, n2.y, (s0 + s1) * 0.5f * n2.x);
                    float u0 = Mathf.PI * 2f * r0 * i / segments * uvScale;
                    float u1 = Mathf.PI * 2f * r0 * (i + 1) / segments * uvScale;
                    if (outward)
                    {
                        AddQuad(b0, t0, t1, b1, midN, new Vector2(u0, y0 * uvScale), new Vector2(u0, y1 * uvScale), new Vector2(u1, y1 * uvScale), new Vector2(u1, y0 * uvScale));
                    }
                    else
                    {
                        AddQuad(b0, b1, t1, t0, -midN, new Vector2(u0, y0 * uvScale), new Vector2(u1, y0 * uvScale), new Vector2(u1, y1 * uvScale), new Vector2(u0, y1 * uvScale));
                    }
                }
            }
        }

        public void AddTorus(float centerY, float majorRadius, float minorRadius, int majorSegments, int minorSegments, float uvScale = 0.5f)
        {
            for (int i = 0; i < majorSegments; i++)
            {
                float a0 = Mathf.PI * 2f * i / majorSegments;
                float a1 = Mathf.PI * 2f * (i + 1) / majorSegments;
                for (int j = 0; j < minorSegments; j++)
                {
                    float b0 = Mathf.PI * 2f * j / minorSegments;
                    float b1 = Mathf.PI * 2f * (j + 1) / minorSegments;
                    Vector3 p00 = TorusPoint(a0, b0, centerY, majorRadius, minorRadius);
                    Vector3 p01 = TorusPoint(a0, b1, centerY, majorRadius, minorRadius);
                    Vector3 p11 = TorusPoint(a1, b1, centerY, majorRadius, minorRadius);
                    Vector3 p10 = TorusPoint(a1, b0, centerY, majorRadius, minorRadius);
                    Vector3 n = new Vector3(Mathf.Cos((a0 + a1) * 0.5f), 0f, Mathf.Sin((a0 + a1) * 0.5f));
                    Vector3 nrm = (p00 - new Vector3(n.x * majorRadius, centerY, n.z * majorRadius)).normalized;
                    AddQuad(p00, p10, p11, p01, nrm, new Vector2(i, j), new Vector2(i + 1, j), new Vector2(i + 1, j + 1), new Vector2(i, j + 1));
                }
            }
        }

        static Vector3 TorusPoint(float a, float b, float centerY, float majorRadius, float minorRadius)
        {
            float r = majorRadius + Mathf.Cos(b) * minorRadius;
            return new Vector3(Mathf.Cos(a) * r, centerY + Mathf.Sin(b) * minorRadius, Mathf.Sin(a) * r);
        }

        /// <summary>Curved wall segment: solid ring band clipped to an angular range.</summary>
        public void AddArcBand(float rInner, float rOuter, float y0, float y1, float startDeg, float endDeg, int segments, bool top, bool bottom, bool inner, bool outer, float uvScale = 0.5f)
        {
            float a0 = startDeg * Mathf.Deg2Rad;
            float a1 = endDeg * Mathf.Deg2Rad;
            for (int i = 0; i < segments; i++)
            {
                float t0 = Mathf.Lerp(a0, a1, (float)i / segments);
                float t1 = Mathf.Lerp(a0, a1, (float)(i + 1) / segments);
                Vector3 inner0 = new Vector3(Mathf.Cos(t0) * rInner, 0f, Mathf.Sin(t0) * rInner);
                Vector3 inner1 = new Vector3(Mathf.Cos(t1) * rInner, 0f, Mathf.Sin(t1) * rInner);
                Vector3 outer0 = new Vector3(Mathf.Cos(t0) * rOuter, 0f, Mathf.Sin(t0) * rOuter);
                Vector3 outer1 = new Vector3(Mathf.Cos(t1) * rOuter, 0f, Mathf.Sin(t1) * rOuter);
                Vector3 midN = new Vector3(Mathf.Cos((t0 + t1) * 0.5f), 0f, Mathf.Sin((t0 + t1) * 0.5f));
                float u0 = i * (rOuter - rInner) * uvScale;
                if (outer) AddQuad(outer0 + Vector3.up * y0, outer1 + Vector3.up * y0, outer1 + Vector3.up * y1, outer0 + Vector3.up * y1, midN,
                    new Vector2(u0, y0 * uvScale), new Vector2(u0 + (rOuter - rInner) * uvScale, y0 * uvScale), new Vector2(u0 + (rOuter - rInner) * uvScale, y1 * uvScale), new Vector2(u0, y1 * uvScale));
                if (inner) AddQuad(inner0 + Vector3.up * y0, inner0 + Vector3.up * y1, inner1 + Vector3.up * y1, inner1 + Vector3.up * y0, -midN,
                    new Vector2(u0, y0 * uvScale), new Vector2(u0, y1 * uvScale), new Vector2(u0 + (rOuter - rInner) * uvScale, y1 * uvScale), new Vector2(u0 + (rOuter - rInner) * uvScale, y0 * uvScale));
                if (top) AddQuad(inner0 + Vector3.up * y1, inner1 + Vector3.up * y1, outer1 + Vector3.up * y1, outer0 + Vector3.up * y1, Vector3.up,
                    new Vector2(inner0.x, inner0.z) * uvScale, new Vector2(inner1.x, inner1.z) * uvScale, new Vector2(outer1.x, outer1.z) * uvScale, new Vector2(outer0.x, outer0.z) * uvScale);
                if (bottom) AddQuad(inner0 + Vector3.up * y0, outer0 + Vector3.up * y0, outer1 + Vector3.up * y0, inner1 + Vector3.up * y0, Vector3.down,
                    new Vector2(inner0.x, inner0.z) * uvScale, new Vector2(outer0.x, outer0.z) * uvScale, new Vector2(outer1.x, outer1.z) * uvScale, new Vector2(inner1.x, inner1.z) * uvScale);
            }
            // end caps (jambs)
            AddCap(rInner, rOuter, y0, y1, a0, uvScale);
            AddCap(rInner, rOuter, y0, y1, a1, uvScale);
        }

        void AddCap(float rInner, float rOuter, float y0, float y1, float angle, float uvScale)
        {
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 side = new Vector3(-Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            Vector3 a = dir * rInner + Vector3.up * y0;
            Vector3 b = dir * rOuter + Vector3.up * y0;
            Vector3 c = dir * rOuter + Vector3.up * y1;
            Vector3 d = dir * rInner + Vector3.up * y1;
            AddQuad(a, b, c, d, side, new Vector2(0, y0 * uvScale), new Vector2((rOuter - rInner) * uvScale, y0 * uvScale), new Vector2((rOuter - rInner) * uvScale, y1 * uvScale), new Vector2(0, y1 * uvScale));
        }

        public Mesh Build(string name, bool recalculateNormals = true)
        {
            var mesh = new Mesh { name = name };
            if (_v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_v);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
