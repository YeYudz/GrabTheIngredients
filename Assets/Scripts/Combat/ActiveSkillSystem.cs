using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 主动技能系统（业务层单例）：持有战前技能组，执行 AOE / 回血 / 临时增益 / 弹幕四类效果，
/// 并统一管理冷却与临时 Buff。
/// 与 UI 完全解耦——只订阅 HUD 事件、只广播结果，不触碰任何显示对象，便于替换表现层。
/// </summary>
public class ActiveSkillSystem : MonoSingleton<ActiveSkillSystem>
{
    /// <summary>战前技能组（按序，供 HUD 生成按钮）</summary>
    private List<ActiveSkillConfig> mSkills = new();

    /// <summary>各技能冷却剩余时间（skillId → 剩余秒）</summary>
    private readonly Dictionary<string, float> mCooldownRemain = new();

    /// <summary>各技能冷却总时长（skillId → 总秒）</summary>
    private readonly Dictionary<string, float> mCooldownTotal = new();

    /// <summary>临时Buff剩余时间（buffType → (value, remain)）</summary>
    private readonly Dictionary<string, KeyValuePair<float, float>> mTempBuffs = new();

    /// <summary>Update 遍历字典键的复用缓冲（避免每帧 new List&lt;string&gt; 造成 GC 抖动）</summary>
    private static readonly List<string> s_keyBuffer = new();

    /// <summary>冷却缩减遍历字典键的复用缓冲（buff201 每次命中都会触发，属高频路径）</summary>
    private static readonly List<string> s_reduceBuffer = new();

    // ===== 技能触发的限时战斗修饰（本局数值，断点结算后重置） =====
    private float mTrueDmgRemain;   // 命中附加真伤剩余秒
    private float mTrueDmgFlat;     // 命中附加固定真伤
    private float mTrueDmgPct;      // 命中附加攻击%真伤
    private int mLimitedHits;       // 限次附加剩余攻击次数
    private float mLimitedPct;      // 限次附加攻击%
    private float mCondRemain;      // 条件增伤剩余秒
    private int mCondType;          // 条件类型 1=目标低血 2=目标高血 3=自身低血
    private float mCondPct;         // 条件增伤攻击%
    private float mKillHealRemain;  // 击杀回血剩余秒
    private float mKillHealPct;     // 击杀回血%最大生命
    private float mManaRemain;      // 限时回蓝剩余秒
    private float mHitManaPct;      // 命中回蓝%最大法力
    private float mKillManaPct;     // 击杀回蓝%最大法力
    private float mAttackRangeRemain; // 攻击距离加成剩余秒
    private float mAttackRangePct;    // 攻击距离加成%

    /// <summary>攻击距离倍率（AutoAttack 查询；无加成时 1）</summary>
    public float AttackRangeMultiplier => mAttackRangeRemain > 0f ? 1f + mAttackRangePct : 1f;

    /// <summary>空间网格查询复用列表（避免每帧分配）</summary>
    private readonly List<EnemySimple> mQueryList = new();

    protected override void Awake()
    {
        base.Awake();

        // 订阅 HUD 技能按钮点击（EventKit 解耦）
        EventKit.Type.Register<GameEvents.ActiveSkillRequested>(OnSkillRequested)
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        // 订阅命中/击杀：执行"技能触发的命中附加/回蓝/回血"（如 chef 真伤、共有条件增伤、命/击杀回蓝）
        EventKit.Type.Register<GameEvents.DamageDealt>(OnDamageDealt)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    /// <summary>初始化战前技能组（进入游戏时由 GameRunningState 调用）</summary>
    public void Init(List<string> skillIds)
    {
        mSkills.Clear();
        mCooldownRemain.Clear();
        mCooldownTotal.Clear();
        mTempBuffs.Clear();
        ClearCombatMods();

        if (skillIds == null) return;

        foreach (var id in skillIds)
        {
            if (string.IsNullOrEmpty(id)) continue; // 空槽跳过

            var config = GameDataMgr.Instance.GetActiveSkill(id);
            if (config == null)
            {
                LogKit.Warning($"[ActiveSkillSystem] 找不到主动技能配置: {id}");
                continue;
            }
            mSkills.Add(config);
            mCooldownTotal[id] = ResolveCooldown(config.cooldown);
            mCooldownRemain[id] = 0f;
        }
        LogKit.Log($"[ActiveSkillSystem] 技能组初始化完成，共 {mSkills.Count} 个主动技能");
    }

    /// <summary>重置（重开/回主菜单时调用）</summary>
    public void Reset()
    {
        mSkills.Clear();
        mCooldownRemain.Clear();
        mCooldownTotal.Clear();
        mTempBuffs.Clear();
        ClearCombatMods();
    }

    /// <summary>清空技能触发的限时战斗修饰（Init/Reset 调用）</summary>
    private void ClearCombatMods()
    {
        mTrueDmgRemain = 0f; mTrueDmgFlat = 0f; mTrueDmgPct = 0f;
        mLimitedHits = 0; mLimitedPct = 0f;
        mCondRemain = 0f; mCondType = 0; mCondPct = 0f;
        mKillHealRemain = 0f; mKillHealPct = 0f;
        mManaRemain = 0f; mHitManaPct = 0f; mKillManaPct = 0f;
        mAttackRangeRemain = 0f; mAttackRangePct = 0f;
    }

    /// <summary>断点存档：导出限时战斗修饰（真伤/条件增伤/击杀回血/回蓝/攻击距离）</summary>
    public SkillModSave ExportCombatMods()
    {
        return new SkillModSave
        {
            trueDmgRemain = mTrueDmgRemain, trueDmgFlat = mTrueDmgFlat, trueDmgPct = mTrueDmgPct,
            limitedHits = mLimitedHits, limitedPct = mLimitedPct,
            condRemain = mCondRemain, condType = mCondType, condPct = mCondPct,
            killHealRemain = mKillHealRemain, killHealPct = mKillHealPct,
            manaRemain = mManaRemain, hitManaPct = mHitManaPct, killManaPct = mKillManaPct,
            attackRangeRemain = mAttackRangeRemain, attackRangePct = mAttackRangePct,
        };
    }

    /// <summary>断点恢复：写回限时战斗修饰（剩余时间按保存时的值续算）</summary>
    public void RestoreCombatMods(SkillModSave s)
    {
        ClearCombatMods();
        if (s == null) return;

        mTrueDmgRemain = Mathf.Max(0f, s.trueDmgRemain);
        mTrueDmgFlat = s.trueDmgFlat;
        mTrueDmgPct = s.trueDmgPct;
        mLimitedHits = Mathf.Max(0, s.limitedHits);
        mLimitedPct = s.limitedPct;
        mCondRemain = Mathf.Max(0f, s.condRemain);
        mCondType = s.condType;
        mCondPct = s.condPct;
        mKillHealRemain = Mathf.Max(0f, s.killHealRemain);
        mKillHealPct = s.killHealPct;
        mManaRemain = Mathf.Max(0f, s.manaRemain);
        mHitManaPct = s.hitManaPct;
        mKillManaPct = s.killManaPct;
        mAttackRangeRemain = Mathf.Max(0f, s.attackRangeRemain);
        mAttackRangePct = s.attackRangePct;
    }

    /// <summary>命中事件：执行技能触发的命中附加（真伤/限次/条件增伤/命中回蓝）</summary>
    private void OnDamageDealt(GameEvents.DamageDealt evt)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        // 命中附加真伤（chef 独有1）
        if (mTrueDmgRemain > 0f)
        {
            float bonus = mTrueDmgFlat + player.Attack * mTrueDmgPct;
            ApplyBonusToTarget(evt, bonus);
        }

        // 限次攻击附加（chef 独有2/3）
        if (mLimitedHits > 0)
        {
            ApplyBonusToTarget(evt, player.Attack * mLimitedPct);
            mLimitedHits--;
            if (mLimitedHits <= 0) mLimitedPct = 0f;
        }

        // 条件增伤（共有1/2/3）
        if (mCondRemain > 0f) ApplyConditionalBonus(evt, player);

        // 命中回蓝（共有5）
        if (mManaRemain > 0f && mHitManaPct > 0f)
            player.RestoreMana(player.MaxMana * mHitManaPct);
    }

