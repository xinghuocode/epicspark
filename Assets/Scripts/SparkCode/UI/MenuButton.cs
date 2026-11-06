using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SparkCode.UI
{
    /// <summary>
    /// One menu / panel button built entirely from baked sprites.
    /// Handles pointer hover, click pulse and forced hover for keyboard navigation
    /// and editor captures. Initial visuals are authored by StartMenuBuilder, so the
    /// button reads correctly even before the first Update (and in edit mode).
    /// </summary>
    public class MenuButton : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler,
        IPointerDownHandler, IPointerUpHandler
    {
        public StartMenuController controller;
        public MenuAction action = MenuAction.Start;
        public int index;
        public bool alwaysInteractable;

        [Header("Visuals")]
        public RectTransform visual;
        public Image frame;
        public Image frameHover;
        public Image halo;
        public Image label;
        public Image marker;
        public Image glowDot;

        [Header("Tuning")]
        public Color labelNormal = new Color(1f, 0.97f, 0.90f, 1f);
        public Color labelHover = new Color(1f, 1f, 1f, 1f);
        public float hoverScale = 1.04f;
        public float hoverSpeed = 12f;

        float _hover;
        float _pulse;
        bool _pointer;
        bool _forced;
        bool _forcedValue;

        public bool Allowed
        {
            get { return controller == null || controller.ButtonInteractable(this); }
        }

        public void SetHoverForced(bool on)
        {
            _forced = true;
            _forcedValue = on;
            _hover = on ? 1f : 0f;
            Apply();
        }

        public void ClearForced()
        {
            _forced = false;
            _forcedValue = false;
            _hover = 0f;
            Apply();
        }

        public void Pulse()
        {
            _pulse = 1f;
            Apply();
        }

        void Update()
        {
            bool allowed = Allowed;
            float target;
            if (_forced) target = _forcedValue ? 1f : 0f;
            else target = (_pointer && allowed) ? 1f : 0f;

            _hover = Mathf.MoveTowards(_hover, target, Time.deltaTime * hoverSpeed);
            _pulse = Mathf.MoveTowards(_pulse, 0f, Time.deltaTime * 4f);
            Apply();
        }

        void Apply()
        {
            float h = Mathf.Clamp01(_hover);
            float eased = h * h * (3f - 2f * h);

            if (visual != null)
            {
                float s = Mathf.Lerp(1f, hoverScale, eased) * (1f + _pulse * 0.05f);
                visual.localScale = new Vector3(s, s, 1f);
            }
            if (frame != null)
            {
                Color c = frame.color;
                c.a = Mathf.Lerp(0.84f, 1f, eased);
                frame.color = c;
            }
            if (frameHover != null)
            {
                Color c = frameHover.color;
                c.a = eased;
                frameHover.color = c;
            }
            if (halo != null)
            {
                Color c = halo.color;
                c.a = Mathf.Lerp(0.07f, 0.40f, eased);
                halo.color = c;
            }
            if (glowDot != null)
            {
                Color c = glowDot.color;
                c.a = Mathf.Lerp(0.22f, 0.85f, eased);
                glowDot.color = c;
            }
            if (marker != null)
            {
                Color c = marker.color;
                c.a = Mathf.Lerp(0.45f, 1f, eased);
                marker.color = c;
            }
            if (label != null)
            {
                label.color = Color.Lerp(labelNormal, labelHover, eased);
            }
        }

        void OnDisable()
        {
            _pointer = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointer = true;
            if (!Allowed) return;
            if (controller != null) controller.NotifyPointerHover(index, this);
            Apply();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointer = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Allowed) return;
            _pulse = 1f;
            Apply();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!Allowed) return;
            if (controller != null) controller.OnMenuAction(action);
        }
    }
}
