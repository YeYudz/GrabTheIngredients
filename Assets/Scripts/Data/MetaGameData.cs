using System.Collections.Generic;

/// <summary>
/// 元游戏存档数据（成就 + 烹饪图鉴）。与 MetaSaveData 分文件存放，
/// 字段均为 LitJson 兼容形式（用 List 而非 Dictionary）。
/// </summary>

/// <summary>单个成就的存档进度</summary>
public class AchievementSave
{
    public string achievementId;             // 成就ID
    public List<int> conditionCurrent = new(); // 各条件当前进度（与配置 conditions 下标对应）
    public bool isUnlocked;                  // 是否已解锁（进度达标）
    public bool claimed;                     // 奖励是否已领取（解锁后需手动领取）
    public long unlockTimestamp;             // 解锁时间戳
}

/// <summary>图鉴条目：记录一道菜的收集与烹饪历史</summary>
public class RecipeAtlasEntry
{
    public string recipeId;                  // 菜品ID
    public int source;                       // 首次发现来源：0=画中画炒菜产出（映射成品菜） 1=成品菜谱（Boss 音游/商店解锁）
    public bool isUnlocked;                  // 是否已解锁
    public int cookCount;                    // 烹饪次数
    public int perfectCount;                 // 完美判定次数
    public int maxCombo;                     // 单次最高连招
    public long lastCookTimestamp;           // 最后烹饪时间
    public string discoveredSource = "";     // 发现来源：default / unlock / first_cook
    public List<int> collectedStars = new(); // 曾获得过的成品菜星级（去重 1~5，图鉴按星级分格解锁依据）
}

/// <summary>烹饪全局统计（图鉴顶部展示 + 成就条件数据源）</summary>
public class CookingStatisticsData
{
    public int totalCookingCount;            // 总烹饪次数（战场+Boss战后）
    public int totalPerfectCount;            // 总完美判定次数
    public int battlefieldCookingCount;      // 战场炒菜次数
    public int bossCookingCount;             // Boss战后烹饪次数
    public int maxComboChain;                // 历史最高连招
    public int differentRecipesCooked;       // 烹饪过的不同菜品数
    public int recipeUnlockedCount;          // 图鉴已解锁菜谱数
}
