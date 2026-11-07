// 左上角剧情文本流：显示最近 6 条叙事（含 NPC 回复）。
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace EpicSpark.UI
{
    /// <summary>剧情文本视图。可被 HudController 创建，也可单独挂在场景里自建 Canvas。</summary>
    public class NarrativeView : MonoBehaviour
    {
        [Header("显示")]
        public int maxLines = 6;
        public int fontSize = 20;

        private readonly List<string> _lines = new List<string>();
        private Text _text;
        private bool _built;

        public int LineCount { get { return _lines.Count; } }

        /// <summary>HudController 用的工厂：在给定父节点（Canvas）下创建文本面板。</summary>
        public static NarrativeView Create(Transform parent, int textSize)
        {
            GameObject go = new GameObject("NarrativeView", typeof(RectTransform));
            if (parent != null) go.transform.SetParent(parent, false);
            NarrativeView view = go.AddComponent<NarrativeView>();
            if (textSize > 0) view.fontSize = textSize;
            view.Build(parent);
            return view;
        }

        private void Awake()
        {
            if (GetComponent<RectTransform>() == null) return;

            // 单独放在场景里时自建 Canvas，保证可见。
            if (GetComponentInParent<Canvas>() == null)
            {
                Canvas canvas = UiFactory.CreateCanvas("NarrativeCanvas", 12, false);
                transform.SetParent(canvas.transform, false);
            }
            Build(transform.parent);
        }

        /// <summary>在 parent 下搭建面板（重复调用只做重挂与字号同步）。</summary>
        public void Build(Transform parent)
        {
            RectTransform rect = GetComponent<RectTransform>();
            if (rect == null) return;

            if (parent != null && rect.parent != parent) rect.SetParent(parent, false);

            if (_built)
            {
                if (_text != null) _text.fontSize = fontSize;
                return;
            }
            _built = true;

            UiFactory.SetAnchors(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(32f, -28f), new Vector2(760f, 230f));

            Image background = UiFactory.NewImage("Background", rect, new Color(0f, 0f, 0f, 0.18f));
            UiFactory.Stretch(background.rectTransform, 0f, 0f, 0f, 0f);

            _text = UiFactory.NewText("Lines", rect, fontSize, new Color(1f, 1f, 1f, 0.94f), TextAnchor.UpperLeft);
            UiFactory.Stretch(_text.rectTransform, 14f, 12f, 14f, 12f);
            _text.lineSpacing = 1.15f;

            Refresh();
        }

        /// <summary>推入一条剧情文本（超出 maxLines 时丢弃最旧的）。</summary>
        public void Push(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnsureBuilt();
            _lines.Add(text.Trim());
            while (_lines.Count > Mathf.Max(1, maxLines)) _lines.RemoveAt(0);
            Refresh();
        }

        /// <summary>整批替换显示内容。</summary>
        public void SetLines(IEnumerable<string> lines)
        {
            EnsureBuilt();
            _lines.Clear();
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    _lines.Add(line.Trim());
                }
            }
            while (_lines.Count > Mathf.Max(1, maxLines)) _lines.RemoveAt(0);
            Refresh();
        }

        public void Clear()
        {
            _lines.Clear();
            Refresh();
        }

        private void EnsureBuilt()
        {
            if (!_built) Build(transform.parent);
            if (_text == null) Build(transform.parent);
        }

        private void Refresh()
        {
            if (_text == null) return;
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(_lines[i]);
            }
            _text.text = builder.ToString();
        }
    }
}
