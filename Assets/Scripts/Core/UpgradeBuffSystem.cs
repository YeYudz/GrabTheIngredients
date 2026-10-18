using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 局内升级 Buff 系统：把 3 选 1 的 BuffUpgradeConfig 应用到玩家。
/// 采用 Modifier 模式——不直接累加玩家属性，而是累积百分比 / 固定值修饰器，
/// 每次应用后由 StatCalculator 基于基础属性重算并写回，避免多次加成相互污染。
/// </summary>
public class UpgradeBuffSystem : MonoSingleton<UpgradeBuffSystem>
{
    /// <summary>单属性 Modifier 累计（百分比 / 固定值）</summary>
    private struct StatModifier
    {
        public float Flat;
        public float Percent;
    }

    /// <summary>各属性类型的 Modifier 累计</summary>
    private readonly Dictionary<UpgradeBuffType, StatModifier> mModifiers = new Dictionary<UpgradeBuffType, StatModifier>();

    /// <summary>本局已选的复杂增益效果ID（按选择顺序，断点恢复时重放）</summary>
    private readonly List<string> mSelectedEffectIds = new();

    /// <summary>本局已获得的增益卡ID（按获取顺序，可重复；状态面板卡片墙展示）</summary>
    private readonly List<string> mSelectedBuffIds = new();

    /// <summary>当前已写入玩家的升级加成（重算时先移除）</summary>
    private PlayerAttribute mLastBonus;

    /// <summary>是否已写入过（首次无需移除）</summary>
    private bool mHasApplied;

    /// <summary>应用一个升级 Buff（由 LevelUpManager 在玩家选择后调用）</summary>
    public void ApplyBuff(string buffId)
    {
        if (string.IsNullOrEmpty(buffId)) return;

        var config = GameDataMgr.Instance.GetBuffUpgrade(buffId);
        if (config == null)
        {
            LogKit.Error($"[UpgradeBuffSystem] 找不到升级Buff配置: {buffId}");
            return;
        }

        var player = GameManager.Instance?.Player;
        if (player == null) return;

        // 记录增益卡（每次获取一条，状态面板卡片墙展示）
        mSelectedBuffIds.Add(buffId);

        // 复杂增益（效果引擎）：转发给 PlayerEffectController，并记录 ID 以便断点恢复重放
        if (config.buffType == UpgradeBuffType.Effect)
        {
            ApplyComplexEffect(config, player);
            LogKit.Log($"[UpgradeBuffSystem] 升级Buff生效(复杂): {config.buffName} ({config.description})");
            return;
        }

        // 回血 / 拾取倍率：即时生效，不进入 Modifier
        if (config.buffType == UpgradeBuffType.Heal)
        {
            player.Health?.Heal(config.value);
            LogKit.Log($"[UpgradeBuffSystem] 升级Buff生效: {config.buffName} ({config.description})");
            return;
        }
        if (config.buffType == UpgradeBuffType.DropMultiplier)
        {
            if (DropManager.HasInstance)
                DropManager.Instance.DropMultiplierBonus += config.value;
            LogKit.Log($"[UpgradeBuffSystem] 升级Buff生效: {config.buffName} ({config.description})");
            return;
        }

        // 属性类：累计 Modifier（百分比/固定值语义化）
        Accumulate(config.buffType, config);

        // 基于基础属性重算最终加成并写回
        ReapplyModifiers(player, config.buffType);

        LogKit.Log($"[UpgradeBuffSystem] 升级Buff生效: {config.buffName} ({config.description})");
    }

