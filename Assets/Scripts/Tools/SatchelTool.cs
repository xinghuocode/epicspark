// 挎包工具：容器，保存玩家收集到的道具 id 列表。
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Tools
{
    /// <summary>挎包：装东西的容器，提供 Add / Remove / Has。</summary>
    public class SatchelTool : HeldTool
    {
        private readonly List<string> _items = new List<string>();

        public override ToolKind Kind { get { return ToolKind.Satchel; } }
        public override string DisplayName { get { return "挎包"; } }

        /// <summary>道具列表（只读视图）。</summary>
        public IReadOnlyList<string> Items { get { return _items; } }
        public int Count { get { return _items.Count; } }

        public override void OnEquip()
        {
            base.OnEquip();
        }

        public bool Add(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            _items.Add(itemId);
            LogAction("take", itemId, "把「" + itemId + "」放进挎包（共 " + _items.Count + " 件）", transform.position);
            return true;
        }

        public bool Remove(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            if (!_items.Remove(itemId)) return false;
            LogAction("drop", itemId, "从挎包取出「" + itemId + "」（剩余 " + _items.Count + " 件）", transform.position);
            return true;
        }

        public bool Has(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            return _items.Contains(itemId);
        }

        public void Clear()
        {
            if (_items.Count == 0) return;
            _items.Clear();
            LogAction("drop", "", "清空挎包", transform.position);
        }

        public override void Use(Transform viewTransform)
        {
            string summary = _items.Count == 0 ? "空空如也" : string.Join("、", _items.ToArray());
            LogAction("examine", "", "翻看挎包（" + _items.Count + " 件）：" + summary, viewTransform != null ? viewTransform.position : transform.position);
        }
    }
}
