// 灰盒阶段的可视化：用灰色线条框出灰盒规划阶段每一部分区域的边界。
// 设计文档第六节第 2 步：用灰色线条显示矢量图，线条为灰盒规划阶段每部分区域的边界。
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Loading
{
    public static class GrayboxRenderer
    {
        public const float LineHeight = 0.06f;

        /// <summary>为整个灰盒规划生成灰线可视化，返回根对象（加载动画结束后销毁）。</summary>
        public static GameObject Build(GrayboxPlan plan, Transform parent)
        {
            var root = new GameObject("GrayboxLines");
            if (parent != null) root.transform.SetParent(parent, false);
            if (plan == null) return root;

            Material lineMaterial = CreateLineMaterial(new Color(0.62f, 0.62f, 0.62f, 1f));

            foreach (GrayboxRegion region in plan.regions)
            {
                if (region == null || region.polygon == null || region.polygon.Count < 3) continue;
                var points = new List<Vector3>(region.polygon.Count + 1);
                for (int i = 0; i < region.polygon.Count; i++)
                    points.Add(ToWorld(region.polygon[i], region.groundHeight + LineHeight));
                points.Add(ToWorld(region.polygon[0], region.groundHeight + LineHeight));
                CreateLine("region_" + region.regionId, points, lineMaterial, root.transform, false);
            }

            foreach (GrayboxPath path in plan.paths)
            {
                if (path == null || path.nodes == null || path.nodes.Count < 2) continue;
                var points = new List<Vector3>(path.nodes.Count);
                for (int i = 0; i < path.nodes.Count; i++)
                    points.Add(ToWorld(path.nodes[i], LineHeight));
                Color color = path.kind == "wall" ? new Color(0.72f, 0.72f, 0.72f, 1f) : new Color(0.55f, 0.55f, 0.55f, 1f);
                CreateLine("path_" + path.pathId, points, CreateLineMaterial(color), root.transform, false);
            }

            foreach (NpcPath npcPath in plan.npcPaths)
            {
                if (npcPath == null || npcPath.waypoints == null || npcPath.waypoints.Count < 2) continue;
                var points = new List<Vector3>(npcPath.waypoints.Count);
                for (int i = 0; i < npcPath.waypoints.Count; i++)
                    points.Add(ToWorld(npcPath.waypoints[i], LineHeight * 2f));
                if (npcPath.loop && npcPath.waypoints.Count > 2) points.Add(points[0]);
                CreateLine("npcpath_" + npcPath.npcId, points, CreateLineMaterial(new Color(0.78f, 0.78f, 0.78f, 1f)), root.transform, true);
            }

            return root;
        }

        public static Vector3 ToWorld(Vector2 flat, float height)
        {
            // 灰盒平面坐标 (x, y) → 世界坐标 (x, height, z=y)
            return new Vector3(flat.x, height, flat.y);
        }

        public static Material CreateLineMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = "LineMat" };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            return material;
        }

        private static LineRenderer CreateLine(string name, List<Vector3> points, Material material, Transform parent, bool dashed)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.material = material;
            line.widthMultiplier = dashed ? 0.05f : 0.09f;
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.textureMode = LineTextureMode.Stretch;
            if (dashed)
            {
                line.textureMode = LineTextureMode.Tile;
                line.material.mainTextureScale = new Vector2(points.Count * 2f, 1f);
            }
            return line;
        }
    }
}
