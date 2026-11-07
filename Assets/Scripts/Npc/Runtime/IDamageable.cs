// NPC 受损契约（命名空间 EpicSpark.Npc.Runtime）
namespace EpicSpark.Npc.Runtime
{
    /// <summary>可被伤害的东西：NPC、可破坏道具等。</summary>
    public interface IDamageable
    {
        /// <summary>受到伤害。sourceId 为攻击者 id（player / npcId）。</summary>
        void ApplyDamage(float amount, string sourceId);
    }
}
