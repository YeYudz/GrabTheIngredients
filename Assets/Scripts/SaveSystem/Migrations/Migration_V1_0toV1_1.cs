using System.Collections.Generic;

/// <summary>
/// V1.0.0 → V1.1.0：补充关卡进度字段。
/// 只做空值兜底，初始解锁关卡交由 MetaManager 按配置补齐，避免迁移依赖配置加载。
/// </summary>
public class Migration_V1_0toV1_1 : ISaveMigration
{
    public string FromVersion => "1.0.0";
    public string ToVersion => "1.1.0";

    public void Migrate(SaveDataModel model, ISaveLogger logger)
    {
        logger.Info("执行 V1.0.0 -> V1.1.0 迁移：新增关卡进度字段");
        if (model.data == null) model.data = new MetaSaveData();

        model.data.unlockedDungeonIds ??= new List<string>();
        model.data.selectedDungeonId ??= "";

        logger.Info("V1.0.0 -> V1.1.0 迁移完成");
    }
}
