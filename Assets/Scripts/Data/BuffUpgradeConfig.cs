using System.Collections.Generic;

/// <summary>
/// 局内升级Buff类型（3选1池，与主动技能无关）
/// </summary>
public enum UpgradeBuffType
{
    Unknown = 0,      // 占位
    Attack,           // 加攻击
    MaxHp,            // 加最大血量
    Heal,             // 立即回血
    CritChance,       // 加暴击率
    CritDamage,       // 加暴击伤害
    MoveSpeed,        // 加移速
    PhysDamage,       // 物理伤害加成
    ElemDamage,       // 元素伤害加成
    DropMultiplier,   // 拾取掉落物数量倍率
    Dodge,            // 闪避几率
    Effect            // 复杂增益（引用 GameEffectConfig 效果库，本局常驻/条件触发）
}

/// <summary>
/// 加成类型（数值工厂语义化）
/// Flat   - 固定值加成：直接加（如 暴击率 +0.05、物元加成 +0.15）
/// Percent- 百分比加成：作用于基础值（如 攻击 +15%），随角色基础成长
/// </summary>
public enum BuffValueType
{
    Flat = 0,     // 固定值
    Percent = 1   // 百分比
}

/// <summary>
/// 局内升级Buff配置（原 SkillConfig 改造）
/// 升级时3选1池从本配置随机抽取，选择后应用对应加成
/// </summary>
public class BuffUpgradeConfig
{
    public string id;                    // 条目唯一ID
    public string buffName;              // 显示名称，如 "力量强化"
    public string description;           // 描述，如 "攻击力 +10"
    public string iconPath = "";         // 增益图标 Resources 路径（可空，空则隐藏）
    public string rankIconPath = "";     // 阶级图标 Resources 路径（tier 展示用，可空，空则隐藏）
    public UpgradeBuffType buffType;     // Buff类型
    public float value = 0f;             // 数值（攻击+10 / 回血30 / 暴击率0.05 / 倍率0.2等）
    public BuffValueType valueType = BuffValueType.Flat; // 加成类型（默认固定值，兼容旧配置）
    public float weight = 1f;            // 阶级内抽选权重（越高越容易出）
    public int tier = 1;                 // 强化阶级（1/2/3；对应 3选1 文档「阶级」列，越高越稀有越强）

    /// <summary>
    /// 本局最多可获得次数（默认 1 = 唯一卡，抽过即移出池）。
    /// ⚠ 必须 >0，否则该卡永远抽不到；字段缺失时 LitJson 保留默认值 1。
    /// 设此上限是因为升级池原不跨等级去重，百分比加成卡会线性叠加，
    /// 把攻速与后期 DPS 推到 BOSS / 波次数值无法标定的程度。
    /// </summary>
    public int maxStack = 1;

    // 复杂增益：buffType=Effect 时必填，引用 GameEffectConfig 效果库（本局常驻/条件触发）
    public string effectId = "";
}

public class BuffUpgradeConfigWrapper
{
    public int version = 1;
    public List<BuffUpgradeConfig> Buffs = new();

    private Dictionary<string, BuffUpgradeConfig> _dict;

    public BuffUpgradeConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, BuffUpgradeConfig>();
            foreach (var b in Buffs)
                if (!string.IsNullOrEmpty(b?.id) && !_dict.ContainsKey(b.id))
                    _dict.Add(b.id, b);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Buffs?.Count ?? 0;
}
