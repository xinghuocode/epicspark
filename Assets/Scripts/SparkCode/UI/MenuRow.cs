using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SparkCode.UI
{
    /// <summary>
    /// One settings row (quality / volume / fullscreen). Click cycles the value,
    /// the concrete setting is applied &amp; persisted by StartMenuController.
    /// Value labels are baked sprites, swapped by SetValue().
    /// </summary>
    public class MenuRow : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public StartMenuController controller;
        public RowKind kind = RowKind.Quality;

        [Header("Visuals")]
        public Image hoverBg;
        public Image label;
        public Image value;
        public Image chevronLeft;
        public Image chevronRight;

        [Header("Value sprites")]
        public Sprite[] valueSprites;

        public int index;

        float _hover;
        bool _pointer;
        bool _highlight;

        public void SetValue(int i)
        {
            if (valueSprites == null || valueSprites.Length == 0) return;
            index = Mathf.Clamp(i, 0, valueSprites.Length - 1);
            Sprite sprite = valueSprites[index];
            if (value == null || sprite == null) return;
            value.sprite = sprite;
            var rt = value.rectTransform;
            rt.sizeDelta = new Vector2(sprite.rect.width, sprite.rect.height) * 0.475f;
        }

        public void SetHighlight(bool on)
        {
            _highlight = on;
            _hover = on ? 1f : 0f;
            Apply();
        }

        void Update()
        {
            bool allowed = controller == null || (controller.IsPanelOpen);
            float target = _highlight ? 1f : ((_pointer && allowed) ? 1f : 0f);
            _hover = Mathf.MoveTowards(_hover, target, Time.deltaTime * 10f);
            Apply();
        }

        void Apply()
        {
            float h = Mathf.Clamp01(_hover);
            if (hoverBg != null)
            {
                Color c = hoverBg.color;
                c.a = h * 0.9f;
                hoverBg.color = c;
            }
            if (chevronLeft != null)
            {
                Color c = chevronLeft.color;
                c.a = Mathf.Lerp(0.45f, 0.95f, h);
                chevronLeft.color = c;
            }
            if (chevronRight != null)
            {
                Color c = chevronRight.color;
                c.a = Mathf.Lerp(0.45f, 0.95f, h);
                chevronRight.color = c;
            }
            if (value != null)
            {
                Color c = value.color;
                c.r = Mathf.Lerp(0.92f, 1f, h);
                c.g = Mathf.Lerp(0.88f, 1f, h);
                c.b = Mathf.Lerp(0.76f, 0.92f, h);
                c.a = 1f;
                value.color = c;
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointer = true;
            if (controller != null && controller.IsPanelOpen) controller.RefreshRows();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointer = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (controller == null || !controller.IsPanelOpen) return;
            controller.CycleRow(this, eventData.button == PointerEventData.InputButton.Right ? -1 : 1);
        }
    }
}
