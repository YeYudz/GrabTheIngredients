using System.Collections.Generic;

/// <summary>
/// 食材分类（与 AttackType 对齐，方便映射）
/// </summary>
public enum IngredientType
{
    Vegetable,   // 蔬菜
    Meat,        // 肉类
    Spice,       // 香料
    Universal    // 通用/其他
}

/// <summary>
/// 食材稀有度
/// </summary>
public enum IngredientRarity
{
    Common = 1,
    Uncommon = 2,
    Rare = 3
}

/// <summary>
/// 食材配置数据（敌人掉落物、烹饪材料）
/// 对应文档第三节"食材用途设计"
/// </summary>
public class IngredientConfig
{
    public string id;                       // 唯一ID
    public string ingredientName;           // 显示名称，如 "番茄"、"牛肉"
    public IngredientType ingredientType;   // 分类（决定可用于哪类菜品）
    public IngredientRarity rarity;         // 稀有度（影响掉落概率）
    public string iconPath;                 // 图标 Resources 路径
    public string prefabPath;               // 掉落物预制体 Resources 路径
    public int sellPrice = 1;               // 出售价格（局外出售食材获得钱币）
}

public class IngredientConfigWrapper
{
    public int version = 1;
    public List<IngredientConfig> Ingredients = new();

    private Dictionary<string, IngredientConfig> _dict;

    public IngredientConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, IngredientConfig>();
            foreach (var i in Ingredients)
                if (!string.IsNullOrEmpty(i?.id) && !_dict.ContainsKey(i.id))
                    _dict.Add(i.id, i);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Ingredients?.Count ?? 0;
}