    /// <summary>
    /// 应用复杂增益：引用 GameEffectConfig 效果库
    /// - Passive(1)：直接装备为整局常驻
    /// - LowHp(2)：挂载为条件触发（带冷却）
    /// - None(0)：选择瞬间结算一次（如立即获得护盾）
    /// </summary>
    private void ApplyComplexEffect(BuffUpgradeConfig config, PlayerController player)
    {
        if (string.IsNullOrEmpty(config.effectId))
        {
            LogKit.Error($"[UpgradeBuffSystem] {config.id} 标记为复杂增益但未配置 effectId");
            return;
        }

        var effect = GameDataMgr.Instance.GetEffect(config.effectId);
        if (effect == null)
        {
            LogKit.Error($"[UpgradeBuffSystem] 找不到效果配置: {config.effectId}");
            return;
        }

        PlayerEffectController.GetOrAdd(player)?.ApplyEffect(effect);
        // 记录选择次数（同一效果卡可被多次选择，断点恢复按次数重放）
        mSelectedEffectIds.Add(config.effectId);
    }

    /// <summary>本局已选的复杂增益效果ID快照（断点存档用）</summary>
    public List<string> GetSelectedEffectIds() => new List<string>(mSelectedEffectIds);

    /// <summary>本局已获得的增益卡ID快照（状态面板展示）</summary>
    public List<string> GetSelectedBuffIds() => new List<string>(mSelectedBuffIds);

    /// <summary>断点恢复：还原增益卡ID历史（仅展示用）</summary>
    public void RestoreSelectedBuffIds(List<string> ids)
    {
        mSelectedBuffIds.Clear();
        if (ids != null) mSelectedBuffIds.AddRange(ids);
    }

    /// <summary>累计单个属性的 Modifier</summary>
    private void Accumulate(UpgradeBuffType type, BuffUpgradeConfig config)
    {
        StatModifier m = mModifiers.TryGetValue(type, out var existing) ? existing : default;
        if (config.valueType == BuffValueType.Percent)
            m.Percent += config.value;
        else
            m.Flat += config.value;
        mModifiers[type] = m;
    }

    /// <summary>基于玩家基础属性重算全部升级加成并写回（先移除旧值，再加新值）</summary>
    private void ReapplyModifiers(PlayerController player, UpgradeBuffType changedType)
    {
        // 1. 移除旧的升级加成
        if (mHasApplied)
        {
            player.RemoveBonusAttribute(mLastBonus);
        }

        // 2. 基于基础属性计算最终加成（增量 = FinalValue(base) - base）
        var b = player.BaseAttr;
        var bonus = new PlayerAttribute
        {
            Attack = Delta(UpgradeBuffType.Attack, b.Attack),
            MaxHp = Delta(UpgradeBuffType.MaxHp, b.MaxHp),
            CritChance = Delta(UpgradeBuffType.CritChance, b.CritChance),
            CritDamage = Delta(UpgradeBuffType.CritDamage, b.CritDamage),
            MoveSpeed = Delta(UpgradeBuffType.MoveSpeed, b.MoveSpeed),
            PhysDamageBonus = Delta(UpgradeBuffType.PhysDamage, b.PhysDamageBonus),
            ElemDamageBonus = Delta(UpgradeBuffType.ElemDamage, b.ElemDamageBonus),
            DodgeChance = Delta(UpgradeBuffType.Dodge, b.DodgeChance)
        };

        // 3. 写回玩家
        player.AddBonusAttribute(bonus);
        mLastBonus = bonus;
        mHasApplied = true;

        // 4. 血量上限变化 → 重新 Init + 通知 HUD
        if (changedType == UpgradeBuffType.MaxHp)
        {
            player.Health?.Init(player.MaxHp);
            EventKit.Type.Send(new GameEvents.PlayerDamaged
            {
                Damage = 0f,
                CurrentHp = player.Health != null ? player.Health.CurrentHp : player.MaxHp,
                MaxHp = player.MaxHp
            });
        }
    }

    /// <summary>某属性的固定值累计</summary>
    private float FlatOf(UpgradeBuffType type)
    {
        return mModifiers.TryGetValue(type, out var m) ? m.Flat : 0f;
    }

