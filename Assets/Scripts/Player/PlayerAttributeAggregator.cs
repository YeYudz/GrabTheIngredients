using System.Collections.Generic;

/// <summary>
/// 属性聚合器：把角色基础属性与战前装配的武器基础属性汇总为玩家「基础属性」。
/// 成品菜由 starEffects 效果引擎承载、局内 Buff 走 PlayerController.BonusAttr，
/// 两者都不参与本聚合，避免同一加成被重复计算。
/// </summary>
public static class PlayerAttributeAggregator
{
    /// <summary>从角色基础属性构建（菜品为空时的兜底）</summary>
    public static PlayerAttribute BuildFromCharacter(CharacterConfig character)
    {
        var attr = new PlayerAttribute
        {
            MaxHp = character?.maxHp ?? 100f,
            Attack = character?.attack ?? 10f,
            PhysDef = character?.physDef ?? 0f,
            ElemDef = character?.elemDef ?? 0f,
            MoveSpeed = character?.moveSpeed ?? 5f,
            AttackSpeed = character?.attackSpeed ?? 1f,
            DodgeChance = character?.dodgeChance ?? 0f,
            CritChance = character?.critChance ?? 0f,
            CritDamage = character?.critDamage ?? 1.5f,
            PhysDamageBonus = character?.physDamageBonus ?? 0f,
            ElemDamageBonus = character?.elemDamageBonus ?? 0f,
            MaxMana = character?.maxMana ?? 0f,
            ManaRegen = character?.manaRegen ?? 0f
        };
        return attr;
    }

    /// <summary>计算最终基础属性 = 角色基础 + 装配武器基础 + 局外永久升级加成（成品菜属性改由 starEffects 效果引擎承载）</summary>
    public static PlayerAttribute BuildBase(CharacterConfig character, List<string> weaponIds = null)
    {
        PlayerAttribute attr = BuildFromCharacter(character);
        AddWeaponBase(ref attr, weaponIds);
        AddMetaUpgradeBase(ref attr);
        return attr;
    }

    /// <summary>叠加装配武器的基础属性（武器 baseAttrs，不含法力两类；法力由 InitMana 单独叠加武器值）</summary>
    private static void AddWeaponBase(ref PlayerAttribute attr, List<string> weaponIds)
    {
        if (weaponIds == null) return;
        foreach (var wid in weaponIds)
        {
            if (string.IsNullOrEmpty(wid)) continue;
            var w = GameDataMgr.Instance.GetWeapon(wid);
            if (w == null) continue;
            attr += w.baseAttrs;   // PlayerAttribute.operator+ 累加；法力两类 baseAttrs 不含，保持角色值
        }
    }

    /// <summary>叠加局外永久升级加成（MetaManager 未就绪时跳过，保证任何调用时机安全）</summary>
    private static void AddMetaUpgradeBase(ref PlayerAttribute attr)
    {
        if (!MetaManager.HasInstance || !MetaManager.Instance.IsLoaded) return;
        attr += MetaManager.Instance.BuildMetaUpgradeAttribute();
    }
}
