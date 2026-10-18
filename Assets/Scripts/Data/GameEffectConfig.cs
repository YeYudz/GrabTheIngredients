using System.Collections.Generic;

/// <summary>
/// 效果触发器（阶段1：None/Passive/LowHp；阶段2：事件类）
/// </summary>
public enum EffectTriggerType
{
    None = 0,        // 立即生效（应用瞬间执行一次）
    Passive = 1,     // 常驻被动（直接应用，持续整局）
    LowHp = 2,       // 生命低于阈值（triggerParam 0~1）时触发一次（按冷却）
    OnHit = 3,       // 攻击命中敌人时
    OnSkill = 4,     // 释放技能时
    OnKill = 5,      // 击杀敌人时
    OnDamaged = 6,   // 受到伤害时
    OnCrit = 7,      // 暴击时
    OnCooking = 8,   // 完成一次烹饪时
    OnWaveStart = 9, // 波次开始时
    OnDodge = 10,    // 施放闪避（翻滚）时
    Periodic = 11,   // 每 triggerParam 秒自动触发一次（如回力镖/定时护盾）
    OnBuffGain = 12  // 每次获得本条升级增益时（动态快照类，如"获得X%当前攻击"）
}

/// <summary>
/// 效果行为（属性/护盾/回血/事件/飞弹/层数 等）
/// action 0-20 为阶段1/既有能力；21+ 为 3选1 升级增益引入的新能力
/// </summary>
public enum EffectActionType
{
    DamageBonus = 0,            // 伤害加成（直接加攻击）
    AttackSpeedBonus = 1,       // 攻速加成
    MoveSpeedBonus = 2,         // 移速加成
    MaxHpBonus = 3,             // 最大生命（percentOfMaxHp 为相对值）
    Shield = 4,                 // 护盾（吸收伤害，支持衰减）
    Heal = 5,                   // 回复生命
    LifeSteal = 6,              // 吸血（阶段2）
    CritChanceBonus = 7,        // 暴击率加成
    CritDamageBonus = 8,        // 暴击伤害加成
    PhysDamageBonus = 9,        // 物理伤害加成
    ElemDamageBonus = 10,       // 元素伤害加成
    FireProjectile = 11,        // 发射飞弹
    Burn = 12,                  // 燃烧 DOT
    Slow = 13,                  // 减速敌人
    Stun = 14,                  // 眩晕
    AoeDamage = 15,             // 范围伤害
    ExpBonus = 16,              // 经验加成
    DamageReflect = 17,         // 反伤
    ScaleSize = 18,             // 缩放
    GoldBonus = 19,             // 金币加成
    IngredientBonus = 20,       // 食材掉落加成

    AttackOfMaxHp = 21,         // 攻击 = 最大生命 ×percentOfMaxHp(加法)      (buff10)
    AttackOfCurrentAttack = 22, // 攻击 += 当前攻击 ×percentOfAttack(快照)     (buff11/buff110/buff202/buff205 也走此通道)
    CooldownReduction = 23,     // 技能冷却缩减 percentOfAttack(0.2=20%)       (buff109/buff203)
    DefensePenetration = 24,    // 攻击无视目标防御 percentOfAttack(0.2=20%)   (buff14/buff209)
    HealShieldStrength = 25,    // 治疗/护盾强度 amplification percentOfAttack (buff214)
    SkillDamageMultiplier = 26, // 技能伤害乘区 multiplicative percentOfAttack(buff107/buff200)
    ManaCostMultiplier = 27,    // 法力消耗乘区 percentOfAttack(2=翻倍)        (buff107)
    ManaRestore = 28,           // 回复法力 baseValue                         (buff4)
    Execute = 29,               // 处决：低于阈值直接击杀并回血                 (buff208)
    Immunity = 30,              // 免疫伤害 duration 秒                        (buff210)
    BlockNextAttack = 31,       // 格挡下一次敌方攻击（周期性层数）             (buff6)
    KillBuff = 32,              // 击杀获得限时攻速/移速                       (buff113)
    ExplodeOnDodge = 33,        // 闪避引发爆炸 percentOfAttack               (buff13)
    OnCritProjectile = 34,      // 暴击时发射飞弹                             (buff102/buff111)
    PeriodicProjectile = 35,    // 每 triggerParam 秒发射飞弹（回力镖）         (buff103)
    DamageOnHitMaxHp = 36,      // 攻击额外造成"最大生命×percentOfMaxHp"伤害    (buff8)
    EnableFinalDamage = 37,     // 最终伤害乘区 multiplicative percentOfAttack (buff200/buff110)
    DisableActiveSkills = 38,   // 禁用所有主动技能                             (buff200)
    CooldownReduceOnHit = 39,   // 普攻命中降低技能冷却 baseValue 秒              (buff201/buff213)
    OnHitBonusByMoveSpeed = 40, // 攻击额外造成"移速×percentOfMoveSpeed"伤害      (buff3)
    PeriodicExplosionAttack = 41, // 闪避引发爆炸(percentOfAttack AOE)            (buff13)
    DodgeCooldownReduction = 42,   // 闪避冷却缩减 baseValue/perfentOfAttack(0.5=50%) (buff200/buff207)
    AttackOfMoveSpeed = 43,        // 攻击 = 基础攻击 ×移速 ×百分比 (chef被动：每1移速→2%攻击；baseValue=0.02)
    OnHitLowHpBonus = 44           // 命中低血目标(<50%)额外 percentOfAttack×攻 伤害 (pepper被动)；baseValue=每目标冷却秒
}

