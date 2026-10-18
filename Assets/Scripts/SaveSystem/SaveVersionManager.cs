using System;
using System.Collections.Generic;
using YokiFrame;

/// <summary>
/// 版本迁移管理：迁移脚本按 FromVersion 升序注册，数据沿版本链逐步升级到当前版本。
/// 单个脚本失败不中断整条链——宁可留下部分旧字段，也不让玩家卡在进不去的版本上。
/// </summary>
public class SaveVersionManager : ISaveLogger
{
    private readonly List<ISaveMigration> _migrations = new();
    private bool _hasMigrationHappened;

    /// <summary>注册迁移脚本（按 FromVersion 升序排列，保证版本链顺序执行）</summary>
    public void Initialize(IEnumerable<ISaveMigration> migrations)
    {
        _migrations.Clear();
        if (migrations != null) _migrations.AddRange(migrations);
        _migrations.Sort((a, b) => SaveVersion.Compare(a.FromVersion, b.FromVersion));
    }

    /// <summary>从存档当前版本迁移到最新版本（失败不中断），返回迁移后的存档</summary>
    public SaveDataModel Migrate(SaveDataModel model)
    {
        if (model == null) return model;

        string current = string.IsNullOrEmpty(model.saveVersion) ? SaveVersion.LEGACY_VERSION : model.saveVersion;
        if (SaveVersion.Compare(current, SaveVersion.CURRENT_VERSION) >= 0)
        {
            Info($"存档版本已是最新（{current}），无需迁移");
            return model;
        }

        Info($"开始迁移: {current} -> {SaveVersion.CURRENT_VERSION}");
        foreach (var m in _migrations)
        {
            // 当前版本低于该脚本目标版本 → 执行（数据升级到 ToVersion）
            if (SaveVersion.Compare(current, m.ToVersion) >= 0) continue;

            Info($"执行迁移: {m.FromVersion} -> {m.ToVersion}");
            try
            {
                m.Migrate(model, this);
                Info($"迁移成功: {m.FromVersion} -> {m.ToVersion}");
            }
            catch (Exception ex)
            {
                // 迁移失败不中断，记录警告继续尝试下一个
                Error($"迁移失败: {m.FromVersion} -> {m.ToVersion}", ex);
            }
        }

        model.saveVersion = SaveVersion.CURRENT_VERSION;
        model.lastSaveTimestamp = SaveVersion.NowUnix();
        _hasMigrationHappened = true;
        Info($"迁移完成，当前版本: {model.saveVersion}");
        return model;
    }

    /// <summary>本次加载是否发生过迁移（用于日志/表现提示）</summary>
    public bool HasMigrationHappened => _hasMigrationHappened;

    // ===== ISaveLogger 实现 =====
    public void Info(string message) => LogKit.Log($"[SaveMigration] {message}");
    public void Warn(string message) => LogKit.Warning($"[SaveMigration] {message}");
    public void Error(string message, Exception ex = null)
    {
        UnityEngine.Debug.LogError($"[SaveMigration] {message}" + (ex != null ? $" | {ex.Message}" : ""));
    }
}
