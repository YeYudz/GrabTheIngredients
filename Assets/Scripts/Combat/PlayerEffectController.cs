using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 玩家效果控制器：复杂增益引擎的核心，把 GameEffectConfig 的触发器与行为落到玩家身上。
/// 设计为「触发器 × 行为」的正交组合——十余种触发（被动/低血/命中/击杀/暴击/闪避/技能/受击/周期）
/// 与数十种行为自由搭配，新增玩法只需扩配置，不必为每个效果写专用代码。
/// 事件统一经 EventKit 订阅，副作用收敛在本类，避免散落到各系统。
/// </summary>
public class PlayerEffectController : MonoBehaviour
{
    private enum ModKind { DamageField, AttackSpeedField, Attribute, MaxHp, Shield }
    private enum StackKind { MaxHp, Defend, MoveSpeed, AttackSpeed, CooldownReduction, FlatMoveSpeed }

    /// <summary>活跃的临时/衰减修改（到期还原或线性衰减到0）</summary>
    private class ActiveModifier
    {
        public ModKind Kind;
        public EffectActionType ActionType;   // Attribute 类时用于换算 PlayerAttribute
        public float CurrentValue;            // 当前已应用值（还原用）
        public float Remaining;               // 剩余时长
        public float DecayPerSecond;          // 衰减速率（decay=true 时）
        public Coroutine Routine;
        public string SourceName;             // 来源效果名（UI 显示；空则按行为类型兜底）
        public string SourceDesc;             // 来源效果描述（UI 显示；空则按当前值生成）
        public bool Debuff;                   // 是否为减益（按初始值正负判定）
    }

    /// <summary>条件/事件触发效果（带冷却）</summary>
    private class RuntimeEffect
    {
        public GameEffectConfig Config;
        public float CooldownRemain;
        public float PeriodicTimer;
    }

    /// <summary>层数叠层状态（按 action 维度独立，从所属 effect 的 action 查询配置）</summary>
    private class StackState
    {
        public int Count;
        public float LoseDurationRemain;      // stackRefreshDuration 到期丢失
    }

    private PlayerController mPlayer;
    private Health mHealth;
    private float mShield;
    private PlayerAttribute mAttrTotal;   // 本控制器写入 BonusAttr 的属性类累计（断点剔除用）
    private float mImmunityRemain;        // 免疫伤害剩余秒
    private int mBlockCharges;            // 格挡下一次攻击的层数

    private readonly List<ActiveModifier> mMods = new();
    private readonly List<RuntimeEffect> mConditional = new();
    private readonly Dictionary<EffectActionData, StackState> mStacks = new();

    /// <summary>层数到期回收缓冲（复用，避免 Update 每帧复制整个字典）</summary>
    private readonly List<EffectActionData> mStackReleaseBuffer = new();

    /// <summary>层数键快照缓冲（复用，避免事件触发时 new List）</summary>
    private readonly List<EffectActionData> mStackKeyBuffer = new();

    /// <summary>受击掉层快照缓冲（独立于 mStackKeyBuffer：防事件链重入）</summary>
    private readonly List<EffectActionData> mStackLossBuffer = new();

    /// <summary>命中低血目标额外伤害：每目标上次触发时间（pepper 被动，baseValue=冷却秒）</summary>
    private readonly Dictionary<GameObject, float> mHitLowHpLastTime = new();

    /// <summary>大厨被动（AttackOfMoveSpeed）动态同步状态：null=未装备该被动</summary>
    private EffectActionData mMoveSpeedToAttackAction;
    private float mMoveSpeedToAttackApplied;   // 当前已写入 BonusAttr.Attack 的加成量
    private float mMoveSpeedToAttackLastSpeed; // 上次求值时的玩家总移速（变化检测）

    // ===== 被动查询累计（由带冷却/直接行为写入，供战斗管线读取） =====
    private float mCooldownReduction;
    private float mDodgeCooldownReduction;
    private float mLifeSteal;
    private float mSkillDamageMultiplier;
    private float mManaCostMultiplier;
    private float mManaRestoreOnHit;
    private float mHealShieldStrength;

    /// <summary>当前护盾值</summary>
    public float Shield => mShield;

    /// <summary>免疫伤害剩余秒（断点存档用）</summary>
    public float ImmunityRemain => mImmunityRemain;

    /// <summary>格挡剩余层数（断点存档用）</summary>
    public int BlockCharges => mBlockCharges;

    /// <summary>断点恢复：写回护盾/免疫/格挡（纯瞬态防御状态，不参与属性重算）</summary>
    public void RestoreTransientDefense(float shield, float immunityRemain, int blockCharges)
    {
        mShield = Mathf.Max(0f, shield);
        mImmunityRemain = Mathf.Max(0f, immunityRemain);
        mBlockCharges = Mathf.Max(0, blockCharges);
    }

    /// <summary>最终伤害乘区（1 + 加成）</summary>
    public float FinalDamageMultiplier { get; private set; } = 1f;

    /// <summary>防御穿透（无视目标防御比例，0~1）</summary>
    public float DefensePenetration { get; private set; }

    /// <summary>命中附加伤害（来自 buff8 最大生命% / buff3 移速差 / buff209）</summary>
    public float GetOnHitBonus(float damage, bool isCrit, EnemySimple target, BossHealth boss)
        => mOnHitBonusOnHit;

    // ===== 处决（buff208）参数 =====
    private float mExecuteHpPercent;      // 目标生命低于（目标最大生命×此% + 攻击×att%）则处决
    private float mExecuteAttackPercent;  // 处决阈值中的攻击系数
    private float mExecuteHealPercent;    // 处决成功回复玩家最大生命%

