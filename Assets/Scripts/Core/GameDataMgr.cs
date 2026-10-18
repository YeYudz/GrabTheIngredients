using UnityEngine;
using YokiFrame;

/// <summary>
/// 全局配置管理器（只读）：启动时从 Resources/Data 下的 JSON 一次性加载，
/// 每项配置一个独立 Wrapper，并在运行时构建字典索引以便 O(1) 查表。
/// 存档读写不在此处，统一走 SaveSystem。
/// </summary>
public class GameDataMgr
{
    private static GameDataMgr instance = new GameDataMgr();
    public static GameDataMgr Instance => instance;

    // ===== 本项目配置数据（只读，Resources/Data/*.json） =====

    public PlayerConfigWrapper Player;
    public IngredientConfigWrapper Ingredients;
    public EnemyConfigWrapper Enemies;
    public WaveConfigWrapper Waves;
    public BossConfigWrapper Bosses;
    public RecipeConfigWrapper Recipes;
    public RhythmConfigWrapper Rhythm;

    // ===== Step10 新增配置（角色/武器/主动技能/升级Buff池） =====
    public CharacterConfigWrapper Characters;
    public WeaponConfigWrapper Weapons;
    public ActiveSkillConfigWrapper ActiveSkills;
    public BuffUpgradeConfigWrapper Buffs;

    // ===== 战场炒菜配置（随机事件临时增益菜品） =====
    public CookingRecipeConfigWrapper CookingRecipes;

    // ===== 副本配置（波次序列 + 事件槽） =====
    public DungeonConfigWrapper Dungeons;

    // ===== 成就配置（元游戏） =====
    public AchievementConfigWrapper Achievements;

    // ===== 复杂增益效果库（效果引擎） =====
    public GameEffectConfigWrapper GameEffects;

    // ===== 敌人属性修饰器（词缀：按 关卡/阶段/波次/怪物 作用域调整属性） =====
    public EnemyStatModifierWrapper EnemyModifiers;

    // ===== 合成/出售规则（元游戏经济） =====
    public CraftConfigWrapper Craft;

    // ===== 局外永久升级（钱币购买永久属性加成） =====
    public MetaUpgradeConfigWrapper MetaUpgrade;

    // ===== 商店上架（元游戏：控制商店卖什么/价格） =====
    public MetaShopConfigWrapper MetaShop;

    // ===== 主菜单底部随机提示（文案池） =====
    public TipsConfigWrapper Tips;

    /// <summary>是否已加载过配置（懒加载保护）</summary>
    public bool IsConfigLoaded { get; private set; }

    private GameDataMgr()
    {
        // Rhythm 是单例配置（无 List），直接构造
        Rhythm = new RhythmConfigWrapper();
    }

    /// <summary>
    /// 确保配置已加载：任何查询方法调用前自动调用
    /// 解决脚本 Awake 执行顺序不确定导致的"找不到配置"问题
    /// </summary>
    private void EnsureConfigLoaded()
    {
        if (!IsConfigLoaded)
        {
            ReloadConfigs();
        }
    }

    /// <summary>
    /// 加载全部 JSON 配置文件。游戏启动（游戏场景首次进入）时调用一次。
    /// 数据来源：Assets/Resources/Data/{Name}.json
    /// </summary>
    public void ReloadConfigs()
    {
        Player       = JsonMgr.Instance.LoadConfig<PlayerConfigWrapper>("PlayerConfig");
        Ingredients  = JsonMgr.Instance.LoadConfig<IngredientConfigWrapper>("IngredientConfig");
        Enemies      = JsonMgr.Instance.LoadConfig<EnemyConfigWrapper>("EnemyConfig");
        Waves        = JsonMgr.Instance.LoadConfig<WaveConfigWrapper>("WaveConfig");
        Bosses       = JsonMgr.Instance.LoadConfig<BossConfigWrapper>("BossConfig");
        Recipes      = JsonMgr.Instance.LoadConfig<RecipeConfigWrapper>("RecipeConfig");

        // Step10 新增配置
        Characters   = JsonMgr.Instance.LoadConfig<CharacterConfigWrapper>("CharacterConfig");
        Weapons      = JsonMgr.Instance.LoadConfig<WeaponConfigWrapper>("WeaponConfig");
        ActiveSkills = JsonMgr.Instance.LoadConfig<ActiveSkillConfigWrapper>("ActiveSkillConfig");
        Buffs        = JsonMgr.Instance.LoadConfig<BuffUpgradeConfigWrapper>("BuffUpgradeConfig");
        CookingRecipes = JsonMgr.Instance.LoadConfig<CookingRecipeConfigWrapper>("CookingRecipeConfig");
        Dungeons     = JsonMgr.Instance.LoadConfig<DungeonConfigWrapper>("DungeonConfig");
        Achievements = JsonMgr.Instance.LoadConfig<AchievementConfigWrapper>("AchievementConfig");
        GameEffects  = JsonMgr.Instance.LoadConfig<GameEffectConfigWrapper>("GameEffectConfig");
        EnemyModifiers = JsonMgr.Instance.LoadConfig<EnemyStatModifierWrapper>("EnemyStatModifierConfig");
        Craft        = JsonMgr.Instance.LoadConfig<CraftConfigWrapper>("CraftConfig");
        MetaUpgrade  = JsonMgr.Instance.LoadConfig<MetaUpgradeConfigWrapper>("MetaUpgradeConfig");
        MetaShop     = JsonMgr.Instance.LoadConfig<MetaShopConfigWrapper>("MetaShopConfig");
        Tips         = JsonMgr.Instance.LoadConfig<TipsConfigWrapper>("TipsConfig");

        // Rhythm 构造时已初始化，如果 Resources 有配置则覆盖
        var rhythmConfig = JsonMgr.Instance.LoadConfig<RhythmConfigWrapper>("RhythmConfig");
        if (rhythmConfig != null) Rhythm = rhythmConfig;

        IsConfigLoaded = true;

        LogKit.Log($"[GameDataMgr] 配置加载完成 - " +
                   $"食材:{Ingredients?.Count ?? 0}, " +
                   $"敌人:{Enemies?.Count ?? 0}, " +
                   $"波次:{Waves?.Count ?? 0}, " +
                   $"BOSS:{Bosses?.Count ?? 0}, " +
                   $"菜谱:{Recipes?.Count ?? 0}, " +
                   $"角色:{Characters?.Count ?? 0}, " +
                   $"武器:{Weapons?.Count ?? 0}, " +
                   $"主动技能:{ActiveSkills?.Count ?? 0}, " +
                   $"升级Buff:{Buffs?.Count ?? 0}, " +
                   $"提示:{Tips?.Count ?? 0}");

        // 加载后自动检查必需配置是否存在
        ValidateConfigs();
    }