    /// <summary>某属性的百分比累计</summary>
    private float PercentOf(UpgradeBuffType type)
    {
        return mModifiers.TryGetValue(type, out var m) ? m.Percent : 0f;
    }

    /// <summary>通过增益效果加成计算器计算某属性相对基础值的加成增量（内部沿用 StatCalculator）</summary>
    private float Delta(UpgradeBuffType type, float baseValue)
    {
        // 暴击率/暴击伤害：百分比/固定值均直接加数值，不做 base 缩放（需求：加多少百分比就是加多少）
        if (type == UpgradeBuffType.CritChance || type == UpgradeBuffType.CritDamage)
            return PercentOf(type) + FlatOf(type);
        return BuffEffectBonusCalculator.AttributeGainDelta(baseValue, PercentOf(type), FlatOf(type));
    }

    /// <summary>断点存档：导出 Modifier 快照（升级Buff）</summary>
    public List<ModifierSaveData> GetModifiers()
    {
        var list = new List<ModifierSaveData>();
        foreach (var kv in mModifiers)
        {
            list.Add(new ModifierSaveData
            {
                type = (int)kv.Key,
                flat = kv.Value.Flat,
                percent = kv.Value.Percent
            });
        }
        return list;
    }

    /// <summary>
    /// 当前已写入玩家的升级加成（断点存档时从 BonusAttr 中剔除，避免恢复时双算）
    /// 含复杂增益写入 BonusAttr 的属性部分；伤害/攻速走 SkillSystem 独立通道，本就不在快照内
    /// </summary>
    public PlayerAttribute GetLastBonus()
    {
        var total = mLastBonus;
        var player = GameManager.Instance?.Player;
        var ctrl = player != null ? player.GetComponent<PlayerEffectController>() : null;
        if (ctrl != null) total = total + ctrl.GetTotalAttributeBonus();
        return total;
    }

    /// <summary>是否已写入过升级加成</summary>
    public bool HasAppliedBonus => mHasApplied;

    /// <summary>断点恢复：还原 Modifier 快照 + 重放复杂增益，并基于基础属性重算写回玩家（回血/拾取倍率不在此列）</summary>
    public void RestoreModifiers(List<ModifierSaveData> data, List<string> effectIds = null)
    {
        mModifiers.Clear();
        if (data != null)
        {
            foreach (var d in data)
            {
                if (d == null) continue;
                mModifiers[(UpgradeBuffType)d.type] = new StatModifier { Flat = d.flat, Percent = d.percent };
            }
        }

        var player = GameManager.Instance?.Player;
        if (player == null) return;

        // changedType 用非 MaxHp 类型，避免重复初始化血量（血量由断点单独恢复）
        ReapplyModifiers(player, UpgradeBuffType.Attack);

        // 复杂增益重放（只保留本局常驻/条件触发的；限时增益不入快照，符合"回到波次开头"语义）
        mSelectedEffectIds.Clear();
        if (effectIds != null && effectIds.Count > 0)
        {
            var ctrl = PlayerEffectController.GetOrAdd(player);
            foreach (var id in effectIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                var effect = GameDataMgr.Instance.GetEffect(id);
                if (effect == null)
                {
                    LogKit.Warning($"[UpgradeBuffSystem] 断点恢复时找不到效果配置: {id}");
                    continue;
                }
                ctrl.ApplyEffect(effect);
                mSelectedEffectIds.Add(id);
            }
        }

        LogKit.Log($"[UpgradeBuffSystem] 断点恢复：{mModifiers.Count} 项升级Buff + {mSelectedEffectIds.Count} 个复杂增益");
    }

    /// <summary>重置（重开游戏/回主菜单时调用）</summary>
    public void Reset()
    {
        mModifiers.Clear();
        mSelectedEffectIds.Clear();
        mSelectedBuffIds.Clear();
        mLastBonus = default;
        mHasApplied = false;
        LogKit.Log("[UpgradeBuffSystem] 已重置");
    }
}
