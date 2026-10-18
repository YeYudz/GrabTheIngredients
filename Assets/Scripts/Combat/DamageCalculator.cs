using UnityEngine;

/// <summary>
/// 伤害计算器：收敛子弹 / 近战 / 主动技能 / 敌人攻击共用的伤害公式。
/// 玩家对敌为「攻击 × 武器倍率 ×(1 + 属性加成)」并判暴击；
/// 敌人对玩家先判闪避、再减对应防御（保底 1）。
/// </summary>
public static class DamageCalculator
{
    /// <summary>玩家造成伤害（含暴击判定，不扣目标防御）</summary>
    public static float PlayerAttackDamage(float attack, WeaponConfig weapon, DamageType damageType,
        float critChance, float critDamage, float physDamageBonus, float elemDamageBonus)
    {
        return PlayerAttackDamage(attack, weapon, damageType, critChance, critDamage,
            physDamageBonus, elemDamageBonus, out _);
    }

    /// <summary>玩家造成伤害（out isCrit 输出是否暴击，供表现层飘字使用）</summary>
    public static float PlayerAttackDamage(float attack, WeaponConfig weapon, DamageType damageType,
        float critChance, float critDamage, float physDamageBonus, float elemDamageBonus, out bool isCrit)
    {
        isCrit = false;
        if (weapon == null) return Mathf.Max(1f, attack);

        float bonus = damageType == DamageType.Physical ? physDamageBonus : elemDamageBonus;
        float dmg = attack * weapon.damageMultiplier * (1f + bonus);
        if (dmg <= 0f) return 1f;

        // 暴击判定
        if (Random.value < critChance)
        {
            dmg *= critDamage;
            isCrit = true;
        }
        return dmg;
    }

    /// <summary>玩家造成伤害（无武器，直接用攻击力×倍率，供主动技能等使用）</summary>
    public static float PlayerRawDamage(float attack, float multiplier, DamageType damageType,
        float critChance, float critDamage, float physDamageBonus, float elemDamageBonus)
    {
        return PlayerRawDamage(attack, multiplier, damageType, critChance, critDamage,
            physDamageBonus, elemDamageBonus, out _);
    }

    /// <summary>玩家造成伤害（out isCrit 输出是否暴击）</summary>
    public static float PlayerRawDamage(float attack, float multiplier, DamageType damageType,
        float critChance, float critDamage, float physDamageBonus, float elemDamageBonus, out bool isCrit)
    {
        isCrit = false;
        float bonus = damageType == DamageType.Physical ? physDamageBonus : elemDamageBonus;
        float dmg = attack * multiplier * (1f + bonus);
        if (dmg <= 0f) return 1f;
        if (Random.value < critChance)
        {
            dmg *= critDamage;
            isCrit = true;
        }
        return dmg;
    }

    /// <summary>敌人对玩家伤害（闪避判定 + 扣对应防御，保底1）</summary>
    public static float EnemyAttackDamage(float enemyAttack, DamageType damageType,
        float dodgeChance, float physDef, float elemDef)
    {
        // 闪避判定
        if (Random.value < dodgeChance) return 0f;

        float def = damageType == DamageType.Physical ? physDef : elemDef;
        return Mathf.Max(1f, enemyAttack - def);
    }
}
