using System.Collections.Generic;

/// <summary>
/// 敌人属性修饰器（词缀）：按作用域过滤后对属性做调整，作用域字段为空 / 0 表示不限，多个叠加生效。
/// 最终属性 = EnemyConfig 基础值 × 关卡难度倍率 × 命中的全部修饰器。
/// </summary>
public class EnemyStatModifier
{
    public string modifierId;               // 唯一ID
    public string modifierName;             // 显示名

    // ===== 作用域（空/0 = 不限，可任意组合） =====
    public string dungeonId;                // 限定副本（引用 DungeonConfig.dungeonId）
    public string phaseId;                  // 限定阶段（引用 DungeonPhase.phaseId）
    public int waveId;                      // 限定波次（0=不限；此处为全局 waveId）
    public string enemyId;                  // 限定怪物类型（引用 EnemyConfig.id）

    // ===== 属性倍率 =====
    public float hpScale = 1f;
    public float damageScale = 1f;
    public float speedScale = 1f;
    public float expScale = 1f;
    public float dropScale = 1f;

    // ===== 视觉 =====
    public float sizeScale = 1f;            // 体型倍率
    public int tintColor = -1;              // 颜色索引，-1=不改色
}

public class EnemyStatModifierWrapper
{
    public int version = 1;
    public List<EnemyStatModifier> Modifiers = new();

    /// <summary>收集匹配指定作用域的全部修饰器（结果写入 result，调用方负责清空）</summary>
    public void Collect(string dungeonId, string phaseId, int waveId, string enemyId, List<EnemyStatModifier> result)
    {
        if (Modifiers == null || result == null) return;
        foreach (var m in Modifiers)
        {
            if (m == null) continue;
            if (!string.IsNullOrEmpty(m.dungeonId) && m.dungeonId != dungeonId) continue;
            if (!string.IsNullOrEmpty(m.phaseId) && m.phaseId != phaseId) continue;
            if (m.waveId > 0 && m.waveId != waveId) continue;
            if (!string.IsNullOrEmpty(m.enemyId) && m.enemyId != enemyId) continue;
            result.Add(m);
        }
    }

    public int Count => Modifiers?.Count ?? 0;
}
