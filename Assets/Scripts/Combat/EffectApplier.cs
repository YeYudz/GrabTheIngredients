using UnityEngine;
using YokiFrame;

/// <summary>
/// 特殊效果应用器：在命中点或目标上落地 SkillEffectData。
/// 爆炸与燃烧在此处理，穿透由 Bullet 自身完成，故这里不重复处理。
/// </summary>
public static class EffectApplier
{
    /// <summary>空间网格查询复用列表（避免每帧分配）</summary>
    private static readonly System.Collections.Generic.List<EnemySimple> mQueryList = new();
    /// <summary>触发命中效果（爆炸/燃烧），在 Bullet 命中后调用</summary>
    public static void ApplyOnHit(SkillEffectData effect, Vector3 hitPoint, GameObject target, float baseDamage)
    {
        if (!effect.IsActive) return;

        switch (effect.type)
        {
            case SkillEffectType.Explosion:
                ApplyExplosion(hitPoint, effect.param, baseDamage);
                break;

            case SkillEffectType.Burn:
                ApplyBurn(target, baseDamage * effect.param, 0.5f, effect.duration);
                break;
        }
    }

    /// <summary>爆炸：命中点周围 radius 内所有敌人/BOSS 受到伤害</summary>
    private static void ApplyExplosion(Vector3 center, float radius, float damage)
    {
        // 敌人（空间网格查询，O(k)）
        SpatialGrid.GetInRadius(center, radius, mQueryList);
        foreach (var enemy in mQueryList)
        {
            if (enemy == null) continue;

            enemy.TakeDamage(damage);
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = enemy.transform.position,
                Damage = damage,
                IsCrit = false,
                Target = enemy.gameObject,
                DamageType = DamageType.Element,
            });
        }

        // BOSS
        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead)
        {
            if (Vector2.Distance(boss.transform.position, center) <= radius)
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
            }
        }

        // 广播爆炸事件（音效/特效由 UI 层订阅）
        EventKit.Type.Send(new GameEvents.StatusEffectTriggered
        {
            EffectType = SkillEffectType.Explosion,
            Position = center,
            Radius = radius
        });
    }

    /// <summary>燃烧：给目标挂 DoT</summary>
    private static void ApplyBurn(GameObject target, float damagePerTick, float tickInterval, float duration)
    {
        if (target == null) return;

        // 判定目标是否可受 DoT（敌人 或 BOSS）
        bool isValid = target.GetComponent<EnemySimple>() != null || target.GetComponent<BossHealth>() != null;
        if (!isValid) return;

        var status = target.GetComponent<StatusEffect>();
        if (status == null)
            status = target.AddComponent<StatusEffect>();
        status.Apply(damagePerTick, tickInterval, duration);

        // 广播燃烧事件
        EventKit.Type.Send(new GameEvents.StatusEffectTriggered
        {
            EffectType = SkillEffectType.Burn,
            Position = target.transform.position,
            Radius = 0f
        });
    }
}
