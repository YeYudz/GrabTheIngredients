using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 自动攻击：3 个武器槽并行生效、各自独立计时，不存在「切换持有」的概念。
/// 近战用轨道模型——轴枢外移量与刃身 OBB 严格贴合武器图，刀扫到才命中；
/// 每个目标每转一圈至多受击一次，使命中频率与转速解耦、DPS 保持稳定。
/// 远程向最近敌人发射弹道。数据读 WeaponConfig，只发事件不碰显示。
/// </summary>
public class AutoAttack : MonoBehaviour
{
    /// <summary>单个武器槽的运行时数据（每武器独立计时/角度）</summary>
    private class WeaponRuntime
    {
        public string Id;               // 武器 ID（调试定位）
        public WeaponConfig Config;     // 武器配置
        public float Timer;             // 攻击计时（秒，远程用；近战改为每帧扫掠检测）
        public float AngleDeg;          // 近战刃身当前角度（度，0=+X 方向；顺时针旋转即该值递减；远程不用）
        public int ViewIndex = -1;      // 近战视觉轴枢索引（与 WeaponOrbitView.mPivots 对齐；远程为 -1）
        public float RotationAccum;     // 近战累计旋转角（度，不取模；用于圈次推进）
        public int LapId;               // 近战当前圈次（RotationAccum/360 取整）
        public readonly HashSet<int> HitTargets = new();  // 本圈已命中目标 InstanceID（每圈每目标至多一次）
    }

    /// <summary>命中容差（世界单位）：OBB 盒四向外扩余量（0.3≈敌人碰撞体半径 + 高速扫掠补余，即"身体贴刃即命中"；设 0 为像素级严格）
    /// 说明：近战靠"每帧采样刃身 OBB"判定，转速越高单帧转过的角度越大，容差过小会在低帧率下漏判（刀扫过却没打中）</summary>
    private const float kHitEpsilon = 0.3f;

    private float mOrbitRadius;         // 基础轴枢半径（环带内缘，PlayerConfig.meleeOrbitRadius）
    private float mPivotRadius;         // 轴枢实际半径 = 基础半径 + 外移量（视觉与判定共用）
    private float mSweepPerTick;        // 扇区角度基数（360/ticksPerLap），用作转速公式输入
    private float mMaxOuterRadius;      // 环带外缘硬上限

    private readonly List<WeaponRuntime> mWeapons = new();

    /// <summary>空间网格查询复用列表（避免每帧分配）</summary>
    private readonly List<EnemySimple> mQueryList = new();

    private PlayerController mPlayer;
    private WeaponOrbitView mView;

    /// <summary>当前生效的武器槽数量（HUD 显示用）</summary>
    public int WeaponCount => mWeapons.Count;

    /// <summary>获取指定槽位的武器配置（越界返回 null）</summary>
    public WeaponConfig GetWeaponAt(int index)
        => index >= 0 && index < mWeapons.Count ? mWeapons[index].Config : null;

    /// <summary>第一个远程武器配置（技能弹幕复用其子弹外观；无远程武器返回 null）</summary>
    public WeaponConfig GetFirstRangedWeapon()
    {
        foreach (var w in mWeapons)
            if (w.Config != null && w.Config.attackMode == WeaponAttackMode.Ranged)
                return w.Config;
        return null;
    }

    private void Awake()
    {
        mPlayer = GetComponent<PlayerController>();

        mView = GetComponent<WeaponOrbitView>();
        if (mView == null) mView = gameObject.AddComponent<WeaponOrbitView>();
        mView.EnsureRoot();

        // 环带模型参数（PlayerConfig.json）
        var pc = GameDataMgr.Instance.Player?.Player;
        mOrbitRadius = Mathf.Max(0.5f, pc?.meleeOrbitRadius ?? 2f);
        mPivotRadius = mOrbitRadius + Mathf.Max(0f, pc?.meleePivotOffset ?? 0.5f);   // 轴枢外移（视觉/判定同步）
        int ticksPerLap = Mathf.Max(1, pc?.meleeTicksPerLap ?? 4);
        mSweepPerTick = 360f / ticksPerLap;
        mMaxOuterRadius = Mathf.Max(mPivotRadius + 1f, pc?.meleeMaxOuterRadius ?? 10f);
    }

