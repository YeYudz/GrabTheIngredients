using UnityEngine;

/// <summary>
/// 成品菜稀有度（复用星级 1-5：白 / 绿 / 蓝 / 紫 / 金）。
/// 收敛为唯一入口，避免各 UI 条目各写一份颜色魔法值。
/// </summary>
public static class DishRarity
{
    /// <summary>按星级取稀有度颜色（非法值回落白色）</summary>
    public static Color GetColor(int star)
    {
        switch (star)
        {
            case 2: return new Color(0.40f, 0.85f, 0.45f); // 绿
            case 3: return new Color(0.35f, 0.60f, 1.00f); // 蓝
            case 4: return new Color(0.75f, 0.50f, 1.00f); // 紫
            case 5: return new Color(1.00f, 0.80f, 0.25f); // 金
            default: return new Color(0.92f, 0.92f, 0.92f); // 白
        }
    }

    /// <summary>稀有度名称（展示用）</summary>
    public static string GetName(int star)
    {
        switch (star)
        {
            case 2: return "绿";
            case 3: return "蓝";
            case 4: return "紫";
            case 5: return "金";
            default: return "白";
        }
    }
}
