using YokiFrame;

/// <summary>
/// 增益加成计算器：把「单个增益 / 单个行为」换算为属性提升，无状态纯函数。
/// 升级卡、角色被动、成品菜等所有 GameEffectConfig 效果共用此处公式
/// （最终值 = 基础值 ×(1 + 百分比) + 固定值），避免公式散落在效果引擎的 switch 中；
/// 事件触发、状态管理与副作用仍归效果引擎。
/// </summary>
public static class BuffEffectBonusCalculator
{
    /// <summary>公式求值所需的玩家基础/当前值快照（由调用方从玩家构造）</summary>
    public struct BuffContext
    {
        public PlayerAttribute Base;        // 基础属性（BaseAttr）
        public float CurrentPhysDef;        // 当前物理防御（层数防御公式用）
        public float CurrentElemDef;        // 当前元素防御（层数防御公式用）
    }

    /// <summary>创建默认上下文（无基础/当前值时）</summary>
    public static BuffContext EmptyContext()
    {
        return default;
    }

    /// <summary>
    /// 取"直接百分比"数值：这些动作的数值是固定比例，不随玩家当前攻击/生命/移速缩放。
    /// baseValue → percentOfAttack → percentOfMaxHp → percentOfMoveSpeed，取首个非零。
    /// </summary>
    public static float PercentValue(EffectActionData a)
    {
        if (a.baseValue != 0f) return a.baseValue;
        if (a.percentOfAttack != 0f) return a.percentOfAttack;
        if (a.percentOfMaxHp != 0f) return a.percentOfMaxHp;
        if (a.percentOfMoveSpeed != 0f) return a.percentOfMoveSpeed;
        return 0f;
    }

    /// <summary>
    /// 简单 Modifier 重算增量 = FinalValue(base, percent, flat) - base。
    /// 作为简单数值重算的唯一入口（UpgradeBuffSystem 委托），内部沿用 StatCalculator。
    /// </summary>
    public static float AttributeGainDelta(float baseValue, float percent, float flat)
    {
        return StatCalculator.BonusDelta(baseValue, percent, flat);
    }

    /// <summary>
    /// 单个属性类行为的纯属性增量（被动/常驻/瞬时应用）。
    /// 唯一特殊公式：AttackOfMoveSpeed（chef 被动：攻击 = 基础攻击 ×基础移速 ×百分比），
    /// 其余属性类行为把已求得的原始值 rawValue 按通道映射写入对应属性。
    /// </summary>
    public static PlayerAttribute AttributeDelta(EffectActionData a, BuffContext ctx, float rawValue)
    {
        var type = (EffectActionType)a.action;
        if (type == EffectActionType.AttackOfMoveSpeed)
            return new PlayerAttribute { Attack = ctx.Base.Attack * ctx.Base.MoveSpeed * PercentValue(a) };
        return MapToAttribute(type, rawValue);
    }

    /// <summary>
    /// 层数叠加"每新增一层"的属性贡献（不处理攻速/CDR 侧通道与副作用）。
    /// stackToMoveSpeed=基础移速×系数；stackToMaxHp=基础最大生命×系数；stackToDefend=当前防御×系数。
    /// </summary>
    public static PlayerAttribute OneStackDelta(EffectActionData a, BuffContext ctx)
    {
        var attr = new PlayerAttribute();
        if (a.stackToMoveSpeed > 0f) attr.MoveSpeed = ctx.Base.MoveSpeed * a.stackToMoveSpeed;
        if (a.stackToMaxHp > 0f) attr.MaxHp = ctx.Base.MaxHp * a.stackToMaxHp;
        if (a.stackToDefend > 0f)
        {
            attr.PhysDef = ctx.CurrentPhysDef * a.stackToDefend;
            attr.ElemDef = ctx.CurrentElemDef * a.stackToDefend;
        }
        return attr;
    }

    /// <summary>
    /// 层数损失"删除若干层"的属性回退（正值表示要扣减的量；副作用由调用方按通道执行）。
    /// </summary>
    public static PlayerAttribute LossStackDelta(EffectActionData a, BuffContext ctx, int layerTotal)
    {
        var attr = new PlayerAttribute();
        if (a.stackToMoveSpeed > 0f) attr.MoveSpeed = ctx.Base.MoveSpeed * a.stackToMoveSpeed * layerTotal;
        if (a.stackToMaxHp > 0f) attr.MaxHp = ctx.Base.MaxHp * a.stackToMaxHp * layerTotal;
        if (a.stackToDefend > 0f)
        {
            attr.PhysDef = ctx.CurrentPhysDef * a.stackToDefend * layerTotal;
            attr.ElemDef = ctx.CurrentElemDef * a.stackToDefend * layerTotal;
        }
        return attr;
    }

    /// <summary>
    /// 行为 → 玩家属性通道映射（属性类行为的"写入哪个字段"）。
    /// 供：属性类被动应用 / 断点存档剔除 / 临时 Mod 还原 共用的通道定义，单一来源。
    /// </summary>
    public static PlayerAttribute MapToAttribute(EffectActionType type, float value)
    {
        var attr = new PlayerAttribute();
        switch (type)
        {
            case EffectActionType.DamageBonus:
            case EffectActionType.AttackOfMaxHp:
            case EffectActionType.AttackOfCurrentAttack:
            case EffectActionType.AttackOfMoveSpeed: attr.Attack = value; break;
            case EffectActionType.MoveSpeedBonus: attr.MoveSpeed = value; break;
            case EffectActionType.CritChanceBonus: attr.CritChance = value; break;
            case EffectActionType.CritDamageBonus: attr.CritDamage = value; break;
            case EffectActionType.PhysDamageBonus: attr.PhysDamageBonus = value; break;
            case EffectActionType.ElemDamageBonus: attr.ElemDamageBonus = value; break;
            default:
                LogKit.Warning($"[效果] 行为 {type} 不是玩家属性类，属性通道已忽略");
                break;
        }
        return attr;
    }
}