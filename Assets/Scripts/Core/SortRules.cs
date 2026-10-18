using System;

/// <summary>
/// 统一排序规则：战前准备与背包共用，保证两处列表顺序一致。
/// 优先级为 已装备 > 优先级值降序（如星级）> 同类相邻。
/// </summary>
public static class SortRules
{
    /// <summary>
    /// 通用比较器：
    ///   equippedA/B - 是否已装备（true 排前）
    ///   priorityA/B - 次要优先级（越大越靠前，如星级；无优先级传 0）
    ///   groupA/B    - 同类分组 id（同组相邻，如 recipeId/weaponId）
    /// </summary>
    public static int CompareItem(
        bool equippedA, int priorityA, string groupA,
        bool equippedB, int priorityB, string groupB)
    {
        // 最高优先级：已装备最前
        if (equippedA != equippedB)
            return equippedA ? -1 : 1;

        // 中优先级：降序（如星级越高越前）
        int priorityCmp = priorityB.CompareTo(priorityA);
        if (priorityCmp != 0)
            return priorityCmp;

        // 低优先级：同类相邻
        return string.CompareOrdinal(groupA ?? string.Empty, groupB ?? string.Empty);
    }
}
