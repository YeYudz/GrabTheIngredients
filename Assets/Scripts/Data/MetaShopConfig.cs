using System;
using System.Collections.Generic;

/// <summary>
/// 商店上架项：控制「卖什么、卖多少」。type 取 Character / Weapon / Recipe / Skill，
/// price &lt;= 0 时回退商品配置自带的 buyPrice。
/// </summary>
[Serializable]
public class MetaShopEntry
{
    public string id;                  // 商品配置ID（对应各配置表的 id）
    public string type;                // 商品类型
    public int price = 0;              // 购买价覆盖（0 = 用商品配置的 buyPrice）
    public bool onSale = true;         // 是否上架
}

/// <summary>
/// 货币来源项（元游戏：控制"钱从哪来"）
/// key 取值：perWave / perKill / victoryBonus（与局末结算发币规则对应）
/// </summary>
[Serializable]
public class CoinSourceEntry
{
    public string key;                 // 来源标识
    public string desc;                // 说明（策划可读）
    public int value = 0;              // 单次/单波/单击杀发币数
}

/// <summary>
/// 货币消耗项（元游戏：控制"钱往哪去"）
/// key 取值：metaUpgrade / shop / craft（与各消耗系统对应）
/// </summary>
[Serializable]
public class CoinCostEntry
{
    public string key;                 // 消耗去向标识
    public string desc;                // 说明（策划可读）
    public bool enabled = true;        // 是否启用该消耗去向
}

/// <summary>
/// 商店上架配置（Resources/Data/MetaShopConfig.json）
/// 某类型若配置了上架项 → 该类型由本表接管（只上架表内 onSale 项，价格可覆盖）；
/// 未配置任何项的类型 → 回退旧的"全量扫描 buyPrice"逻辑，保证行为兼容
/// </summary>
public class MetaShopConfigWrapper
{
    public int version = 1;

    /// <summary>上架项列表</summary>
    public List<MetaShopEntry> entries = new();

    /// <summary>货币来源表（钱从哪来）</summary>
    public List<CoinSourceEntry> coinSources = new();

    /// <summary>货币消耗去向表（钱往哪去）</summary>
    public List<CoinCostEntry> coinCosts = new();

    private Dictionary<string, List<MetaShopEntry>> m_byType;

    /// <summary>上架项列表（只读）</summary>
    public List<MetaShopEntry> Entries => entries;

    /// <summary>上架项条数</summary>
    public int Count => entries?.Count ?? 0;

    /// <summary>取某项货币来源的发币值（key = perWave / perKill / victoryBonus 等）；未配置返回 fallback</summary>
    public int GetCoinSourceValue(string key, int fallback = 0)
    {
        if (coinSources == null || string.IsNullOrEmpty(key)) return fallback;
        foreach (var s in coinSources)
        {
            if (s != null && s.key == key) return s.value;
        }
        return fallback;
    }

    /// <summary>取某类型的上架项；该类型无配置时返回 null（视为不接管）</summary>
    public List<MetaShopEntry> GetByType(string type)
    {
        BuildTypeMap();
        if (string.IsNullOrEmpty(type)) return null;
        return m_byType.TryGetValue(type, out var list) ? list : null;
    }

    private void BuildTypeMap()
    {
        if (m_byType != null) return;

        m_byType = new Dictionary<string, List<MetaShopEntry>>();
        if (entries == null) return;

        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrEmpty(e.id) || string.IsNullOrEmpty(e.type)) continue;
            if (!m_byType.TryGetValue(e.type, out var list))
            {
                list = new List<MetaShopEntry>();
                m_byType.Add(e.type, list);
            }
            list.Add(e);
        }
    }
}
