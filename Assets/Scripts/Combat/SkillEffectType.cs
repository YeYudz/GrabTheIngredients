/// <summary>
/// 特殊效果类型。原属已移除的技能 Build 体系，
/// 效果载体（EffectApplier / StatusEffect）保留复用，供菜品化学反应与装备效果使用。
/// </summary>
public enum SkillEffectType
{
    None,        // 无特殊效果
    Explosion,   // 爆炸：命中点 AOE 范围伤害
    Burn,        // 燃烧：目标持续掉血（DoT）
    Pierce       // 穿透：命中后不消失继续飞行（对应 Bullet.isPiercing）
}

/// <summary>
/// 特殊效果数据：由 SkillSystem 维护，传入子弹/效果应用器
/// </summary>
public struct SkillEffectData
{
    public SkillEffectType type;   // 效果类型
    public float param;            // 效果参数：爆炸=半径，燃烧=每跳伤害
    public float duration;         // 效果持续时间（燃烧=总时长；爆炸=0）

    public static readonly SkillEffectData None = new SkillEffectData { type = SkillEffectType.None };

    /// <summary>是否有生效效果</summary>
    public bool IsActive => type != SkillEffectType.None && param > 0f;
}
