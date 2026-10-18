using System.Collections.Generic;

/// <summary>
/// V1.2.0 → V1.3.0：副本字段改名（Stage → Dungeon）。
/// 原始 JSON 的键替换在反序列化前由 SaveBackupManager.MigrateLegacyFieldNames 完成，
/// 这里只对类型化模型做空值兜底。
/// </summary>
public class Migration_V1_2toV1_3 : ISaveMigration
{
    public string FromVersion => "1.2.0";
    public string ToVersion => "1.3.0";

    public void Migrate(SaveDataModel model, ISaveLogger logger)
    {
        logger.Info("执行 V1.2.0 -> V1.3.0 迁移：副本进度字段改名（Stage → Dungeon）");
        if (model.data == null) model.data = new MetaSaveData();

        model.data.unlockedDungeonIds ??= new List<string>();
        model.data.selectedDungeonId ??= "";

        logger.Info("V1.2.0 -> V1.3.0 迁移完成");
    }
}
