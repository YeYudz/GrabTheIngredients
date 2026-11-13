using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 无尽模式轮数成长：每打完一轮（全部阶段回绕一次）敌人属性/BOSS 血量按固定倍率提升（乘法叠加）。
/// 与副本难度倍率、词缀修饰器、波次成长相乘；倍率 &lt;=1 视为不成长。
/// </summary>
public class EndlessCycleGrowth
{
    public float hpPerCycle = 1f;         // 每轮血量倍率（1.15 = 每轮 +15%）
    public float damagePerCycle = 1f;     // 每轮攻击倍率
    public float expPerCycle = 1f;        // 每轮经验倍率（一般保持 1，避免后期等级爆炸）
    public float dropPerCycle = 1f;       // 每轮掉落倍率
    public float bossHpPerCycle = 1f;     // 每轮 BOSS 血量倍率（叠加在 phase.bossHpScale 之上）
    public int startCycle = 1;            // 首次开始叠加倍率的轮数（0 起；默认 1 = 第 2 轮起每轮递增，第 1 轮为基准）
}

/// <summary>
/// 无尽模式配置：波次无限循环的试炼副本。
/// 对应 DungeonConfig 中 endless=true 的条目；本配置缺失时按非无尽行为处理。
/// </summary>
public class EndlessConfig
{
    public string dungeonId = "dungeon_endless";       // 无尽副本ID（须与 DungeonConfig 条目一致）
    public EndlessCycleGrowth growth = new();           // 轮数成长倍率
    public List<string> bossRotation = new();           // BOSS 轮换池（按 轮数+阶段索引 取模轮换；空 = 用阶段配置的 bossId）
    public List<string> rhythmDifficulties = new();     // 音游难度档随轮递进（按轮取值，越界取最后一档；空 = 用副本 rhythmDifficultyId）
}

public class EndlessConfigWrapper
{
    public int version = 1;
    public EndlessConfig Endless = new();

    /// <summary>
    /// 计算轮数成长幂次数（0 = 无成长）。cycle 从 0 起（第 1 轮 = 0）。
    /// startCycle=1 时：cycle 0 → 0 步（基准），cycle 1 → 1 步，cycle 2 → 2 步。
    /// </summary>
    public int GetGrowthSteps(int cycle)
    {
        var g = Endless?.growth;
        if (g == null || cycle <= 0) return 0;
        int start = g.startCycle > 0 ? g.startCycle : 1;
        int steps = cycle - start + 1;
        return steps > 0 ? steps : 0;
    }

    /// <summary>指定轮数的 BOSS 血量倍率（1 = 无加成）</summary>
    public float GetBossHpFactor(int cycle)
    {
        var g = Endless?.growth;
        if (g == null || g.bossHpPerCycle <= 1f) return 1f;
        int steps = GetGrowthSteps(cycle);
        return steps > 0 ? Mathf.Pow(g.bossHpPerCycle, steps) : 1f;
    }

    /// <summary>
    /// 指定轮数的音游难度档ID；未配置/越界返回 null，调用方回退副本 rhythmDifficultyId。
    /// </summary>
    public string GetRhythmDifficultyId(int cycle)
    {
        var list = Endless?.rhythmDifficulties;
        if (list == null || list.Count == 0) return null;
        return list[Mathf.Clamp(cycle, 0, list.Count - 1)];
    }
}
