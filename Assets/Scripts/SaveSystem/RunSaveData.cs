using System;
using System.Collections.Generic;

/// <summary>
/// 局内对局断点存档（完整断点续玩）：**保存瞬间**的完整战场快照。
/// 与旧版（v1：回到波次开头）不同，v2 连「场上每只怪的位置与血量」「地上每个掉落物」
/// 「刷怪游标」「BOSS 破韧/虚弱/音游进行中」都一并落盘，续玩后原样接着打。
/// </summary>
[Serializable]
public class RunSaveData
{
    // ⚠ 这个类会被 LitJson 用 type.GetFields() 整体反射（public static / const 字段也会被枚举）：
    //   类里**不能放任何 public const / static 成员**——写档会把常量值写进 JSON，
    //   读档时 SetValue 给 const 会抛「Cannot set a constant field」→ 整份档被判坏档丢弃。
    //   版本常量放在 RunSaveManager.SAVE_VERSION 里。

    /// <summary>
    /// 存档结构版本：**故意不给初始值**——旧档 JSON 没有 version 键时保持 0，
    /// 才能被 <see cref="RunSaveManager.IsCompatible"/> 判为旧档丢弃（若给初值 2，旧档会被误认成 v2）。
    /// 落盘前由 RunSaveManager.Save 统一写成 SAVE_VERSION。
    /// </summary>
    public int version;
    public long saveTimestamp;

    // ===== 玩家 =====
    public PlayerAttribute baseAttr;      // 基础属性（角色+装配菜）
    public PlayerAttribute bonusAttr;     // 局内动态加成（已剔除升级Modifier/效果引擎属性/临时Buff，恢复时重放）
    public float playerHp;                // 当前血量
    public float healthMax;               // 保存瞬间 Health 的血量上限（含效果引擎逐层加的上限，恢复时原样写回）
    public int playerLevel = 1;           // 等级
    public int playerExp;                 // 经验
    public float posX, posY;              // 玩家位置
    public float mana = -1f;              // 当前法力（<0 = 未记录）
    public float dodgeCooldown;           // 闪避冷却剩余秒
    public float shield;                  // 护盾剩余值（效果引擎瞬态状态）
    public float immunityRemain;          // 免疫伤害剩余秒
    public int blockCharges;              // 格挡层数

    // ===== 局内烹饪/效果（SkillSystem） =====
    public float cookingDamageBonus;      // 烹饪伤害加成
    // 特殊效果（SkillEffectData 扁平化存储：其含 public static 字段，LitJson 直接序列化会无限递归）
    public int effectType;                // SkillEffectType 整数
    public float effectParam;             // 效果参数
    public float effectDuration;          // 效果持续时间
    public float effectPowerBonus = 1f;   // 化学反应强化倍率

    // ===== 波次运行时 =====
    public string dungeonId;              // 本局所在副本
    public int phaseIndex;                // 当前阶段索引（0 起）
    public int waveIndex = 1;             // 当前波次（当前阶段内的序号，1 起）
    public float runElapsed;              // 本局已用时长（秒，HUD 计时基准）
    public int waveState;                 // WaveState：0 Prep / 1 Spawning / 2 Clearing / 3 Boss
    public float prepRemain;              // Prep 状态剩余倒计时
    public int killCount;                 // 保存瞬间的累计击杀数（不再回退到波首）
    public int waveStartKill;             // 本波开局击杀基准（HUD 波次进度条）
    public int waveTotalEnemies;          // 本波敌人总数
    public int spawnGroupIndex;           // 刷怪游标：当前组索引（组内已全部生成则指向下一组）
    public int spawnIndexInGroup;         // 刷怪游标：当前组内已生成数量
    public bool spawnFinished;            // 本波刷怪是否已全部发完
    public bool wavesStopped;             // 保存时波次循环是否已停止（最终 BOSS 已清 = 本局已结束）

    // ===== 掉落 =====
    public float dropMultiplierBonus = 1f;// 局内拾取倍率（升级Buff）
    public List<IngredientStack> ingredientInventory = new(); // 局内食材背包
    public List<RunDishStackSave> runDishGains = new();   // 本局炒出的成品菜（状态面板背包展示用）
    public int streakCount;               // 连杀数
    public float streakRemain;            // 连杀剩余秒

    // ===== 升级Buff（UpgradeBuffSystem Modifier 快照） =====
    public List<ModifierSaveData> buffModifiers = new();

    // ===== 升级3选1 的复杂增益（效果引擎；恢复时按顺序重放） =====
    public List<string> upgradeEffectIds = new();

    // ===== 本局已获得的增益卡ID（状态面板卡片墙；仅展示用） =====
    public List<string> selectedBuffIds = new();

    // ===== 升级抽取计数（buffId → 已获得次数；maxStack 过滤用） =====
    public List<BuffPickCountSave> pickedBuffCounts = new();

    // ===== 未选完的 3 选 1（保存时升级面板正开着） =====
    public List<string> pendingUpgradeIds = new();

