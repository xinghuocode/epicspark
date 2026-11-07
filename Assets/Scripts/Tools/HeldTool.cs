// 手持工具基类：设计文档第二节「一切被主观能动者携带和使用的东西」。
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Tools
{
    /// <summary>工具基类。每个主观能动者同一时间只持有一个工具。</summary>
    public abstract class HeldTool : MonoBehaviour
    {
        /// <summary>工具类型。</summary>
        public abstract ToolKind Kind { get; }

        /// <summary>显示名（中文）。</summary>
        public virtual string DisplayName { get { return Kind.ToString(); } }

        /// <summary>是否正被持有。</summary>
        public bool IsEquipped { get; set; }

        /// <summary>持有者（玩家或 NPC 的 ToolInventory）。</summary>
        public ToolInventory Owner { get; set; }

        public virtual void OnEquip()
        {
            IsEquipped = true;
            gameObject.SetActive(true);
        }

        public virtual void OnUnequip()
        {
            IsEquipped = false;
            gameObject.SetActive(false);
        }

        /// <summary>使用工具（玩家左键 / NPC 行为）。</summary>
        public virtual void Use(Transform viewTransform)
        {
        }

        /// <summary>写一条玩家/NPC 行为日志。</summary>
        protected void LogAction(string actionType, string targetId, string detail, Vector3 position)
        {
            string actorId = Owner != null && !string.IsNullOrEmpty(Owner.ActorId) ? Owner.ActorId : "player";
            GameLog.Record(new PlayerActionRecord
            {
                sceneId = RuntimeContext.ResolveSceneId(),
                actorId = actorId,
                actionType = actionType ?? "",
                targetId = targetId ?? "",
                detail = detail ?? "",
                gameMinutes = GameClock.TotalMinutes,
                position = position,
            });
        }
    }
}
