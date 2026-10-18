using System.Collections;
using UnityEngine;
using YokiFrame;

/// <summary>
/// BOSS 血量组件：独立于通用 Health，因 BOSS 另有阶段切换、弱点匹配、破韧条与虚弱态。
/// 血量归零不触发死亡，只通知音游开始；生死由烹饪音游的结果裁定。
/// </summary>
public class BossHealth : MonoBehaviour
{
    /// <summary>当前场景中存活的 BOSS（供 AutoAttack 索敌等外部访问）</summary>
    public static BossHealth Current { get; private set; }

    // ===== 配置数据 =====
    private BossConfig mConfig;
    private int mCurrentPhaseIndex;

    // ===== 血量 =====
    private float mMaxHp;
    private float mCurrentHp;

    // ===== 破韧条 =====
    private float mBreakBar;
    private bool mIsBreakBarFull;  // 是否已破韧（防止重复触发）

    // ===== 虚弱状态（奖励状态，非必经） =====
    private bool mIsWeakened;
    private Coroutine mWeaknessCoroutine;
    private float mWeaknessRemain;  // 虚弱剩余秒（断点存档用）

    // ===== 状态 =====
    private bool mIsDead;
    private bool mIsCookingTriggered;  // 血量清空已触发音游（防止重复触发）

    /// <summary>当前阶段配置</summary>
    public BossPhase CurrentPhase => mConfig?.phases != null && mCurrentPhaseIndex < mConfig.phases.Count
        ? mConfig.phases[mCurrentPhaseIndex] : null;

    /// <summary>当前血量</summary>
    public float CurrentHp => mCurrentHp;

    /// <summary>最大血量</summary>
    public float MaxHp => mMaxHp;

    /// <summary>血量比例 0~1</summary>
    public float HpRatio => mMaxHp <= 0f ? 0f : mCurrentHp / mMaxHp;

    /// <summary>破韧条比例 0~1</summary>
    public float BreakBarRatio => mConfig == null ? 0f : mBreakBar / mConfig.breakBarMax;

    /// <summary>是否虚弱</summary>
    public bool IsWeakened => mIsWeakened;

    /// <summary>是否已死亡</summary>
    public bool IsDead => mIsDead;

    /// <summary>血量清空后是否已进入/进入过烹饪音游（断点保存用来判定"音游进行中"）</summary>
    public bool IsCookingTriggered => mIsCookingTriggered;

    /// <summary>BOSS ID</summary>
    public string BossId => mConfig?.id;

    // ===== 断点存档只读接口 =====

    /// <summary>当前阶段索引</summary>
    public int CurrentPhaseIndex => mCurrentPhaseIndex;

    /// <summary>破韧条当前值</summary>
    public float BreakBarValue => mBreakBar;

    /// <summary>是否已破韧</summary>
    public bool IsBreakBarFull => mIsBreakBarFull;

    /// <summary>虚弱剩余秒（未虚弱为 0）</summary>
    public float GetWeaknessRemain() => mIsWeakened ? mWeaknessRemain : 0f;

    /// <summary>最大生命值倍率（阶段难度覆写；由 BossSpawner 在 Init 前设置）</summary>
    private float mHpScale = 1f;

    /// <summary>设置最大生命值倍率（须在 Init 之前调用；&lt;=0 按 1 处理）</summary>
    public void SetHpScale(float scale) => mHpScale = scale > 0f ? scale : 1f;

    /// <summary>初始化（由 BossController 在生成时调用）</summary>
    public void Init(BossConfig config)
    {
        Current = this;
        mConfig = config;
        mMaxHp = config.maxHp * mHpScale;
        mCurrentHp = mMaxHp;
        mCurrentPhaseIndex = 0;
        mBreakBar = 0f;
        mIsBreakBarFull = false;
        mIsWeakened = false;
        mIsDead = false;
        mIsCookingTriggered = false;

        EventKit.Type.Send(new GameEvents.BossSpawned
        {
            BossId = config.id,
            MaxHp = mMaxHp
        });

        // 广播初始阶段
        var phase = CurrentPhase;
        if (phase != null)
        {
            EventKit.Type.Send(new GameEvents.BossPhaseChanged
            {
                PhaseIndex = phase.phaseIndex,
                WeaknessType = phase.weaknessType
            });
        }

        LogKit.Log($"[BossHealth] {config.bossName} 生成，HP={mMaxHp}，阶段0 弱点={phase?.weaknessType}");
    }

    /// <summary>受到伤害（带弱点判定：弱点伤害高+破韧快，非弱点伤害低+破韧慢）</summary>
    public void TakeDamage(float damage, AttackType attackType)
        => TakeDamage(damage, attackType, DamageType.Physical, false);

