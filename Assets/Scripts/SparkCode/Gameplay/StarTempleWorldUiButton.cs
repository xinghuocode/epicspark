using UnityEngine;
using UnityEngine.UI;

namespace SparkCode.Gameplay
{
    public enum StarTempleUiAction
    {
        Back,
        EnterEpic,
        Settings,
        ExitYes,
        ExitNo,
        /// <summary>进入史诗 → 创建新史诗（打开漂浮窗内的新建页）。</summary>
        CreateEpic,
        /// <summary>进入史诗 → 加载已有史诗（读取上次存档）。</summary>
        LoadEpic,
        /// <summary>设置页 → 打开 API Key 与模型设置。</summary>
        OpenApi
    }

    /// <summary>A UGUI button without EventSystem dependencies; the UI controller tests its rect.</summary>
    public class StarTempleWorldUiButton : MonoBehaviour
    {
        public StarTempleUiAction action;
        public Image background;
        public Text label;
        public Color normalColor = new Color(0.08f, 0.11f, 0.15f, 0.96f);
        public Color hoverColor = new Color(0.30f, 0.22f, 0.09f, 1f);

        public void SetHovered(bool hovered)
        {
            if (background != null)
                background.color = hovered ? hoverColor : normalColor;
        }
    }
}
