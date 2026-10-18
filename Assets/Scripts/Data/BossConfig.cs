using System.Collections.Generic;

/// <summary>
/// BOSS 弱点阶段配置（对应文档"弱点颜色编码机制"）
/// </summary>
public class BossPhase
{
    public int phaseIndex;               // 阶段序号（0 = 第一阶段）
    public float hpThreshold;            // 进入此阶段的血量比例阈值（0~1）
    public AttackType weaknessType;       // 该阶段的弱点类型
    public float weaknessDamageMultiplier = 2f;  // 弱点克制伤害倍率
    public float breakBarOnHit = 10f;     // 弱点命中对破韧条的累积量
}

/// <summary>
/// BOSS 配置数据
/// 对应文档"BOSS战设计：弱点匹配+节拍伤害机制"
/// </summary>
public class BossConfig
{
    public string id;
    public string bossName;
    public float maxHp;
    public float moveSpeed;

    // BOSS 攻击（独立于 maxHp 配置：避免血量调大时按比例计算的接触伤害失控）
    public float attack = 20f;                              // BOSS 攻击力（接触/普通攻击）
    public DamageType damageType = DamageType.Physical;     // BOSS 伤害类型（物理/元素，对应减防/闪避）

    // BOSS 弱点阶段列表（按 phaseIndex 排序）
    public List<BossPhase> phases = new();

    // 破韧条参数
    public float breakBarMax = 100f;      // 破韧条满值
    public float weaknessMultiplier = 3f; // 破韧后虚弱状态伤害倍率（奖励状态）
    public float weaknessDuration = 5f;   // 虚弱持续时间（秒）
    public float nonWeaknessBreakRatio = 0.25f; // 非弱点攻击的破韧效率倍率（效率低但仍可破韧）

    // ===== 血量自适应（按玩家实际输出反推，解决"build 太强把 BOSS 秒掉"） =====
    /// <summary>
    /// 是否在生成时按玩家当前单目标 DPS 反推血量。
    /// 玩家 DPS 在「单武器裸装」与「三武器满增益」之间相差 40 倍以上，任何固定血量都必然一端失衡；
    /// 开启后血量 = clamp(targetFightSeconds × 当前 DPS, 基准 × min~maxHpFactor)，
    /// 使任意 build 的遭遇时长落在同一区间，且带宽刻意放宽以便强力 build 仍能明显更快收场。
    /// 根治手段仍是收敛 DPS 跨度（见方案文档 §7 武器 baseAttrs 重配）。
    /// </summary>
    public bool scaleHpWithPlayerDps = false;

    /// <summary>目标输出秒数（基准血量 = 本值 × 玩家 DPS）。含弱点/虚弱倍率的实际战斗时长 ≈ 本值 ÷ 平均弱点倍率。</summary>
    public float targetFightSeconds = 70f;

    /// <summary>血量下限倍率（相对 maxHp × hpScale）：防止弱 build 遇到打不动的 BOSS</summary>
    public float minHpFactor = 0.25f;

    /// <summary>血量上限倍率（相对 maxHp × hpScale）：防止强 build 把血量推到荒谬量级</summary>
    public float maxHpFactor = 8f;

    // 节拍参数
    public float bpm = 120f;              // BOSS 战 BGM 的 BPM（音游基准速度）
    public float beatWindowMs = 100f;     // 节拍判定窗口（毫秒）

    // 烹饪音游对应（BOSS 类型 → 菜品，对应文档"BOSS 战烹饪音游"）
    public string recipeId;               // 对应菜谱ID

    /// <summary>
    /// 是否为最终 BOSS，仅在副本未配置 phases 的旧模式下生效。
    /// 阶段模式下「是否最终」由阶段序列决定——打完最后一个阶段即通关，本字段不参与判定；
    /// 因为同一 BOSS 会被跨副本、跨阶段复用，若参与判定会把非终阶段误当成通关。
    /// </summary>
    public bool isFinal;

    // 掉落
    public string dropRareIngredientId;   // 稀有食材掉落ID

    // 外观
    public string prefabPath;

    // 图鉴展示（缺省时图鉴按"暂无描述"处理，图标走占位）
    public string iconPath;
    public string description;

    // BOSS 战 BGM 候选列表（BossSpawned 时随机选一首播放；空列表/路径不存在则回退战斗 BGM 曲库）
    public List<string> bgmPaths;
}

public class BossConfigWrapper
{
    public int version = 1;
    public List<BossConfig> Bosses = new();

    private Dictionary<string, BossConfig> _dict;

    public BossConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, BossConfig>();
            foreach (var b in Bosses)
                if (!string.IsNullOrEmpty(b?.id) && !_dict.ContainsKey(b.id))
                    _dict.Add(b.id, b);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Bosses?.Count ?? 0;
}