    private void Start()
    {
        // 战前装备（RosterManager 提供槽位；无存档时兜底默认角色的 defaultWeaponId）
        List<string> slots = null;
        if (RosterManager.HasInstance)
            slots = RosterManager.Instance.GetCurrentWeaponIds();
        if (slots == null || slots.Count == 0)
        {
            slots = new List<string>();
            var characters = GameDataMgr.Instance.Characters?.Characters;
            if (characters != null && characters.Count > 0
                && !string.IsNullOrEmpty(characters[0].defaultWeaponId))
                slots.Add(characters[0].defaultWeaponId);
        }
        SetWeaponSlots(slots);
    }

    private void Update()
    {
        if (mPlayer == null || !mPlayer.IsAlive || mWeapons.Count == 0) return;

        // 攻速 = 角色攻速 × (1 + 攻速加成) × 武器自身攻速倍率（近战=刃身转速，远程=射速）
        float atkSpeed = Mathf.Max(0.01f, mPlayer.AttackSpeed * (1f + mPlayer.AttackSpeedBonus));

        for (int i = 0; i < mWeapons.Count; i++)
        {
            var w = mWeapons[i];
            float weaponSpeed = atkSpeed * Mathf.Max(0.01f, w.Config.attackSpeedScale);

            if (w.Config.attackMode == WeaponAttackMode.Melee)
            {
                // 度/秒：一圈耗时 = meleeTicksPerLap / (角色攻速 × 武器攻速倍率) 秒
                float rotSpeed = mSweepPerTick * weaponSpeed;
                // 刃身顺时针旋转（表现与判定共用同一角度；逆时针为正角，故顺时针即递减）
                float dAngle = rotSpeed * Time.deltaTime;
                w.AngleDeg = Mathf.Repeat(w.AngleDeg - dAngle, 360f);
                w.RotationAccum += dAngle;

                // 刃长 = 配置刃长 × 范围加成，夹在外缘硬上限内（轴枢不动，成长=刃长变长）
                float bladeLen = Mathf.Min(
                    w.Config.meleeBladeLength * RangeScale(),
                    mMaxOuterRadius - mPivotRadius);

                // 每帧同步轴枢姿态（位置由父子层级自动跟随角色移动）
                mView.ApplyPose(w.ViewIndex, w.AngleDeg, mPivotRadius, bladeLen);

                // 每帧检测刃身图片覆盖区域（胶囊），刀扫到才命中
                ProcessMeleeSweep(w, bladeLen);
            }
            else
            {
                w.Timer += Time.deltaTime;
                if (w.Timer >= 1f / weaponSpeed)
                {
                    w.Timer = 0f;
                    RangedAttack(w);
                }
            }
        }
    }

    /// <summary>设置武器槽（RosterManager/存档恢复调用；3 槽同时生效，空槽跳过）</summary>
    public void SetWeaponSlots(List<string> slots)
    {
        mWeapons.Clear();
        if (slots != null)
        {
            foreach (var id in slots)
            {
                if (string.IsNullOrEmpty(id)) continue;   // 空槽不生效
                var weapon = GameDataMgr.Instance.GetWeapon(id);
                if (weapon == null)
                {
                    LogKit.Error($"[AutoAttack] 找不到武器配置: {id}");
                    continue;
                }
                mWeapons.Add(new WeaponRuntime { Id = id, Config = weapon });
            }
        }

        // 近战视觉轴枢路径（仅近战武器有轴枢；远程从角色中心发射，无轴枢）
        var meleePaths = new List<string>();
        foreach (var w in mWeapons)
            if (w.Config.attackMode == WeaponAttackMode.Melee)
                meleePaths.Add(w.Config.meleePrefabPath);

        // 近战武器按序均分圆周：n(≥2) 把两两间隔 360°/n；单把时相位归 0（不受角度限制）
        int meleeCount = meleePaths.Count;
        float spacing = meleeCount > 0 ? 360f / meleeCount : 0f;
        int meleeOrdinal = 0;
        foreach (var w in mWeapons)
        {
            if (w.Config.attackMode != WeaponAttackMode.Melee) continue;

            w.AngleDeg = meleeOrdinal * spacing;
            w.ViewIndex = meleeOrdinal;
            meleeOrdinal++;
        }

        mView.Rebuild(meleePaths);

        LogKit.Log($"[AutoAttack] 武器槽生效 {mWeapons.Count} 个（同时攻击，无切换）");

        // 武器主动技能与槽位同源同序初始化（每武器独立主动：独立CD+法力+自动/手动双轨）
        WeaponActiveSystem.Instance.Init(slots);

        EventKit.Type.Send(new GameEvents.WeaponLoadoutChanged());
    }

