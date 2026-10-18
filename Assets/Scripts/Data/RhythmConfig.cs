using System.Collections.Generic;

/// <summary>
/// 音游难度档：按关卡递进选用，倍率作用于音游参数
/// </summary>
public class RhythmDifficulty
{
    public string id;                        // 唯一ID（easy/normal/hard/hell）
    public string name;                      // 显示名
    public float beatWindowScale = 1f;       // 判定窗口倍率（越大越宽松）
    public float bpmScale = 1f;              // 速度倍率（越大音符越密）
    public float fallDurationScale = 1f;     // 下落时间倍率（越大越好读）
}

/// <summary>
/// 音游全局配置：难度分级（按关卡递进选用）
/// 判定窗口/下落时间/音符密度的"基准值"分别来自 BossConfig.beatWindowMs、RecipeConfig.fallDuration、RecipeConfig.bpm；
/// 本配置只提供难度倍率，不重复定义基准值
/// </summary>
public class RhythmConfig
{
    /// <summary>关卡未指定难度时使用的档位ID</summary>
    public string defaultDifficultyId = "normal";

    /// <summary>难度档列表（按关卡递进选用）</summary>
    public List<RhythmDifficulty> difficulties = new();
}

public class RhythmConfigWrapper
{
    public int version = 1;
    public RhythmConfig Rhythm = new();

    /// <summary>按ID取难度档（未找到返回 null）</summary>
    public RhythmDifficulty GetDifficulty(string id)
    {
        if (Rhythm?.difficulties == null || string.IsNullOrEmpty(id)) return null;
        foreach (var d in Rhythm.difficulties)
            if (d != null && d.id == id) return d;
        return null;
    }
}
