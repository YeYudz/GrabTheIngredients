using System;
using System.Collections.Generic;

/// <summary>
/// 局外永久存档（Meta 进度）：作为 SaveDataModel 的载荷由 SaveFactory 持久化到 saves/save.json。
/// 运行时可变的玩家数据，与 Resources/Data 下的只读配置严格分开存放。
/// </summary>
[Serializable]
public class MetaSaveData
{
    /// <summary>存档版本（便于以后迁移）</summary>
    public int version = 1;

    // ===== 菜谱解锁 =====
    /// <summary>已解锁的菜谱ID列表（配置里的 isUnlocked 是初始值，最终以这里为准）</summary>
    public List<string> unlockedRecipeIds = new();

    /// <summary>
    /// 已解锁的主动技能ID列表（技能"拥有"的唯一口径）。
    /// 写入来源：① 商店购买（MetaManager.BuySkill）② 角色附赠（EnsureDefaultSkills 补 starterSkillIds）。
    /// 注意：拥有角色不会写入这里 —— 角色专属技能同样需要购买才解锁。
    /// </summary>
    public List<string> unlockedSkillIds = new();

    // ===== 永久 Buff =====
    /// <summary>已获得的永久 Buff 列表（buffId 去重）</summary>
    public List<string> permanentBuffIds = new();

    // ===== 历史纪录（统计用） =====
    /// <summary>总游戏局数</summary>
    public int totalRuns;
    /// <summary>胜利局数</summary>
    public int victoryRuns;
    /// <summary>历史最高到达波次</summary>
    public int bestWave;
    /// <summary>历史最高单局击杀</summary>
    public int bestKills;

    // ===== Step10 局外养成 =====

    /// <summary>钱币（出售食材获得）</summary>
    public int coins;

    /// <summary>局外食材背包（本局未消耗食材局后返还）</summary>
    public List<IngredientStack> ingredientBackpack = new();

    /// <summary>成品菜背包（实例化：每个实例独立 id，多个同星同菜用不同实例表现）</summary>
    public List<DishInstance> dishInventory = new();

    /// <summary>拥有的角色ID列表</summary>
    public List<string> ownedCharacters = new();

    /// <summary>武器库存（实例化：每个实例独立 id，战前准备可选）</summary>
    public List<WeaponInstance> weaponInventory = new();

    /// <summary>战前配置存档（记住上次准备内容）</summary>
    public PreparationSaveData preparation = new();

    // ===== 局外永久升级（MetaUpgrade） =====

    /// <summary>永久升级等级（LitJson 兼容用 List；缺省为空，兼容旧档）</summary>
    public List<MetaUpgradeLevel> metaUpgradeLevels = new();

    // ===== 副本进度（选关系统） =====

    /// <summary>已解锁的副本ID列表（配置里的 isUnlocked 是初始值，最终以这里为准）</summary>
    public List<string> unlockedDungeonIds = new();

    /// <summary>当前选中的副本ID（选关面板写入；为空时回退配置默认副本）</summary>
    public string selectedDungeonId = "";

    // ===== 成就系统（元游戏） =====

    /// <summary>成就进度（LitJson 兼容：用 List 存储，运行时建索引）</summary>
    public List<AchievementSave> achievementProgress = new();

    /// <summary>已解锁成就ID列表（快捷查询）</summary>
    public List<string> unlockedAchievementIds = new();

    /// <summary>累计成就点数</summary>
    public int totalAchievementPoints;

    // ===== 烹饪图鉴（元游戏） =====

    /// <summary>图鉴记录（每道菜一条，LitJson 兼容用 List）</summary>
    public List<RecipeAtlasEntry> recipeCollection = new();

    /// <summary>烹饪全局统计</summary>
    public CookingStatisticsData cookingStats = new();

    // ===== 图鉴（元游戏） =====

    /// <summary>历史选择过的局内升级Buff ID（去重；图鉴"增益"分类解锁依据，出现未选视为未解锁）</summary>
    public List<string> selectedBuffIds = new();

    /// <summary>快捷查询：菜谱是否已解锁</summary>
    public bool IsRecipeUnlocked(string recipeId)
    {
        return unlockedRecipeIds != null && unlockedRecipeIds.Contains(recipeId);
    }

    /// <summary>快捷查询：是否拥有某个永久Buff</summary>
    public bool HasBuff(string buffId)
    {
        return permanentBuffIds != null && permanentBuffIds.Contains(buffId);
    }
}

/// <summary>食材堆叠（LitJson 兼容：用 List 而非 Dictionary）</summary>
[Serializable]
public class IngredientStack
{
    public string id;
    public int count;
}

/// <summary>成品菜实例（背包格子对应一个实例，instanceId 全局唯一）</summary>
[Serializable]
public class DishInstance
{
    public string instanceId;   // 实例唯一ID（Guid，旧档迁移时自动补）
    public string recipeId;     // 菜谱ID
    public int star;            // 星级（1~5）
}

/// <summary>武器实例（独立 id；当前每把武器一个实例，为后续"多把同武器/强化"预留）</summary>
[Serializable]
public class WeaponInstance
{
    public string instanceId;   // 实例唯一ID（Guid）
    public string weaponId;     // 武器配置ID
}

/// <summary>战前配置存档（角色/武器槽位/装配菜品/技能组）</summary>
[Serializable]
public class PreparationSaveData
{
    public string characterId = "";                      // 当前角色ID
    public List<string> weaponIds = new();               // 武器槽位（0-2，3槽，空槽以缺省表示）
    public List<EquippedDish> equippedDishes = new();    // 装配的成品菜（0-2，3槽）
    public List<string> skillIds = new();                // 技能槽位（0-2，3槽）
}

/// <summary>局外永久升级等级（id + 已升级数；LitJson 兼容用 List 存储）</summary>
[Serializable]
public class MetaUpgradeLevel
{
    public string id;      // 升级项ID
    public int level;      // 已升级数（0 = 未升级）
}
