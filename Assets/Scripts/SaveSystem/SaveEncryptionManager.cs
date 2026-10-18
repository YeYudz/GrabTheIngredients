using System;
using System.Security.Cryptography;
using System.Text;
using LitJson;

/// <summary>
/// 轻量存档校验（SHA256 + 盐），用于防简单篡改，并非完整加密；
/// 校验码覆盖序列化后的模型但排除自身字段，避免循环依赖。
/// </summary>
public class SaveEncryptionManager
{
    /// <summary>盐值（商业化项目应放到服务器或混淆代码中）</summary>
    private const string SALT = "GrabTheIngredients_Save_Salt_2026";

    /// <summary>生成校验码（不修改原对象）</summary>
    public string GenerateChecksum(SaveDataModel model)
    {
        if (model == null) return null;

        // 拷贝一份（checksum 置空），避免自引用且不污染原对象
        var copy = new SaveDataModel
        {
            saveVersion = model.saveVersion,
            lastSaveTimestamp = model.lastSaveTimestamp,
            checksum = null,
            data = model.data
        };

        string json = JsonMapper.ToJson(copy);
        string combined = json + SALT;
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return Convert.ToBase64String(hash);
        }
    }

    /// <summary>校验码验证</summary>
    public bool VerifyChecksum(SaveDataModel model)
    {
        if (model == null || string.IsNullOrEmpty(model.checksum)) return false;
        return model.checksum == GenerateChecksum(model);
    }
}
