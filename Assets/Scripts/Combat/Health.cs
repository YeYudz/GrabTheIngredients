using System;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 通用血量组件（玩家 / 敌人 / BOSS 共用）：只负责数值，显示由 UI 通过事件驱动。
/// </summary>
public class Health : MonoBehaviour
{
    private float mMaxHp;
    private float mCurrentHp;

    /// <summary>是否死亡</summary>
    public bool IsDead => mCurrentHp <= 0f;

    /// <summary>当前血量比例（0~1）</summary>
    public float HpRatio => mMaxHp <= 0f ? 0f : mCurrentHp / mMaxHp;

    /// <summary>当前血量</summary>
    public float CurrentHp => mCurrentHp;

    /// <summary>最大血量（处决阈值计算用）</summary>
    public float MaxHp => mMaxHp;

    /// <summary>死亡事件（由订阅方决定后续逻辑，实现解耦）</summary>
    public event Action OnDied;

    /// <summary>伤害吸收委托（护盾等；返回被吸收的伤害量，实际扣血 = damage - absorbed；敌人不会设置，无影响）</summary>
    public System.Func<float, float> DamageAbsorber;

    /// <summary>初始化血量（由挂载方调用）</summary>
    public void Init(float maxHp)
    {
        mMaxHp = maxHp;
        mCurrentHp = maxHp;
    }

    /// <summary>临时增加最大生命（增益用；当前血量同步上涨）</summary>
    public void AddMaxHp(float amount)
    {
        if (amount <= 0f) return;
        mMaxHp += amount;
        mCurrentHp += amount;
    }

    /// <summary>还原最大生命（增益结束；当前血量裁剪到上限）</summary>
    public void RemoveMaxHp(float amount)
    {
        if (amount <= 0f) return;
        mMaxHp = Mathf.Max(1f, mMaxHp - amount);
        mCurrentHp = Mathf.Min(mCurrentHp, mMaxHp);
    }

    /// <summary>受到伤害</summary>
    public void TakeDamage(float damage)
    {
        if (IsDead || damage <= 0f) return;

        // 伤害吸收（护盾等）：实际扣血 = 伤害 - 吸收量
        float absorbed = 0f;
        if (DamageAbsorber != null) absorbed = Mathf.Max(0f, DamageAbsorber(damage));
        mCurrentHp = Mathf.Max(0f, mCurrentHp - (damage - absorbed));
        NotifyDamaged(damage);

        if (IsDead)
        {
            OnDied?.Invoke();
            BroadcastDead();
        }
    }

    /// <summary>治疗</summary>
    public void Heal(float amount)
    {
        if (IsDead) return;
        mCurrentHp = Mathf.Min(mMaxHp, mCurrentHp + amount);
    }

    /// <summary>设置当前血量（断点恢复用，含上下限裁剪）</summary>
    public void SetHp(float hp)
    {
        mCurrentHp = Mathf.Clamp(hp, 0f, mMaxHp);
    }

    /// <summary>广播受伤事件（玩家走玩家事件，敌人走通用事件）</summary>
    private void NotifyDamaged(float damage)
    {
        // 玩家受伤 → 通过事件通知 UI/音效（面板只订阅事件，不直接引用）
        if (GetComponent<PlayerController>() != null)
        {
            EventKit.Type.Send(new GameEvents.PlayerDamaged
            {
                Damage = damage,
                CurrentHp = mCurrentHp,
                MaxHp = mMaxHp
            });
        }
    }

    /// <summary>广播死亡事件</summary>
    private void BroadcastDead()
    {
        if (GetComponent<PlayerController>() != null)
        {
            EventKit.Type.Send(new GameEvents.PlayerDied());
        }
        else if (GetComponent<EnemySimple>() != null)
        {
            var enemy = GetComponent<EnemySimple>();
            EventKit.Type.Send(new GameEvents.EnemyKilled
            {
                EnemyId = enemy.ConfigId,
                Position = transform.position,
                HitDirection = enemy.LastHitDirection
            });
        }
        // BOSS 死亡在 Step5 由 BossHealth 单独处理
    }
}
