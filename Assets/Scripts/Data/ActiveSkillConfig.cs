using System.Collections.Generic;

/// <summary>
/// 主动技能效果类型
/// </summary>
public enum ActiveSkillEffectType
{
    None = 0,          // 占位
    DamageAoe,         // AOE伤害（以玩家为中心或指定位置）
    Heal,              // 回血
    Buff,              // 增益（临时属性加成）
    Projectile,        // 弹幕（发射多枚子弹）
    TimedOnHitTrue,    // 限时命中附加真伤（持续 duration 秒：每次命中 + onHitFlat + onHitPct×攻 真伤，附带攻速 attackSpeedBonus）
    LimitedOnHit,      // 限次数命中附加（接下来 hits 次攻击额外造成 onHitPct×攻 伤害）
    ConditionalOnHit,  // 限时条件命中增伤（持续 duration：对低/高/自身低血敌人额外 onHitPct×攻 伤害）
    DefenseBuff,       // 限时防御提升（持续 duration 秒，防御 +defensePct%）
    HealPercent,       // 回复最大生命 healPctMaxHp%
    AttackSpeedBuff,   // 限时攻速提升（持续 duration 秒，攻速 +attackSpeedBonus）
    AttackRangeBuff,   // 限时攻击距离提升（持续 duration 秒，距离 +rangeBonus%）
    ForwardProjectile, // 朝移动方向发射单发法球（value×攻 伤害）
    DashStrike,        // 突进打击（对最近目标造成 value×攻；命中返还 refundPct% 冷却）
    AoeSlow,           // 范围伤害+减速（范围 radius，value×攻 伤害；敌人减速 slowPct，持续 duration 秒）
    TimedKillHeal,     // 限时击杀回血（持续 duration 秒：击杀回复 healPctMaxHp% 最大生命）
    TimedMana          // 限时回蓝（持续 duration 秒：命中回复 hitManaPct% 最大法力，击杀额外 killManaPct%）
}

/// <summary>
/// 主动技能配置（战前准备-选择技能组）
/// 战前选定的技能组为本局主动技能，局内通过 HUD SkillRoot 释放（冷却）
/// </summary>
public class ActiveSkillConfig
{
    public string id;                  // 技能唯一ID
    public string skillName;           // 技能名
    public string description;         // 描述
    public string iconPath;            // 图标 Resources 路径

    public ActiveSkillEffectType effectType;  // 效果类型
    public float cooldown = 5f;               // 冷却时间（秒）
    public float value = 0f;                  // 效果数值（伤害/回复量）
    public float radius = 3f;                 // AOE半径 / 弹幕范围
    public float duration = 0f;               // 持续时间（Buff用，0=瞬时）
    public string buffType;                  // Buff时加成的属性名（如 "Attack"/"MoveSpeed"）
    public float buffValue = 0f;             // Buff数值（绝对值或小数，按属性语义）

    // ===== 进阶技能参数（TIPS 技能体系） =====
    public int manaCost = -1;                // 法力消耗（-1=不消耗；>0 按此消耗）
    public float onHitFlat;                  // TimedOnHitTrue：每次命中附加固定真伤
    public float onHitPct;                   // 命中外加攻击%（真伤 / 条件 / 次数附加）
    public int hits;                         // LimitedOnHit：影响攻击次数
    public int condType;                     // ConditionalOnHit：1=目标低血 2=目标高血 3=自身低血
    public float defensePct;                 // DefenseBuff：防御提升%
    public float healPctMaxHp;               // HealPercent / TimedKillHeal：回血%最大生命
    public float attackSpeedBonus;           // AttackSpeedBuff / TimedOnHitTrue：攻速提升
    public float rangeBonus;                 // AttackRangeBuff：攻击距离提升%
    public float refundPct;                  // DashStrike：命中返还冷却%
    public float slowPct;                    // AoeSlow：敌人减速%
    public float hitManaPct;                 // TimedMana：命中回蓝%最大法力
    public float killManaPct;                // TimedMana：击杀回蓝%最大法力

    // ===== 商店 =====
    public int buyPrice;                      // 商店购买解锁价格（未解锁时可购买）
    public int sellPrice;                     // 商店出售价格（技能暂不可出售，预留）
}

public class ActiveSkillConfigWrapper
{
    public int version = 1;
    public List<ActiveSkillConfig> Skills = new();

    private Dictionary<string, ActiveSkillConfig> _dict;

    public ActiveSkillConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, ActiveSkillConfig>();
            foreach (var s in Skills)
                if (!string.IsNullOrEmpty(s?.id) && !_dict.ContainsKey(s.id))
                    _dict.Add(s.id, s);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Skills?.Count ?? 0;
}
