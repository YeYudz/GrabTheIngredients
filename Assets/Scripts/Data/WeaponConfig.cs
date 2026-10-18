using System.Collections.Generic;

/// <summary>
/// 武器攻击方式
/// </summary>
public enum WeaponAttackMode
{
    Melee,   // 近战（挥砍/范围瞬时伤害）
    Ranged   // 远程（发射子弹）
}

/// <summary>
/// 伤害类型（新属性系统：物理/元素，对应物防/元防）
/// </summary>
public enum DamageType
{
    Physical,  // 物理伤害
    Element    // 元素伤害
}

/// <summary>
/// 攻击属性类型（BOSS 弱点克制机制：蔬菜/肉类/香料）
/// 由 WeaponConfig.attackType 引用，用于 BOSS 弱点匹配
/// </summary>
public enum AttackType
{
    Vegetable,   // 蔬菜类 → 绿色弱点
    Meat,        // 肉类   → 红色弱点
    Spice,       // 香料类 → 黄色弱点
    Universal    // 通用
}

/// <summary>
/// 武器配置（战前准备-装备武器）
/// 武器取代攻击物：攻击方式(近战/远程)与伤害类型(物理/元素)由武器决定
/// attackType 沿用旧属性枚举，用于 BOSS 弱点匹配
/// </summary>
public class WeaponConfig
{
    public string id;                       // 武器唯一ID
    public string weaponName;               // 武器名
    public string description;              // 描述
    public string iconPath;                 // 图标 Resources 路径

    // ===== 攻击方式 =====
    public WeaponAttackMode attackMode;     // Melee/Ranged
    public DamageType damageType;           // Physical/Element（伤害公式用）
    public AttackType attackType;           // BOSS弱点匹配（Vegetable/Meat/Spice/Universal）

    // ===== 武器基础属性（战前并入玩家 BaseAttr，供百分比加成作为基数；不含法力两类） =====
    public PlayerAttribute baseAttrs;

    // ===== 武器主动技能（每武器独立主动，由 WeaponActiveSystem 管理；空=无主动技能） =====
    public string activeSkillId;

    // ===== 攻击参数 =====
    public float damageMultiplier = 1f;     // 伤害倍率（×角色攻击力，单次命中）
    public float attackSpeedScale = 1f;     // 攻速倍率（近战=刃身转速倍率，远程=射速倍率）；与角色攻速相乘，1=跟随角色攻速
                                            // ⚠️ 近战是轨道模型「每圈每目标至多一击」→ 转速=命中频率 → 该值会同比放大近战 DPS，
                                            //    提速时必须同步下调 damageMultiplier（÷该值）才能保持 DPS 不变
    public float attackRange = 8f;          // 远程索敌范围（多远的敌人会被自动攻击；近战不用，近战范围由环带决定）
    public float projectileRange = 8f;      // 远程子弹最大飞行距离
    public float projectileSpeed = 10f;     // 远程子弹速度
    public int projectileCount = 1;         // 每次发射子弹数
    public string projectilePrefabPath;     // 远程子弹预制体 Resources 路径
    public float meleeBladeLength = 3f;     // 近战刃长（攻击环带 = [meleeOrbitRadius, meleeOrbitRadius + 刃长]；范围成长=增加刃长，轴枢不动）
    public string meleePrefabPath;          // 近战武器视觉预制体 Resources 路径（pivot 在柄部、刃朝 +Y，美术按 1 世界单位长度绘制，代码按刃长拉伸）
    public float meleeBladeWidth = 0f;      // 近战刃身全宽（世界单位，命中盒宽，取图片最宽处；0=自动从 sprite 网格顶点测量）

    // ===== 法力（武器提供部分：装备时累加到初始法力/回复） =====
    public float maxMana = 0f;           // 武器提供的最大法力值
    public float manaRegen = 0f;         // 武器提供的每秒法力回复

    // ===== 商店 =====
    public int buyPrice;                    // 商店购买价格（未拥有时可购买）
    public int sellPrice;                   // 商店出售价格
}

public class WeaponConfigWrapper
{
    public int version = 1;
    public List<WeaponConfig> Weapons = new();

    private Dictionary<string, WeaponConfig> _dict;

    public WeaponConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, WeaponConfig>();
            foreach (var w in Weapons)
                if (!string.IsNullOrEmpty(w?.id) && !_dict.ContainsKey(w.id))
                    _dict.Add(w.id, w);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Weapons?.Count ?? 0;
}