    /// <summary>攻击距离倍率（pepper 独有：限时攻击距离提升；无则 1）</summary>
    private float RangeScale()
        => (ActiveSkillSystem.HasInstance) ? ActiveSkillSystem.Instance.AttackRangeMultiplier : 1f;

    /// <summary>
    /// 近战扫掠检测（每帧）：伤害区域 = 刃身 OBB 命中盒（轴向 = 柄部→刃尖，侧向 = 刃身真实半宽，四向外扩 kHitEpsilon）
    /// 刀扫到才命中；每个目标每转一圈至多受击一次（圈次由 RotationAccum 推进，换圈清空记录）
    /// </summary>
    private void ProcessMeleeSweep(WeaponRuntime w, float bladeLen)
    {
        // 圈次推进：进入新一圈时清空已命中记录（命中频率与旧扇区模型的每圈一击等效）
        int lapId = Mathf.FloorToInt(w.RotationAccum / 360f);
        if (lapId != w.LapId)
        {
            w.LapId = lapId;
            w.HitTargets.Clear();
        }

        var cfg = w.Config;
        Vector2 myPos = transform.position;
        Vector2 bladeDir = new Vector2(Mathf.Cos(w.AngleDeg * Mathf.Deg2Rad), Mathf.Sin(w.AngleDeg * Mathf.Deg2Rad));
        Vector2 perpDir = new Vector2(-bladeDir.y, bladeDir.x);   // 刃宽方向（垂直于刃身）
        Vector2 handle = myPos + bladeDir * mPivotRadius;         // 刃身起点 = 柄部（轴枢位置）

        // 命中盒半宽：配置刃身全宽优先（严格贴合图片最宽处），0=用 sprite 网格自动测量值
        float cfgWidth = cfg.meleeBladeWidth;
        float halfWidth = cfgWidth > 0f ? cfgWidth * 0.5f : mView.GetBladeHalfWidth(w.ViewIndex);

        bool hit = false;
        float lastHitReal = 0f;

        // 空间网格查询（O(k)）：按命中盒外缘粗查（三角不等式放缩，必不漏），再做刃身 OBB 精查
        SpatialGrid.GetInRadius(myPos, mPivotRadius + bladeLen + halfWidth + kHitEpsilon, mQueryList);
        for (int i = 0; i < mQueryList.Count; i++)
        {
            var e = mQueryList[i];
            if (e == null) continue;
            int targetId = e.GetInstanceID();
            if (w.HitTargets.Contains(targetId)) continue;    // 本圈已命中
            if (!IsInsideBladeOBB(e.transform.position, handle, bladeDir, perpDir, bladeLen, halfWidth)) continue;
            w.HitTargets.Add(targetId);

            // 每目标独立伤害/暴击结算：攻击力 × 武器倍率 × 对应属性加成 + 暴击判定
            float damage = DamageCalculator.PlayerAttackDamage(
                mPlayer.Attack, cfg, cfg.damageType,
                mPlayer.CritChance, mPlayer.CritDamage,
                mPlayer.PhysDamageBonus, mPlayer.ElemDamageBonus, out bool isCrit);
            float real = ResolveHitDamage(damage, e, null);
            e.TakeDamage(real);
            lastHitReal = real;
            // 表现层事件（飘字/闪白）
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = e.transform.position,
                Damage = real,
                IsCrit = isCrit,
                Target = e.gameObject,
                DamageType = cfg.damageType
            });
            hit = true;
        }

        // 命中刃身 OBB 内的 BOSS（先测命中盒再登记圈次，未命中不占用本圈名额）
        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy
            && IsInsideBladeOBB(boss.transform.position, handle, bladeDir, perpDir, bladeLen, halfWidth)
            && w.HitTargets.Add(boss.gameObject.GetInstanceID()))
        {
            float damage = DamageCalculator.PlayerAttackDamage(
                mPlayer.Attack, cfg, cfg.damageType,
                mPlayer.CritChance, mPlayer.CritDamage,
                mPlayer.PhysDamageBonus, mPlayer.ElemDamageBonus, out bool isCrit);
            float real = ResolveHitDamage(damage, null, boss);
            boss.TakeDamage(real, cfg.attackType, cfg.damageType, isCrit);
            lastHitReal = real;
            // 表现层事件（飘字/闪白）
            EventKit.Type.Send(new GameEvents.DamageDealt
            {
                Position = boss.transform.position,
                Damage = real,
                IsCrit = isCrit,
                Target = boss.gameObject,
                DamageType = cfg.damageType
            });
            hit = true;
        }

        if (hit)
        {
            // 广播命中（音效/飘字由订阅方处理）
            EventKit.Type.Send(new GameEvents.BulletHit
            {
                HitPoint = transform.position,
                Damage = lastHitReal
            });
            if (AudioManager.HasInstance)
            {
                AudioManager.Instance.PlayShoot();
            }

            // 命中敌人 → 进入战斗状态（武器主动自动释放窗口）
            WeaponActiveSystem.NotifyCombat();
        }
    }

    /// <summary>
    /// 刃身 OBB 命中测试（目标中心点 vs 命中盒，kHitEpsilon 已覆盖目标体积与浮点误差）：
    /// 轴向投影 ∈ [-eps, 刃长+eps]（柄部到刃尖，含刃尖外沿），侧向偏移 ≤ 半宽+eps（刃身真实宽度）
    /// </summary>
    private static bool IsInsideBladeOBB(Vector2 target, Vector2 handle, Vector2 bladeDir, Vector2 perpDir, float bladeLen, float halfWidth)
    {
        Vector2 d = target - handle;
        float axial = Vector2.Dot(d, bladeDir);
        if (axial < -kHitEpsilon || axial > bladeLen + kHitEpsilon) return false;
        return Mathf.Abs(Vector2.Dot(d, perpDir)) <= halfWidth + kHitEpsilon;
    }

    /// <summary>远程攻击：向最近敌人方向发射子弹（携带弱点属性 + 物理/元素伤害），发射点 = 角色位置</summary>
    private void RangedAttack(WeaponRuntime w)
    {
        var cfg = w.Config;
        Vector2 dir = FindNearestEnemyDirection(cfg);
        if (dir == Vector2.zero) return;

        // 发射弹道 → 进入战斗状态（武器主动自动释放窗口，发射即算无延迟感）
        WeaponActiveSystem.NotifyCombat();

        // 新伤害公式：攻击力 × 武器倍率 × 对应属性加成 + 暴击判定
        float damage = DamageCalculator.PlayerAttackDamage(
            mPlayer.Attack, cfg, cfg.damageType,
            mPlayer.CritChance, mPlayer.CritDamage,
            mPlayer.PhysDamageBonus, mPlayer.ElemDamageBonus, out bool isCrit);

        // 携带当前特殊效果（SkillSystem 汇总）
        SkillEffectData effect = SkillSystem.HasInstance
            ? SkillSystem.Instance.ActiveEffect
            : SkillEffectData.None;

        GameObject go = PoolFactory.Get(cfg.projectilePrefabPath, transform.position, Quaternion.identity);
        Bullet bullet = go.GetComponent<Bullet>();
        if (bullet != null)
        {
            bullet.Setup(
                dir,
                cfg.projectileSpeed,
                damage,
                cfg.attackType,
                cfg.damageType,
                cfg.projectileRange * RangeScale(),
                false,
                effect,
                isCrit);

            // 发射音效（AudioManager，失败静默不打断游戏）
            if (AudioManager.HasInstance)
            {
                AudioManager.Instance.PlayShoot();
            }
        }
    }

    /// <summary>把 3选1 增益的最终伤害/命中附加/处决/吸血/伤害转治疗，结算到目标实际伤害上</summary>
    private float ResolveHitDamage(float baseDamage, EnemySimple enemy, BossHealth boss)
    {
        var pec = mPlayer != null ? mPlayer.GetComponent<PlayerEffectController>() : null;
        return pec != null ? pec.ResolveHit(baseDamage, false, enemy, boss) : baseDamage;
    }

    /// <summary>
    /// 查找攻击范围内的最近敌人方向（BOSS 也参与索敌）
    /// 超出武器 attackRange 的敌人不索敌（避免屏幕外攻击）
    /// </summary>
    private Vector2 FindNearestEnemyDirection(WeaponConfig cfg)
    {
        Vector3 myPos = transform.position;
        float range = cfg.attackRange * RangeScale();
        float rangeSq = range * range;
        float minDistSq = float.MaxValue;
        Vector2 dir = Vector2.zero;
        bool found = false;

        // 空间网格查询（O(k)），GetInRadius 已做激活 + 精确距离剔除
        SpatialGrid.GetInRadius(myPos, range, mQueryList);
        for (int i = 0; i < mQueryList.Count; i++)
        {
            var e = mQueryList[i];
            if (e == null) continue;
            float distSq = ((Vector2)(e.transform.position - myPos)).sqrMagnitude;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                dir = ((Vector2)(e.transform.position - myPos)).normalized;
                found = true;
            }
        }

        var boss = BossHealth.Current;
        if (boss != null && !boss.IsDead && boss.gameObject.activeInHierarchy)
        {
            float distSq = ((Vector2)(boss.transform.position - myPos)).sqrMagnitude;
            if (distSq <= rangeSq && distSq < minDistSq)
            {
                minDistSq = distSq;
                dir = ((Vector2)(boss.transform.position - myPos)).normalized;
                found = true;
            }
        }

        return found ? dir : Vector2.zero;
    }

