using System.Collections.Generic;

/// <summary>
/// 局外永久 Buff 类型：对应「传说菜品提供永久增益」的设计，
/// 每种类型对应一种加成，经 BuffConfig.buffType 字符串映射。
/// </summary>
public enum MetaBuffType
{
    Unknown,          // 未识别类型
    Damage,           // 攻击力加成（永久）
    AttackSpeed,      // 攻击速度加成（永久）
    MaxHp,            // 血量上限加成（永久）
    IngredientDropRate // 初始食材获取率加成（永久）
}

/// <summary>
/// 通用永久 Buff 系统（业务层）
/// 由 MetaManager 持有：根据已解锁的永久Buff ID，从 RecipeConfig 的 Legendary buffEffects 中查找配置，
/// 汇总各类加成数值，供玩家生成/掉落系统查询。
/// 与 UI 解耦：只提供查询接口
/// </summary>
public class MetaBuffSystem
{
    private readonly Dictionary<MetaBuffType, float> mTotals = new();

    /// <summary>攻击力加成（百分比小数，如 0.25 = +25%）</summary>
    public float DamageBonusPercent { get; private set; }

    /// <summary>攻击速度加成（百分比小数）</summary>
    public float AttackSpeedBonusPercent { get; private set; }

    /// <summary>血量上限加成（百分比小数）</summary>
    public float MaxHpBonusPercent { get; private set; }

    /// <summary>食材获取率加成（百分比小数）</summary>
    public float IngredientDropRateBonusPercent { get; private set; }

    /// <summary>
    /// 根据已获得的永久Buff ID 列表，从配置中解析并汇总加成。
    /// 配置来源：RecipeConfig.json 中 isMetaBuff=true 的 Legendary buffEffects
    /// </summary>
    public void BuildFromBuffIds(List<string> buffIds)
    {
        // 清零重算
        DamageBonusPercent = 0f;
        AttackSpeedBonusPercent = 0f;
        MaxHpBonusPercent = 0f;
        IngredientDropRateBonusPercent = 0f;
        mTotals.Clear();

        if (buffIds == null || buffIds.Count == 0) return;

        // 遍历所有菜谱的 buffEffects，找 isMetaBuff=true 且 ID 匹配的
        var recipes = GameDataMgr.Instance.Recipes?.Recipes;
        if (recipes == null) return;

        foreach (var recipe in recipes)
        {
            if (recipe?.buffEffects == null) continue;
            foreach (var buff in recipe.buffEffects)
            {
                if (buff == null || !buff.isMetaBuff) continue;
                if (!buffIds.Contains(buff.buffId)) continue;

                MetaBuffType type = ParseBuffType(buff.buffType);
                if (type == MetaBuffType.Unknown) continue;

                float ratio = buff.value / 100f; // 配置里是百分数值（如 10 = 10%）
                switch (type)
                {
                    case MetaBuffType.Damage: DamageBonusPercent += ratio; break;
                    case MetaBuffType.AttackSpeed: AttackSpeedBonusPercent += ratio; break;
                    case MetaBuffType.MaxHp: MaxHpBonusPercent += ratio; break;
                    case MetaBuffType.IngredientDropRate: IngredientDropRateBonusPercent += ratio; break;
                }
                mTotals[type] = ratio;
            }
        }
    }

    /// <summary>把字符串 buffType 映射为枚举</summary>
    public static MetaBuffType ParseBuffType(string buffType)
    {
        if (string.IsNullOrEmpty(buffType)) return MetaBuffType.Unknown;
        switch (buffType.ToLowerInvariant())
        {
            case "damage": return MetaBuffType.Damage;
            case "attackspeed": return MetaBuffType.AttackSpeed;
            case "maxhp": return MetaBuffType.MaxHp;
            case "ingredientdroprate": return MetaBuffType.IngredientDropRate;
            default: return MetaBuffType.Unknown;
        }
    }
}
