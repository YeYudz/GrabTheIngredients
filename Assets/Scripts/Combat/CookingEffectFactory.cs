using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 战场炒菜效果工厂：按菜品配置的 EffectType 生成局内临时增益，
/// 强度 = 配置值 × 品质 / 火候倍率（由结算时的 FieldCookingEngine 传入）。
/// 复用玩家属性通道与空间查询能力，只广播事件、不操作显示。
/// </summary>
public class CookingEffectFactory : MonoSingleton<CookingEffectFactory>
{
    private static readonly List<EnemySimple> mQueryList = new();
    private Coroutine mActiveRoutine;

    /// <summary>应用菜品效果（按效果类型分发；持续时间类用协程到时回退）</summary>
    public void ApplyEffect(CookingRecipeConfig recipe, float multiplier)
    {
        if (mActiveRoutine != null) StopCoroutine(mActiveRoutine);
        mActiveRoutine = StartCoroutine(RunEffect(recipe, multiplier));
    }

    private IEnumerator RunEffect(CookingRecipeConfig recipe, float multiplier)
    {
        if (recipe == null) yield break;
        var player = GameManager.Instance?.Player;
        float value = recipe.effectValue * multiplier;

        // 复杂增益（效果引擎）最先结算：简单效果含 WaitForSeconds，不能让它把复杂增益往后拖
        ApplyGameEffects(recipe, player, multiplier);

        switch (recipe.effectType)
        {
            // 说明：走 SkillSystem 追加通道（它是 DamageBonus/AttackSpeedBonus 唯一写入方），
            // 否则中途发生属性重算（如再次烹饪）会把本次限时增益打回，而回退时变成负数
            case CookingEffectType.DamageBoost:
                if (player != null)
                {
                    SkillSystem.Instance.AddExtraDamageBonus(value);
                    yield return new WaitForSeconds(recipe.effectDuration);
                    SkillSystem.Instance.AddExtraDamageBonus(-value);
                }
                break;

            case CookingEffectType.AttackSpeedBoost:
                if (player != null)
                {
                    SkillSystem.Instance.AddExtraAttackSpeedBonus(value);
                    yield return new WaitForSeconds(recipe.effectDuration);
                    SkillSystem.Instance.AddExtraAttackSpeedBonus(-value);
                }
                break;

            case CookingEffectType.MoveSpeedBoost:
                if (player != null)
                {
                    var bonus = new PlayerAttribute { MoveSpeed = value };
                    player.AddBonusAttribute(bonus);
                    yield return new WaitForSeconds(recipe.effectDuration);
                    player.RemoveBonusAttribute(bonus);
                }
                break;

            case CookingEffectType.Heal:
                player?.Health?.Heal(value);
                break;

            case CookingEffectType.AOEDamage:
                ApplyAOEDamage(player, value, recipe.effectRadius);
                break;

            case CookingEffectType.SlowEnemies:
                yield return SlowEnemies(player, value, recipe.effectRadius, recipe.effectDuration);
                break;

            default:
                LogKit.Warning($"[CookingEffectFactory] 未实现效果类型: {recipe.effectType}");
                break;
        }

        mActiveRoutine = null;
    }

    /// <summary>按 gameEffectIds 触发复杂增益（品质/火候倍率放大一次性行为数值）</summary>
    private void ApplyGameEffects(CookingRecipeConfig recipe, PlayerController player, float multiplier)
    {
        if (player == null || recipe.gameEffectIds == null || recipe.gameEffectIds.Count == 0) return;

        var ctrl = PlayerEffectController.GetOrAdd(player);
        foreach (var id in recipe.gameEffectIds)
        {
            var cfg = GameDataMgr.Instance.GetEffect(id);
            if (cfg == null)
            {
                LogKit.Warning($"[CookingEffectFactory] 未找到效果配置: {id}");
                continue;
            }
            ctrl.ApplyEffect(cfg, multiplier);
        }
    }

    /// <summary>范围伤害：以玩家为中心，对半径内敌人/BOSS 造成伤害（复用空间网格查询）</summary>
    private void ApplyAOEDamage(PlayerController player, float damage, float radius)
    {
        if (player == null || radius <= 0f) return;
        Vector3 center = player.transform.position;

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

        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && Vector2.Distance(boss.transform.position, center) <= radius)
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

    /// <summary>减速敌人：半径内敌人移速 ×slowFactor，持续 duration 后恢复</summary>
    private IEnumerator SlowEnemies(PlayerController player, float slowFactor, float radius, float duration)
    {
        if (player == null || radius <= 0f) yield break;
        if (slowFactor <= 0f) slowFactor = 0.4f;

        SpatialGrid.GetInRadius(player.transform.position, radius, mQueryList);
        foreach (var enemy in mQueryList)
        {
            if (enemy != null) enemy.SpeedMultiplier = slowFactor;
        }

        yield return new WaitForSeconds(Mathf.Max(0.1f, duration));

        foreach (var enemy in mQueryList)
        {
            if (enemy != null) enemy.SpeedMultiplier = 1f;
        }
    }

    /// <summary>重置（重开/回主菜单时调用）</summary>
    public void Reset()
    {
        if (mActiveRoutine != null)
        {
            StopCoroutine(mActiveRoutine);
            mActiveRoutine = null;
        }
    }
}
