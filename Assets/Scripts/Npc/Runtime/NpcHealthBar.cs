// NPC 血条：世界空间朝向相机的纯代码血条（SpriteRenderer 拼背景条 + 前景区），满血隐藏。
using UnityEngine;

namespace EpicSpark.Npc.Runtime
{
    /// <summary>世界空间血条。受伤才显示，满血隐藏。</summary>
    public class NpcHealthBar : MonoBehaviour
    {
        [Header("跟随")]
        public Transform followTarget;
        public float headHeight = 2.05f;

        [Header("外观")]
        public float barWidth = 1.05f;
        public float barHeight = 0.13f;
        public Color backgroundColor = new Color(0.04f, 0.04f, 0.06f, 1f);
        public Color fillColor = new Color(0.9f, 0.18f, 0.18f, 1f);
        public Color lowColor = new Color(0.55f, 0.04f, 0.04f, 1f);
        public float lowThreshold = 0.3f;
        public int sortingOrder = 200;

        [Header("行为")]
        public bool hideWhenFull = true;
        public string DisplayName = "";

        private Transform _fill;
        private SpriteRenderer _fillRenderer;
        private Transform _cameraTransform;
        private float _ratio = 1f;
        private bool _built;
        private static Sprite _whiteSprite;

        private void Awake()
        {
            Build();
        }

        /// <summary>便捷工厂。</summary>
        public static NpcHealthBar Create(Transform parent, float height)
        {
            GameObject go = new GameObject("HealthBar");
            go.transform.SetParent(parent, false);
            NpcHealthBar bar = go.AddComponent<NpcHealthBar>();
            bar.followTarget = parent;
            bar.headHeight = height;
            return bar;
        }

        /// <summary>刷新血条；满血时按 hideWhenFull 自动隐藏。</summary>
        public void SetValue(float current, float max)
        {
            Build();
            _ratio = max > 0.001f ? Mathf.Clamp01(current / max) : 0f;
            UpdateFill();

            bool visible = !(hideWhenFull && _ratio >= 0.999f);
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        }

        public void Show()
        {
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private void Build()
        {
            if (_built) return;
            _built = true;

            GameObject background = new GameObject("BarBackground");
            background.transform.SetParent(transform, false);
            background.transform.localPosition = Vector3.zero;
            background.transform.localScale = new Vector3(barWidth, barHeight, 1f);
            SpriteRenderer backgroundRenderer = background.AddComponent<SpriteRenderer>();
            backgroundRenderer.sprite = WhiteSprite();
            backgroundRenderer.color = backgroundColor;
            backgroundRenderer.sortingOrder = sortingOrder;
            backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            backgroundRenderer.receiveShadows = false;

            GameObject fill = new GameObject("BarFill");
            fill.transform.SetParent(transform, false);
            fill.transform.localPosition = Vector3.zero;
            fill.transform.localScale = new Vector3(barWidth, barHeight * 0.72f, 1f);
            _fillRenderer = fill.AddComponent<SpriteRenderer>();
            _fillRenderer.sprite = WhiteSprite();
            _fillRenderer.color = fillColor;
            _fillRenderer.sortingOrder = sortingOrder + 1;
            _fillRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _fillRenderer.receiveShadows = false;
            _fill = fill.transform;

            UpdateFill();
        }

        private void UpdateFill()
        {
            if (_fill == null) return;
            float width = Mathf.Max(0.0001f, barWidth * Mathf.Clamp01(_ratio));
            _fill.localScale = new Vector3(width, barHeight * 0.72f, 1f);
            Vector3 local = _fill.localPosition;
            local.x = -(barWidth - width) * 0.5f;
            local.z = -0.01f;
            _fill.localPosition = local;
            if (_fillRenderer != null)
                _fillRenderer.color = _ratio <= lowThreshold ? lowColor : fillColor;
        }

        private void LateUpdate()
        {
            Transform cameraTransform = ResolveCamera();
            if (cameraTransform != null) transform.rotation = cameraTransform.rotation;

            Transform target = followTarget != null ? followTarget : transform.parent;
            if (target != null) transform.position = target.position + Vector3.up * headHeight;
        }

        private Transform ResolveCamera()
        {
            if (_cameraTransform != null) return _cameraTransform;
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam != null) _cameraTransform = cam.transform;
            return _cameraTransform;
        }

        private static Sprite WhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            texture.name = "NpcBarWhite";
            Color[] pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
            texture.SetPixels(pixels);
            texture.Apply();
            texture.wrapMode = TextureWrapMode.Clamp;
            _whiteSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f);
            _whiteSprite.name = "NpcBarWhiteSprite";
            return _whiteSprite;
        }
    }
}
