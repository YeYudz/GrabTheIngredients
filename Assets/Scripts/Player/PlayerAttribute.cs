using System;
using UnityEngine;

/// <summary>
/// 玩家战斗属性集合：基础部分（角色 + 战前装配）与局内 Bonus 经 + 运算符叠加。
/// 用结构体实现，使高频属性聚合不产生堆分配。
/// </summary>
[Serializable]
public struct PlayerAttribute
{
    public float MaxHp;           // 血量
    public float Attack;          // 攻击力
    public float PhysDef;         // 物理防御
    public float ElemDef;         // 元素防御
    public float MoveSpeed;       // 移动速度
    public float AttackSpeed;     // 攻击速度（每秒攻击次数）
    public float DodgeChance;     // 闪避几率（0~1）
    public float CritChance;      // 暴击率（0~1）
    public float CritDamage;      // 暴击伤害倍率（1.5 = 150%）
    public float PhysDamageBonus; // 物理伤害加成（0.1 = +10%）
    public float ElemDamageBonus; // 元素伤害加成（0.1 = +10%）
    public float MaxMana;         // 最大法力（技能资源，角色基础属性）
    public float ManaRegen;       // 每秒法力回复

    public static PlayerAttribute operator +(PlayerAttribute a, PlayerAttribute b)
    {
        a.MaxHp += b.MaxHp;
        a.Attack += b.Attack;
        a.PhysDef += b.PhysDef;
        a.ElemDef += b.ElemDef;
        a.MoveSpeed += b.MoveSpeed;
        a.AttackSpeed += b.AttackSpeed;
        a.DodgeChance += b.DodgeChance;
        a.CritChance += b.CritChance;
        a.CritDamage += b.CritDamage;
        a.PhysDamageBonus += b.PhysDamageBonus;
        a.ElemDamageBonus += b.ElemDamageBonus;
        a.MaxMana += b.MaxMana;
        a.ManaRegen += b.ManaRegen;
        return a;
    }

    public static PlayerAttribute operator -(PlayerAttribute a, PlayerAttribute b)
    {
        a.MaxHp -= b.MaxHp;
        a.Attack -= b.Attack;
        a.PhysDef -= b.PhysDef;
        a.ElemDef -= b.ElemDef;
        a.MoveSpeed -= b.MoveSpeed;
        a.AttackSpeed -= b.AttackSpeed;
        a.DodgeChance -= b.DodgeChance;
        a.CritChance -= b.CritChance;
        a.CritDamage -= b.CritDamage;
        a.PhysDamageBonus -= b.PhysDamageBonus;
        a.ElemDamageBonus -= b.ElemDamageBonus;
        a.MaxMana -= b.MaxMana;
        a.ManaRegen -= b.ManaRegen;
        return a;
    }
}

/// <summary>
/// 战前装配的成品菜（菜ID + 星级 + 具体实例ID）
/// InstanceId 用于区分同菜同星的不同实例（每个背包物品独立实例，装配精确到单个实例）
/// 旧档/未指定时为空字符串（兼容：按 菜+星级 匹配）
/// 由 DishInventorySystem 的装配列表提供，战前准备页可增删
/// </summary>
[Serializable]
public struct EquippedDish
{
    public string RecipeId;   // 菜谱ID
    public int Star;          // 星级（1~5）
    public string InstanceId; // 具体实例ID（区分同菜同星的不同实例；空=未指定/旧档）
}
