using System.Collections.Generic;
using UnityEngine;

/// <summary>敌人最终属性倍率（各字段为乘法倍率；Tint &lt; 0 表示不改色）</summary>
public struct EnemyStatScale
{
    public float Hp;
    public float Damage;
    public float Speed;
    public float Exp;
    public float Drop;
    public float Size;
    public int Tint;

    public static EnemyStatScale One => new EnemyStatScale
    {
        Hp = 1f, Damage = 1f, Speed = 1f, Exp = 1f, Drop = 1f, Size = 1f, Tint = -1
    };
}

/// <summary>
/// 敌人属性解析器：把「副本难度倍率 × 作用域匹配的词缀修饰器」合成为一次乘法结果
/// 上下文取自 WaveManager（当前副本/阶段/波次），供 EnemySimple / DropManager 使用
/// </summary>
public static class EnemyStatResolver
{
    private static readonly List<EnemyStatModifier> sBuffer = new();

    /// <summary>解析指定敌人在"当前副本/阶段/波次"下的最终属性倍率</summary>
    public static EnemyStatScale Resolve(string enemyId)
    {
        var scale = EnemyStatScale.One;

        string dungeonId = WaveManager.HasInstance ? WaveManager.Instance.CurrentDungeonId : null;
        string phaseId = WaveManager.HasInstance ? WaveManager.Instance.CurrentPhaseId : null;
        int waveId = WaveManager.HasInstance ? WaveManager.Instance.CurrentGlobalWaveId : 0;

        // 1. 副本难度倍率
        var dungeon = GameDataMgr.Instance.GetDungeon(dungeonId);
        if (dungeon != null)
        {
            scale.Hp *= SafeScale(dungeon.enemyHpScale);
            scale.Damage *= SafeScale(dungeon.enemyDamageScale);
            scale.Speed *= SafeScale(dungeon.enemySpeedScale);
            scale.Exp *= SafeScale(dungeon.enemyExpScale);
            scale.Drop *= SafeScale(dungeon.dropRateScale);
        }

        // 2. 词缀修饰器（作用域匹配后叠加）
        sBuffer.Clear();
        GameDataMgr.Instance.EnemyModifiers?.Collect(dungeonId, phaseId, waveId, enemyId, sBuffer);
        foreach (var m in sBuffer)
        {
            scale.Hp *= SafeScale(m.hpScale);
            scale.Damage *= SafeScale(m.damageScale);
            scale.Speed *= SafeScale(m.speedScale);
            scale.Exp *= SafeScale(m.expScale);
            scale.Drop *= SafeScale(m.dropScale);
            if (m.sizeScale > 0f) scale.Size *= m.sizeScale;
            if (m.tintColor >= 0) scale.Tint = m.tintColor;
        }

        // 3. 波次成长：每经过一波敌人属性按固定倍率提升（"后期越硬越疼"的主旋钮）
        //    与副本难度、词缀修饰器相乘；waveStatGrowth 未配置时完全不影响旧配置
        var growth = GameDataMgr.Instance.Waves?.waveStatGrowth;
        if (growth != null && waveId > 0)
        {
            int startWave = growth.startWave > 0 ? growth.startWave : 1;
            int steps = waveId - startWave;
            if (steps > 0)
            {
                if (growth.hpPerWave > 0f) scale.Hp *= Mathf.Pow(growth.hpPerWave, steps);
                if (growth.damagePerWave > 0f) scale.Damage *= Mathf.Pow(growth.damagePerWave, steps);
                if (growth.expPerWave > 0f) scale.Exp *= Mathf.Pow(growth.expPerWave, steps);
            }
        }

        // 4. 无尽模式轮数成长：每打完一轮（全部阶段回绕一次）属性再叠加一层倍率
        //    第 1 轮（Cycle=0）为基准；与副本难度、词缀、波次成长相乘
        if (WaveManager.HasInstance && WaveManager.Instance.IsEndless)
        {
            var endless = GameDataMgr.Instance.GetEndless();
            int cycleSteps = endless?.GetGrowthSteps(WaveManager.Instance.Cycle) ?? 0;
            if (cycleSteps > 0)
            {
                var g = endless.Endless.growth;
                if (g.hpPerCycle > 1f) scale.Hp *= Mathf.Pow(g.hpPerCycle, cycleSteps);
                if (g.damagePerCycle > 1f) scale.Damage *= Mathf.Pow(g.damagePerCycle, cycleSteps);
                if (g.expPerCycle > 1f) scale.Exp *= Mathf.Pow(g.expPerCycle, cycleSteps);
                if (g.dropPerCycle > 1f) scale.Drop *= Mathf.Pow(g.dropPerCycle, cycleSteps);
            }
        }
        return scale;
    }

    /// <summary>倍率兜底：&lt;=0 视为 1，避免配置漏填导致属性归零</summary>
    private static float SafeScale(float v) => v > 0f ? v : 1f;
}
