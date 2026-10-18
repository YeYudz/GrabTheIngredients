using UnityEngine;

/// <summary>
/// 持续伤害状态（DoT）：由 EffectApplier 挂到目标上，按固定间隔结算伤害。
/// </summary>
public class StatusEffect : MonoBehaviour
{
    private float mDamagePerTick;   // 每跳伤害
    private float mTickInterval;    // 跳动间隔（秒）
    private float mRemainingTime;   // 剩余总时长
    private float mTickTimer;

    /// <summary>是否仍在生效</summary>
    public bool IsActive => mRemainingTime > 0f;

    /// <summary>
    /// 开始一个持续伤害效果（重复调用则叠加时长，取更大伤害）
    /// </summary>
    /// <param name="damagePerTick">每跳伤害</param>
    /// <param name="tickInterval">跳动间隔（秒）</param>
    /// <param name="duration">总时长（秒）</param>
    public void Apply(float damagePerTick, float tickInterval, float duration)
    {
        mDamagePerTick = Mathf.Max(mDamagePerTick, damagePerTick);
        mTickInterval = Mathf.Max(0.1f, tickInterval);
        mRemainingTime = Mathf.Max(mRemainingTime, duration);
        mTickTimer = 0f;
    }

    private void Update()
    {
        if (mRemainingTime <= 0f) return;

        mRemainingTime -= Time.deltaTime;
        mTickTimer += Time.deltaTime;

        if (mTickTimer >= mTickInterval)
        {
            mTickTimer = 0f;
            DealTick();
        }

        if (mRemainingTime <= 0f)
        {
            // 效果结束，移除组件
            Destroy(this);
        }
    }

    private void DealTick()
    {
        // 敌人走 Health 组件，BOSS 走 BossHealth（带弱点类型为 Universal，不触发破韧）
        var enemy = GetComponent<EnemySimple>();
        if (enemy != null)
        {
            enemy.TakeDamage(mDamagePerTick);
            return;
        }

        var boss = GetComponent<BossHealth>();
        if (boss != null && !boss.IsDead)
        {
            boss.TakeDamage(mDamagePerTick, AttackType.Universal, DamageType.Element, false);
        }
    }
}