    /// <summary>
    /// 结算一次玩家对目标命中（把 3选1 增益接入伤害管线）：
    /// 应用 最终伤害乘区(FinalDamageMultiplier) → 命中附加(OnHitBonus) → 处决(buff208) → 吸血 → 伤害转治疗(buff212)。
    /// 返回最终应造成的伤害；处决命中时返回足以击杀的数值并回复玩家生命。
    /// </summary>
    public float ResolveHit(float baseDamage, bool isCrit, EnemySimple enemy, BossHealth boss)
    {
        float damage = Mathf.Max(0f, baseDamage);
        if (FinalDamageMultiplier > 1f) damage *= FinalDamageMultiplier;
        if (mOnHitBonusOnHit > 0f) damage += mOnHitBonusOnHit;
        damage = Mathf.Max(1f, damage);

        // 处决（buff208）：仅对普通敌人，目标当前血量 ≤ 阈值 → 直接击杀并回血
        if (enemy != null && !enemy.IsDead && (mExecuteHpPercent > 0f || mExecuteAttackPercent > 0f))
        {
            float threshold = enemy.MaxHpValue * mExecuteHpPercent + mPlayer.Attack * mExecuteAttackPercent;
            if (enemy.CurrentHp <= threshold)
            {
                if (mExecuteHealPercent > 0f && mPlayer != null)
                    mPlayer.Health?.Heal(mPlayer.MaxHp * mExecuteHealPercent);
                LogKit.Log($"[效果] 处决！目标 {enemy.name}（HP {enemy.CurrentHp:F0} ≤ {threshold:F0}）");
                return Mathf.Max(enemy.CurrentHp + 1f, damage);
            }
        }

        // 伤害转治疗（buff212 满层后）
        if (mDamageConvertToHeal > 0f && mPlayer != null)
            mPlayer.Health?.Heal(damage * mDamageConvertToHeal);

        // 吸血（buff9/buff100/buff200）
        if (mLifeSteal > 0f && mPlayer != null)
            mPlayer.Health?.Heal(damage * mLifeSteal);

        return damage;
    }

    /// <summary>技能是否被禁用（buff200）</summary>
    public bool ActiveSkillsDisabled { get; private set; }

    /// <summary>技能伤害乘区（buff107/200，1+加成）</summary>
    public float SkillDamageMultiplier => 1f + mSkillDamageMultiplier;

    /// <summary>法力消耗乘区（buff107，1x = 正常）</summary>
    public float ManaCostMultiplier => 1f + mManaCostMultiplier;

    /// <summary>
    /// 技能冷却缩减（buff109 20% / buff203 30% / buff101 层数型，0.2 = -20% 冷却）。
    /// 实时查询：层数型（101）会随击杀叠层、受击掉层，不能缓存。
    /// 消费方 ActiveSkillSystem.ResolveCooldown。
    /// </summary>
    public float CooldownReduction => Mathf.Clamp01(mCooldownReduction);

    /// <summary>
    /// 闪避冷却缩减（buff200 35% / buff207 50%，0.35 = -35% 冷却）。与技能冷却缩减是**独立**的通道。
    /// 消费方 DodgeSkill.SetCooldownReduction（那边夹到 [0, 0.9]），此处同口径夹一次，
    /// 保证 UI 显示值 = 实际生效值。
    /// </summary>
    public float DodgeCooldownReduction =>
        Mathf.Clamp(mDodgeCooldownReduction, 0f, DodgeSkill.MaxCooldownReduction);

    /// <summary>拾取倍数（Update 查询用）</summary>
    public Dictionary<string, float> DropMultipliers { get; } = new();

    private float mOnHitBonusOnHit;   // 每命中附加伤害

    /// <summary>暴击飞弹行为（passive 挂载，随 OnCrit 触发；buff102）</summary>
    private readonly List<EffectActionData> mOnCritProjectiles = new();

    private void Awake()
    {
        mPlayer = GetComponent<PlayerController>();
        mHealth = mPlayer != null ? mPlayer.Health : GetComponent<Health>();
        if (mHealth != null && mHealth.DamageAbsorber == null)
        {
            mHealth.DamageAbsorber = AbsorbDamage;
        }
        RegisterEvents();
    }

    private void OnDestroy()
    {
        if (mHealth != null && mHealth.DamageAbsorber == AbsorbDamage)
        {
            mHealth.DamageAbsorber = null;
        }
        ClearAllMods();
    }

