/// <summary>
/// 合成与出售规则：单例配置（无 List），由 CraftConfig.json 加载，调规则不必改代码。
/// </summary>
public class CraftConfig
{
    public int combineRequiredCount = 3;      // 合成所需同星同菜数量
    public int combineMaxStar = 5;            // 合成星级上限（达到后不可再合）
    public float dishSellStarMultiplier = 1f; // 成品菜出售：卖价 = sellPrice × 星级 × 倍率
    public float weaponSellMultiplier = 1f;   // 武器出售：卖价 = sellPrice × 倍率
    public float ingredientSellMultiplier = 1f; // 食材出售：卖价 = sellPrice × 倍率
}

public class CraftConfigWrapper
{
    public int version = 1;
    public CraftConfig Craft = new CraftConfig();

    /// <summary>取规则（配置缺失时兜底默认值）</summary>
    public CraftConfig Get() => Craft ?? new CraftConfig();
}