#if UNITY_EDITOR
    /// <summary>
    /// 画出近战刃身 OBB 命中盒（Play 模式选中本物体可见）：红色四边形即实际判定范围，供肉眼验证贴合度
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (mWeapons == null || mWeapons.Count == 0) return;
        Vector2 myPos = transform.position;
        foreach (var w in mWeapons)
        {
            if (w.Config == null || w.Config.attackMode != WeaponAttackMode.Melee) continue;

            float bladeLen = Mathf.Min(w.Config.meleeBladeLength * RangeScale(), mMaxOuterRadius - mPivotRadius);
            float cfgWidth = w.Config.meleeBladeWidth;
            float halfWidth = (cfgWidth > 0f ? cfgWidth * 0.5f : mView.GetBladeHalfWidth(w.ViewIndex)) + kHitEpsilon;
            float len = bladeLen + kHitEpsilon;

            Vector2 dir = new Vector2(Mathf.Cos(w.AngleDeg * Mathf.Deg2Rad), Mathf.Sin(w.AngleDeg * Mathf.Deg2Rad));
            Vector2 perp = new Vector2(-dir.y, dir.x);
            Vector2 handle = myPos + dir * mPivotRadius;

            Vector2 c1 = handle + perp * halfWidth;               // 柄部外侧角
            Vector2 c2 = handle - perp * halfWidth;               // 柄部内侧角
            Vector2 c3 = handle + dir * len - perp * halfWidth;   // 刃尖内侧角
            Vector2 c4 = handle + dir * len + perp * halfWidth;   // 刃尖外侧角

            Gizmos.color = Color.red;
            Gizmos.DrawLine(c1, c2);
            Gizmos.DrawLine(c3, c4);
            Gizmos.DrawLine(c1, c4);
            Gizmos.DrawLine(c2, c3);
        }
    }
#endif
}
