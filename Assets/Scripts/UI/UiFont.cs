// UI 字体解析：优先系统中文字体，失败回退 Unity 内置字体（项目不依赖 TMP 与字体资源）。
using UnityEngine;

namespace EpicSpark.UI
{
    /// <summary>运行时字体获取。全部 UI 共用同一份字体。</summary>
    public static class UiFont
    {
        private static Font _font;
        private static bool _tried;

        public static Font Get()
        {
            if (_tried) return _font;
            _tried = true;

            string[] candidates =
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimHei",
                "SimSun",
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "PingFang SC",
                "Hiragino Sans GB",
                "Arial Unicode MS",
                "Arial"
            };

            try
            {
                _font = Font.CreateDynamicFontFromOSFont(candidates, 28);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[EpicSpark] 系统字体加载失败：" + e.Message);
            }

            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch (System.Exception e) { Debug.LogWarning("[EpicSpark] 内置字体 LegacyRuntime 不可用：" + e.Message); }
            }
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch (System.Exception e) { Debug.LogWarning("[EpicSpark] 内置字体 Arial 不可用：" + e.Message); }
            }
            if (_font == null) Debug.LogWarning("[EpicSpark] 未找到可用字体，UI 文本可能无法显示。");

            return _font;
        }
    }
}
