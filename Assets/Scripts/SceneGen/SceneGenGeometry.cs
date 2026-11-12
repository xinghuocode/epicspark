// SceneGen 几何工具：全部为确定性纯函数，不依赖时间/随机源/场景状态。
// 约定：所有占地尺寸都是「世界轴对齐 AABB」，rotation 只表示立面朝向，不参与占地计算。
// 这条约定是"施工图不混乱"的地基（旧链路失败根因：校验用未旋转尺寸、渲染用旋转后的尺寸）。
using System;
using System.Collections.Generic;
using EpicSpark.Core;
using UnityEngine;

namespace EpicSpark.SceneGen
{
    /// <summary>确定性随机源（splitmix64）：同一 seed 必定产出同一布局，便于回放与离线验收。</summary>
    public struct SceneGenRng
    {
        private ulong _state;

        public SceneGenRng(int seed)
        {
            _state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x2545F4914F6CDD1DUL;
        }

        public ulong NextULong()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>[0,1)</summary>
        public float Next01()
        {
            return (NextULong() >> 40) / 16777216f;
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * Next01();
        }

        public int RangeInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextULong() % (ulong)(maxExclusive - minInclusive));
        }
    }

    /// <summary>轴对齐矩形上的几何运算（World XZ 平面）。</summary>
    public static class SceneGenRect
    {
        public const float Eps = 0.001f;

        public static Rect FromCenter(Vector2 center, float width, float depth)
        {
            return new Rect(center.x - width * 0.5f, center.y - depth * 0.5f, width, depth);
        }

        public static Rect FromMinMax(float minX, float minZ, float maxX, float maxZ)
        {
            return new Rect(minX, minZ, Mathf.Max(Eps, maxX - minX), Mathf.Max(Eps, maxZ - minZ));
        }

        public static Rect Bounds(List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count == 0) return new Rect(0f, 0f, Eps, Eps);
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                minX = Mathf.Min(minX, polygon[i].x);
                maxX = Mathf.Max(maxX, polygon[i].x);
                minZ = Mathf.Min(minZ, polygon[i].y);
                maxZ = Mathf.Max(maxZ, polygon[i].y);
            }
            return FromMinMax(minX, minZ, maxX, maxZ);
        }

        /// <summary>矩形四角（顺时针：左下、右下、右上、左上）。</summary>
        public static List<Vector2> ToPolygon(Rect rect)
        {
            return new List<Vector2>(4)
            {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
        }

        public static bool Overlaps(Rect a, Rect b, float tolerance = Eps)
        {
            float ix = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float iz = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return ix > tolerance && iz > tolerance;
        }

        public static float OverlapArea(Rect a, Rect b)
        {
            float ix = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float iz = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return (ix <= 0f || iz <= 0f) ? 0f : ix * iz;
        }

        public static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin);
            float z0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax);
            float z1 = Mathf.Min(a.yMax, b.yMax);
            if (x1 <= x0 || z1 <= z0) return new Rect(0f, 0f, 0f, 0f);
            return FromMinMax(x0, z0, x1, z1);
        }

        /// <summary>a 减去 b，返回 0..4 个互不重叠的残片（切带方式，避免碎片化）。</summary>
        public static List<Rect> Subtract(Rect a, Rect b)
        {
            var result = new List<Rect>(4);
            if (!Overlaps(a, b)) { result.Add(a); return result; }

            float ax0 = a.xMin, ax1 = a.xMax, az0 = a.yMin, az1 = a.yMax;
            float bx0 = Mathf.Max(b.xMin, ax0), bx1 = Mathf.Min(b.xMax, ax1);
            float bz0 = Mathf.Max(b.yMin, az0), bz1 = Mathf.Min(b.yMax, az1);

            if (bx0 > ax0) result.Add(FromMinMax(ax0, az0, bx0, az1));
            if (bx1 < ax1) result.Add(FromMinMax(bx1, az0, ax1, az1));
            if (bz0 > az0) result.Add(FromMinMax(bx0, az0, bx1, bz0));
            if (bz1 < az1) result.Add(FromMinMax(bx0, bz1, bx1, az1));
            return result;
        }

        public static bool Contains(Rect container, Vector2 point, float margin = 0f)
        {
            return point.x >= container.xMin - margin && point.x <= container.xMax + margin
                   && point.y >= container.yMin - margin && point.y <= container.yMax + margin;
        }

        public static Vector2 ClosestPoint(Rect rect, Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp(point.x, rect.xMin, rect.xMax),
                Mathf.Clamp(point.y, rect.yMin, rect.yMax));
        }

        public static Vector2 Center(Rect rect)
        {
            return new Vector2(rect.xMin + rect.width * 0.5f, rect.yMin + rect.height * 0.5f);
        }

        public static Rect Inflate(Rect rect, float amount)
        {
            return FromMinMax(rect.xMin - amount, rect.yMin - amount, rect.xMax + amount, rect.yMax + amount);
        }

        public static Rect ClipTo(Rect rect, Rect bounds)
        {
            return Intersect(rect, bounds);
        }
    }

    /// <summary>街道走廊的轴对齐切分（用于临街条带的裁剪与缺口计算）。</summary>
    public static class SceneGenSegments
    {
        /// <summary>一条折线在 axis-aligned 情况下的线段；斜线段只作为道路走廊，不参与临街切分。</summary>
        public struct Segment
        {
            public Vector2 a;
            public Vector2 b;
            public bool vertical;
            public bool horizontal;

            public float Length
            {
                get { return Vector2.Distance(a, b); }
            }

            public bool AxisAligned
            {
                get { return vertical || horizontal; }
            }
        }

        public static List<Segment> Split(List<Vector2> nodes)
        {
            var list = new List<Segment>();
            if (nodes == null) return list;
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                Vector2 a = nodes[i];
                Vector2 b = nodes[i + 1];
                var seg = new Segment { a = a, b = b };
                seg.vertical = Mathf.Abs(a.x - b.x) <= 0.01f;
                seg.horizontal = Mathf.Abs(a.y - b.y) <= 0.01f;
                list.Add(seg);
            }
            return list;
        }

        /// <summary>道路走廊矩形（含半宽）。斜线段用其包围盒近似（只影响判断，不参与切分）。</summary>
        public static Rect Corridor(Segment seg, float halfWidth)
        {
            float x0 = Mathf.Min(seg.a.x, seg.b.x) - halfWidth;
            float x1 = Mathf.Max(seg.a.x, seg.b.x) + halfWidth;
            float z0 = Mathf.Min(seg.a.y, seg.b.y) - halfWidth;
            float z1 = Mathf.Max(seg.a.y, seg.b.y) + halfWidth;
            return SceneGenRect.FromMinMax(x0, z0, x1, z1);
        }

        /// <summary>把边界的缺口区间合并成「可走段」以外的扫地区间。</summary>
        public static List<Vector2> GapsOnEdge(List<Vector2> points, bool onXEdge, float atValue, float tolerance)
        {
            var gaps = new List<Vector2>();
            if (points == null) return gaps;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 p = points[i];
                float v = onXEdge ? p.y : p.x;
                if (Mathf.Abs(v - atValue) <= tolerance) gaps.Add(p);
            }
            return gaps;
        }
    }

    /// <summary>立面朝向：世界方向的单位法线 ↔ 角度（0 = 正立面朝 +Z 北）。</summary>
    public static class SceneGenFacing
    {
        public static Vector2 NormalFromAngle(float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        public static float AngleFromNormal(Vector2 normal)
        {
            if (normal.sqrMagnitude < 0.0001f) return 0f;
            return Mathf.Atan2(normal.x, normal.y) * Mathf.Rad2Deg;
        }

        public static float AngleFromWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0f;
            string w = word.Trim().ToLowerInvariant();
            if (w == "north" || w == "n" || w == "up") return 0f;
            if (w == "east" || w == "e" || w == "right") return 90f;
            if (w == "south" || w == "s" || w == "down") return 180f;
            if (w == "west" || w == "w" || w == "left") return -90f;
            if (w.IndexOf("north", StringComparison.Ordinal) >= 0) return 0f;
            if (w.IndexOf("east", StringComparison.Ordinal) >= 0) return 90f;
            if (w.IndexOf("south", StringComparison.Ordinal) >= 0) return 180f;
            if (w.IndexOf("west", StringComparison.Ordinal) >= 0) return -90f;
            if (w.IndexOf("北", StringComparison.Ordinal) >= 0 && w.IndexOf("东", StringComparison.Ordinal) < 0
                && w.IndexOf("西", StringComparison.Ordinal) < 0) return 0f;
            if (w.IndexOf("东", StringComparison.Ordinal) >= 0) return 90f;
            if (w.IndexOf("南", StringComparison.Ordinal) >= 0) return 180f;
            if (w.IndexOf("西", StringComparison.Ordinal) >= 0) return -90f;
            return 0f;
        }

        /// <summary>把法线吸附到 4 个主轴之一，保证临街单元严格对齐。</summary>
        public static Vector2 SnapToAxis(Vector2 normal)
        {
            if (Mathf.Abs(normal.x) >= Mathf.Abs(normal.y))
                return new Vector2(normal.x >= 0f ? 1f : -1f, 0f);
            return new Vector2(0f, normal.y >= 0f ? 1f : -1f);
        }
    }

    /// <summary>真实旋转占地（OBB）工具；与 PrimitiveKit 共用同一套世界 AABB → 本地尺寸换算。</summary>
    public static class SceneGenOBB
    {
        public static Vector2 LocalSizeFromWorldAABB(Vector2 world, float yaw)
        {
            float c = Mathf.Abs(Mathf.Cos(yaw * Mathf.Deg2Rad));
            float s = Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad));
            float determinant = c * c - s * s;
            if (Mathf.Abs(determinant) > 0.001f)
            {
                Vector2 local = new Vector2((c * world.x - s * world.y) / determinant,
                    (c * world.y - s * world.x) / determinant);
                if (local.x > 0.1f && local.y > 0.1f) return local;
            }
            float conservative = Mathf.Min(world.x, world.y) / Mathf.Max(0.001f, c + s);
            return new Vector2(conservative, conservative);
        }

        /// <summary>本地 +X/+Z 经 Unity Yaw 映射到世界 XZ 后的四个角。</summary>
        public static Vector2[] Corners(Vector2 center, Vector2 worldSize, float yaw)
        {
            Vector2 local = LocalSizeFromWorldAABB(worldSize, yaw);
            float rad = yaw * Mathf.Deg2Rad;
            Vector2 axisX = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
            Vector2 axisZ = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            Vector2 x = axisX * (local.x * 0.5f);
            Vector2 z = axisZ * (local.y * 0.5f);
            return new[] { center - x - z, center + x - z, center + x + z, center - x + z };
        }

        public static Vector2[] Corners(LayoutItemSpec item)
        {
            return Corners(item.center, item.size, item.rotation);
        }

        public static float ProjectedHalfWidth(LayoutItemSpec item, Vector2 direction)
        {
            Vector2[] corners = Corners(item);
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                float projection = Vector2.Dot(corners[i] - item.center, direction);
                min = Mathf.Min(min, projection);
                max = Mathf.Max(max, projection);
            }
            return (max - min) * 0.5f;
        }

        /// <summary>分离轴碰撞；容忍 1cm 以内数值接触，不把贴边当成穿插。</summary>
        public static bool Overlaps(LayoutItemSpec a, LayoutItemSpec b, float tolerance = 0.01f)
        {
            return PolygonsOverlap(Corners(a), Corners(b), tolerance);
        }

        public static bool Overlaps(LayoutItemSpec item, Rect rect, float tolerance = 0.01f)
        {
            return PolygonsOverlap(Corners(item), RectCorners(rect), tolerance);
        }

        public static float SegmentDistance(LayoutItemSpec item, Vector2 a, Vector2 b)
        {
            return SegmentPolygonDistance(a, b, Corners(item));
        }

        public static float SegmentPolygonDistance(Vector2 a, Vector2 b, Vector2[] polygon)
        {
            if (polygon == null || polygon.Length < 3) return float.MaxValue;
            if (PointInPolygon(a, polygon) || PointInPolygon(b, polygon)) return 0f;
            float best = float.MaxValue;
            for (int i = 0; i < polygon.Length; i++)
                best = Mathf.Min(best, SegmentSegmentDistance(a, b, polygon[i], polygon[(i + 1) % polygon.Length]));
            return best;
        }

        public static float PointSegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 segment = b - a;
            float length2 = segment.sqrMagnitude;
            if (length2 < 0.00001f) return Vector2.Distance(point, a);
            float t = Mathf.Clamp01(Vector2.Dot(point - a, segment) / length2);
            return Vector2.Distance(point, a + segment * t);
        }

        private static bool PolygonsOverlap(Vector2[] a, Vector2[] b, float tolerance)
        {
            return HasOverlapOnAxes(a, b, a, tolerance) && HasOverlapOnAxes(a, b, b, tolerance);
        }

        private static bool HasOverlapOnAxes(Vector2[] a, Vector2[] b, Vector2[] axesSource, float tolerance)
        {
            for (int i = 0; i < axesSource.Length; i++)
            {
                Vector2 edge = axesSource[(i + 1) % axesSource.Length] - axesSource[i];
                if (edge.sqrMagnitude < 0.00001f) continue;
                Vector2 axis = new Vector2(-edge.y, edge.x).normalized;
                float aMin, aMax, bMin, bMax;
                Project(a, axis, out aMin, out aMax);
                Project(b, axis, out bMin, out bMax);
                if (Mathf.Min(aMax, bMax) - Mathf.Max(aMin, bMin) <= tolerance) return false;
            }
            return true;
        }

        private static void Project(Vector2[] polygon, Vector2 axis, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                float value = Vector2.Dot(polygon[i], axis);
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }
        }

        private static Vector2[] RectCorners(Rect rect)
        {
            return new[]
            {
                new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax),
            };
        }

        private static bool PointInPolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > point.y) == (polygon[j].y > point.y)) continue;
                float x = (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y)
                          / (polygon[j].y - polygon[i].y) + polygon[i].x;
                if (point.x < x) inside = !inside;
            }
            return inside;
        }

        private static float SegmentSegmentDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            if (SegmentsIntersect(a, b, c, d)) return 0f;
            return Mathf.Min(
                Mathf.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
                Mathf.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Cross(b - a, c - a);
            float abD = Cross(b - a, d - a);
            float cdA = Cross(d - c, a - c);
            float cdB = Cross(d - c, b - c);
            const float epsilon = 0.0001f;
            return abC * abD <= epsilon && cdA * cdB <= epsilon
                   && Mathf.Sign(abC) != Mathf.Sign(abD) && Mathf.Sign(cdA) != Mathf.Sign(cdB);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }
    }
}