    /// <summary>
    /// 受到伤害（表现层完整版）：damageType / isCrit 只用于飘字区分「物理·元素 / 暴击」，
    /// 不参与伤害数值计算（暴击已经由 DamageCalculator 结算在 damage 里）。
    /// </summary>
    public void TakeDamage(float damage, AttackType attackType, DamageType damageType, bool isCrit)
    {
        if (mIsDead || damage <= 0f) return;

        var phase = CurrentPhase;
        if (phase == null)
        {
            ApplyDamage(damage);
            // 无阶段配置时也要给飘字一个出口（否则表现层收不到伤害）
            EventKit.Type.Send(new GameEvents.BossDamageApplied
            {
                Position = transform.position,
                Damage = damage,
                IsCrit = isCrit,
                IsWeaknessHit = false,
                DamageType = damageType
            });
            return;
        }

        bool isWeaknessHit = attackType == phase.weaknessType;

        // 弱点匹配 → 伤害×倍率
        float finalDamage = isWeaknessHit ? damage * phase.weaknessDamageMultiplier : damage;

        // 破韧条累加：弱点效率高，非弱点效率低（但仍可累积，而非无法破韧）
        if (!mIsBreakBarFull)
        {
            float ratio = isWeaknessHit ? 1f : Mathf.Max(0f, mConfig.nonWeaknessBreakRatio);
            mBreakBar = Mathf.Min(mConfig.breakBarMax, mBreakBar + phase.breakBarOnHit * ratio);
            if (mBreakBar >= mConfig.breakBarMax)
            {
                mIsBreakBarFull = true;
                EnterWeakness();
            }
            LogKit.Log($"[BossHealth] {(isWeaknessHit ? "弱点" : "非弱点")}命中 伤害={finalDamage:F0}，破韧条={mBreakBar:F0}/{mConfig.breakBarMax}");
        }

        // 虚弱状态 → 所有伤害×weaknessMultiplier（奖励：加速击杀）
        if (mIsWeakened)
        {
            finalDamage *= mConfig.weaknessMultiplier;
        }

        ApplyDamage(finalDamage);

        // 广播实际伤害（含弱点/虚弱倍率后的最终值，供飘字显示真实伤害）
        EventKit.Type.Send(new GameEvents.BossDamageApplied
        {
            Position = transform.position,
            Damage = finalDamage,
            IsCrit = isCrit,
            IsWeaknessHit = isWeaknessHit,
            DamageType = damageType
        });
    }

    /// <summary>应用伤害 + 阶段切换检测 + 血量清空触发音游</summary>
    private void ApplyDamage(float damage)
    {
        // 血量正常扣减（无锁血：血量可先于破韧条被清空）
        mCurrentHp = Mathf.Max(0f, mCurrentHp - damage);

        // 阶段切换检测（从高阶段往低阶段检测，避免跨阶段）
        if (mConfig?.phases != null)
        {
            for (int i = mConfig.phases.Count - 1; i > mCurrentPhaseIndex; i--)
            {
                var p = mConfig.phases[i];
                if (mCurrentHp <= mMaxHp * p.hpThreshold)
                {
                    ChangePhase(i);
                    break;
                }
            }
        }

        // 血量清空 → 直接进入烹饪音游阶段（不依赖破韧/虚弱；只触发一次）
        if (mCurrentHp <= 0f && !mIsCookingTriggered)
        {
            mIsCookingTriggered = true;
            LogKit.Log("[BossHealth] BOSS 血量清空，进入烹饪音游阶段");

            // 血量清空时若还在虚弱计时，停止（进入音游）
            if (mWeaknessCoroutine != null)
            {
                StopCoroutine(mWeaknessCoroutine);
                mWeaknessCoroutine = null;
            }
            mIsWeakened = false;

            EventKit.Type.Send(new GameEvents.BossHpDepleted { BossId = mConfig.id });
        }

        // 事件驱动：广播血量/破韧条变化（HUD 订阅更新，替代每帧轮询 FindObjectOfType）
        EventKit.Type.Send(new GameEvents.BossHealthChanged
        {
            BossId = mConfig?.id,
            CurrentHp = mCurrentHp,
            MaxHp = mMaxHp,
            HpRatio = HpRatio,
            BreakBarRatio = BreakBarRatio
        });

        // 注意：BOSS 不会因血量归零而死亡，死亡由烹饪音游结果决定
    }

    /// <summary>切换阶段</summary>
    private void ChangePhase(int newPhaseIndex)
    {
        if (newPhaseIndex == mCurrentPhaseIndex) return;
        mCurrentPhaseIndex = newPhaseIndex;
        var phase = CurrentPhase;
        LogKit.Log($"[BossHealth] 阶段切换 → {newPhaseIndex}，弱点={phase?.weaknessType}");

        EventKit.Type.Send(new GameEvents.BossPhaseChanged
        {
            PhaseIndex = newPhaseIndex,
            WeaknessType = phase != null ? phase.weaknessType : AttackType.Universal
        });
    }

