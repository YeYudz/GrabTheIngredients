using System;
using System.Collections.Generic;
using System.IO;
using LitJson;
using UnityEngine;

/// <summary>
/// 存档备份与损坏恢复：每次保存前先备份主档，保留最近若干快照。
/// 加载时按 主档 → 备份快照 → 旧版裸存档迁移 → 默认存档 四级降级，
/// 任一步失败都不崩，保证玩家至少能进得去游戏。
/// </summary>
public class SaveBackupManager
{
    private const string SAVE_DIR = "saves";
    private const string BACKUP_SUFFIX = ".bak";
    private const int MAX_BACKUPS = 5;

    private string SavePath => Path.Combine(Application.persistentDataPath, SAVE_DIR);
    private string MainSavePath => Path.Combine(SavePath, "save.json");

    /// <summary>旧版裸存档路径（首次迁移时读取，随后不再使用）</summary>
    private string LegacySavePath => Path.Combine(Application.persistentDataPath, "MetaSaveData.json");

    /// <summary>
    /// 保存：备份当前档 → 更新时间戳 → 生成校验码 → 序列化写入主档
    /// </summary>
    public bool Save(SaveDataModel model, SaveEncryptionManager encryptor)
    {
        try
        {
            BackupCurrentSave();

            model.lastSaveTimestamp = SaveVersion.NowUnix();
            if (encryptor != null) model.checksum = encryptor.GenerateChecksum(model);

            Directory.CreateDirectory(SavePath);
            string json = JsonMapper.ToJson(model);
            File.WriteAllText(MainSavePath, json);

            Debug.Log($"[SaveBackup] 存档保存成功: {MainSavePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SaveBackup] 存档保存失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 加载：主档（校验+迁移）→ 备份快照（校验+迁移）→ 旧版裸存档（迁移并落盘）→ 默认存档
    /// </summary>
    public SaveDataModel Load(SaveVersionManager versionManager, SaveEncryptionManager encryptor)
    {
        // 策略1：主存档
        var data = LoadFromFile(MainSavePath);
        if (data != null && VerifyIntegrity(data, encryptor))
        {
            data = versionManager.Migrate(data);
            Debug.Log($"[SaveBackup] 主存档加载成功，版本: {data.saveVersion}");
            return data;
        }
        if (data != null)
            Debug.LogWarning("[SaveBackup] 主存档校验失败，尝试恢复快照...");

        // 策略2：最近备份快照
        data = LoadLatestBackup();
        if (data != null && VerifyIntegrity(data, encryptor))
        {
            data = versionManager.Migrate(data);
            Debug.LogWarning("[SaveBackup] 已从备份快照恢复存档");
            return data;
        }

        // 策略3：旧版裸存档（无版本号）→ 迁移到新格式并落盘
        data = LoadLegacySave();
        if (data != null)
        {
            data = versionManager.Migrate(data);
            Save(data, encryptor);
            Debug.LogWarning("[SaveBackup] 旧版存档已迁移到新格式");
            return data;
        }

        // 策略4：默认存档
        Debug.LogWarning("[SaveBackup] 无可用存档，生成默认存档");
        return CreateDefault();
    }

    /// <summary>从文件解析（失败返回 null，不抛异常）</summary>
    private SaveDataModel LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            json = MigrateLegacyFieldNames(json);
            return JsonMapper.ToObject<SaveDataModel>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SaveBackup] 存档解析失败: {path} | {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 旧档字段改名兼容（V1.2 及更早：副本进度字段 Stage 命名 → Dungeon 命名）。
    /// 必须在类型化反序列化前做原始 JSON 替换：LitJson 会静默丢弃不认识的旧键；
    /// 且校验码覆盖整个模型序列化结果，字段改名后旧校验码必然失配 → 同时置空 checksum，
    /// VerifyIntegrity 对空校验码跳过校验（本次加载落盘后会重新生成有效校验码）。
    /// </summary>
    private string MigrateLegacyFieldNames(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        bool renamed = false;
        if (json.Contains("\"unlockedStageIds\""))
        {
            json = json.Replace("\"unlockedStageIds\"", "\"unlockedDungeonIds\"");
            renamed = true;
        }
        if (json.Contains("\"selectedStageId\""))
        {
            json = json.Replace("\"selectedStageId\"", "\"selectedDungeonId\"");
            renamed = true;
        }
        // 字段改名过 → 旧校验码作废，置空以便本次加载跳过校验
        if (renamed)
            json = System.Text.RegularExpressions.Regex.Replace(
                json, "\"checksum\"\\s*:\\s*\"[^\"]*\"", "\"checksum\":\"\"");
        return json;
    }

    /// <summary>读取旧版裸存档并包装为根对象（版本标记为 0.0.0）</summary>
    private SaveDataModel LoadLegacySave()
    {
        try
        {
            if (!File.Exists(LegacySavePath)) return null;
            string json = File.ReadAllText(LegacySavePath);
            json = MigrateLegacyFieldNames(json);
            var meta = JsonMapper.ToObject<MetaSaveData>(json);
            if (meta == null) return null;
            return new SaveDataModel
            {
                saveVersion = SaveVersion.LEGACY_VERSION,
                data = meta
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SaveBackup] 旧版存档解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>完整性校验：数据非空 + 校验码验证（旧档无校验码则跳过）</summary>
    private bool VerifyIntegrity(SaveDataModel data, SaveEncryptionManager encryptor)
    {
        if (data == null || data.data == null) return false;

        if (encryptor != null && !string.IsNullOrEmpty(data.checksum))
        {
            if (!encryptor.VerifyChecksum(data))
            {
                Debug.LogWarning("[SaveBackup] 校验码验证失败，存档可能被篡改");
                return false;
            }
        }
        return true;
    }

    // ===== 备份 =====

    private void BackupCurrentSave()
    {
        if (!File.Exists(MainSavePath)) return;

        DeleteOldestBackup();
        string backupPath = GetNextBackupPath();
        File.Copy(MainSavePath, backupPath, true);
        Debug.Log($"[SaveBackup] 备份已创建: {backupPath}");
    }

    private string GetNextBackupPath()
    {
        int index = 1;
        while (File.Exists(Path.Combine(SavePath, $"save_{index:00}{BACKUP_SUFFIX}")))
            index++;
        return Path.Combine(SavePath, $"save_{index:00}{BACKUP_SUFFIX}");
    }

    /// <summary>删除最旧备份，保留 MAX_BACKUPS 个</summary>
    private void DeleteOldestBackup()
    {
        if (!Directory.Exists(SavePath)) return;
        var backups = Directory.GetFiles(SavePath, $"save_*{BACKUP_SUFFIX}");
        Array.Sort(backups); // 文件名零填充，字典序 = 时间序
        for (int i = 0; i < backups.Length - (MAX_BACKUPS - 1); i++)
        {
            try { File.Delete(backups[i]); }
            catch (Exception ex) { Debug.LogWarning($"[SaveBackup] 删除旧备份失败: {ex.Message}"); }
        }
    }

    /// <summary>加载最新备份快照（逐个尝试，取第一个可用的）</summary>
    private SaveDataModel LoadLatestBackup()
    {
        if (!Directory.Exists(SavePath)) return null;
        var backups = Directory.GetFiles(SavePath, $"save_*{BACKUP_SUFFIX}");
        Array.Sort(backups);
        for (int i = backups.Length - 1; i >= 0; i--)
        {
            var data = LoadFromFile(backups[i]);
            if (data != null) return data;
        }
        return null;
    }

    // ===== 默认 / 删除 =====

    private SaveDataModel CreateDefault()
    {
        return new SaveDataModel
        {
            saveVersion = SaveVersion.CURRENT_VERSION,
            lastSaveTimestamp = SaveVersion.NowUnix(),
            data = new MetaSaveData(),
            settings = new SettingsSaveData { musicVolume = 1f, sfxVolume = 1f }
        };
    }

    /// <summary>删除所有存档（含快照与旧版裸档）</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(SavePath))
            Directory.Delete(SavePath, true);
        if (File.Exists(LegacySavePath))
            File.Delete(LegacySavePath);
        Debug.Log("[SaveBackup] 所有存档已删除");
    }

    public bool HasSave()
    {
        return File.Exists(MainSavePath);
    }
}