    // ===== 主动技能冷却/临时Buff =====
    public List<SkillCooldownSave> skillCooldowns = new();
    public List<TempBuffSave> tempBuffs = new();

    // ===== 主动技能产生的限时战斗修饰（真伤/条件增伤/击杀回血/回蓝/攻击距离） =====
    public SkillModSave skillMods;

    // ===== 场上实体 =====
    public List<EnemySave> enemies = new();   // 场上的普通怪/精英怪（含血量与位置）
    public List<DropSave> drops = new();      // 地上的食材/经验宝石

    // ===== BOSS（null bossId = 无BOSS战） =====
    public string bossId;                 // BOSS 配置ID
    public float bossHp;                  // BOSS 当前血量
    public int bossPhaseIndex;            // BOSS 阶段索引
    public float bossBreakBar;            // 破韧条值
    public bool bossBreakBarFull;         // 是否已破韧
    public bool bossWeakened;             // 是否虚弱
    public float bossWeaknessRemain;      // 虚弱剩余秒
    public float bossPosX, bossPosY;      // BOSS 位置
    public bool bossCooking;              // BOSS 已进入烹饪音游（血量已空、音游未结算）
    public string cookingRecipeId;        // 正在进行的音游菜谱（bossCooking 时有效）

    // ===== 战场炒菜（画中画）触发进度 =====
    public int fieldCookingKills;

    // ===== 画中画炒菜进行中（菜谱ID；空 = 没有在进行的炒菜） =====
    public string fieldCookingRecipeId;

    // ===== 整局/阶段统计（续玩后结算页数据完整） =====
    public RunStatsSave stats;
}

/// <summary>升级Buff Modifier 快照（LitJson 兼容，type = UpgradeBuffType 整数）</summary>
[Serializable]
public class ModifierSaveData
{
    public int type;      // UpgradeBuffType
    public float flat;    // 固定值累计
    public float percent; // 百分比累计
}

/// <summary>主动技能冷却快照</summary>
[Serializable]
public class SkillCooldownSave
{
    public string skillId;
    public float remain;
}

/// <summary>主动技能临时Buff快照（buffType 为属性类型字符串，如 attack/movespeed）</summary>
[Serializable]
public class TempBuffSave
{
    public string buffType;   // 属性类型（attack/movespeed/maxhp/critchance/...）
    public float value;       // 加成值
    public float remain;      // 剩余秒数
}

/// <summary>升级卡抽取计数（buffId → 次数）</summary>
[Serializable]
public class BuffPickCountSave
{
    public string id;
    public int count;
}

/// <summary>本局炒出的成品菜聚合条目（菜谱ID + 星级 → 数量；DropManager.RunDishGains 的存档形态）</summary>
[Serializable]
public class RunDishStackSave
{
    public string recipeId;
    public int star;
    public int count;
}

/// <summary>主动技能限时战斗修饰快照（真伤/条件增伤/击杀回血/回蓝/攻击距离）</summary>
[Serializable]
public class SkillModSave
{
    public float trueDmgRemain, trueDmgFlat, trueDmgPct;
    public int limitedHits;
    public float limitedPct;
    public float condRemain;
    public int condType;
    public float condPct;
    public float killHealRemain, killHealPct;
    public float manaRemain, hitManaPct, killManaPct;
    public float attackRangeRemain, attackRangePct;
}

/// <summary>场上单只敌人的快照</summary>
[Serializable]
public class EnemySave
{
    public string enemyId;    // EnemyConfig.id（同时决定预制体、外观、属性倍率）
    public float x, y;        // 位置
    public float hp;          // 当前血量
    public float maxHp;       // 最大血量（同 ID 理论上可复算；用于校验/夹取）
    public float speedMul = 1f; // 移速倍率（战场炒菜减速效果）
}

/// <summary>地上单个掉落物的快照</summary>
[Serializable]
public class DropSave
{
    public int kind;          // 0 = 食材，1 = 经验宝石
    public string itemId;     // 食材ID（kind=0）
    public int value;         // 经验值（kind=1）
    public float x, y;
}

/// <summary>整局/阶段统计快照（BattleStatsCollector 导出）</summary>
[Serializable]
public class RunStatsSave
{
    // 阶段
    public float stageDamage;
    public float stageTime;
    public int stageKills;
    public int stageMaxStreak;
    public int stageWavesCleared;
    public int stageFieldCookCount;
    public string bossQuality;
    public List<string> stageRewards = new();
    public List<string> fieldCookRewards = new();
    public List<IngredientStack> stageIngredients = new();
    public List<DishStack> stageDishes = new();

    // 整局
    public float runDamage;
    public float runTime;
    public int runKills;
    public int runMaxStreak;
    public int runWavesCleared;
    public int runFieldCookCount;
    public int bossesDefeated;
    public int maxLevel;
    public List<IngredientStack> runIngredients = new();
}
