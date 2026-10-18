using UnityEngine;
using YokiFrame;

/// <summary>
/// 局内加成系统：汇总烹饪 Buff 与局外永久 Buff 写入玩家属性，并维护特殊效果扩展点。
/// 原 3 选 1 技能体系已由 BuffUpgradeConfig 取代，此处仅保留加成聚合职责，由业务方直接调用。
/// </summary>
public class SkillSystem : MonoSingleton<SkillSystem>
{
    /// <summary>烹饪音游获得的伤害加成（Step5：菜品品质 Buff）</summary>
    public float CookingDamageBonus { get; private set; }

    /// <summary>当前生效的特殊效果（菜品化学反应强化，暂无技能来源时恒 None）</summary>
    public SkillEffectData ActiveEffect { get; private set; }

    /// <summary>菜品Buff对特殊效果的强化倍率（化学反应，如燃烧伤害×1.5）</summary>
    private float mEffectPowerBonus = 1f;

    /// <summary>其他系统（效果引擎/战场炒菜）追加的伤害加成；DamageBonus 由本类唯一写入，避免被重算打回</summary>
    public float ExtraDamageBonus { get; private set; }

    /// <summary>其他系统追加的攻击速度加成</summary>
    public float ExtraAttackSpeedBonus { get; private set; }

    /// <summary>追加伤害加成（负值回退）并立即重算</summary>
    public void AddExtraDamageBonus(float delta)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        ExtraDamageBonus += delta;
        RecalculatePlayerStats();
    }

    /// <summary>追加攻击速度加成（负值回退）并立即重算</summary>
    public void AddExtraAttackSpeedBonus(float delta)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        ExtraAttackSpeedBonus += delta;
        RecalculatePlayerStats();
    }

    /// <summary>重置（重开游戏时调用）</summary>
    public void Reset()
    {
        CookingDamageBonus = 0f;
        mEffectPowerBonus = 1f;
        ExtraDamageBonus = 0f;
        ExtraAttackSpeedBonus = 0f;
        ActiveEffect = SkillEffectData.None;
        RecalculatePlayerStats();
        LogKit.Log("[SkillSystem] 已重置");
    }

    /// <summary>公开重算玩家属性（进入游戏/应用永久Buff后调用）</summary>
    public void RefreshPlayerStats() => RecalculatePlayerStats();

    /// <summary>应用烹饪 Buff（由 BossBattleManager 在音游结束时调用）</summary>
    public void ApplyCookingBuff(float damageBonus)
    {
        CookingDamageBonus = damageBonus;
        RecalculatePlayerStats();
        LogKit.Log($"[SkillSystem] 烹饪 Buff 生效：伤害加成 +{damageBonus}");
    }

    /// <summary>应用烹饪特殊效果Buff（化学反应：强化燃烧/爆炸等效果）</summary>
    public void ApplyCookingEffectBuff(SkillEffectType effectType, float powerMultiplier)
    {
        // 只强化与当前效果类型匹配的加成（如燃烧菜品 → 强化燃烧效果）
        if (ActiveEffect.type == effectType)
        {
            mEffectPowerBonus = Mathf.Max(mEffectPowerBonus, powerMultiplier);
            RefreshActiveEffect();
            LogKit.Log($"[SkillSystem] 化学反应：{effectType} 效果强化 ×{powerMultiplier}");
        }
    }

    /// <summary>重新汇总特殊效果：应用菜品化学反应强化倍率</summary>
    private void RefreshActiveEffect()
    {
        if (!ActiveEffect.IsActive) return;

        // 属性返回 struct 副本，需取副本修改后写回
        var effect = ActiveEffect;
        effect.param *= mEffectPowerBonus;
        ActiveEffect = effect;
    }

    /// <summary>化学反应强化倍率（断点存档用）</summary>
    public float GetEffectPowerBonus() => mEffectPowerBonus;

    /// <summary>断点恢复：还原烹饪加成/特殊效果/化学反应倍率，并重算玩家属性</summary>
    public void RestoreState(float cookingDamageBonus, SkillEffectData effect, float effectPowerBonus)
    {
        CookingDamageBonus = cookingDamageBonus;
        ActiveEffect = effect;
        mEffectPowerBonus = effectPowerBonus;
        RecalculatePlayerStats();
        LogKit.Log($"[SkillSystem] 断点恢复：烹饪加成 {cookingDamageBonus}，效果 {effect.type}");
    }

    /// <summary>重算玩家属性：烹饪Buff + 局外永久Buff（Meta）+ 其他系统追加，写入玩家</summary>
    private void RecalculatePlayerStats()
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        float totalDamageBonus = 0f;
        float totalAttackSpeedBonus = 0f;

        // 局外永久Buff（Meta）加成
        if (MetaManager.HasInstance)
        {
            var meta = MetaManager.Instance.BuffSystem;
            totalDamageBonus += meta.DamageBonusPercent;
            totalAttackSpeedBonus += meta.AttackSpeedBonusPercent;
        }

        player.DamageBonus = totalDamageBonus + CookingDamageBonus + ExtraDamageBonus;

        // 攻速上下限安全阀：只截断写入面板的值，不动累加器（ExtraAttackSpeedBonus）
        // —— 这样限时增益到期按原始 delta 回退时不会产生"截断后减不回去"的漂移。
        // 下限用于兜住任何"扣减未配对"的异常累积（如历史上战场炒菜失败 debuff 叠加会把攻速拖到接近 0）。
        var cfg = GameDataMgr.Instance?.Player?.Player;
        float atkSpeedCap = cfg?.maxAttackSpeedBonus ?? 1.2f;
        float atkSpeedFloor = cfg?.minAttackSpeedBonus ?? -0.6f;
        player.AttackSpeedBonus = Mathf.Clamp(
            totalAttackSpeedBonus + ExtraAttackSpeedBonus, atkSpeedFloor, atkSpeedCap);
    }
}
