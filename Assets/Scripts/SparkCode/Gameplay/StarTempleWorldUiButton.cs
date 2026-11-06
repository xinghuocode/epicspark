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
        ExitNo
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
