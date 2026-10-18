using System;

/// <summary>存档迁移脚本接口：实现版本间的数据升级</summary>
public interface ISaveMigration
{
    /// <summary>源版本号</summary>
    string FromVersion { get; }

    /// <summary>目标版本号</summary>
    string ToVersion { get; }

    /// <summary>执行迁移逻辑（把存档数据升级到 ToVersion）</summary>
    void Migrate(SaveDataModel model, ISaveLogger logger);
}

/// <summary>迁移日志接口（SaveVersionManager 实现，统一日志出口）</summary>
public interface ISaveLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception ex = null);
}
