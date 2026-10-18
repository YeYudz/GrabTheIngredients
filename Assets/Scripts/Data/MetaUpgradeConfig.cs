using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 局外永久升级项（钱币购买，只做加值）。
/// 增量曲线：第 L 级增量 = valuePerLevel × (1 + valueGrowth × (L-1))；
/// 价格曲线：L → L+1 的花费 = round(baseCost × (1 + costGrowth)^L)。
/// </summary>
[Serializable]
public class MetaUpgradeEntry
{
    public string id;                 // 升级项ID
    public string name;               // 显示名
    public string desc;               // 描述
    public string attrKey;            // 作用属性键（PlayerAttribute 字段名：MaxHp/Attack/...）
    public float valuePerLevel = 1f;  // 每级基础增量（加值）
    public float valueGrowth = 0f;    // 数值曲线斜率（0=每级等量）
    public int maxLevel = 20;         // 等级上限
    public int baseCost = 100;        // 基础花费（0→1 级）
    public float costGrowth = 0.15f;  // 价格曲线（每级递增率）
}

/// <summary>
/// 局外永久升级配置（Resources/Data/MetaUpgradeConfig.json）
/// 仅开核心几项属性；每条上限 20 级；价格/数值均由曲线字段驱动
/// 发币规则不在本配置内（统一由 MetaShopConfig.coinSources 提供）
/// </summary>
public class MetaUpgradeConfigWrapper
{
    public int version = 1;

    /// <summary>永久升级项列表（列表式，一条一列）</summary>
    public List<MetaUpgradeEntry> entries = new();

    private Dictionary<string, MetaUpgradeEntry> m_dict;

    /// <summary>升级项列表（只读）</summary>
    public List<MetaUpgradeEntry> Entries => entries;

    /// <summary>升级项条数</summary>
    public int Count => entries?.Count ?? 0;

    /// <summary>按ID取升级项</summary>
    public MetaUpgradeEntry GetById(string id)
    {
        if (m_dict == null)
        {
            m_dict = new Dictionary<string, MetaUpgradeEntry>();
            if (entries != null)
            {
                foreach (var e in entries)
                    if (!string.IsNullOrEmpty(e?.id) && !m_dict.ContainsKey(e.id))
                        m_dict.Add(e.id, e);
            }
        }
        return string.IsNullOrEmpty(id) ? null : (m_dict.TryGetValue(id, out var cfg) ? cfg : null);
    }
}
