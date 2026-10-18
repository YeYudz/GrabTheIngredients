using System.Collections.Generic;

/// <summary>
/// V0（无版本号裸存档）→ V1：包装为根对象并补齐缺失字段。
/// </summary>
public class Migration_V0toV1 : ISaveMigration
{
    public string FromVersion => SaveVersion.LEGACY_VERSION;
    public string ToVersion => "1.0.0";

    public void Migrate(SaveDataModel model, ISaveLogger logger)
    {
        logger.Info("执行 V0 -> V1 迁移：包装旧版存档，初始化缺失字段");
        if (model.data == null) model.data = new MetaSaveData();

        // 旧版字段兜底（防止旧档缺字段导致空引用）
        model.data.unlockedRecipeIds ??= new List<string>();
        model.data.permanentBuffIds ??= new List<string>();
        model.data.unlockedSkillIds ??= new List<string>();
        model.data.ingredientBackpack ??= new List<IngredientStack>();
        model.data.dishInventory ??= new List<DishInstance>();
        model.data.ownedCharacters ??= new List<string>();
        model.data.weaponInventory ??= new List<WeaponInstance>();
        model.data.preparation ??= new PreparationSaveData();

        // 设置兜底（旧档无设置 → 默认音量）
        model.settings ??= new SettingsSaveData { musicVolume = 1f, sfxVolume = 1f };

        logger.Info("V0 -> V1 迁移完成");
    }
}
