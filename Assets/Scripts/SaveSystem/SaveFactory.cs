using System;
using UnityEngine;

/// <summary>
/// 存档统一入口：业务层零耦合存档实现，只与本类的 Load / Save / DeleteAll 交互。
/// 加载内置版本迁移与损坏恢复兜底，永不抛异常；保存则先备份、再算校验码写入。
/// </summary>
public static class SaveFactory
{
    private static readonly SaveVersionManager VersionMgr = new SaveVersionManager();
    private static readonly SaveBackupManager BackupMgr = new SaveBackupManager();
    private static readonly SaveEncryptionManager Encryptor = new SaveEncryptionManager();

    static SaveFactory()
    {
        // 注册迁移脚本（按版本顺序；未来新增版本在此追加）
        VersionMgr.Initialize(new ISaveMigration[]
        {
            new Migration_V0toV1(),
            new Migration_V1_0toV1_1(),
            new Migration_V1_1toV1_2(),
            new Migration_V1_2toV1_3(),
            // 未来: new Migration_V1_3toV1_4(),
        });
    }

    /// <summary>存档保存/加载失败事件（可用于 UI 弹窗提示）</summary>
    public static event Action<string> OnSaveError;

    /// <summary>本次加载是否发生过版本迁移（用于日志/表现）</summary>
    public static bool HasMigrationHappened => VersionMgr.HasMigrationHappened;

    /// <summary>加载存档（主档 → 快照 → 旧档迁移 → 默认），永不抛异常</summary>
    public static SaveDataModel Load()
    {
        try
        {
            return BackupMgr.Load(VersionMgr, Encryptor);
        }
        catch (Exception ex)
        {
            string msg = $"[SaveFactory] 存档加载异常: {ex.Message}";
            OnSaveError?.Invoke(msg);
            Debug.LogError(msg);
            return null;
        }
    }

    /// <summary>保存存档（备份 + 校验码 + 写入），返回是否成功</summary>
    public static bool Save(SaveDataModel model)
    {
        if (model == null) return false;
        try
        {
            bool ok = BackupMgr.Save(model, Encryptor);
            if (!ok) OnSaveError?.Invoke("[SaveFactory] 存档保存失败");
            return ok;
        }
        catch (Exception ex)
        {
            string msg = $"[SaveFactory] 存档保存异常: {ex.Message}";
            OnSaveError?.Invoke(msg);
            Debug.LogError(msg);
            return false;
        }
    }

    /// <summary>删除所有存档（含快照与旧版裸档）</summary>
    public static void DeleteAll() => BackupMgr.DeleteAll();

    /// <summary>是否已有主存档</summary>
    public static bool HasSave() => BackupMgr.HasSave();
}
