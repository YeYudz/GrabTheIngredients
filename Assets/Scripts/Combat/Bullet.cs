using UnityEngine;
using YokiFrame;

/// <summary>
/// 子弹：从对象池取出，负责飞行与命中判定，命中后归还池中以免产生 GC 压力。
/// </summary>
public class Bullet : MonoBehaviour
{
    private Vector2 mDirection;
    private float mSpeed;
    private float mDamage;
    private AttackType mAttackType;
    private DamageType mDamageType;  // Step10：物理/元素伤害标签
    private bool mIsPiercing;
    private SkillEffectData mEffect; // 特殊效果（Step8：爆炸/燃烧）
    private bool mIsDead;          // 防止重复命中
    private float mLifeTime;       // 存活计时（超过则回收）
    private float mMaxDistance;    // 最大飞行距离（超过则回收，防止跨屏攻击）
    private float mTraveledDistance;
    private bool mIsCrit;          // 是否暴击（表现层飘字用）
    private bool mFromEffect;      // 是否为效果弹（PlayerEffectController 发射的弩箭等）：命中不触发 OnHit 类效果，防止"弹触发弹"无限连锁

    /// <summary>所属对象池 key（由 PoolFactory.Get 时自动写入 Poolable 组件）</summary>
    private string mPoolKey;

    /// <summary>由 AutoAttack 调用，注入子弹参数（含物理/元素标签与特殊效果）</summary>
    public void Setup(Vector2 direction, float speed, float damage, AttackType attackType,
        DamageType damageType, float maxDistance, bool isPiercing, SkillEffectData effect = default,
        bool isCrit = false, bool fromEffectProjectile = false)
    {
        mDirection = direction.normalized;

        // 弹头按"朝上"绘制（本地 +Y）：旋转 方向角-90°，使弹头指向飞行方向（即目标）
        float angleDeg = Mathf.Atan2(mDirection.y, mDirection.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, angleDeg - 90f);

        mSpeed = speed;
        mDamage = damage;
        mAttackType = attackType;
        mDamageType = damageType;
        mIsPiercing = isPiercing;
        mEffect = effect;
        mIsCrit = isCrit;
        mFromEffect = fromEffectProjectile; // 每次 Setup 都重置：池化复用时普通弹不会被上一发的标记污染
        mIsDead = false;
        mLifeTime = 3f; // 3秒后自动回收
        mMaxDistance = maxDistance;
        mTraveledDistance = 0f;
    }

    private void Update()
    {
        if (mIsDead) return;

        // 移动
        float step = mSpeed * Time.deltaTime;
        transform.position += (Vector3)(mDirection * step);
        mTraveledDistance += step;

        // 超过最大飞行距离 → 回收（防止跨屏打屏幕外的怪）
        if (mTraveledDistance >= mMaxDistance)
        {
            Recycle();
            return;
        }

        // 超时回收
        mLifeTime -= Time.deltaTime;
        if (mLifeTime <= 0f)
        {
            Recycle();
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (mIsDead) return;

        // 命中 BOSS（优先检测，BOSS 不挂 EnemySimple）
        var boss = other.GetComponent<BossHealth>();
        if (boss != null)
        {
            float bossReal = ResolveDamage(mDamage, null, boss);
            boss.TakeDamage(bossReal, mAttackType, mDamageType, mIsCrit);
            // 触发特殊效果（爆炸/燃烧）
            EffectApplier.ApplyOnHit(mEffect, transform.position, other.gameObject, bossReal);
            // 表现层事件（飘字/闪白/震动由 CombatFeedbackFactory 订阅）
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = transform.position,
                Damage = bossReal,
                IsCrit = mIsCrit,
                Target = other.gameObject,
                FromEffectProjectile = mFromEffect,
                DamageType = mDamageType
            });
            EventKit.Type.Send(new GameEvents.BulletHit
            {
                HitPoint = transform.position,
                Damage = bossReal
            });
            if (!mIsPiercing)
            {
                mIsDead = true;
                Recycle();
            }
            return;
        }

        // 命中敌人（只检测敌人，不检测玩家自身/其他子弹）
        var enemy = other.GetComponent<EnemySimple>();
        if (enemy == null) return;

        // 造成伤害（传入子弹飞行方向：供死亡喷血表现沿受击方向喷射）
        float real = ResolveDamage(mDamage, enemy, null);
        enemy.TakeDamage(real, mDirection);
        // 触发特殊效果（爆炸/燃烧）
        EffectApplier.ApplyOnHit(mEffect, transform.position, other.gameObject, real);

        // 表现层事件（飘字/闪白/震动由 CombatFeedbackFactory 订阅）
        EventKit.Type.Send(new GameEvents.DamageDealt
        {
            Position = transform.position,
            Damage = real,
            IsCrit = mIsCrit,
            Target = other.gameObject,
            FromEffectProjectile = mFromEffect,
            DamageType = mDamageType
        });

        // 广播命中事件（音效由订阅方处理）
        EventKit.Type.Send(new GameEvents.BulletHit
        {
            HitPoint = transform.position,
            Damage = real
        });

        // 穿透则继续飞行，否则回收
        if (!mIsPiercing)
        {
            mIsDead = true;
            Recycle();
        }
    }

    /// <summary>把 3选1 增益的最终伤害/命中附加/处决/吸血/伤害转治疗，结算到本子弹的实际伤害上</summary>
    private float ResolveDamage(float baseDamage, EnemySimple enemy, BossHealth boss)
    {
        var player = GameManager.Instance?.Player;
        var pec = player != null ? player.GetComponent<PlayerEffectController>() : null;
        return pec != null ? pec.ResolveHit(baseDamage, mIsCrit, enemy, boss) : baseDamage;
    }

    /// <summary>归还对象池</summary>
    private void Recycle()
    {
        mIsDead = true;
        // 从 Poolable 标记读取真实池 key（首次 Get 时写入）
        var poolable = GetComponent<Poolable>();
        if (poolable != null && !string.IsNullOrEmpty(poolable.PoolKey))
        {
            PoolFactory.Release(poolable.PoolKey, gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
