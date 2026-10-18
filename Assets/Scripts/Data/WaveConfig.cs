using System.Collections.Generic;

/// <summary>
/// 波次刷怪组：一组相同敌人成批刷新
/// </summary>
public class WaveSpawnGroup
{
    public string enemyId;         // 要刷的敌人ID
    public int count;              // 该组敌人总数
    public float spawnInterval;    // 该组敌人之间的刷新间隔（秒；仅在没有 spawnPacing 配置时生效）
}

/// <summary>
/// 生成速度曲线（跨波次递进）：把「敌人来得多快」抽成显式变量，而非靠人数反推。
/// 线性段：rate = baseSpawnRate + (wave - firstWave) × spawnRateGainPerWave；
/// 渐进段：rate 超过 rateSoftCap 后，每波向 rateHardCap 补齐 rateTightenPerWave。
/// 于是前期每波明显变快，后期增速收敛且永不越过硬上限，兼顾性能与可读性。
/// </summary>
public class SpawnPacingConfig
{
    public float baseSpawnRate = 1f;            // 起始波次的生成速度（只/秒）
    public float spawnRateGainPerWave = 0f;     // 线性段：每波生成速度增量（只/秒）
    public float rateSoftCap = 0f;              // 软上限：达到后进入渐进段（<=0 = 不启用渐进段）
    public float rateTightenPerWave = 0.05f;    // 渐进段：每波补齐剩余差距的比例（0.05 = 5%）
    public float rateHardCap = 0f;              // 硬上限（<=0 = 不限）；对应最小间隔 1/rateHardCap
    public float firstWave = 1f;                // 曲线起始波次（waveId 小于它的按起始波处理）
}

/// <summary>
/// 波次属性成长：每经过一波，敌人属性按固定倍率提升（乘法叠加）。
/// 这是"后期怪更硬更疼"的主旋钮，与副本难度倍率、词缀修饰器相乘。
/// </summary>
public class WaveStatGrowthConfig
{
    public float hpPerWave = 1f;        // 每波血量倍率（1.045 = 每波 +4.5%）
    public float damagePerWave = 1f;    // 每波攻击倍率
    public float expPerWave = 1f;       // 每波经验倍率（一般保持 1，避免后期经验爆炸）
    public int startWave = 1;           // 从该波次开始生效（该波倍率为 1）
}

/// <summary>
/// 波次配置数据
/// 对应文档"波次随时间递增"：后期触发 BOSS
/// </summary>
public class WaveConfig
{
    public int waveId;                            // 波次序号（1,2,3...）
    public string description;                     // 波次描述（UI 显示）
    public List<WaveSpawnGroup> spawnGroups = new();  // 该波次的刷怪组（顺序执行）
    public float prepTime;                        // 波次开始前的准备时间（秒）
    public float spawnRadiusMin = 6f;             // 刷怪最小半径（距玩家）
    public float spawnRadiusMax = 9f;             // 刷怪最大半径（距玩家）
    public bool triggersBoss;                     // 是否在波次结束后触发 BOSS
    public string bossId;                         // 对应 BOSS 配置ID（triggersBoss=true 时必填）
    public int maxAlive;                          // 本波同屏存活上限（<=0 = 用全局值）
}

public class WaveConfigWrapper
{
    public int version = 1;
    public List<WaveConfig> Waves = new();

    /// <summary>生成速度曲线（可空：为空时沿用各 spawnGroup 的 spawnInterval）</summary>
    public SpawnPacingConfig spawnPacing;

    /// <summary>波次属性成长（可空：为空时敌人属性不随波次变化）</summary>
    public WaveStatGrowthConfig waveStatGrowth;

    /// <summary>全局同屏存活上限（本波 maxAlive <=0 时兜底；<=0 = 不限）</summary>
    public int maxAlive;

    private Dictionary<int, WaveConfig> _dict;

    public WaveConfig GetById(int waveId)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<int, WaveConfig>();
            foreach (var w in Waves)
                if (w != null && !_dict.ContainsKey(w.waveId))
                    _dict.Add(w.waveId, w);
        }
        return _dict.TryGetValue(waveId, out var c) ? c : null;
    }

    public WaveConfig GetLast()
    {
        if (Waves == null || Waves.Count == 0) return null;
        WaveConfig last = Waves[0];
        foreach (var w in Waves)
            if (w.waveId > last.waveId) last = w;
        return last;
    }

    public int Count => Waves?.Count ?? 0;

    /// <summary>
    /// 解析某波次的生成速度（只/秒）：线性段 → 软上限后渐进收紧 → 硬上限。
    /// 未配置 spawnPacing 时返回 0，调用方回退到 spawnGroup.spawnInterval。
    /// </summary>
    public float ResolveSpawnRate(int targetWaveId)
    {
        if (spawnPacing == null || spawnPacing.baseSpawnRate <= 0f) return 0f;

        var p = spawnPacing;
        float baseWave = p.firstWave > 0f ? p.firstWave : 1f;
        int steps = targetWaveId - (int)baseWave;
        if (steps < 0) steps = 0;

        float linear = p.baseSpawnRate + steps * p.spawnRateGainPerWave;

        // 软上限之后：每波只补齐剩余差距的一部分（渐进逼近硬上限）
        if (p.rateSoftCap > 0f && p.rateHardCap > p.rateSoftCap && linear > p.rateSoftCap)
        {
            int softStep = p.spawnRateGainPerWave > 0f
                ? UnityEngine.Mathf.CeilToInt((p.rateSoftCap - p.baseSpawnRate) / p.spawnRateGainPerWave)
                : 0;
            if (softStep < 0) softStep = 0;

            float rate = p.rateSoftCap;
            for (int k = softStep; k < steps; k++)
                rate += (p.rateHardCap - rate) * p.rateTightenPerWave;
            linear = rate;
        }

        if (p.rateHardCap > 0f) linear = UnityEngine.Mathf.Min(linear, p.rateHardCap);
        return linear > 0.01f ? linear : 0.01f;
    }

    /// <summary>解析某波次的刷怪间隔（秒）= 1 / 生成速度；未配置时返回 0</summary>
    public float ResolveSpawnInterval(int targetWaveId)
    {
        float rate = ResolveSpawnRate(targetWaveId);
        return rate > 0f ? 1f / rate : 0f;
    }

    /// <summary>解析某波次的同屏存活上限（<=0 = 不限）</summary>
    public int ResolveMaxAlive(int targetWaveId)
    {
        var w = GetById(targetWaveId);
        if (w != null && w.maxAlive > 0) return w.maxAlive;
        return maxAlive;
    }
}