/// <summary>
/// 单个行为数值（公式：baseValue / min~max随机 + percentOfAttack/MaxHp/MoveSpeed 百分比换算）
/// 新增：层数（stack）相关字段 与 过程性触发所需的参数
/// </summary>
public class EffectActionData
{
    public int action;               // EffectActionType
    public float baseValue;          // 基础数值
    public float minValue;           // 随机下限（-1 表示不用）
    public float maxValue;           // 随机上限
    public float percentOfAttack;    // 额外攻击力百分比（0.07 = +7%攻击）或专用比例
    public float percentOfMaxHp;     // 额外最大生命百分比（0.65 = +65%生命）
    public float percentOfMoveSpeed; // 额外移速百分比（1.5 = +150%移速）
    public float duration;           // 持续时间（秒，0=即时/常驻）
    public bool decay;               // 是否线性衰减到0
    public float radius;             // 范围（阶段2）
    public int projectileCount;      // 飞弹数量（阶段2）
    public float projectilePerPercent; // 每X%追加飞弹（阶段2）

    // ===== 层数（stack）系统 =====
    public int stackMax;             // 层数上限（0=不叠加，仅一次性）
    public int stackGainPerTrigger;  // 每次触发获得层数（默认1）
    public float stackToMaxHp;       // 每层提供的最大生命%（层数 × 此值，加法）
    public float stackToDefend;      // 每层提供的防御%（层数 × 此值，加法）
    public float stackToMoveSpeed;   // 每层提供的移速%（层数 × 此值，加法）
    public float stackToAttackSpeed; // 每层提供的攻速%（层数 × 此值，加法）
    public float stackToCooldownReduction; // 每层提供CDR%
    public int stackTriggerAt;       // 层数达到该值时触发一次（0=不触发）
    public float stackDamageConvertToHeal; // 达到 stackTriggerAt 后，把伤害×此值转为治疗（buff212）
    public string stackTriggerEvent = ""; // 叠层的事件源：OnHit/OnCrit/OnKill/OnDamaged
    public float stackLossPercent;   // 每次 OnDamaged 时损失的层数比例（0.65=损失65%）
    public float stackRefreshDuration; // 层数刷新持续时间（秒，0=永久；用于按持续时间到期移除的层）
    public float stackProjectile;    // 层数满(=stackTriggerAt)时发射的弩箭基础伤害（0=不发射）
    public float stackToDefendExtra; // 每层提供的额外伤害%（层数 × 此值，加法）
    public string damageType = "";   // 伤害类型（阶段2）
    public float healOnExecutePercent; // 处决成功时回复最大生命百分比（buff208）
}

/// <summary>
/// 复杂增益定义（数据驱动：JSON 效果库，战场菜与成品菜共用引用）
/// </summary>
public class GameEffectConfig
{
    public string effectId;          // 唯一ID
    public string effectName;        // 显示名称
    public string description;       // 描述（角色被动等 UI 展示用）
    public int trigger;              // EffectTriggerType
    public float triggerParam;       // 触发参数（LowHp 阈值 0~1）
    public float cooldown;           // 触发冷却（秒，0=无）
    public List<EffectActionData> actions = new();
}

public class GameEffectConfigWrapper
{
    public int version = 1;
    public List<GameEffectConfig> Effects = new();

    private Dictionary<string, GameEffectConfig> _dict;

    public GameEffectConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, GameEffectConfig>();
            foreach (var e in Effects)
                if (e != null && !string.IsNullOrEmpty(e.effectId) && !_dict.ContainsKey(e.effectId))
                    _dict.Add(e.effectId, e);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Effects?.Count ?? 0;
}
