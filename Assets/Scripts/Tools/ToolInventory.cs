// 工具系统入口：每个主观能动者同一时间只能持有一个工具（枪 / 挎包 / 空手）。
using System;
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Tools
{
    /// <summary>主观能动者的工具背包。切换工具会写日志并广播 ToolEquipped。</summary>
    public class ToolInventory : MonoBehaviour
    {
        /// <summary>工具切换广播（Lead 可用于记录 / 刷新 HUD）。</summary>
        public static Action<ToolKind> ToolEquipped;

        /// <summary>当前场景内全部工具背包。</summary>
        public static readonly List<ToolInventory> All = new List<ToolInventory>();

        [Header("持有者")]
        [Tooltip("写入日志的 actorId，玩家为 player，NPC 为 npcId")]
        public string ActorId = "player";

        [Tooltip("进入运行时自动持有的工具")]
        public ToolKind startingTool = ToolKind.Gun;

        public bool autoCreateTools = true;

        public ToolKind Current { get; private set; }
        public GunTool Gun { get; private set; }
        public SatchelTool Satchel { get; private set; }

        public HeldTool CurrentTool { get { return KindToTool(Current); } }
        public float Ammo { get { return Gun != null ? Gun.Ammo : 0f; } }
        public float AmmoCapacity { get { return Gun != null ? Gun.Capacity : 0f; } }
        public float AmmoReserve { get { return Gun != null ? Gun.Reserve : 0f; } }

        private void Awake()
        {
            Current = ToolKind.None;
            All.Add(this);

            Gun = GetComponentInChildren<GunTool>(true);
            Satchel = GetComponentInChildren<SatchelTool>(true);
            if (autoCreateTools)
            {
                if (Gun == null) Gun = CreateTool<GunTool>("Gun");
                if (Satchel == null) Satchel = CreateTool<SatchelTool>("Satchel");
            }

            if (Gun != null)
            {
                Gun.Owner = this;
                Gun.OnUnequip();
            }
            if (Satchel != null)
            {
                Satchel.Owner = this;
                Satchel.OnUnequip();
            }
        }

        private void Start()
        {
            if (startingTool != ToolKind.None && Current == ToolKind.None) Equip(startingTool);
        }

        private void OnDestroy()
        {
            All.Remove(this);
        }

        /// <summary>切换到指定工具（None = 空手）。同一时间只有一个工具处于持有状态。</summary>
        public void Equip(ToolKind kind)
        {
            ApplyEquip(kind, true, true);
        }

        /// <summary>
        /// 初始装备 / NPC 装备：只改变手持外观，不写玩家行为日志、也不广播 ToolEquipped，
        /// 避免 NPC 的初始化污染剧情 Agent 的 recentActions。
        /// </summary>
        public void EquipSilent(ToolKind kind)
        {
            ApplyEquip(kind, false, false);
        }

        private void ApplyEquip(ToolKind kind, bool record, bool broadcast)
        {
            if (kind == Current) return;

            HeldTool previous = CurrentTool;
            if (previous != null) previous.OnUnequip();

            Current = kind;
            HeldTool next = KindToTool(kind);
            if (next != null) next.OnEquip();

            if (record) RecordEquip(kind, previous != null ? previous.DisplayName : "空手");

            if (broadcast)
            {
                Action<ToolKind> handler = ToolEquipped;
                if (handler != null) handler(kind);
            }
        }

        /// <summary>在枪与挎包之间轮换。</summary>
        public void CycleTool()
        {
            Equip(Current == ToolKind.Gun ? ToolKind.Satchel : ToolKind.Gun);
        }

        /// <summary>用当前工具开火（仅枪有效）。</summary>
        public bool TryFire(Transform viewTransform)
        {
            if (Current != ToolKind.Gun || Gun == null) return false;
            return Gun.TryFire(viewTransform);
        }

        public void Reload()
        {
            if (Gun != null) Gun.Reload();
        }

        /// <summary>使用当前工具。</summary>
        public void UseCurrent(Transform viewTransform)
        {
            HeldTool tool = CurrentTool;
            if (tool != null) tool.Use(viewTransform);
        }

        public bool HasSatchelItem(string itemId)
        {
            return Satchel != null && Satchel.Has(itemId);
        }

        public bool AddSatchelItem(string itemId)
        {
            if (Satchel == null) return false;
            return Satchel.Add(itemId);
        }

        public bool RemoveSatchelItem(string itemId)
        {
            if (Satchel == null) return false;
            return Satchel.Remove(itemId);
        }

        public string DescribeCurrent()
        {
            switch (Current)
            {
                case ToolKind.Gun:
                    return Gun != null
                        ? "枪 " + Gun.Ammo.ToString("0") + "/" + Gun.Capacity.ToString("0")
                        : "枪";
                case ToolKind.Satchel:
                    return Satchel != null ? "挎包 " + Satchel.Count + " 件" : "挎包";
                default:
                    return "空手";
            }
        }

        private HeldTool KindToTool(ToolKind kind)
        {
            switch (kind)
            {
                case ToolKind.Gun: return Gun;
                case ToolKind.Satchel: return Satchel;
                default: return null;
            }
        }

        private T CreateTool<T>(string childName) where T : HeldTool
        {
            GameObject go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            T tool = go.AddComponent<T>();
            go.SetActive(true);
            return tool;
        }

        private void RecordEquip(ToolKind kind, string fromName)
        {
            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = string.IsNullOrEmpty(ActorId) ? "player" : ActorId,
                actionType = "equip",
                targetId = kind.ToString().ToLowerInvariant(),
                detail = "切换手持工具：" + fromName + " → " + DescribeKind(kind),
                gameMinutes = GameClock.TotalMinutes,
                position = transform.position,
            });
        }

        private static string DescribeKind(ToolKind kind)
        {
            switch (kind)
            {
                case ToolKind.Gun: return "枪";
                case ToolKind.Satchel: return "挎包";
                default: return "空手";
            }
        }
    }
}