    private void RegisterEvents()
    {
        var go = gameObject;
        EventKit.Type.Register<GameEvents.DamageDealt>(OnDamageDealt).UnRegisterWhenGameObjectDestroyed(go);
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled).UnRegisterWhenGameObjectDestroyed(go);
        EventKit.Type.Register<GameEvents.DodgePerformed>(OnDodgePerformed).UnRegisterWhenGameObjectDestroyed(go);
        EventKit.Type.Register<GameEvents.ActiveSkillReleased>(OnSkillReleased).UnRegisterWhenGameObjectDestroyed(go);
        EventKit.Type.Register<GameEvents.PlayerDamaged>(OnPlayerDamaged).UnRegisterWhenGameObjectDestroyed(go);
    }

    // ===== 对外接口 =====

    public void ApplyEffect(GameEffectConfig cfg, float multiplier = 1f)
    {
        if (cfg == null) return;

        switch ((EffectTriggerType)cfg.trigger)
        {
            case EffectTriggerType.Passive: ApplyPassive(cfg); break;
            case EffectTriggerType.None:
            case EffectTriggerType.OnBuffGain: ExecuteActions(cfg, multiplier); break;
            case EffectTriggerType.LowHp:
            case EffectTriggerType.OnHit:
            case EffectTriggerType.OnKill:
            case EffectTriggerType.OnCrit:
            case EffectTriggerType.OnDodge:
            case EffectTriggerType.OnSkill:
            case EffectTriggerType.OnDamaged:
            case EffectTriggerType.Periodic:
                mConditional.Add(new RuntimeEffect { Config = cfg, CooldownRemain = 0f, PeriodicTimer = cfg.triggerParam <= 0f ? 5f : cfg.triggerParam });
                LogKit.Log($"[效果] 已挂载触发器 {cmdTrigName((EffectTriggerType)cfg.trigger)}：{cfg.effectName}");
                break;
            default:
                LogKit.Warning($"[效果] {cfg.effectName} 触发器 {cfg.trigger} 未实现");
                break;
        }
    }

    private static string cmdTrigName(EffectTriggerType t) => t.ToString();

    /// <summary>清空全部效果</summary>
    public void ResetEffects()
    {
        ClearAllMods();
        mConditional.Clear();
        mStacks.Clear();
        mShield = 0f;
        mAttrTotal = default;
        mCooldownReduction = 0f;
        mDodgeCooldownReduction = 0f;
        mLifeSteal = 0f;
        mSkillDamageMultiplier = 0f;
        mManaCostMultiplier = 0f;
        mManaRestoreOnHit = 0f;
        mHealShieldStrength = 0f;
        FinalDamageMultiplier = 1f;
        DefensePenetration = 0f;
        ActiveSkillsDisabled = false;
        mImmunityRemain = 0f;
        mBlockCharges = 0;
        mExecuteHpPercent = 0f;
        mExecuteAttackPercent = 0f;
        mExecuteHealPercent = 0f;
        mDamageConvertToHeal = 0f;
        mOnCritProjectiles.Clear();
        mOnHitBonusOnHit = 0f;
        mHitLowHpLastTime.Clear();
        mMoveSpeedToAttackAction = null;
        mMoveSpeedToAttackApplied = 0f;
        mMoveSpeedToAttackLastSpeed = 0f;
    }

    // ===== 被动（常驻整局） =====

    private void ApplyPassive(GameEffectConfig cfg)
    {
        foreach (var a in cfg.actions)
        {
            if (a == null) continue;
            float value = EffectiveValue(a, 1f);
            ApplyActionPassiveSoft(a, value);

            // 层数型被动：注册空层状态，事件驱动叠层（OnKill/OnHit/OnCrit）
            if (a.stackGainPerTrigger > 0 && !mStacks.ContainsKey(a))
            {
                var st = new StackState();
                if (a.stackRefreshDuration > 0f) st.LoseDurationRemain = a.stackRefreshDuration;
                mStacks[a] = st;
            }
            // 暴击飞弹型被动（buff102）：挂载到 OnCrit，随命中触发
            if ((EffectActionType)a.action == EffectActionType.OnCritProjectile && !mOnCritProjectiles.Contains(a))
            {
                mOnCritProjectiles.Add(a);
            }
        }
        LogKit.Log($"[效果] 被动已装备：{cfg.effectName}");
    }

    /// <summary>被动行为的软应用（写入累计字段 / 属性 / 层数配置）</summary>
    private void ApplyActionPassiveSoft(EffectActionData a, float value)
    {
        var type = (EffectActionType)a.action;
        float pv = BuffEffectBonusCalculator.PercentValue(a);
        switch (type)
        {
            // 属性类 → 走 BonusAttr（公式统一交给 BuffEffectBonusCalculator 计算）
            case EffectActionType.DamageBonus:
            case EffectActionType.AttackOfMaxHp:
            case EffectActionType.AttackOfCurrentAttack:
            case EffectActionType.AttackOfMoveSpeed:
                {
                    if (type == EffectActionType.AttackOfMoveSpeed)
                    {
                        // 大厨被动：攻击加成 = 基础攻击 × 当前总移速 × 百分比，且随移速成长动态同步
                        // （原实现按基础移速一次性快照，局内移速提升不会转化为攻击）
                        mMoveSpeedToAttackAction = a;
                        float percent = BuffEffectBonusCalculator.PercentValue(a);
                        float bonus = mPlayer.BaseAttr.Attack * mPlayer.MoveSpeed * percent;
                        mMoveSpeedToAttackApplied = bonus;
                        mMoveSpeedToAttackLastSpeed = mPlayer.MoveSpeed;
                        mPlayer.AddBonusAttribute(new PlayerAttribute { Attack = bonus });
                        AddAttrTotal(type, bonus);
                        LogKit.Log($"[效果] 厨神之心：移速 {mPlayer.MoveSpeed:F1} → 攻击加成 +{bonus:F2}");
                    }
                    else
                    {
                        var delta = BuffEffectBonusCalculator.AttributeDelta(a, CurrentContext(), value);
                        mPlayer.AddBonusAttribute(delta);
                        AddAttrTotal(type, delta.Attack);
                    }
                    break;
                }
            case EffectActionType.MaxHpBonus:
                if (value != 0f)
                {
                    if (value > 0f) mHealth?.AddMaxHp(value);
                    else mHealth?.RemoveMaxHp(-value);
                }
                mPlayer.RefreshToMaxHp();
                AddAttrTotal(type, value);
                break;
            case EffectActionType.MoveSpeedBonus:
            case EffectActionType.CritChanceBonus:
            case EffectActionType.CritDamageBonus:
            case EffectActionType.PhysDamageBonus:
            case EffectActionType.ElemDamageBonus:
                mPlayer.AddBonusAttribute(BuffEffectBonusCalculator.MapToAttribute(type, value)); AddAttrTotal(type, value); break;
            case EffectActionType.AttackSpeedBonus:
                if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(value);
                break;
            case EffectActionType.CooldownReduction: mCooldownReduction += pv; break;
            case EffectActionType.DefensePenetration: DefensePenetration = Mathf.Clamp01(DefensePenetration + pv); break;
            case EffectActionType.HealShieldStrength: mHealShieldStrength += pv; break;
            case EffectActionType.SkillDamageMultiplier: mSkillDamageMultiplier += pv; break;
            case EffectActionType.ManaCostMultiplier: mManaCostMultiplier += pv; break;
            case EffectActionType.EnableFinalDamage: FinalDamageMultiplier += pv; break;
            case EffectActionType.DodgeCooldownReduction:
                mDodgeCooldownReduction += pv;
                if (mPlayer != null)
                {
                    var dodge = mPlayer.GetComponent<DodgeSkill>();
                    if (dodge != null) dodge.SetCooldownReduction(mDodgeCooldownReduction);
                }
                break;
            case EffectActionType.ManaRestore: mPlayer?.RestoreMana(Mathf.Abs(value)); break;
            case EffectActionType.LifeSteal: mLifeSteal += Mathf.Abs(value); break;
            case EffectActionType.DisableActiveSkills: ActiveSkillsDisabled = true; break;
            case EffectActionType.DamageOnHitMaxHp:
            case EffectActionType.OnHitBonusByMoveSpeed:
                // 命中附加伤害（buff8：最大生命% / buff3：移速%）——被动即写入每击附加
                mOnHitBonusOnHit += value;
                break;
            case EffectActionType.Execute:
            case EffectActionType.Immunity:
            case EffectActionType.OnCritProjectile:
            case EffectActionType.PeriodicProjectile:
            case EffectActionType.ExplodeOnDodge:
            case EffectActionType.PeriodicExplosionAttack:
            case EffectActionType.CooldownReduceOnHit:
                LogKit.Warning($"[效果] action {type} 属于事件型，应挂在事件触发器而非 Passive（已跳过即时应用）");
                break;
            default:
                LogKit.Warning($"[效果] 被动行为 {type} 未处理");
                break;
        }
    }

    // ===== 层数 =====

    /// <summary>触发器事件驱动叠层（OnKill/OnCrit/OnHit 等）</summary>
    private void AddStack(EffectActionData a, string eventName)
    {
        if (a.stackGainPerTrigger <= 0) return;
        var st = mStacks.TryGetValue(a, out var s) ? s : new StackState();
        if (a.stackMax > 0 && st.Count >= a.stackMax) return; // 已满
        st.Count = Mathf.Min(a.stackMax > 0 ? a.stackMax : int.MaxValue, st.Count + a.stackGainPerTrigger);
        if (a.stackRefreshDuration > 0f) st.LoseDurationRemain = a.stackRefreshDuration;
        mStacks[a] = st;
        ApplyOneStack(a);

        // 层数达到阈值触发（如满层发射弩箭 / 触发伤害转治疗）
        if (a.stackTriggerAt > 0 && st.Count >= a.stackTriggerAt)
        {
            OnStackTriggered(a, st);
        }
        LogKit.Log($"[效果] 叠层 {eventName} → {st.Count}");
    }

    /// <summary>层数满阈值时的场景动作（发射弩箭 / 伤害转治疗标记）</summary>
    private void OnStackTriggered(EffectActionData a, StackState st)
    {
        // 发射弩箭（buff211）
        if (a.stackProjectile > 0f)
        {
            FireMissileAtNearest(a.stackProjectile, a.damageType);
        }
        // 达到满层：攻击转化治疗（buff212）。此处记录标记，由 GetOnHitBonus 消费。
        if (a.stackDamageConvertToHeal > 0f)
        {
            mDamageConvertToHeal = a.stackDamageConvertToHeal;
            LogKit.Log($"[效果] 已达 {st.Count} 层，伤害 x{a.stackDamageConvertToHeal} 转为治疗");
        }
    }

    private float mDamageConvertToHeal;

    /// <summary>受击损失层数（OnDamaged 触发）</summary>
    private void OnDamagedStackLoss()
    {
        if (mStacks.Count == 0) return;   // 无层数状态：直接返回，避免每次受击都分配快照

        mStackLossBuffer.Clear();
        foreach (var a in mStacks.Keys) mStackLossBuffer.Add(a);

        for (int i = 0; i < mStackLossBuffer.Count; i++)
        {
            var a = mStackLossBuffer[i];
            if (a.stackLossPercent <= 0f) continue;
            if (!mStacks.TryGetValue(a, out var st)) continue;
            int lost = Mathf.CeilToInt(st.Count * a.stackLossPercent);
            if (lost <= 0) continue;
            int prev = st.Count;
            st.Count = Mathf.Max(0, st.Count - lost);
            RemoveStacksFromPlayer(a, prev - st.Count);
            if (st.Count <= 0) mStacks.Remove(a);
        }
    }

    /// <summary>从玩家移除指定层数的逐层贡献（简化做整量回退）</summary>
    private void RemoveStacksFromPlayer(EffectActionData a, int layerTotal)
    {
        var delta = BuffEffectBonusCalculator.LossStackDelta(a, CurrentContext(), layerTotal);
        if (delta.MoveSpeed != 0f) mPlayer.AddBonusAttribute(new PlayerAttribute { MoveSpeed = -delta.MoveSpeed });
        if (delta.MaxHp != 0f) mHealth?.RemoveMaxHp(delta.MaxHp);
        if (delta.PhysDef != 0f || delta.ElemDef != 0f)
            mPlayer.AddBonusAttribute(new PlayerAttribute { PhysDef = -delta.PhysDef, ElemDef = -delta.ElemDef });
        if (a.stackToAttackSpeed > 0f)
        {
            if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(-a.stackToAttackSpeed * layerTotal);
        }
        if (a.stackToCooldownReduction > 0f)
        {
            mCooldownReduction = Mathf.Max(0f, mCooldownReduction - a.stackToCooldownReduction * layerTotal);
        }
    }

    /// <summary>层数新增一层的逐层应用（在 AddStack 中调用）</summary>
    private void ApplyOneStack(EffectActionData a)
    {
        var delta = BuffEffectBonusCalculator.OneStackDelta(a, CurrentContext());
        if (delta.MoveSpeed != 0f) mPlayer.AddBonusAttribute(new PlayerAttribute { MoveSpeed = delta.MoveSpeed });
        if (delta.MaxHp != 0f) mHealth?.AddMaxHp(delta.MaxHp);
        if (delta.PhysDef != 0f || delta.ElemDef != 0f)
            mPlayer.AddBonusAttribute(new PlayerAttribute { PhysDef = delta.PhysDef, ElemDef = delta.ElemDef });
        if (a.stackToAttackSpeed > 0f) if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(a.stackToAttackSpeed);
        if (a.stackToCooldownReduction > 0f) mCooldownReduction += a.stackToCooldownReduction;
    }

    // ===== 事件触发分发 =====

    private void OnDamageDealt(GameEvents.DamageDealt evt)
    {
        // 效果弹（重弩追加/回力镖/暴击飞弹等）命中不触发任何 OnHit 类效果：
        // 否则"弹命中→触发效果→再发弹→再命中"会形成自持/指数级连锁，直接卡死游戏
        // 命中附加被动（如椒娘嗜辣成性）发出的展示事件同样跳过：防止"附加伤害再触发附加"自递归
        if (evt.FromEffectProjectile || evt.FromOnHitBonus) return;

        // 命中附加伤害：buff8(最大生命%) / buff3(移速%) —— 直接追加到目标
        float damage = evt.Damage;
        if (mOnHitBonusOnHit > 0f && evt.Target != null)
        {
            var target = evt.Target.GetComponent<EnemySimple>();
            if (target != null && !target.IsDead)
            {
                target.TakeDamage(mOnHitBonusOnHit);
            }
        }
        TriggerEvent("OnHit", evt.Position);
        if (evt.IsCrit) TriggerEvent("OnCrit", evt.Position);
        // 命中叠层
        TriggerStackEvent("OnHit");
        if (evt.IsCrit)
        {
            TriggerStackEvent("OnCrit");
            FireCritProjectiles();
        }
        // 战力：OnHit 附加的 CD 缩减
        ApplyOnHitBonusDamage(evt.Position);
        ApplyOnHitManaRestore();
        ApplyOnHitLowHpBonus(evt);
    }

    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        TriggerEvent("OnKill", evt.Position);
        TriggerStackEvent("OnKill");
    }

    private void OnDodgePerformed(GameEvents.DodgePerformed evt)
    {
        TriggerEvent("OnDodge", mPlayer != null ? mPlayer.transform.position : Vector3.zero);
    }

    private void OnSkillReleased(GameEvents.ActiveSkillReleased evt)
    {
        TriggerEvent("OnSkill", mPlayer != null ? mPlayer.transform.position : Vector3.zero);
    }

    private void OnPlayerDamaged(GameEvents.PlayerDamaged evt)
    {
        if (evt.Damage <= 0f) return; // 0 伤害仅用于 HUD 刷新
        TriggerEvent("OnDamaged", mPlayer != null ? mPlayer.transform.position : Vector3.zero);
        OnDamagedStackLoss();
    }

    private void TriggerEvent(string eventName, Vector3 position)
    {
        foreach (var re in mConditional)
        {
            var cfg = re.Config;
            string trigName = ((EffectTriggerType)cfg.trigger).ToString();
            if (trigName != eventName) continue;
            if (re.CooldownRemain > 0f) continue;
            ExecuteActions(cfg, 1f);
            if (cfg.cooldown > 0f) re.CooldownRemain = cfg.cooldown;
            LogKit.Log($"[效果] 触发 {trigName}：{cfg.effectName}");
        }
    }

    private void TriggerStackEvent(string eventName)
    {
        if (mStacks.Count == 0) return;   // 无层数状态：命中/击杀高频事件下直接返回，避免分配快照

        // 对键做快照再枚举：AddStack 会向 mStacks 写入/新增键，不能在 foreach 中直接改
        mStackKeyBuffer.Clear();
        foreach (var a in mStacks.Keys) mStackKeyBuffer.Add(a);

        for (int i = 0; i < mStackKeyBuffer.Count; i++)
        {
            var a = mStackKeyBuffer[i];
            if (a.stackTriggerEvent != eventName) continue;
            AddStack(a, eventName);
        }
    }

    // ===== 事件行为（命中附加） =====

    private void ApplyOnHitBonusDamage(Vector3 pos)
    {
        foreach (var re in mConditional)
        {
            var cfg = re.Config;
            if (((EffectTriggerType)cfg.trigger) != EffectTriggerType.OnHit) continue;
            foreach (var a in cfg.actions)
            {
                if (a == null) continue;
                var type = (EffectActionType)a.action;
                // 注意：action 39(CooldownReduceOnHit) 不在此处处理。
                // OnDamageDealt 已先执行 TriggerEvent("OnHit") → ExecuteActions，此处再跑一次会让 buff201 每次命中扣两份冷却。
                if (type == EffectActionType.DamageOnHitMaxHp && mPlayer != null)
                {
                    mOnHitBonusOnHit = mPlayer.MaxHp * a.percentOfMaxHp;
                }
                else if (type == EffectActionType.OnHitBonusByMoveSpeed && mPlayer != null)
                {
                    mOnHitBonusOnHit = mPlayer.MoveSpeed * a.percentOfMoveSpeed;
                }
                else if (type == EffectActionType.OnCritProjectile && mPlayer != null)
                {
                    // 暴击飞弹
                    int count = a.projectileCount + Mathf.FloorToInt(mPlayer.CritChance * 100f / Mathf.Max(1f, a.projectilePerPercent));
                    for (int i = 0; i < count; i++)
                    {
                        float dmg = mPlayer.Attack * a.percentOfAttack;
                        FireMissileAtNearest(dmg, a.damageType);
                    }
                }
            }
        }
    }

    private void ApplyOnHitManaRestore()
    {
        if (mManaRestoreOnHit > 0f && mPlayer != null) mPlayer.RestoreMana(mManaRestoreOnHit);
    }

    /// <summary>命中低血目标额外伤害（pepper 被动：目标<50% 生命时额外 percentOfAttack×攻，每目标 baseValue 秒冷却）</summary>
    private void ApplyOnHitLowHpBonus(GameEvents.DamageDealt evt)
    {
        if (mPlayer == null || evt.Target == null) return;
        var enemy = evt.Target.GetComponent<EnemySimple>();
        BossHealth boss = null;
        float targetRatio = -1f;
        if (enemy != null && enemy.MaxHpValue > 0f) targetRatio = enemy.CurrentHp / enemy.MaxHpValue;
        else
        {
            boss = evt.Target.GetComponent<BossHealth>();
            if (boss != null && boss.MaxHp > 0f) targetRatio = boss.CurrentHp / boss.MaxHp;
        }
        if (targetRatio < 0f || targetRatio >= 0.5f) return; // 非低血目标不触发

        foreach (var re in mConditional)
        {
            var cfg = re.Config;
            if (cfg == null || ((EffectTriggerType)cfg.trigger) != EffectTriggerType.OnHit) continue;
            foreach (var a in cfg.actions)
            {
                if (a == null || (EffectActionType)a.action != EffectActionType.OnHitLowHpBonus) continue;
                float cd = a.baseValue > 0f ? a.baseValue : 12f;
                if (mHitLowHpLastTime.TryGetValue(evt.Target, out float last) && (Time.time - last) < cd) continue;
                mHitLowHpLastTime[evt.Target] = Time.time;

                float bonus = mPlayer.Attack * (a.percentOfAttack != 0f ? a.percentOfAttack : 0.5f);
                // FromOnHitBonus=true：表现层照常飘字/闪白（否则小怪无血条，额外伤害完全不可见、冷却无从验证），
                // 但效果类订阅方跳过该事件，防止"附加伤害再触发附加"自递归
                var bonusEvt = new GameEvents.DamageDealt
                {
                    Position = evt.Target.transform.position,
                    Damage = bonus,
                    IsCrit = false,
                    Target = evt.Target,
                    FromOnHitBonus = true,
                    DamageType = evt.DamageType
                };
                if (enemy != null && !enemy.IsDead) { enemy.TakeDamage(bonus); EventKit.Type.Send(bonusEvt); }
                else if (boss != null && !boss.IsDead) { boss.TakeDamage(bonus, AttackType.Universal); EventKit.Type.Send(bonusEvt); }
                LogKit.Log($"[效果] 低血目标额外伤害 +{bonus:F0}");
            }
        }
    }

    /// <summary>暴击飞弹（buff102：1+(2%暴击率)个飞弹，每个 2% 攻击力伤害）</summary>
    private void FireCritProjectiles()
    {
        if (mOnCritProjectiles.Count == 0 || mPlayer == null) return;
        foreach (var a in mOnCritProjectiles)
        {
            int count = a.projectileCount + Mathf.FloorToInt(mPlayer.CritChance * 100f / Mathf.Max(1f, a.projectilePerPercent));
            for (int i = 0; i < count; i++)
            {
                float dmg = mPlayer.Attack * a.percentOfAttack;
                FireMissileAtNearest(dmg, a.damageType);
            }
        }
    }

    /// <summary>降低所有主动技能冷却（buff201 每次命中 / buff213 技能返还 30%）</summary>
    public void ReduceSkillCooldowns(float seconds)
    {
        if (ActiveSkillSystem.HasInstance) ActiveSkillSystem.Instance.ReduceCooldowns(seconds);
    }

    /// <summary>按各技能自身冷却总时长百分比降低冷却（buff213：返还30%原冷却）</summary>
    public void ReduceSkillCooldownsPercent(float percent)
    {
        if (ActiveSkillSystem.HasInstance) ActiveSkillSystem.Instance.ReduceCooldownsByPercent(percent);
    }

    // ===== 执行行为 =====

    private void ExecuteActions(GameEffectConfig cfg, float multiplier)
    {
        foreach (var a in cfg.actions)
        {
            if (a == null) continue;
            var type = (EffectActionType)a.action;
            float value = EffectiveValue(a, multiplier);

            switch (type)
            {
                case EffectActionType.Shield: StartShieldMod(a, value * HealStrengthMult(), cfg); break;
                case EffectActionType.Heal: if (mHealth != null) { mHealth.Heal(value * HealStrengthMult()); LogKit.Log($"[效果] 回复生命 +{value:F0}"); } break;
                case EffectActionType.ManaRestore: mPlayer?.RestoreMana(value); mPlayer?.RestoreMana(0); break;
                case EffectActionType.DamageBonus: StartAttrMod(ModKind.DamageField, type, a, value, cfg); break;
                case EffectActionType.AttackSpeedBonus: StartAttrMod(ModKind.AttackSpeedField, type, a, value, cfg); break;
                case EffectActionType.MaxHpBonus:
                    if (value != 0f)
                    {
                        if (value > 0f) mHealth?.AddMaxHp(value);
                        else mHealth?.RemoveMaxHp(-value);
                        mPlayer?.RefreshToMaxHp();
                    }
                    break;
                case EffectActionType.MoveSpeedBonus:
                case EffectActionType.CritChanceBonus:
                case EffectActionType.CritDamageBonus:
                case EffectActionType.PhysDamageBonus:
                case EffectActionType.ElemDamageBonus:
                    StartAttrMod(ModKind.Attribute, type, a, value, cfg);
                    break;
                case EffectActionType.Immunity:
                    mImmunityRemain = a.duration > 0f ? a.duration : value;
                    LogKit.Log($"[效果] 进入免疫伤害 {mImmunityRemain:F1}s");
                    break;
                case EffectActionType.BlockNextAttack:
                    mBlockCharges += Mathf.Max(1, Mathf.RoundToInt(value));
                    LogKit.Log($"[效果] 获得格挡层数 x{mBlockCharges}");
                    break;
                case EffectActionType.KillBuff:
                    // 击杀限时移速/攻速
                    StartAttrMod(ModKind.Attribute, EffectActionType.MoveSpeedBonus, new EffectActionData { action = 2, baseValue = value * 0f + 0.5f, duration = a.duration }, a.percentOfMoveSpeed, cfg);
                    if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(a.baseValue);
                    if (a.duration > 0f) StartCoroutine(LoseTemporaryKillBuff(a.baseValue, a.duration));
                    break;
                case EffectActionType.ExplodeOnDodge:
                case EffectActionType.PeriodicExplosionAttack:
                    ExplodeAroundPlayer(value, a.radius);
                    break;
                case EffectActionType.PeriodicProjectile:
                    FireMissileAtNearest(value, a.damageType);
                    break;
                case EffectActionType.CooldownReduceOnHit:
                    // 绝对值优先（buff201 每次命中 -0.2s）；纯 percentOfAttack 写法视为"按自身冷却百分比返还"（buff213 施放技能返还 30%）。
                    // 注意不能直接用 EffectiveValue 的 value：那是 baseValue + 攻击×百分比，会把 0.3 当成 45 秒。
                    if (a.baseValue > 0f) ReduceSkillCooldowns(a.baseValue);
                    else if (a.percentOfAttack > 0f) ReduceSkillCooldownsPercent(a.percentOfAttack);
                    break;
                case EffectActionType.Execute:
                    mExecuteHpPercent = a.percentOfMaxHp;
                    mExecuteAttackPercent = a.percentOfAttack;
                    mExecuteHealPercent = a.healOnExecutePercent;
                    LogKit.Log($"[效果] 处决已启用（阈值 {mExecuteHpPercent:P0}最大生命 + {mExecuteAttackPercent:P0}攻击）");
                    break;
                case EffectActionType.FireProjectile:
                    FireMissileAtNearest(value, a.damageType);
                    break;
                case EffectActionType.OnHitLowHpBonus:
                    // 已在 ApplyOnHitLowHpBonus 处理，此处无需执行
                    break;
                default:
                    LogKit.Warning($"[效果] 行为 {type} 待实现");
                    break;
            }
        }
    }

    private IEnumerator LoseTemporaryKillBuff(float atkSpeed, float duration)
    {
        yield return new WaitForSeconds(duration);
        if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(-atkSpeed);
    }

    /// <summary>治疗/护盾强度（buff214）</summary>
    private float HealStrengthMult() => 1f + mHealShieldStrength;

    /// <summary>闪避爆炸（buff13）：玩家周围 AOE 伤害</summary>
    private void ExplodeAroundPlayer(float damage, float radius)
    {
        if (mPlayer == null) return;
        var list = new List<EnemySimple>();
        SpatialGrid.GetInRadius(mPlayer.transform.position, radius, list);
        foreach (var e in list)
        {
            if (e == null) continue;
            e.TakeDamage(damage);
        }
        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy)
        {
            if (Vector2.Distance(boss.transform.position, mPlayer.transform.position) <= radius)
                boss.TakeDamage(damage, AttackType.Universal);
        }
    }

    /// <summary>向最近敌人发射导弹（回力镖/弩箭/暴击飞弹）</summary>
    private void FireMissileAtNearest(float damage, string damageType)
    {
        if (mPlayer == null) return;
        var auto = mPlayer.GetComponent<AutoAttack>();
        var weapon = auto != null ? auto.GetFirstRangedWeapon() : null;
        string prefab = AssetKeys.BULLET_SEASONING;
        float speed = 10f;
        float range = 8f;
        if (weapon != null && weapon.attackMode == WeaponAttackMode.Ranged && !string.IsNullOrEmpty(weapon.projectilePrefabPath))
        {
            prefab = weapon.projectilePrefabPath;
            speed = weapon.projectileSpeed > 0f ? weapon.projectileSpeed : speed;
            range = weapon.projectileRange > 0f ? weapon.projectileRange : range;
        }
        Vector2 dir = FindNearestEnemyDirection();
        if (dir == Vector2.zero) return;

        var go = PoolFactory.Get(prefab, mPlayer.transform.position, Quaternion.identity);
        var bullet = go.GetComponent<Bullet>();
        if (bullet != null)
        {
            // 回力镖可穿透，普通弹不穿透
            bool pierce = damageType == "pierce";
            // fromEffectProjectile=true：标记为效果弹，命中不再触发 OnHit 类效果（防"弹触发弹"无限连锁）
            bullet.Setup(dir, speed, damage, AttackType.Universal, DamageType.Physical, range, pierce, SkillEffectData.None, false, true);
        }
    }

    private Vector2 FindNearestEnemyDirection()
    {
        if (mPlayer == null) return Vector2.zero;
        var list = new List<EnemySimple>();
        SpatialGrid.GetInRadius(mPlayer.transform.position, 20f, list);
        float minDistSq = float.MaxValue;
        Vector2 dir = Vector2.zero;
        foreach (var e in list)
        {
            if (e == null) continue;
            float dsq = ((Vector2)(e.transform.position - mPlayer.transform.position)).sqrMagnitude;
            if (dsq < minDistSq) { minDistSq = dsq; dir = ((Vector2)(e.transform.position - mPlayer.transform.position)).normalized; }
        }
        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy)
        {
            float dsq = ((Vector2)(boss.transform.position - mPlayer.transform.position)).sqrMagnitude;
            if (dsq < minDistSq) dir = ((Vector2)(boss.transform.position - mPlayer.transform.position)).normalized;
        }
        return dir;
    }

    /// <summary>数值换算：基础(或随机区间) + 攻击%/生命%/移速% 换算，再乘倍率；负百分比支持减益</summary>
    private float EffectiveValue(EffectActionData a, float multiplier)
    {
        float v = (a.minValue >= 0f && a.maxValue > a.minValue)
            ? Random.Range(a.minValue, a.maxValue)
            : a.baseValue;
        if (a.percentOfAttack != 0f) v += mPlayer.Attack * a.percentOfAttack;
        if (a.percentOfMaxHp != 0f) v += mPlayer.MaxHp * a.percentOfMaxHp;
        if (a.percentOfMoveSpeed != 0f) v += mPlayer.MoveSpeed * a.percentOfMoveSpeed;
        return v * multiplier;
    }

    // ===== 临时修改（持续/衰减） =====

    private void StartAttrMod(ModKind kind, EffectActionType actionType, EffectActionData a, float value, GameEffectConfig source)
    {
        var mod = new ActiveModifier
        {
            Kind = kind,
            ActionType = actionType,
            CurrentValue = 0f,
            Remaining = a.duration,
            DecayPerSecond = (a.decay && a.duration > 0f) ? value / a.duration : 0f,
            SourceName = source?.effectName,
            SourceDesc = source?.description,
            Debuff = value < 0f
        };
        ApplyDelta(mod, value);
        mMods.Add(mod);

        if (a.duration > 0f)
        {
            mod.Routine = StartCoroutine(TickMod(mod));
        }
    }

    private IEnumerator TickMod(ActiveModifier mod)
    {
        while (mod.Remaining > 0f)
        {
            float dt = Time.deltaTime;
            mod.Remaining -= dt;

            if (mod.DecayPerSecond > 0f && mod.Remaining > 0f)
            {
                float tick = Mathf.Min(mod.DecayPerSecond * dt, mod.CurrentValue);
                ApplyDelta(mod, -tick);
            }
            yield return null;
        }
        EndMod(mod);
    }

    private void ApplyDelta(ActiveModifier mod, float delta)
    {
        if (delta == 0f) return;
        switch (mod.Kind)
        {
            case ModKind.DamageField: SkillSystem.Instance?.AddExtraDamageBonus(delta); break;
            case ModKind.AttackSpeedField: SkillSystem.Instance?.AddExtraAttackSpeedBonus(delta); break;
            case ModKind.Attribute:
                var attr = BuffEffectBonusCalculator.MapToAttribute(mod.ActionType, delta);
                if (delta < 0f) mPlayer.RemoveBonusAttribute(attr);
                else mPlayer.AddBonusAttribute(attr);
                AddAttrTotal(mod.ActionType, delta);
                break;
            case ModKind.MaxHp:
                if (delta < 0f) mHealth?.RemoveMaxHp(-delta);
                else mHealth?.AddMaxHp(delta);
                break;
            case ModKind.Shield:
                mShield = Mathf.Max(0f, mShield + delta);
                break;
        }
        mod.CurrentValue += delta;
    }

    private void AddAttrTotal(EffectActionType type, float delta)
        => mAttrTotal = mAttrTotal + BuffEffectBonusCalculator.MapToAttribute(type, delta);

    /// <summary>构造公式求值上下文（基础属性 + 当前防御，供 BuffEffectBonusCalculator 使用）</summary>
    private BuffEffectBonusCalculator.BuffContext CurrentContext()
    {
        return new BuffEffectBonusCalculator.BuffContext
        {
            Base = mPlayer != null ? mPlayer.BaseAttr : default,
            CurrentPhysDef = mPlayer != null ? mPlayer.PhysDef : 0f,
            CurrentElemDef = mPlayer != null ? mPlayer.ElemDef : 0f
        };
    }

    /// <summary>当前本控制器写入玩家 BonusAttr 的属性类总贡献</summary>
    public PlayerAttribute GetTotalAttributeBonus() => mAttrTotal;

    // ===== 状态查询（状态面板只读展示） =====

    /// <summary>
    /// 当前生效中的限时状态快照（属性/护盾修改 + 免疫/格挡）。
    /// 常驻被动与条件触发效果无倒计时，不在此列。
    /// </summary>
    public List<StatusRuntimeView> GetActiveStatusViews()
    {
        var list = new List<StatusRuntimeView>();

        foreach (var mod in mMods)
        {
            // 常驻护盾（duration=0）没有倒计时，但仍要显示；其余无限期修改（常驻被动等）不列
            bool permanentShield = mod.Kind == ModKind.Shield && mShield > 0f;
            if (mod.Remaining <= 0f && !permanentShield) continue;

            list.Add(new StatusRuntimeView
            {
                Name = string.IsNullOrEmpty(mod.SourceName) ? FallbackModName(mod) : mod.SourceName,
                Desc = (mod.Kind == ModKind.Shield || string.IsNullOrEmpty(mod.SourceDesc))
                    ? BuildModDesc(mod)
                    : mod.SourceDesc,
                Remain = mod.Remaining,
                IsDebuff = mod.Debuff
            });
        }

        // 免疫伤害（限时特殊状态）
        if (mImmunityRemain > 0f)
        {
            list.Add(new StatusRuntimeView
            {
                Name = "免疫伤害",
                Desc = "短时间内免疫所有伤害",
                Remain = mImmunityRemain
            });
        }

        // 格挡层数（按次数生效，无时间倒计时 → Remain=0 由 UI 显示“持续”）
        if (mBlockCharges > 0)
        {
            list.Add(new StatusRuntimeView
            {
                Name = "格挡",
                Desc = $"可格挡接下来 {mBlockCharges} 次攻击",
                Remain = 0f
            });
        }

        return list;
    }

    /// <summary>无来源名时按修改类型生成兜底名称</summary>
    private static string FallbackModName(ActiveModifier mod)
    {
        switch (mod.Kind)
        {
            case ModKind.Shield: return "护盾";
            case ModKind.AttackSpeedField: return "攻速加成";
            case ModKind.DamageField: return "伤害加成";
            default: return mod.ActionType.ToString();
        }
    }

    /// <summary>无来源描述时按当前值生成兜底描述（护盾显示真实剩余量，而非初始值）</summary>
    private string BuildModDesc(ActiveModifier mod)
    {
        if (mod.Kind == ModKind.Shield)
            return $"护盾可吸收 {mShield:F0} 点伤害";
        return $"{mod.ActionType}：{mod.CurrentValue:+0.##;-0.##;0}";
    }

    private void EndMod(ActiveModifier mod)
    {
        if (mod.Routine != null) StopCoroutine(mod.Routine);
        mMods.Remove(mod);
        if (mod.CurrentValue != 0f) ApplyDelta(mod, -mod.CurrentValue);
    }

    private void ClearAllMods()
    {
        for (int i = mMods.Count - 1; i >= 0; i--) EndMod(mMods[i]);
    }

    // ===== 护盾吸收 / 免疫 =====

    private void StartShieldMod(EffectActionData a, float value, GameEffectConfig source)
    {
        if (value <= 0f) return;
        var mod = new ActiveModifier
        {
            Kind = ModKind.Shield,
            CurrentValue = 0f,
            Remaining = a.duration,
            DecayPerSecond = (a.decay && a.duration > 0f) ? value / a.duration : 0f,
            SourceName = source?.effectName,
            SourceDesc = source?.description
        };
        ApplyDelta(mod, value);
        mMods.Add(mod);
        LogKit.Log($"[效果] 获得护盾 +{value:F0}（当前 {mShield:F0}）");
        if (a.duration > 0f) mod.Routine = StartCoroutine(TickMod(mod));
    }

    /// <summary>伤害吸收（Health.DamageAbsorber 委托）：免疫 → 格挡 → 护盾</summary>
    private float AbsorbDamage(float damage)
    {
        if (damage <= 0f) return 0f;
        if (mImmunityRemain > 0f) return damage;   // 免疫：全额吸收
        if (mBlockCharges > 0f)
        {
            mBlockCharges--;
            return damage;                          // 格挡下一次攻击
        }
        if (mShield <= 0f) return 0f;
        float absorbed = Mathf.Min(mShield, damage);
        mShield -= absorbed;
        return absorbed;
    }

    // ===== 条件触发（LowHp / Periodic） =====

    private void Update()
    {
        if (mImmunityRemain > 0f) mImmunityRemain -= Time.deltaTime;

        SyncMoveSpeedToAttack();

        // 层数到期丢失：先收集待移除键，再统一移除。
        // （原实现每帧 `new Dictionary<>(mStacks)` 复制一份再枚举，会持续产生 GC 分配 → 帧率抖动）
        if (mStacks.Count > 0)
        {
            mStackReleaseBuffer.Clear();
            foreach (var kv in mStacks)
            {
                if (kv.Value.LoseDurationRemain <= 0f) continue;
                kv.Value.LoseDurationRemain -= Time.deltaTime;
                if (kv.Value.LoseDurationRemain <= 0f) mStackReleaseBuffer.Add(kv.Key);
            }

            for (int i = 0; i < mStackReleaseBuffer.Count; i++)
            {
                var key = mStackReleaseBuffer[i];
                if (!mStacks.TryGetValue(key, out var st)) continue;
                RemoveStacksFromPlayer(key, st.Count);
                mStacks.Remove(key);
            }
        }

        UpdateConditional(Time.deltaTime);
    }

    /// <summary>
    /// 大厨被动同步：总移速变化时按「基础攻击 × 当前总移速 × 百分比」重算攻击加成并写入差值。
    /// 只读 MoveSpeed（该被动自身不写移速），无反馈回路；断点存档剔除走 AddAttrTotal 累计口径。
    /// </summary>
    private void SyncMoveSpeedToAttack()
    {
        if (mMoveSpeedToAttackAction == null || mPlayer == null) return;
        float cur = mPlayer.MoveSpeed;
        if (Mathf.Approximately(cur, mMoveSpeedToAttackLastSpeed)) return;

        mMoveSpeedToAttackLastSpeed = cur;
        float percent = BuffEffectBonusCalculator.PercentValue(mMoveSpeedToAttackAction);
        float bonus = mPlayer.BaseAttr.Attack * cur * percent;
        float delta = bonus - mMoveSpeedToAttackApplied;
        if (Mathf.Approximately(delta, 0f)) return;

        mMoveSpeedToAttackApplied = bonus;
        mPlayer.AddBonusAttribute(new PlayerAttribute { Attack = delta });
        AddAttrTotal(EffectActionType.AttackOfMoveSpeed, delta);
        LogKit.Log($"[效果] 厨神之心同步：移速 {cur:F1} → 攻击加成 +{bonus:F2}");
    }

    private void UpdateConditional(float dt)
    {
        if (mConditional.Count == 0 || mHealth == null) return;

        for (int i = mConditional.Count - 1; i >= 0; i--)
        {
            var re = mConditional[i];
            if (re.CooldownRemain > 0f) re.CooldownRemain -= dt;
            var cfg = re.Config;
            var trig = (EffectTriggerType)cfg.trigger;

            // Periodic：按周期触发
            if (trig == EffectTriggerType.Periodic)
            {
                re.PeriodicTimer -= dt;
                if (re.PeriodicTimer <= 0f)
                {
                    re.PeriodicTimer = cfg.triggerParam <= 0f ? 5f : cfg.triggerParam;
                    ExecuteActions(cfg, 1f);
                    LogKit.Log($"[效果] 周期触发：{cfg.effectName}");
                }
            }
            // LowHp：生命低于阈值触发（带冷却）
            else if (trig == EffectTriggerType.LowHp && re.CooldownRemain <= 0f && mHealth.HpRatio < cfg.triggerParam)
            {
                ExecuteActions(cfg, 1f);
                re.CooldownRemain = cfg.cooldown;
                LogKit.Log($"[效果] 触发：{cfg.effectName}");
            }
        }
    }

    // ===== 工具 =====

    /// <summary>获取或挂载玩家效果控制器</summary>
    public static PlayerEffectController GetOrAdd(PlayerController player)
    {
        if (player == null) return null;
        var ctrl = player.GetComponent<PlayerEffectController>();
        if (ctrl == null) ctrl = player.gameObject.AddComponent<PlayerEffectController>();
        return ctrl;
    }
}