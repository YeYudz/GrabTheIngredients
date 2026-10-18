using System;

/// <summary>
/// 存档根对象：版本号 + 时间戳 + 校验码 + 局外养成载荷。
/// 序列化走 LitJson 以与项目内 JSON 口径一致，落盘于 persistentDataPath/saves/save.json。
/// </summary>
[Serializable]
public class SaveDataModel
{
    /// <summary>存档版本号（语义化版本，版本迁移判断依据）</summary>
    public string saveVersion = SaveVersion.CURRENT_VERSION;

    /// <summary>最后保存时间戳（Unix 秒）</summary>
    public long lastSaveTimestamp;

    /// <summary>完整性校验码（防篡改；空 = 未启用校验，旧档迁移时为空）</summary>
    public string checksum;

    /// <summary>局外养成数据（与旧版 MetaSaveData.json 的载荷一致，迁移时自动包装）</summary>
    public MetaSaveData data;

    /// <summary>游戏设置（音量等）</summary>
    public SettingsSaveData settings;
}
