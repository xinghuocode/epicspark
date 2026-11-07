// EpicSpark 玩家交互契约
using UnityEngine;

namespace EpicSpark.Player
{
    /// <summary>玩家准星射线可命中的可互动对象（锚点、NPC、可操作道具等）。</summary>
    public interface IInteractable
    {
        /// <summary>HUD 上显示的交互提示，例如「按 E 与「马车夫」交谈」。</summary>
        string InteractPrompt { get; }

        /// <summary>玩家按下互动键时被调用。</summary>
        void Interact(EpicSparkPlayerController source);
    }
}
