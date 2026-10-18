using System.Collections.Generic;

/// <summary>
/// V1.1.0 → V1.2.0：补充成就与烹饪图鉴字段。
/// 只做空值兜底，图鉴条目交由 RecipeAtlasManager 按配置补齐。
/// </summary>
public class Migration_V1_1toV1_2 : ISaveMigration
{
    public string FromVersion => "1.1.0";
    public string ToVersion => "1.2.0";

    public void Migrate(SaveDataModel model, ISaveLogger logger)
    {
        logger.Info("执行 V1.1.0 -> V1.2.0 迁移：新增成就系统与烹饪图鉴字段");
        if (model.data == null) model.data = new MetaSaveData();

        model.data.achievementProgress ??= new List<AchievementSave>();
        model.data.unlockedAchievementIds ??= new List<string>();
        model.data.recipeCollection ??= new List<RecipeAtlasEntry>();
        model.data.cookingStats ??= new CookingStatisticsData();

        logger.Info("V1.1.0 -> V1.2.0 迁移完成");
    }
}
