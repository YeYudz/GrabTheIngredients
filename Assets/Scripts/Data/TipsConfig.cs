using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 主菜单底部随机提示（Tips）：每次回到主菜单页面抽一条显示。
/// 数据来源 Resources/Data/TipsConfig.json —— 改文案只改 JSON，不用动代码。
/// </summary>
public class TipsConfigWrapper
{
    public int version = 1;

    /// <summary>统一前缀（如「提示：」）；留空则直接显示 tips 原文</summary>
    public string prefix = "提示：";

    /// <summary>提示文案池（支持 UGUI Text 富文本标签；空串/空白行会被跳过）</summary>
    public List<string> tips = new List<string>();

    /// <summary>跨面板实例保留「上一条」下标：面板被销毁重建后也不会紧接着重复同一条</summary>
    private static int sLastIndex = -1;

    public int Count => tips?.Count ?? 0;

    /// <summary>
    /// 随机抽一条提示（自动拼上 prefix）。
    /// 池内只剩一条时必然重复；有多条时保证与上一条不同。
    /// </summary>
    /// <param name="emptyTip">完全没有可用配置时的兜底文案</param>
    public string PickRandom(string emptyTip = "")
    {
        if (tips == null || tips.Count == 0) return emptyTip;

        // 先收集有效下标（跳过空行），避免抽到空串导致"提示消失一次"
        var pool = new List<int>(tips.Count);
        for (int i = 0; i < tips.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(tips[i])) pool.Add(i);
        }
        if (pool.Count == 0) return emptyTip;

        int index = pool[Random.Range(0, pool.Count)];
        if (pool.Count > 1)
        {
            // 有重复风险时才重摇，最多 16 次，避免脏数据把这里拖成死循环
            int guard = 0;
            while (index == sLastIndex && guard++ < 16)
                index = pool[Random.Range(0, pool.Count)];
        }
        sLastIndex = index;

        return Compose(tips[index]);
    }

    /// <summary>把内容拼上统一前缀（prefix 为空则原样返回）</summary>
    public string Compose(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return string.IsNullOrEmpty(prefix) ? content : prefix + content;
    }
}
