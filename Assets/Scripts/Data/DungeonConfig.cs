using System.Collections.Generic;

/// <summary>
/// 副本事件槽类型：目前仅战场炒菜有消费者，其余为玩法预留；
/// 未接入消费者的槽只记日志、不派发，避免留下无声的空事件。
/// </summary>
public enum DungeonEventKind
{
    BattlefieldCooking = 0,   // 战场炒菜
    ResourceChest = 1,        // 资源箱（预留）
    BuffDrop = 2,             // 随机Buff掉落（预留）
    EnemyStrengthen = 3       // 敌人强化（预留）
}

/// <summary>
/// 战场炒菜触发来源
/// </summary>
public enum DungeonCookingTriggerMode
{
    KillThreshold = 0,   // 击杀数阈值（现有行为，默认）
    EventSlot = 1        // 仅由副本事件槽掷骰触发
}

/// <summary>
/// 副本事件槽：在指定波次区间内按概率触发一次随机事件
/// </summary>
public class DungeonEventSlot
{
    public string eventId;                                  // 事件唯一ID（用于冷却/一次性记录）
    public DungeonEventKind kind = DungeonEventKind.BattlefieldCooking; // 事件类型
    public float probability = 0f;                          // 触发概率 0~1
    public int minWave = 1;                                 // 生效波次区间下限（本关累计波次序号，跨阶段连续，1起）
    public int maxWave = 999;                               // 生效波次区间上限
    public bool oncePerDungeon = false;                     // 每局只触发一次
    public int cooldownWaves = 0;                           // 触发后冷却波次数（0=无冷却）
}

/// <summary>
/// 副本内的"阶段"：若干小怪波次 + 末尾 1 个 BOSS 波
/// waveIds 按序给出本阶段全部波次，**最后一项即 BOSS 波**（其 WaveConfig 带 triggersBoss/bossId 与出场小怪）
/// 阶段是"阶段结算"的单位：末尾 BOSS 被击败后出结算页，确认后进入下一阶段
/// </summary>
public class DungeonPhase
{
    public string phaseId;                  // 阶段唯一ID（词缀作用域引用）
    public string phaseName;                // 阶段显示名
    public List<int> waveIds = new();       // 本阶段波次序列（引用 WaveConfig.waveId；最后一项为 BOSS 波）
    public string bossId;                   // 末尾 BOSS（引用 BossConfig.id；留空则取 BOSS 波自带 bossId）
    public float bossHpScale = 1f;          // 本阶段 BOSS 血量倍率（可选覆写）
}

/// <summary>
/// 副本配置：整局的"剧本"——阶段序列（每阶段 = N 波次 + 末尾 BOSS）
/// 兼容：phases 为空时退回旧字段 waveIds（波次系统按全局波次表推进）
/// </summary>
public class DungeonConfig
{
    public string dungeonId;                                // 副本唯一ID
    public string dungeonName;                              // 副本名称
    public string iconPath = "";                            // 副本图标 Resources 路径（可空，空则隐藏）
    public string BackGround = "";                          // 副本战场背景 Resources 路径（如 Art/Scene/BG_Battle1；空则沿用 Arena 上的默认背景）
    public string info = "";                                // 副本信息（选关详情展示）
    public string enemyOverview = "";                       // 敌人概况（选关详情展示）
    public string tip = "";                                 // 贴心小提示（选关详情展示）
    public int order = 1;                                   // 副本序号（难度递增参考）
    public bool isUnlocked = false;                         // 初始是否解锁（通常只有第一关为 true）

    // ===== 阶段序列（推荐配置方式） =====
    public List<DungeonPhase> phases = new();               // 阶段序列

    // ===== 副本难度倍率（作用于本关全部敌人，实现关卡间难度递增） =====
    public float enemyHpScale = 1f;                         // 敌人血量倍率
    public float enemyDamageScale = 1f;                     // 敌人伤害倍率
    public float enemySpeedScale = 1f;                      // 敌人移速倍率
    public float enemyExpScale = 1f;                        // 敌人经验倍率
    public float dropRateScale = 1f;                        // 敌人掉落倍率

    public DungeonCookingTriggerMode fieldCookingTrigger = DungeonCookingTriggerMode.KillThreshold;
    public string rhythmDifficultyId = "normal";            // 该关卡的音游难度档（按关卡递进；对应 RhythmConfig.difficulties）
    public List<DungeonEventSlot> eventSlots = new();       // 事件槽（概率触发）

    // ===== 旧字段（phases 为空时使用，保持向后兼容） =====
    public List<int> waveIds = new();                       // 波次序列（引用 WaveConfig.waveId）
}

/// <summary>副本阶段判断辅助</summary>
public static class DungeonPhaseExtensions
{
    /// <summary>是否已配置可用的阶段序列（有阶段且至少一个阶段含波次）</summary>
    public static bool HasPhases(this DungeonConfig dungeon)
    {
        if (dungeon?.phases == null || dungeon.phases.Count == 0) return false;
        foreach (var p in dungeon.phases)
            if (p != null && p.waveIds != null && p.waveIds.Count > 0) return true;
        return false;
    }
}

public class DungeonConfigWrapper
{
    public int version = 1;
    public string defaultDungeonId;                         // 默认副本（选关系统未接入前使用）
    public List<DungeonConfig> Dungeons = new();

    private Dictionary<string, DungeonConfig> _dict;

    public DungeonConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, DungeonConfig>();
            foreach (var s in Dungeons)
                if (!string.IsNullOrEmpty(s?.dungeonId) && !_dict.ContainsKey(s.dungeonId))
                    _dict.Add(s.dungeonId, s);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    /// <summary>取第一个副本（defaultDungeonId 未配置或失效时的兜底）</summary>
    public DungeonConfig GetFirst()
        => (Dungeons != null && Dungeons.Count > 0) ? Dungeons[0] : null;

    public int Count => Dungeons?.Count ?? 0;
}
