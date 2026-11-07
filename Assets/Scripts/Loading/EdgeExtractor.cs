// 折边提取：从网格中挑出「棱」，供新场景加载流程的蓝绿线框阶段使用。
// 设计文档：搭建阶段用蓝绿色线条显示每个美术资产的棱，且棱之间有正确的遮挡关系。
// 直接取全部三角形边会得到 12 万条线段（每模型），所以只保留二面角大于阈值的折边与边界边。
using System.Collections.Generic;
using UnityEngine;

namespace EpicSpark.Loading
{
    public static class EdgeExtractor
    {
        private struct EdgeRecord
        {
            public int a;
            public int b;
            public int faceA;
            public int faceB;
        }

        /// <summary>返回 MeshTopology.Lines 用的索引数组（每两个索引一条线段）。</summary>
        public static int[] ExtractCreasedEdges(Mesh mesh, float angleThresholdDeg)
        {
            if (mesh == null) return new int[0];
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            if (vertices.Length == 0 || triangles.Length < 3) return new int[0];

            int faceCount = triangles.Length / 3;
            var faceNormals = new Vector3[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                Vector3 a = vertices[triangles[f * 3]];
                Vector3 b = vertices[triangles[f * 3 + 1]];
                Vector3 c = vertices[triangles[f * 3 + 2]];
                faceNormals[f] = Vector3.Cross(b - a, c - a).normalized;
            }

            // 按位置量化合并顶点，才能识别出真实相邻面（trimesh 输出在 UV 缝处会有重复顶点）。
            var weld = new Dictionary<Vector3Int, int>(vertices.Length);
            var welded = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var key = new Vector3Int(
                    Mathf.RoundToInt(vertices[i].x * 2000f),
                    Mathf.RoundToInt(vertices[i].y * 2000f),
                    Mathf.RoundToInt(vertices[i].z * 2000f));
                int id;
                if (!weld.TryGetValue(key, out id))
                {
                    id = weld.Count;
                    weld.Add(key, id);
                }
                welded[i] = id;
            }

            var edges = new Dictionary<long, EdgeRecord>(faceCount * 3);
            for (int f = 0; f < faceCount; f++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int va = welded[triangles[f * 3 + e]];
                    int vb = welded[triangles[f * 3 + (e + 1) % 3]];
                    if (va == vb) continue;
                    long key = va < vb ? (((long)va << 32) | (uint)vb) : (((long)vb << 32) | (uint)va);

                    EdgeRecord record;
                    if (edges.TryGetValue(key, out record))
                    {
                        if (record.faceB < 0)
                        {
                            record.faceB = f;
                            edges[key] = record;
                        }
                    }
                    else
                    {
                        record.a = triangles[f * 3 + e];
                        record.b = triangles[f * 3 + (e + 1) % 3];
                        record.faceA = f;
                        record.faceB = -1;
                        edges.Add(key, record);
                    }
                }
            }

            float cosLimit = Mathf.Cos(Mathf.Clamp(angleThresholdDeg, 0f, 180f) * Mathf.Deg2Rad);
            var result = new List<int>(edges.Count * 2);
            foreach (KeyValuePair<long, EdgeRecord> pair in edges)
            {
                EdgeRecord record = pair.Value;
                bool keep;
                if (record.faceB < 0)
                {
                    keep = true; // 边界边必须画，否则模型轮廓会缺
                }
                else
                {
                    float dot = Vector3.Dot(faceNormals[record.faceA], faceNormals[record.faceB]);
                    keep = dot < cosLimit;
                }

                if (keep)
                {
                    result.Add(record.a);
                    result.Add(record.b);
                }
            }
            return result.ToArray();
        }
    }
}
