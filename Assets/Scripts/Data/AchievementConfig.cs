using System.Collections.Generic;

/// <summary>
/// 成就分类
/// </summary>
public enum AchievementCategory
{
    Combat = 0,      // 战斗类
    Cooking = 1,     // 烹饪类
    Rhythm = 2,      // 音游类
    Collection = 3,  // 收集类
    Meta = 4,        // 元游戏类
    Special = 5      // 特殊类
}

/// <summary>
/// 成就条件类型（与游戏内事件/统计一一对应）
/// </summary>
public enum AchievementConditionType
{
    TotalKills = 0,                // 累计击杀
    WaveReached = 1,               // 到达波次
    TotalCooking = 2,              // 总烹饪次数（战场炒菜 + Boss战后）
    PerfectCooking = 3,            // 完美判定次数（战场炒菜 Perfect）
    BattlefieldCookingTriggered = 4, // 战场炒菜触发次数
    BossKills = 5,                 // Boss 击败数
    RecipeCollectionCount = 6,     // 图鉴解锁菜谱数
    TotalGoldEarned = 7,           // 累计获得金币
    GamesPlayed = 8,               // 游戏局数
    MaxComboChain = 9              // 历史最高连招
}

/// <summary>
/// 成就奖励类型（裁剪：项目无宝石/碎片；角色与武器解锁预留）
/// </summary>
public enum AchievementRewardType
{
    Gold = 0,             // 金币
    Ingredient = 1,       // 食材（rewardId=食材ID）
    RecipeUnlock = 2,     // 解锁菜谱（rewardId=菜谱ID）
    CharacterUnlock = 3,  // 解锁角色（预留）
    WeaponUnlock = 4      // 解锁武器（预留）
}

/// <summary>单个条件（AND 组合：全部达标才解锁）</summary>
public class AchievementCondition
{
    public AchievementConditionType type;  // 条件类型
    public int targetValue;                // 目标值
}

/// <summary>奖励条目</summary>
public class AchievementReward
{
    public AchievementRewardType type;     // 奖励类型
    public string rewardId = "";           // 食材/菜谱ID（金币可为空）
    public int amount = 1;                 // 数量
}

/// <summary>
/// 成就配置数据（数据驱动：策划改 JSON 即可增删改成就）
/// </summary>
public class AchievementConfig
{
    public string achievementId;           // 唯一ID
    public string title;                   // 标题
    public string description;             // 描述
    public int category;                   // AchievementCategory
    public int pointValue;                 // 成就点数
    public bool isSecret;                  // 隐藏成就（解锁前显示"???"）
    public string secretDescription = "";  // 隐藏成就的模糊描述
    public string iconPath = "";           // 成就图标 Resources 路径（可空，空则隐藏图标）

    public List<AchievementCondition> conditions = new(); // AND 组合
    public List<AchievementReward> rewards = new();       // 奖励列表
}

public class AchievementConfigWrapper
{
    public int version = 1;
    public List<AchievementConfig> Achievements = new();

    private Dictionary<string, AchievementConfig> _dict;

    public AchievementConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, AchievementConfig>();
            foreach (var a in Achievements)
                if (!string.IsNullOrEmpty(a?.achievementId) && !_dict.ContainsKey(a.achievementId))
                    _dict.Add(a.achievementId, a);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Achievements?.Count ?? 0;
}
