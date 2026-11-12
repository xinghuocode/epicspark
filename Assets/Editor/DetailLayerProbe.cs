using System.IO;
using System.Text;
using EpicSpark.Loading;
using UnityEditor;
using UnityEngine;

namespace EpicSpark.EditorTools
{
    /// <summary>
    /// 细节层诊断探针（仅编辑器用，不参与运行时）。
    /// 用法：
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod EpicSpark.EditorTools.DetailLayerProbe.Run -logFile &lt;log&gt;
    /// 作用：真实加载一个 GLB，检查 DetailSurface 是否真的把细节贴图/关键字/平铺挂到了材质上，
    ///       并把程序化生成的细节 albedo / normal 导出成 PNG 便于肉眼确认。
    /// </summary>
    public static class DetailLayerProbe
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            string modelFile = "mdl_church_001.glb";
            string path = Path.Combine(Application.streamingAssetsPath, "Models", modelFile);

            sb.AppendLine("streamingAssets=" + Application.streamingAssetsPath);
            sb.AppendLine("fileExists=" + File.Exists(path));
            if (!File.Exists(path))
            {
                Debug.Log("[Probe] " + sb);
                return;
            }

            byte[] bytes = File.ReadAllBytes(path);
            GlbModel model = GlbModelLoader.Parse(modelFile, bytes);
            sb.AppendLine("meshVertices=" + model.mesh.vertexCount + " tris=" + (model.mesh.triangles.Length / 3));
            sb.AppendLine("meshBounds=" + model.bounds.size.ToString("F3") + " diag=" + model.bounds.size.magnitude.ToString("F4"));
            sb.AppendLine("DetailSurface.Enabled=" + DetailSurface.Enabled);

            Material m = model.material;
            sb.AppendLine("shader=" + (m == null || m.shader == null ? "null" : m.shader.name));
            if (m == null) { Debug.Log("[Probe]\n" + sb); return; }

            sb.AppendLine("hasProperty(_DetailAlbedoMap)=" + m.HasProperty("_DetailAlbedoMap"));
            sb.AppendLine("hasProperty(_DetailNormalMap)=" + m.HasProperty("_DetailNormalMap"));

            Texture2D alb = m.GetTexture("_DetailAlbedoMap") as Texture2D;
            Texture2D nrm = m.GetTexture("_DetailNormalMap") as Texture2D;
            sb.AppendLine("detailAlbedo=" + Describe(alb));
            sb.AppendLine("detailNormal=" + Describe(nrm));
            sb.AppendLine("detailAlbedo_ST=" + m.GetTextureScale("_DetailAlbedoMap"));
            sb.AppendLine("keyword_DETAIL_MULX2=" + m.IsKeywordEnabled("_DETAIL_MULX2"));
            sb.AppendLine("keyword_DETAIL_SCALED=" + m.IsKeywordEnabled("_DETAIL_SCALED"));
            sb.AppendLine("baseMap=" + Describe(m.GetTexture("_BaseMap") as Texture2D));

            // 导出生成的细节图，便于肉眼确认（均值应为中性灰，接缝应无缝）
            TryDump(alb, "detail_albedo_preview.png", sb);
            TryDump(nrm, "detail_normal_preview.png", sb);

            Debug.Log("[Probe]\n" + sb);
            string outFile = Path.Combine(Directory.GetCurrentDirectory(), "_probe-detail.txt");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            Debug.Log("[Probe] 结果已写入 " + outFile);
        }

        private static string Describe(Texture2D t)
        {
            if (t == null) return "null";
            return string.Format("{0} {1}x{2} fmt={3} wrap={4} filter={5} mips={6} aniso={7} linear={8}",
                t.name, t.width, t.height, t.format, t.wrapMode, t.filterMode, t.mipmapCount, t.anisoLevel, t.isDataSRGB ? "sRGB" : "linear");
        }

        private static void TryDump(Texture2D t, string fileName, StringBuilder sb)
        {
            if (t == null) return;
            try
            {
                byte[] png = t.EncodeToPNG();
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, fileName);
                File.WriteAllBytes(p, png);
                sb.AppendLine("dumped=" + p + " (" + png.Length + " bytes)");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("dumpFailed[" + fileName + "]=" + e.Message);
            }
        }
    }
}