    /// <summary>进入虚弱状态（破韧奖励：伤害加成，加速击杀；不触发音游）</summary>
    private void EnterWeakness()
    {
        if (mIsWeakened) return;
        mIsWeakened = true;
        LogKit.Log($"[BossHealth] 破韧！进入虚弱状态 {mConfig.weaknessDuration}s（伤害×{mConfig.weaknessMultiplier}）");

        // 破韧事件（音效/提示订阅）
        EventKit.Type.Send(new GameEvents.BossBreakBarFull { BossId = mConfig.id });

        if (mWeaknessCoroutine != null) StopCoroutine(mWeaknessCoroutine);
        mWeaknessCoroutine = StartCoroutine(WeaknessRoutine(mConfig.weaknessDuration));
    }

    /// <summary>虚弱计时协程（结束后恢复常态，不再触发音游）</summary>
    private IEnumerator WeaknessRoutine(float duration)
    {
        mWeaknessRemain = duration;
        while (mWeaknessRemain > 0f)
        {
            yield return null;
            mWeaknessRemain -= Time.deltaTime;
        }
        mWeaknessRemain = 0f;
        mIsWeakened = false;
        mWeaknessCoroutine = null;
        LogKit.Log("[BossHealth] 虚弱结束，BOSS 恢复常态");
    }

    /// <summary>断点恢复：还原血量/阶段/破韧/虚弱（在 BossSpawner.Spawn + Init 之后调用）</summary>
    public void RestoreState(float hp, int phaseIndex, float breakBar, bool breakBarFull, bool weakened, float weaknessRemain)
    {
        if (mConfig == null) return;

        mCurrentHp = Mathf.Clamp(hp, 0f, mMaxHp);
        mCurrentPhaseIndex = Mathf.Clamp(phaseIndex, 0, (mConfig.phases != null && mConfig.phases.Count > 0) ? mConfig.phases.Count - 1 : 0);
        mBreakBar = Mathf.Clamp(breakBar, 0f, mConfig.breakBarMax);
        mIsBreakBarFull = breakBarFull;
        mIsCookingTriggered = mCurrentHp <= 0f; // 血量已空 = 已触发过音游
        mIsWeakened = weakened;
        if (weakened && weaknessRemain > 0f)
        {
            if (mWeaknessCoroutine != null) StopCoroutine(mWeaknessCoroutine);
            mWeaknessCoroutine = StartCoroutine(WeaknessRoutine(weaknessRemain));
        }

        // 同步 HUD（血量/破韧条/阶段）
        EventKit.Type.Send(new GameEvents.BossHealthChanged
        {
            BossId = mConfig.id,
            CurrentHp = mCurrentHp,
            MaxHp = mMaxHp,
            HpRatio = HpRatio,
            BreakBarRatio = BreakBarRatio
        });
        var phase = CurrentPhase;
        if (phase != null)
        {
            EventKit.Type.Send(new GameEvents.BossPhaseChanged
            {
                PhaseIndex = phase.phaseIndex,
                WeaknessType = phase.weaknessType
            });
        }
        LogKit.Log($"[BossHealth] 断点恢复：HP {mCurrentHp:F0}/{mMaxHp:F0}，阶段 {mCurrentPhaseIndex}");
    }

    /// <summary>烹饪音游胜利 → BOSS 真正死亡</summary>
    public void Die()
    {
        if (mIsDead) return;
        mIsDead = true;
        Current = null;
        if (mWeaknessCoroutine != null) StopCoroutine(mWeaknessCoroutine);

        LogKit.Log($"[BossHealth] {mConfig.bossName} 被击败！");
        EventKit.Type.Send(new GameEvents.BossDied
        {
            BossId = mConfig.id,
            Position = transform.position
        });

        Destroy(gameObject, 0.5f);
    }

    /// <summary>烹饪音游失败 → BOSS 复活（恢复30%血量）</summary>
    public void Revive()
    {
        if (mIsDead) return;
        float healAmount = mMaxHp * 0.3f;
        mCurrentHp = Mathf.Min(mMaxHp, mCurrentHp + healAmount);

        // 重置破韧条与状态，允许再次破韧/再次通过清空血量触发音游
        mBreakBar = 0f;
        mIsBreakBarFull = false;
        mIsCookingTriggered = false;
        mIsWeakened = false;
        if (mWeaknessCoroutine != null)
        {
            StopCoroutine(mWeaknessCoroutine);
            mWeaknessCoroutine = null;
        }

        LogKit.Log($"[BossHealth] {mConfig.bossName} 复活，恢复 {healAmount:F0} HP，当前 {mCurrentHp:F0}/{mMaxHp}");
        EventKit.Type.Send(new GameEvents.BossRevived
        {
            BossId = mConfig.id,
            CurrentHp = mCurrentHp,
            MaxHp = mMaxHp
        });

        // 同步广播血量/破韧条重置（HUD 血条回到复活后状态）
        EventKit.Type.Send(new GameEvents.BossHealthChanged
        {
            BossId = mConfig.id,
            CurrentHp = mCurrentHp,
            MaxHp = mMaxHp,
            HpRatio = HpRatio,
            BreakBarRatio = BreakBarRatio
        });
    }

    private void OnDestroy()
    {
        if (Current == this) Current = null;
    }
}