    /// <summary>击杀事件：执行击杀回血/额外回蓝（共有4/5）</summary>
    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;
        if (mKillHealRemain > 0f && mKillHealPct > 0f)
            player.Health?.Heal(player.MaxHp * mKillHealPct);
        if (mManaRemain > 0f && mKillManaPct > 0f)
            player.RestoreMana(player.MaxMana * mKillManaPct);
    }

    /// <summary>条件增伤结算：是否命中取决于条件类型（自身低血/目标低血/目标高血）</summary>
    private void ApplyConditionalBonus(GameEvents.DamageDealt evt, PlayerController player)
    {
        // 自身低血：施放后自身血量低于 50% 才增伤
        if (mCondType == 3)
        {
            if (player.Health != null && player.Health.HpRatio < 0.5f)
                ApplyBonusToTarget(evt, player.Attack * mCondPct);
            return;
        }

        // 目标低血/高血：读取目标当前生命比例
        float ratio = GetTargetHpRatio(evt.Target);
        if (ratio < 0f) return;
        bool low = ratio < 0.5f;
        if ((mCondType == 1 && low) || (mCondType == 2 && !low))
            ApplyBonusToTarget(evt, player.Attack * mCondPct);
    }

    /// <summary>目标生命比例（-1 = 无法解析）</summary>
    private static float GetTargetHpRatio(GameObject target)
    {
        if (target == null) return -1f;
        var enemy = target.GetComponent<EnemySimple>();
        if (enemy != null && enemy.MaxHpValue > 0f) return enemy.CurrentHp / enemy.MaxHpValue;
        var boss = target.GetComponent<BossHealth>();
        if (boss != null && boss.MaxHp > 0f) return boss.CurrentHp / boss.MaxHp;
        return -1f;
    }

    /// <summary>把额外伤害追加到目标（直接扣血，避免再发 DamageDealt 造成递归）</summary>
    private static void ApplyBonusToTarget(GameEvents.DamageDealt evt, float bonus)
    {
        if (bonus <= 0f || evt.Target == null) return;
        var enemy = evt.Target.GetComponent<EnemySimple>();
        if (enemy != null && !enemy.IsDead) { enemy.TakeDamage(bonus); return; }
        var boss = evt.Target.GetComponent<BossHealth>();
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy)
            boss.TakeDamage(bonus, AttackType.Universal);
    }

    /// <summary>
    /// 当前临时Buff的总属性加成（断点存档时从 BonusAttr 剔除：
    /// 恢复时由 RestoreTempBuffs 重新叠加，避免加成在 BonusAttr 上双算）
    /// </summary>
    public PlayerAttribute GetTempBuffBonus()
    {
        var bonus = new PlayerAttribute();
        foreach (var kv in mTempBuffs)
        {
            switch (kv.Key.ToLowerInvariant())
            {
                case "movespeed": bonus.MoveSpeed += kv.Value.Key; break;
                case "attack": bonus.Attack += kv.Value.Key; break;
                case "maxhp": bonus.MaxHp += kv.Value.Key; break;
                case "critchance": bonus.CritChance += kv.Value.Key; break;
                case "critdamage": bonus.CritDamage += kv.Value.Key; break;
                case "physdamage": bonus.PhysDamageBonus += kv.Value.Key; break;
                case "elemdamage": bonus.ElemDamageBonus += kv.Value.Key; break;
                case "defense": bonus.PhysDef += kv.Value.Key; bonus.ElemDef += kv.Value.Key; break;
                // "attackspeed" 走 SkillSystem 追加通道（不在 BonusAttr），无需在此剔除
            }
        }
        return bonus;
    }

    // ===== 状态查询（状态面板只读展示） =====

    /// <summary>
    /// 当前生效中的限时增益快照（临时属性Buff + 限时战斗修饰）。
    /// 本系统产出的均为增益；减益由面板统一收集分类。
    /// </summary>
    public List<StatusRuntimeView> GetActiveStatusViews()
    {
        var list = new List<StatusRuntimeView>();

        // 1. 临时属性Buff（攻/防/移速/暴击/攻速等）
        foreach (var kv in mTempBuffs)
        {
            if (kv.Value.Value <= 0f) continue;

            list.Add(new StatusRuntimeView
            {
                Name = TempBuffDisplayName(kv.Key),
                Desc = BuildTempBuffDesc(kv.Key, kv.Value.Key),
                Remain = kv.Value.Value
            });
        }

        // 2. 限时战斗修饰
        if (mTrueDmgRemain > 0f)
            list.Add(new StatusRuntimeView { Name = "真伤加持", Desc = "攻击命中附加额外真实伤害", Remain = mTrueDmgRemain });
        if (mCondRemain > 0f)
            list.Add(new StatusRuntimeView { Name = "条件增伤", Desc = CondBuffDesc(), Remain = mCondRemain });
        if (mKillHealRemain > 0f)
            list.Add(new StatusRuntimeView { Name = "击杀回血", Desc = "击杀敌人时回复生命值", Remain = mKillHealRemain });
        if (mManaRemain > 0f)
            list.Add(new StatusRuntimeView { Name = "法力涌动", Desc = "命中/击杀时回复法力", Remain = mManaRemain });
        if (mAttackRangeRemain > 0f)
            list.Add(new StatusRuntimeView { Name = "攻击距离提升", Desc = "攻击距离增加", Remain = mAttackRangeRemain });

        return list;
    }

    /// <summary>条件增伤说明（mCondType：1=目标低血 2=目标高血 3=自身低血）</summary>
    private string CondBuffDesc()
    {
        switch (mCondType)
        {
            case 1: return "攻击低血量目标时造成额外伤害";
            case 2: return "攻击高血量目标时造成额外伤害";
            case 3: return "自身低血量时攻击造成额外伤害";
            default: return "满足条件时攻击造成额外伤害";
        }
    }

    /// <summary>临时Buff类型 → 显示名</summary>
    private static string TempBuffDisplayName(string type)
    {
        switch (type.ToLowerInvariant())
        {
            case "movespeed": return "移动速度";
            case "attack": return "攻击力";
            case "maxhp": return "生命上限";
            case "critchance": return "暴击率";
            case "critdamage": return "暴击伤害";
            case "physdamage": return "物理伤害加成";
            case "elemdamage": return "元素伤害加成";
            case "defense": return "防御强化";
            case "attackspeed": return "攻击速度";
            default: return type;
        }
    }

    /// <summary>临时Buff类型 + 当前加成值 → 描述</summary>
    private static string BuildTempBuffDesc(string type, float value)
    {
        switch (type.ToLowerInvariant())
        {
            case "movespeed": return $"移动速度 +{value:F1}";
            case "attack": return $"攻击力 +{value:F0}";
            case "maxhp": return $"生命上限 +{value:F0}";
            case "critchance": return $"暴击率 +{value * 100f:F0}%";
            case "critdamage": return $"暴击伤害 +{value * 100f:F0}%";
            case "physdamage": return $"物理伤害加成 +{value * 100f:F0}%";
            case "elemdamage": return $"元素伤害加成 +{value * 100f:F0}%";
            case "defense": return $"物理/元素防御 +{value:F0}";
            case "attackspeed": return $"攻击速度 +{value}";
            default: return $"+{value}";
        }
    }

    /// <summary>断点存档：导出当前冷却快照（波次开局时缓存，恢复=回到波次开头的剩余冷却）</summary>
    public List<SkillCooldownSave> GetCooldownSnapshot()
    {
        var list = new List<SkillCooldownSave>();
        foreach (var kv in mCooldownRemain)
        {
            if (kv.Value > 0f)
                list.Add(new SkillCooldownSave { skillId = kv.Key, remain = kv.Value });
        }
        return list;
    }

    /// <summary>断点恢复：还原冷却剩余（临时Buff 由 RestoreTempBuffs 单独恢复）</summary>
    public void RestoreCooldowns(List<SkillCooldownSave> data)
    {
        mCooldownRemain.Clear();
        if (data != null)
        {
            foreach (var d in data)
            {
                if (d == null || string.IsNullOrEmpty(d.skillId)) continue;
                mCooldownRemain[d.skillId] = Mathf.Max(0f, d.remain);
            }
        }
    }

    /// <summary>断点存档：导出当前临时Buff快照（波次开局时缓存，恢复=回到波次开头的剩余时间）</summary>
    public List<TempBuffSave> GetTempBuffSnapshot()
    {
        var list = new List<TempBuffSave>();
        foreach (var kv in mTempBuffs)
        {
            if (kv.Value.Value > 0f)
                list.Add(new TempBuffSave { buffType = kv.Key, value = kv.Value.Key, remain = kv.Value.Value });
        }
        return list;
    }

    /// <summary>断点恢复：还原临时Buff（剩余时间 + 重新叠加属性加成，加成从该剩余时间继续计算）</summary>
    public void RestoreTempBuffs(List<TempBuffSave> data)
    {
        // 兜底清理（Init 已清空，此处确保无残留）
        var keys = new List<string>(mTempBuffs.Keys);
        foreach (var k in keys) RemoveTempBuffValue(k);
        mTempBuffs.Clear();

        if (data == null) return;
        foreach (var d in data)
        {
            if (d == null || string.IsNullOrEmpty(d.buffType) || d.remain <= 0f) continue;
            mTempBuffs[d.buffType] = new KeyValuePair<float, float>(d.value, d.remain);
            ApplyTempBuffValue(d.buffType, d.value);
        }
    }

    // ===== 查询接口（HUD 读取显示） =====

    /// <summary>技能组数量</summary>
    public int SkillCount => mSkills.Count;

    /// <summary>按索引取技能配置</summary>
    public ActiveSkillConfig GetSkill(int index)
    {
        if (index < 0 || index >= mSkills.Count) return null;
        return mSkills[index];
    }

    /// <summary>技能冷却剩余</summary>
    public float GetCooldownRemain(string skillId)
        => mCooldownRemain.TryGetValue(skillId, out var v) ? v : 0f;

    /// <summary>技能冷却总时长</summary>
    public float GetCooldownTotal(string skillId)
        => mCooldownTotal.TryGetValue(skillId, out var v) ? v : 0f;

    /// <summary>统一降低所有主动技能冷却（效果引擎：每次命中/技能返还；下限为0）</summary>
    public void ReduceCooldowns(float seconds)
    {
        if (seconds <= 0f || mCooldownRemain.Count == 0) return;

        // 复用缓冲做键快照（buff201 每次命中都会调到这里，不能每次 new List）
        s_reduceBuffer.Clear();
        foreach (var key in mCooldownRemain.Keys) s_reduceBuffer.Add(key);

        for (int i = 0; i < s_reduceBuffer.Count; i++)
        {
            string key = s_reduceBuffer[i];
            if (!mCooldownRemain.TryGetValue(key, out float cur) || cur <= 0f) continue;

            float remain = Mathf.Max(0f, cur - seconds);
            bool reachedZero = remain <= 0f && cur > 0f;
            mCooldownRemain[key] = remain;
            if (reachedZero)
            {
                EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
                {
                    SkillId = key,
                    Remain = 0f,
                    Total = mCooldownTotal.TryGetValue(key, out var total) ? total : 0f
                });
            }
        }
    }

    /// <summary>按各技能自身冷却总时长百分比降低冷却（buff213：返还30%原冷却）</summary>
    public void ReduceCooldownsByPercent(float percent)
    {
        if (percent <= 0f || mCooldownRemain.Count == 0) return;

        s_reduceBuffer.Clear();
        foreach (var key in mCooldownRemain.Keys) s_reduceBuffer.Add(key);

        for (int i = 0; i < s_reduceBuffer.Count; i++)
        {
            string key = s_reduceBuffer[i];
            if (!mCooldownRemain.TryGetValue(key, out float cur) || cur <= 0f) continue;

            float total = mCooldownTotal.TryGetValue(key, out var t) ? t : 0f;
            float remain = Mathf.Max(0f, cur - total * percent);
            bool reachedZero = remain <= 0f && cur > 0f;
            mCooldownRemain[key] = remain;
            if (reachedZero)
            {
                EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
                {
                    SkillId = key,
                    Remain = 0f,
                    Total = total
                });
            }
        }
    }

    /// <summary>取玩家效果控制器（供禁用/法力/技能伤害乘区查询；无则 null）</summary>
    private PlayerEffectController GetPlayerEffect()
    {
        var player = GameManager.Instance?.Player;
        return player != null ? player.GetComponent<PlayerEffectController>() : null;
    }

    /// <summary>技能伤害乘区（buff107/200，如 +50% 技能伤害 → 1.5）</summary>
    private float GetSkillDamageMultiplier()
    {
        var pec = GetPlayerEffect();
        return pec != null ? pec.SkillDamageMultiplier : 1f;
    }

    /// <summary>
    /// 套用玩家冷却缩减后的实际冷却（buff109 20% / buff203 30% / buff101 层数型）。
    /// 必须实时查询：层数型 CDR 会随击杀叠层、受击掉层。下限 0.1s 防止缩减到 0 导致无限连发。
    /// </summary>
    private float ResolveCooldown(float baseCooldown)
    {
        var pec = GetPlayerEffect();
        float cdr = pec != null ? pec.CooldownReduction : 0f;
        return Mathf.Max(0.1f, baseCooldown * (1f - cdr));
    }

    // ===== 释放流程 =====

    /// <summary>HUD 按钮点击 → 校验冷却 → 执行效果</summary>
    private void OnSkillRequested(GameEvents.ActiveSkillRequested evt)
    {
        var config = GameDataMgr.Instance.GetActiveSkill(evt.SkillId);
        if (config == null) return;

        // buff200：所有主动技能被禁用
        var pec = GetPlayerEffect();
        if (pec != null && pec.ActiveSkillsDisabled)
        {
            LogKit.Log($"[ActiveSkillSystem] 主动技能已被禁用（buff200）");
            return;
        }

        if (GetCooldownRemain(config.id) > 0f) return; // 冷却中

        // 法力消耗（buff107 法力翻倍：cost = 技能基础法力×法力乘区；-1=不消耗）
        if (pec != null)
        {
            float cost = config.manaCost > 0 ? config.manaCost * pec.ManaCostMultiplier : 0f;
            if (cost > 0f)
            {
                var player = GameManager.Instance?.Player;
                if (player != null && player.MaxMana > 0f && !player.SpendMana(cost))
                {
                    LogKit.Log($"[ActiveSkillSystem] 法力不足，无法释放 {config.skillName}（需 {cost:F0}）");
                    return;
                }
            }
        }

        // 执行效果
        ExecuteEffect(config);

        // 进入冷却（套用 buff109/203/101 的冷却缩减，取实时值）
        float cd = ResolveCooldown(config.cooldown);
        mCooldownRemain[config.id] = cd;
        mCooldownTotal[config.id] = cd;

        // 广播释放成功 + 冷却变化（音效/特效/HUD 遮罩订阅）
        EventKit.Type.Send(new GameEvents.ActiveSkillReleased { SkillId = config.id });
        EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
        {
            SkillId = config.id,
            Remain = cd,
            Total = cd
        });

        LogKit.Log($"[ActiveSkillSystem] 释放技能: {config.skillName}");
    }

    /// <summary>按效果类型分发执行</summary>
    private void ExecuteEffect(ActiveSkillConfig skill)
    {
        switch (skill.effectType)
        {
            case ActiveSkillEffectType.DamageAoe: ExecuteDamageAoe(skill); break;
            case ActiveSkillEffectType.Heal: ExecuteHeal(skill); break;
            case ActiveSkillEffectType.Buff: ExecuteTempBuff(skill); break;
            case ActiveSkillEffectType.Projectile: ExecuteProjectile(skill); break;
            case ActiveSkillEffectType.TimedOnHitTrue: ExecuteTrueDamage(skill); break;
            case ActiveSkillEffectType.LimitedOnHit: ExecuteLimitedOnHit(skill); break;
            case ActiveSkillEffectType.ConditionalOnHit: ExecuteConditional(skill); break;
            case ActiveSkillEffectType.DefenseBuff: ExecuteDefenseBuff(skill); break;
            case ActiveSkillEffectType.HealPercent: ExecuteHealPercent(skill); break;
            case ActiveSkillEffectType.AttackSpeedBuff: ExecuteAttackSpeedBuff(skill); break;
            case ActiveSkillEffectType.AttackRangeBuff: ExecuteAttackRangeBuff(skill); break;
            case ActiveSkillEffectType.ForwardProjectile: ExecuteForwardProjectile(skill); break;
            case ActiveSkillEffectType.DashStrike: ExecuteDashStrike(skill); break;
            case ActiveSkillEffectType.AoeSlow: ExecuteAoeSlow(skill); break;
            case ActiveSkillEffectType.TimedKillHeal: ExecuteKillHeal(skill); break;
            case ActiveSkillEffectType.TimedMana: ExecuteTimedMana(skill); break;
        }
    }

    /// <summary>效果执行公共入口（武器主动系统复用；冷却/法力/开关校验由调用方管理）</summary>
    public void ExecuteEffectOnly(ActiveSkillConfig skill)
    {
        if (skill == null) return;
        ExecuteEffect(skill);
    }

    // ===== 新增效果执行（TIPS 技能体系） =====

    /// <summary>命中附加真伤（chef 独有1）：持续 duration 秒，每次命中 + onHitFlat + onHitPct×攻 真伤</summary>
    private void ExecuteTrueDamage(ActiveSkillConfig skill)
    {
        mTrueDmgRemain = skill.duration > 0f ? skill.duration : 1f;
        mTrueDmgFlat = skill.onHitFlat;
        mTrueDmgPct = skill.onHitPct;
        LogKit.Log($"[ActiveSkillSystem] 附加真伤 {skill.duration}s（+{mTrueDmgFlat}+{mTrueDmgPct:P0}攻）");
    }

    /// <summary>限次命中附加（chef 独有2/3）：接下来 hits 次攻击额外造成 onHitPct×攻 伤害；带攻速加成则附加临时攻速</summary>
    private void ExecuteLimitedOnHit(ActiveSkillConfig skill)
    {
        mLimitedHits = Mathf.Max(1, skill.hits);
        mLimitedPct = skill.onHitPct;
        if (skill.attackSpeedBonus > 0f && skill.duration > 0f)
            ApplyTempBuff("attackspeed", skill.attackSpeedBonus, skill.duration);
        LogKit.Log($"[ActiveSkillSystem] 接下来 {mLimitedHits} 次攻击附加 {mLimitedPct:P0} 攻");
    }

    /// <summary>条件增伤（共有1/2/3）：持续 duration 秒，满足条件时额外造成 onHitPct×攻 伤害</summary>
    private void ExecuteConditional(ActiveSkillConfig skill)
    {
        mCondRemain = skill.duration > 0f ? skill.duration : 1f;
        mCondType = skill.condType;
        mCondPct = skill.onHitPct;
        LogKit.Log($"[ActiveSkillSystem] 条件增伤 {skill.duration}s（条件 {mCondType}，+{mCondPct:P0}攻）");
    }

    /// <summary>防御提升（chef 独有4）：持续 duration 秒，防御 +defensePct%（按当前物防换算绝对值加给物防/法防）</summary>
    private void ExecuteDefenseBuff(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || skill.duration <= 0f) return;
        float bonus = player.PhysDef * skill.defensePct;
        ApplyTempBuff("defense", bonus, skill.duration);
        LogKit.Log($"[ActiveSkillSystem] 防御提升 {skill.duration}s +{skill.defensePct:P0}");
    }

    /// <summary>回血（chef 独有5 / 共有4 共用 HealPercent 语义）：回复 healPctMaxHp% 最大生命</summary>
    private void ExecuteHealPercent(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;
        player.Health?.Heal(player.MaxHp * skill.healPctMaxHp);
        LogKit.Log($"[ActiveSkillSystem] 回复 {skill.healPctMaxHp:P0} 最大生命");
    }

    /// <summary>攻速提升（pepper 独有1）：持续 duration 秒，攻速 +attackSpeedBonus</summary>
    private void ExecuteAttackSpeedBuff(ActiveSkillConfig skill)
    {
        if (skill.duration <= 0f) return;
        ApplyTempBuff("attackspeed", skill.attackSpeedBonus, skill.duration);
        LogKit.Log($"[ActiveSkillSystem] 攻速提升 {skill.duration}s +{skill.attackSpeedBonus:P0}");
    }

    /// <summary>攻击距离提升（pepper 独有3）：持续 duration 秒，距离 +rangeBonus%</summary>
    private void ExecuteAttackRangeBuff(ActiveSkillConfig skill)
    {
        if (skill.duration <= 0f) return;
        mAttackRangeRemain = skill.duration;
        mAttackRangePct = skill.rangeBonus;
        LogKit.Log($"[ActiveSkillSystem] 攻击距离提升 {skill.duration}s +{skill.rangeBonus:P0}");
    }

    /// <summary>朝移动方向发射单发法球（pepper 独有2）：value×攻 元素伤害</summary>
    private void ExecuteForwardProjectile(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        // 复用第一个远程武器的子弹预制体（无远程武器 → 回退默认子弹）
        var autoAttack = player.GetComponent<AutoAttack>();
        var weapon = autoAttack != null ? autoAttack.GetFirstRangedWeapon() : null;
        string prefab = AssetKeys.BULLET_SEASONING;
        float speed = 10f;
        float range = 8f;
        if (weapon != null && weapon.attackMode == WeaponAttackMode.Ranged && !string.IsNullOrEmpty(weapon.projectilePrefabPath))
        {
            prefab = weapon.projectilePrefabPath;
            speed = weapon.projectileSpeed > 0f ? weapon.projectileSpeed : speed;
            range = weapon.projectileRange > 0f ? weapon.projectileRange : range;
        }

        float damage = DamageCalculator.PlayerRawDamage(
            player.Attack, skill.value, DamageType.Element,
            player.CritChance, player.CritDamage,
            player.PhysDamageBonus, player.ElemDamageBonus, out bool isCrit);
        damage *= GetSkillDamageMultiplier();

        Vector2 dir = player.GetFacingDirection();
        if (dir == Vector2.zero) return;

        var go = PoolFactory.Get(prefab, player.transform.position, Quaternion.identity);
        var bullet = go.GetComponent<Bullet>();
        if (bullet != null)
        {
            bullet.Setup(dir, speed, damage, AttackType.Universal, DamageType.Element, range, false,
                SkillEffectData.None, isCrit);
        }
    }

    /// <summary>突进打击（pepper 独有4）：对最近敌方目标造成 value×攻；命中返还 refundPct% 冷却</summary>
    private void ExecuteDashStrike(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        Vector2 myPos = player.transform.position;
        float radius = Mathf.Max(0.5f, skill.radius);
        float radiusSq = radius * radius;
        EnemySimple nearest = null;
        float minDsq = radiusSq;

        var list = new List<EnemySimple>();
        SpatialGrid.GetInRadius(myPos, radius, list);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null || e.IsDead) continue;
            float dsq = (((Vector2)e.transform.position) - myPos).sqrMagnitude;
            if (dsq <= minDsq) { minDsq = dsq; nearest = e; }
        }

        bool hit = false;
        float damage = player.Attack * skill.value;
        damage *= GetSkillDamageMultiplier();
        if (nearest != null)
        {
            nearest.TakeDamage(damage);
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = nearest.transform.position,
                Damage = damage,
                IsCrit = false,
                Target = nearest.gameObject,
                DamageType = DamageType.Element,
            });
            hit = true;
        }
        else
        {
            var boss = BossHealth.Current;
            if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy
                && (((Vector2)boss.transform.position - myPos).sqrMagnitude <= radiusSq))
            {
                boss.TakeDamage(damage, AttackType.Universal, DamageType.Element, false);
                EventKit.Type.Send(new GameEvents.DamageDealt
                {
                    Position = boss.transform.position,
                    Damage = damage,
                    IsCrit = false,
                    Target = boss.gameObject,
                    DamageType = DamageType.Element,
                });
                hit = true;
            }
        }

        if (hit && skill.refundPct > 0f)
        {
            float total = mCooldownTotal.TryGetValue(skill.id, out var t) ? t : skill.cooldown;
            foreach (var key in new List<string>(mCooldownRemain.Keys))
            {
                float remain = mCooldownRemain[key];
                if (remain > 0f)
                {
                    float refund = Mathf.Min(total * skill.refundPct, remain);
                    mCooldownRemain[key] = remain - refund;
                }
            }
            LogKit.Log($"[ActiveSkillSystem] 突进命中，返还 {skill.refundPct:P0} 冷却");
        }
    }

    /// <summary>范围伤害+减速（pepper 独有5）：周围 radius 内 value×攻 伤害，敌人减速 slowPct 持续 duration 秒</summary>
    private void ExecuteAoeSlow(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        float damage = DamageCalculator.PlayerRawDamage(
            player.Attack, skill.value, DamageType.Element,
            player.CritChance, player.CritDamage,
            player.PhysDamageBonus, player.ElemDamageBonus, out bool isCrit);
        damage *= GetSkillDamageMultiplier();

        Vector3 myPos = player.transform.position;
        float radius = Mathf.Max(0.5f, skill.radius);
        float radiusSq = radius * radius;

        var slowed = new List<EnemySimple>();
        var list = new List<EnemySimple>();
        SpatialGrid.GetInRadius(myPos, radius, list);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null) continue;
            e.TakeDamage(damage);
            if (skill.slowPct > 0f && skill.duration > 0f && e.SpeedMultiplier >= 1f)
            {
                e.SpeedMultiplier = Mathf.Max(0.1f, 1f - skill.slowPct);
                slowed.Add(e);
            }
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = e.transform.position,
                Damage = damage,
                IsCrit = isCrit,
                Target = e.gameObject,
                DamageType = DamageType.Element,
            });
        }

        if (slowed.Count > 0) StartCoroutine(RestoreEnemySpeed(slowed, skill.duration));

        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy
            && ((Vector2)(boss.transform.position - myPos)).sqrMagnitude <= radiusSq)
        {
            boss.TakeDamage(damage, AttackType.Universal, DamageType.Element, isCrit);
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = boss.transform.position,
                Damage = damage,
                IsCrit = isCrit,
                Target = boss.gameObject,
                DamageType = DamageType.Element,
            });
        }
    }

    /// <summary>范围伤害+减速后恢复敌方移速（pepper 独有5）</summary>
    private IEnumerator RestoreEnemySpeed(List<EnemySimple> slowed, float duration)
    {
        if (duration > 0f) yield return new WaitForSeconds(duration);
        for (int i = 0; i < slowed.Count; i++)
        {
            var e = slowed[i];
            if (e != null && e.SpeedMultiplier < 1f) e.SpeedMultiplier = 1f;
        }
    }

    /// <summary>限时击杀回血（共有4）：持续 duration 秒，击杀回复 healPctMaxHp% 最大生命</summary>
    private void ExecuteKillHeal(ActiveSkillConfig skill)
    {
        mKillHealRemain = skill.duration > 0f ? skill.duration : 1f;
        mKillHealPct = skill.healPctMaxHp;
        LogKit.Log($"[ActiveSkillSystem] 击杀回血 {skill.duration}s +{mKillHealPct:P0}最大生命");
    }

    /// <summary>限时回蓝（共有5）：持续 duration 秒，命中回复 hitManaPct%、击杀额外 killManaPct% 最大法力</summary>
    private void ExecuteTimedMana(ActiveSkillConfig skill)
    {
        mManaRemain = skill.duration > 0f ? skill.duration : 1f;
        mHitManaPct = skill.hitManaPct;
        mKillManaPct = skill.killManaPct;
        LogKit.Log($"[ActiveSkillSystem] 限时回蓝 {skill.duration}s（命中 {mHitManaPct:P0}，击杀 {mKillManaPct:P0}）");
    }

    /// <summary>AOE伤害：以玩家为中心，对半径内敌人/BOSS 造成 value×攻击力 伤害</summary>
    private void ExecuteDamageAoe(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        float damage = DamageCalculator.PlayerRawDamage(
            player.Attack, skill.value, DamageType.Physical,
            player.CritChance, player.CritDamage,
            player.PhysDamageBonus, player.ElemDamageBonus, out bool isCrit);
        damage *= GetSkillDamageMultiplier();

        Vector3 myPos = player.transform.position;
        float radius = skill.radius;
        float radiusSq = radius * radius;

        // 命中半径内敌人（空间网格查询，O(k)）
        SpatialGrid.GetInRadius(myPos, radius, mQueryList);
        for (int i = 0; i < mQueryList.Count; i++)
        {
            var e = mQueryList[i];
            if (e == null) continue;

            e.TakeDamage(damage);
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = e.transform.position,
                Damage = damage,
                IsCrit = isCrit,
                Target = e.gameObject,
                DamageType = DamageType.Physical,
            });
        }

        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy)
        {
            if (((Vector2)(boss.transform.position - myPos)).sqrMagnitude <= radiusSq)
            {
                boss.TakeDamage(damage, AttackType.Universal, DamageType.Physical, isCrit);
                EventKit.Type.Send(new GameEvents.DamageDealt
                {
                    Position = boss.transform.position,
                    Damage = damage,
                    IsCrit = isCrit,
                    DamageType = DamageType.Physical,
                    Target = boss.gameObject
                });
            }
        }

        EventKit.Type.Send(new GameEvents.BulletHit { HitPoint = myPos, Damage = damage });
    }

    /// <summary>回血：回复 value 点生命</summary>
    private void ExecuteHeal(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;
        player.Health?.Heal(skill.value);
        LogKit.Log($"[ActiveSkillSystem] 药膳暖汤回复 {skill.value} 点生命");
    }

    /// <summary>临时增益：duration 秒内加成 buffValue（按 buffType 属性）</summary>
    private void ExecuteTempBuff(ActiveSkillConfig skill)
    {
        if (string.IsNullOrEmpty(skill.buffType) || skill.duration <= 0f) return;
        ApplyTempBuff(skill.buffType, skill.buffValue, skill.duration);
    }

    /// <summary>写入临时Buff（先移除同类型旧Buff，再叠加新值）供进阶技能/常规 Buff 共用</summary>
    private void ApplyTempBuff(string buffType, float value, float duration)
    {
        if (string.IsNullOrEmpty(buffType) || duration <= 0f) return;
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        RemoveTempBuffValue(buffType);
        mTempBuffs[buffType] = new KeyValuePair<float, float>(value, duration);
        ApplyTempBuffValue(buffType, value);
    }

    /// <summary>按 buffType 字符串映射并叠加到玩家属性</summary>
    private void ApplyTempBuffValue(string buffType, float value)
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        var bonus = new PlayerAttribute();
        switch (buffType.ToLowerInvariant())
        {
            case "movespeed": bonus.MoveSpeed = value; break;
            case "attack": bonus.Attack = value; break;
            case "maxhp": bonus.MaxHp = value; break;
            case "critchance": bonus.CritChance = value; break;
            case "critdamage": bonus.CritDamage = value; break;
            case "physdamage": bonus.PhysDamageBonus = value; break;
            case "elemdamage": bonus.ElemDamageBonus = value; break;
            case "defense": bonus.PhysDef = value; bonus.ElemDef = value; break;
            case "attackspeed":
                // 攻速走 SkillSystem 追加通道（不在 BonusAttr，由断点快照统一持久化）
                if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(value);
                return;
            default:
                mTempBuffs.Remove(buffType);
                return;
        }
        player.AddBonusAttribute(bonus);
    }

    /// <summary>移除临时Buff（负值抵消，避免残留）</summary>
    private void RemoveTempBuffValue(string buffType)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !mTempBuffs.ContainsKey(buffType)) return;
        ApplyTempBuffValue(buffType, -mTempBuffs[buffType].Key);
    }

    /// <summary>弹幕：向 8 个方向发射子弹（元素伤害，用当前武器子弹预制体，近战回退默认子弹）</summary>
    private void ExecuteProjectile(ActiveSkillConfig skill)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        // 复用第一个远程武器的子弹预制体（无远程武器 → 回退默认子弹）
        var autoAttack = player.GetComponent<AutoAttack>();
        var weapon = autoAttack != null ? autoAttack.GetFirstRangedWeapon() : null;
        string prefab = AssetKeys.BULLET_SEASONING;
        float speed = 10f;
        float range = 8f;
        if (weapon != null && weapon.attackMode == WeaponAttackMode.Ranged && !string.IsNullOrEmpty(weapon.projectilePrefabPath))
        {
            prefab = weapon.projectilePrefabPath;
            speed = weapon.projectileSpeed > 0f ? weapon.projectileSpeed : speed;
            range = weapon.projectileRange > 0f ? weapon.projectileRange : range;
        }

        float damage = DamageCalculator.PlayerRawDamage(
            player.Attack, skill.value, DamageType.Element,
            player.CritChance, player.CritDamage,
            player.PhysDamageBonus, player.ElemDamageBonus, out bool isCrit);
        damage *= GetSkillDamageMultiplier();

        int count = 8;
        float baseAngle = Random.Range(0f, 360f);
        for (int i = 0; i < count; i++)
        {
            float angle = (baseAngle + 360f * i / count) * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            var go = PoolFactory.Get(prefab, player.transform.position, Quaternion.identity);
            var bullet = go.GetComponent<Bullet>();
            if (bullet != null)
            {
                bullet.Setup(dir, speed, damage, AttackType.Universal, DamageType.Element, range, false,
                    SkillEffectData.None, isCrit);
            }
        }
    }

    /// <summary>
    /// 键盘轮询触发技能（仅存活时）。
    /// 键位不写死：统一从 KeyBindingManager 取（默认 1/2/3=武器主动槽 0/1/2，K/J=角色技能槽 0/1），
    /// 玩家可在设置面板 Control 页签改绑。
    /// </summary>
    private void PollSlotInput()
    {
        // 武器主动：对应武器槽（0/1/2）的主动技能（冷却/法力/禁用校验在 WeaponActiveSystem）
        if (KeyBindingManager.IsDown(GameInputAction.WeaponSkill1))
        {
            RequestWeaponActive(0);
        }
        else if (KeyBindingManager.IsDown(GameInputAction.WeaponSkill2))
        {
            RequestWeaponActive(1);
        }
        else if (KeyBindingManager.IsDown(GameInputAction.WeaponSkill3))
        {
            RequestWeaponActive(2);
        }
        // 角色技能组（RosterManager.GetCurrentSkillIds：[可选1][可选2]，槽序号即列表索引）
        else if (KeyBindingManager.IsDown(GameInputAction.Skill1))
        {
            TriggerBySlot(0);
        }
        else if (KeyBindingManager.IsDown(GameInputAction.Skill2))
        {
            TriggerBySlot(1);
        }
    }

    /// <summary>上报武器主动释放请求（与 HUD 点击同一链路；槽位无主动技能时静默跳过）</summary>
    private static void RequestWeaponActive(int weaponIndex)
    {
        if (WeaponActiveSystem.HasInstance && WeaponActiveSystem.Instance.HasActiveAt(weaponIndex))
            EventKit.Type.Send(new GameEvents.WeaponActiveRequested { WeaponIndex = weaponIndex });
    }

    /// <summary>按技能栏槽位序号触发一次主动技能（复用 HUD 按钮同一校验/施放链路）</summary>
    private void TriggerBySlot(int index)
    {
        var player = GameManager.Instance?.Player;
        if (player == null || !player.IsAlive) return;

        if (index < 0 || index >= mSkills.Count) return;
        var skill = mSkills[index];
        if (skill == null || string.IsNullOrEmpty(skill.id)) return;

        EventKit.Type.Send(new GameEvents.ActiveSkillRequested { SkillId = skill.id });
    }

    private void Update()
    {
        // 键盘触发主动技能：L=武器主动(槽0)、K=角色技能1(槽1)、J=角色技能2(槽2)
        PollSlotInput();

        // 技能触发的限时战斗修饰到期递减（命中附加真伤/条件增伤/击杀回血/回蓝/攻击距离）
        float dt = Time.deltaTime;
        if (mTrueDmgRemain > 0f) mTrueDmgRemain = Mathf.Max(0f, mTrueDmgRemain - dt);
        if (mCondRemain > 0f) mCondRemain = Mathf.Max(0f, mCondRemain - dt);
        if (mKillHealRemain > 0f) mKillHealRemain = Mathf.Max(0f, mKillHealRemain - dt);
        if (mManaRemain > 0f) mManaRemain = Mathf.Max(0f, mManaRemain - dt);
        if (mAttackRangeRemain > 0f) mAttackRangeRemain = Mathf.Max(0f, mAttackRangeRemain - dt);

        // 冷却递减（递减后广播变化供 HUD 刷新）
        if (mCooldownRemain.Count > 0)
        {
            // 复用静态缓冲做键快照（原来每帧 new List<string>(Keys) 会产生持续性 GC 分配）
            s_keyBuffer.Clear();
            foreach (var key in mCooldownRemain.Keys) s_keyBuffer.Add(key);

            for (int i = 0; i < s_keyBuffer.Count; i++)
            {
                string key = s_keyBuffer[i];
                if (!mCooldownRemain.TryGetValue(key, out float remain) || remain <= 0f) continue;

                remain = Mathf.Max(0f, remain - Time.deltaTime);
                mCooldownRemain[key] = remain;
                if (remain <= 0f)
                {
                    EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
                    {
                        SkillId = key,
                        Remain = 0f,
                        Total = mCooldownTotal.TryGetValue(key, out var total) ? total : 0f
                    });
                }
            }
        }

        // 临时Buff到期移除
        if (mTempBuffs.Count > 0)
        {
            s_keyBuffer.Clear();
            foreach (var key in mTempBuffs.Keys) s_keyBuffer.Add(key);

            for (int i = 0; i < s_keyBuffer.Count; i++)
            {
                string key = s_keyBuffer[i];
                if (!mTempBuffs.TryGetValue(key, out var kv)) continue;

                float remain = kv.Value - Time.deltaTime;
                if (remain <= 0f)
                {
                    RemoveTempBuffValue(key);
                    mTempBuffs.Remove(key);
                }
                else
                {
                    mTempBuffs[key] = new KeyValuePair<float, float>(kv.Key, remain);
                }
            }
        }
    }
}
