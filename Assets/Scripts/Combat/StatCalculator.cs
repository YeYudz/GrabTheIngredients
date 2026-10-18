using UnityEngine;

/// <summary>
/// 数值工厂：统一「最终值 = 基础值 ×(1 + 百分比) + 固定值」口径，
/// 供升级 Buff / 烹饪 Buff / 主动技能等所有加成来源调用，避免各系统各写一份公式。
/// 百分比随基础值成长、固定值恒定，使前中期不失控而后期仍有成长空间。
/// </summary>
public static class StatCalculator
{
    /// <summary>
    /// 计算最终属性值：baseValue × (1 + percentBonus) + flatBonus
    /// </summary>
    public static float FinalValue(float baseValue, float percentBonus, float flatBonus)
    {
        return baseValue * (1f + percentBonus) + flatBonus;
    }

    /// <summary>
    /// 计算某属性相对基础值的"加成增量"（用于写入 Bonus 属性）
    /// 增量 = FinalValue(base, percent, flat) - base
    /// </summary>
    public static float BonusDelta(float baseValue, float percentBonus, float flatBonus)
    {
        return FinalValue(baseValue, percentBonus, flatBonus) - baseValue;
    }

    /// <summary>
    /// 计算有效 DPS（含暴击期望，不扣防御）
    /// 公式：攻击力 × 每秒攻击次数 × (1 + 暴击率 × (暴伤 - 1))
    /// </summary>
    public static float EffectiveDPS(float attack, float attacksPerSecond, float critRate, float critDamage)
    {
        float critMultiplier = 1f + critRate * (critDamage - 1f);
        return attack * attacksPerSecond * critMultiplier;
    }
}