    /// <summary>
    /// 简单的配置校验：关键引用是否为空
    /// </summary>
    private void ValidateConfigs()
    {
        if (Player == null)
            LogKit.Warning("[GameDataMgr] PlayerConfig 缺失！");
        if (Enemies == null || Enemies.Count == 0)
            LogKit.Warning("[GameDataMgr] EnemyConfig 为空！");
    }

    // ===== 快捷查询方法（可选，方便业务层调用） =====
    // 每个查询都先 EnsureConfigLoaded()，保证在任何脚本 Awake/Start 中调用都安全

    public EnemyConfig GetEnemy(string id)
    { EnsureConfigLoaded(); return Enemies?.GetById(id); }
    public BossConfig GetBoss(string id)
    { EnsureConfigLoaded(); return Bosses?.GetById(id); }
    public RecipeConfig GetRecipe(string id)
    { EnsureConfigLoaded(); return Recipes?.GetById(id); }
    public IngredientConfig GetIngredient(string id)
    { EnsureConfigLoaded(); return Ingredients?.GetById(id); }
    public WaveConfig GetWave(int waveId)
    { EnsureConfigLoaded(); return Waves?.GetById(waveId); }
    public CharacterConfig GetCharacter(string id)
    { EnsureConfigLoaded(); return Characters?.GetById(id); }
    public WeaponConfig GetWeapon(string id)
    { EnsureConfigLoaded(); return Weapons?.GetById(id); }
    public ActiveSkillConfig GetActiveSkill(string id)
    { EnsureConfigLoaded(); return ActiveSkills?.GetById(id); }
    public BuffUpgradeConfig GetBuffUpgrade(string id)
    { EnsureConfigLoaded(); return Buffs?.GetById(id); }
    public CookingRecipeConfig GetCookingRecipe(string id)
    { EnsureConfigLoaded(); return CookingRecipes?.GetById(id); }
    public int GetCookingTriggerThreshold()
    { EnsureConfigLoaded(); return CookingRecipes?.killTriggerThreshold ?? 10; }

    /// <summary>按ID取副本配置</summary>
    public DungeonConfig GetDungeon(string id)
    { EnsureConfigLoaded(); return Dungeons?.GetById(id); }

    /// <summary>按ID取成就配置</summary>
    public AchievementConfig GetAchievement(string id)
    { EnsureConfigLoaded(); return Achievements?.GetById(id); }

    /// <summary>按ID取复杂增益效果配置</summary>
    public GameEffectConfig GetEffect(string id)
    { EnsureConfigLoaded(); return GameEffects?.GetById(id); }

    /// <summary>收集匹配指定作用域（副本/阶段/波次/怪物）的全部敌人属性修饰器</summary>
    public System.Collections.Generic.List<EnemyStatModifier> CollectEnemyModifiers(
        string dungeonId, string phaseId, int waveId, string enemyId)
    {
        EnsureConfigLoaded();
        var result = new System.Collections.Generic.List<EnemyStatModifier>();
        EnemyModifiers?.Collect(dungeonId, phaseId, waveId, enemyId, result);
        return result;
    }

    /// <summary>取合成/出售规则（配置缺失时返回默认值）</summary>
    public CraftConfig GetCraftRules()
    {
        EnsureConfigLoaded();
        return Craft?.Get() ?? new CraftConfig();
    }

    /// <summary>取局外永久升级配置（配置缺失时返回空 wrapper，保证查询安全）</summary>
    public MetaUpgradeConfigWrapper GetMetaUpgrade()
    {
        EnsureConfigLoaded();
        return MetaUpgrade ?? new MetaUpgradeConfigWrapper();
    }

    /// <summary>取商店上架配置（配置缺失时返回空 wrapper，保证查询安全）</summary>
    public MetaShopConfigWrapper GetMetaShop()
    {
        EnsureConfigLoaded();
        return MetaShop ?? new MetaShopConfigWrapper();
    }

    /// <summary>
    /// 取一条随机主菜单提示（已拼好前缀）。配置缺失/文案池为空时返回空串。
    /// </summary>
    public string GetRandomTip(string emptyTip = "")
    {
        EnsureConfigLoaded();
        return Tips != null ? Tips.PickRandom(emptyTip) : emptyTip;
    }

    /// <summary>
    /// 当前副本：选关系统接入前取配置里的默认副本（失效时取第一个副本）。
    /// 返回 null 表示未配置副本，波次系统退回全局波次表（兼容旧行为）。
    /// </summary>
    public DungeonConfig CurrentDungeon
    {
        get
        {
            EnsureConfigLoaded();
            if (Dungeons == null) return null;
            return Dungeons.GetById(Dungeons.defaultDungeonId) ?? Dungeons.GetFirst();
        }
    }
}
