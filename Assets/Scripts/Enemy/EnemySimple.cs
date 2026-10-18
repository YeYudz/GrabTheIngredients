using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 敌人：数据驱动 + FSM AI。负责注册到静态列表供自动索敌、承伤与死亡处理。
/// 行为由 EnemyAI 按 EnemyConfig 驱动；对象池化复用，故禁用时不销毁 AI 组件。
/// </summary>
public class EnemySimple : MonoBehaviour
{
    /// <summary>场景中所有敌人的注册表（供 AutoAttack 索敌）</summary>
    public static List<EnemySimple> AllEnemies { get; } = new List<EnemySimple>();

    private EnemyConfig mConfig;
    private Health mHealth;
    private Rigidbody2D mRb;
    private EnemyAI mAI;
    // 分离间距用碰撞体缓存（圆形优先，其次盒状取最长半边推算半径）
    private CircleCollider2D m_circleCollider;
    private BoxCollider2D m_boxCollider;
    private bool mDeathPending;
    private float mLastAttackTime = -999f;   // 上次接触攻击时刻（攻击间隔判定用）

    /// <summary>最后一次有效受击方向（供死亡喷血表现使用；零向量表示尚未被定向命中）</summary>
    private Vector2 mLastHitDirection;

    /// <summary>预制体原始缩放/颜色（池化复用时还原，避免倍率累积）</summary>
    private Vector3 mBaseScale = Vector3.one;
    private SpriteRenderer mRenderer;
    private Color mBaseColor = Color.white;

    /// <summary>是否使用序列图美术（有美术图则不做几何换色，保留美术原色）</summary>
    private bool m_hasArtVisual;

    /// <summary>序列帧动画器（有美术图时非空；按配置切换序列图用）</summary>
    private SequenceFrameAnimator m_animator;

    // 是否面向右（true = 面向右）。资源序列图默认朝左，"翻转精灵"即面向右
    private bool m_isFacingRight;

    // 判定水平移动方向的最小阈值（接近 0 视为无水平分量，保持当前朝向）
    private const float k_facingDeadZone = 0.01f;

    // —— 群体分散参数（防止同类怪沿同一直线追击后完全重叠）——
    // 两怪最小中心距 = 双方半径和 × 该系数；0.7 即最多允许 30% 重合
    private const float k_maxOverlapRatio = 0.7f;
    // 分离检测半径（世界单位，需覆盖典型体型，供 SpatialGrid 局部查询）
    private const float k_separationRadius = 3f;
    // 分离力相对追踪方向的权重（>1 保证近距挤压时分离能顶开追踪方向）
    private const float k_separationWeight = 1.5f;
    // 接近偏移衰减距离：距玩家该距离内偏移线性衰减到 0，保证近身可正常攻击
    private const float k_offsetFadeDistance = 3f;
    // 无碰撞体时的回退半径
    private const float k_defaultRadius = 0.5f;

    // —— 群体分散状态 ——
    // 由碰撞体推算的本体世界半径（两两最小间距计算用）
    private float m_worldRadius = 0.5f;
    // 出生时分配的随机接近偏移：同类怪各自瞄准玩家周围不同点，从源头分散轨迹
    private Vector2 m_approachOffset;
    // 实例固定的分离打散角：完全重叠时双方朝各自固定角分开，避免对称抖动
    private float m_separationSeed;
    // 邻居查询结果复用列表（SpatialGrid 查询免分配）
    private readonly List<EnemySimple> m_neighbors = new List<EnemySimple>(16);

    [Header("敌人配置ID（场景手动摆放时需设置，波次生成时会自动覆盖）")]
    [SerializeField] private string mEnemyId = "slime_jelly";

    /// <summary>配置 ID（掉落/击杀事件用）</summary>
    public string ConfigId { get; private set; }

    /// <summary>最终属性倍率（生成时按「关卡难度 × 词缀修饰器」解析一次）</summary>
    private EnemyStatScale mStatScale = EnemyStatScale.One;

    /// <summary>移动速度（AI 读取，来自配置 × 关卡/词缀倍率 × 战场炒菜减速倍率）</summary>
    public float Speed => (mConfig?.speed ?? 1f) * mStatScale.Speed * SpeedMultiplier;

    /// <summary>接触伤害（来自配置 × 关卡/词缀倍率）</summary>
    public float ContactDamage => (mConfig?.contactDamage ?? 0f) * mStatScale.Damage;

    /// <summary>接触攻击间隔（秒，来自配置；下限 0.1 防止配置为 0 时每物理帧结算）</summary>
    public float AttackInterval => Mathf.Max(0.1f, mConfig?.attackInterval ?? 1f);

    /// <summary>是否精英（配置标记）</summary>
    public bool IsElite => mConfig?.isElite ?? false;

    /// <summary>移速倍率（战场炒菜减速效果写入，1=正常，&lt;1=减速）</summary>
    public float SpeedMultiplier { get; set; } = 1f;

    /// <summary>本体世界半径（群体分离的最小间距计算用，由碰撞体推算）</summary>
    public float Radius => m_worldRadius;

    /// <summary>行为参数（冲锋时长/爆炸范围，AI 读取）</summary>
    public float BehaviorParam => mConfig?.behaviorParam ?? 0f;

    /// <summary>是否已死亡（效果引擎/处决判定用）</summary>
    public bool IsDead => mHealth != null && mHealth.IsDead;

    /// <summary>当前血量（处决判定用）</summary>
    public float CurrentHp => mHealth != null ? mHealth.CurrentHp : 0f;

    /// <summary>最大血量（处决阈值计算用）</summary>
    public float MaxHpValue => mHealth != null ? mHealth.MaxHp : 0f;

    /// <summary>最后一次有效受击方向（死亡喷血表现读取，零向量=无有效方向）</summary>
    public Vector2 LastHitDirection => mLastHitDirection;

    private void Awake()
    {
        mRb = GetComponent<Rigidbody2D>();
        mRenderer = GetComponent<SpriteRenderer>();
        // 缓存碰撞体并推算本体世界半径（群体分离的最小间距依赖）
        m_circleCollider = GetComponent<CircleCollider2D>();
        m_boxCollider = GetComponent<BoxCollider2D>();
        RefreshWorldRadius();
        mBaseScale = transform.localScale;
        if (mRenderer != null) mBaseColor = mRenderer.color;

        // 记录初始朝向（资源序列图默认朝左，即 flipX = 0），保证首次移动时翻转判定基准正确
        if (mRenderer != null) m_isFacingRight = mRenderer.flipX;

        // 有序列图美术的敌人（如史莱姆）保留美术原色，不再做几何换色
        m_animator = GetComponent<SequenceFrameAnimator>();
        m_hasArtVisual = m_animator != null;
    }

    private void Start()
    {
        // 手动摆放在场景中的敌人：若未被波次系统 Init，则按序列化字段自动初始化
        if (mConfig == null && !string.IsNullOrEmpty(mEnemyId))
        {
            Init(mEnemyId);
        }
    }

    private void OnEnable()
    {
        if (!AllEnemies.Contains(this))
            AllEnemies.Add(this);
        // 空间网格：注册（索敌性能优化）
        SpatialGrid.Register(this);
    }

    private void OnDisable()
    {
        AllEnemies.Remove(this);
        // 空间网格：注销
        SpatialGrid.Unregister(this);
        // 注意：不在此销毁 AI —— 池化对象会被反复 SetActive(false)/true，
        // 状态机改由 Init 复用重置（EnemyAI.Reset），只有真正销毁时才在 OnDestroy 释放，
        // 避免每次从池取出都重建「FSM + 4 个状态对象 + 黑板」。
    }

    private void OnDestroy()
    {
        mAI?.Dispose();
        mAI = null;
    }

    /// <summary>由生成方调用（波次系统），初始化敌人</summary>
    public void Init(string enemyId)
    {
        ConfigId = enemyId;
        mLastHitDirection = Vector2.zero; // 池化复用：清空上一局的受击方向
        mLastAttackTime = -999f;          // 池化复用：重置攻击计时，首次接触立即结算

        // 每次生成都重新分配随机「接近偏移」与分离打散角（池化复用不串味）
        float spawnAngle = Random.value * Mathf.PI * 2f;
        m_approachOffset = new Vector2(Mathf.Cos(spawnAngle), Mathf.Sin(spawnAngle)) * Random.Range(0.5f, 1.5f);
        m_separationSeed = Random.value * Mathf.PI * 2f;
        mConfig = GameDataMgr.Instance.GetEnemy(enemyId);
        if (mConfig == null)
        {
            LogKit.Error($"[EnemySimple] 找不到敌人配置: {enemyId}");
            return;
        }

        // 解析最终属性倍率（关卡难度 × 作用域词缀修饰器）
        mStatScale = EnemyStatResolver.Resolve(enemyId);

        // 初始化血量（基础血量 × 倍率）
        mHealth = GetComponent<Health>();
        if (mHealth == null)
            mHealth = gameObject.AddComponent<Health>();
        mHealth.Init(mConfig.maxHp * mStatScale.Hp);

        // 美术覆盖：配置指定了序列图文件夹就切过去（精英怪用专属序列图）；未指定 = 还原预制体默认（普通外观）。
        // 必须在生成当帧执行：精英与普通怪共用同一个预制体/对象池，文件夹是每只怪各自的运行时状态。
        if (m_animator != null) m_animator.SetSheetFolder(mConfig.spriteSheetPath);

        // 视觉标记（词缀体型/颜色；精英由自己的 EnemyConfig 配置 color/size 或词缀指定）
        ApplyVisualScale();

        // 初始化 AI 状态机（行为由配置决定）
        // 池化复用：同一 GameObject 只构造一次 EnemyAI，后续生成走 Reset 复位（省掉重建整棵状态树的开销）
        var player = GameManager.Instance.Player;
        var playerTransform = player ? player.transform : null;
        if (mAI == null)
        {
            mAI = new EnemyAI(this, playerTransform);
            mAI.Start(mConfig.behavior);
        }
        else
        {
            mAI.Reset(mConfig.behavior, playerTransform);
        }

        // 血量归零 → 切到 AI 死亡状态（EnemyDeadState.OnEnter 调 HandleDeath）
        // 注意：EnemyKilled 事件由 Health.BroadcastDead 广播，这里不再重复
        mHealth.OnDied -= OnHealthDead;
        mHealth.OnDied += OnHealthDead;
    }

    private void OnHealthDead()
    {
        mAI?.ToDead();
    }

    /// <summary>应用视觉倍率（词缀体型/颜色；精英默认金色标记；每次生成都从预制体原始值重算）</summary>
    private void ApplyVisualScale()
    {
        if (mRenderer != null)
        {
            if (m_hasArtVisual)
            {
                // 有美术图：保留美术原色，不做几何换色（体型缩放仍生效）
                mRenderer.color = mBaseColor;
            }
            else if (mStatScale.Tint >= 0) mRenderer.color = TintPalette(mStatScale.Tint);  // 词缀指定优先
            else if (IsElite) mRenderer.color = TintPalette(3);                          // 精英=金色
            else mRenderer.color = mBaseColor;
        }

        float size = mStatScale.Size > 0f ? mStatScale.Size : 1f;
        transform.localScale = mBaseScale * size;
        RefreshWorldRadius(); // 体型倍率变化后重算世界半径（分离最小间距依赖）
    }

    /// <summary>颜色索引 → 颜色（0蓝 1绿 2紫 3金；超出范围回退金色）</summary>
    private static Color TintPalette(int index)
    {
        switch (index)
        {
            case 0: return new Color(0.45f, 0.75f, 1f);
            case 1: return new Color(0.5f, 0.9f, 0.5f);
            case 2: return new Color(0.8f, 0.55f, 1f);
            default: return new Color(1f, 0.82f, 0.25f);
        }
    }

    /// <summary>由 EnemyDeadState.OnEnter 调用：标记死亡，实际销毁延迟到 Update（避免 FSM 重入）</summary>
    public void MarkDeathPending()
    {
        mDeathPending = true;
    }

    /// <summary>受到伤害（被 Bullet 调用；hitDirection=受击方向，用于死亡喷血表现）</summary>
    public void TakeDamage(float damage, Vector2 hitDirection = default)
    {
        // 记录最后一次有效受击方向：零向量不覆盖，避免 AOE/附加伤害覆盖真实命中方向
        if (hitDirection.sqrMagnitude > 0.0001f)
            mLastHitDirection = hitDirection.normalized;
        mHealth?.TakeDamage(damage);
    }

    /// <summary>朝玩家方向移动（由 AI 状态调用）；叠加群体分离，避免同类怪轨迹汇聚后完全重叠</summary>
    public void MoveTowardPlayer(Vector3 playerPos, float speed)
    {
        if (speed <= 0f) return;

        Vector2 toPlayer = playerPos - transform.position;

        // 接近偏移：瞄准点 = 玩家位置 + 出生时分配的随机偏移，让同类怪从不同方向包抄；
        // 距玩家越近权重越低（k_offsetFadeDistance 内线性衰减到 0），保证近身仍能贴脸攻击
        float offsetWeight = Mathf.Clamp01(toPlayer.magnitude / k_offsetFadeDistance);
        Vector2 aimDir = (toPlayer + m_approachOffset * offsetWeight).normalized;
        if (aimDir.sqrMagnitude < 0.0001f) aimDir = Vector2.right;

        // 分离力：与邻近怪按穿透深度互相推开（两两最多 30% 重合）
        Vector2 separation = ComputeSeparation();

        Vector2 moveDir = aimDir + separation * k_separationWeight;
        moveDir = moveDir.sqrMagnitude > 0.0001f ? moveDir.normalized : aimDir;

        UpdateFacing(moveDir.x); // 按水平移动方向翻转精灵
        if (mRb != null)
        {
            mRb.velocity = moveDir * speed;
        }
        else
        {
            transform.position += (Vector3)(moveDir * speed) * Time.deltaTime;
        }
    }

    /// <summary>计算群体分离力：对检测半径内每个怪，按「穿透深度 / 最小间距」线性累积推离方向</summary>
    private Vector2 ComputeSeparation()
    {
        SpatialGrid.GetInRadius(transform.position, k_separationRadius, m_neighbors);
        Vector2 push = Vector2.zero;

        for (int i = 0; i < m_neighbors.Count; i++)
        {
            var other = m_neighbors[i];
            if (other == null || other == this) continue;

            Vector2 away = (Vector2)(transform.position - other.transform.position);
            float dist = away.magnitude;
            if (dist < 0.0001f)
            {
                // 完全重叠时按实例固定角推开，双方角度不同才能对称打散
                push += new Vector2(Mathf.Cos(m_separationSeed), Mathf.Sin(m_separationSeed));
                continue;
            }

            // 最小中心距 = 双方半径和 × 0.7（允许最多 30% 重合），低于则按穿透深度线性推离
            float minDist = (m_worldRadius + other.Radius) * k_maxOverlapRatio;
            if (dist >= minDist) continue;

            push += away / dist * ((minDist - dist) / minDist);
        }

        return push;
    }

    /// <summary>由碰撞体推算本体世界半径（分离最小间距计算用；无碰撞体时回退默认值）</summary>
    private void RefreshWorldRadius()
    {
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y);
        if (m_circleCollider != null)
        {
            m_worldRadius = m_circleCollider.radius * scale;
        }
        else if (m_boxCollider != null)
        {
            // bounds 为世界空间（自动含缩放/旋转），Unity 6 已移除 BoxCollider2D.extents
            m_worldRadius = Mathf.Max(m_boxCollider.bounds.extents.x, m_boxCollider.bounds.extents.y);
        }
        else
        {
            m_worldRadius = k_defaultRadius;
        }
    }

    /// <summary>根据水平移动方向翻转精灵（无水平分量时保持当前朝向）</summary>
    private void UpdateFacing(float dirX)
    {
        if (mRenderer == null) return;
        if (Mathf.Abs(dirX) < k_facingDeadZone) return;

        // 资源序列图默认朝左：向右移动时才翻转精灵使其朝右，向左移动保持原图（朝左）
        bool isRight = dirX > 0f;
        if (isRight == m_isFacingRight) return;

        m_isFacingRight = isRight;
        mRenderer.flipX = isRight;
    }

    /// <summary>死亡处理：蘑菇系爆炸 + 停用回收（EnemyKilled 由 Health 广播）</summary>
    public void HandleDeath()
    {
        // 蘑菇系（Stationary）：死亡时爆炸，对范围内玩家造成伤害
        if (mConfig != null && mConfig.behavior == EnemyBehavior.Stationary)
        {
            Explode();
        }

        // 停用敌人（触发 OnDisable 移除注册表 + 释放 AI）
        gameObject.SetActive(false);

        // 若是池化对象则归还对象池，否则销毁
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

    /// <summary>蘑菇爆炸：对范围内玩家造成伤害</summary>
    private void Explode()
    {
        float radius = Mathf.Max(1f, mConfig.behaviorParam);
        var player = GameManager.Instance.Player;
        if (player == null || !player.IsAlive) return;

        float dist = Vector2.Distance(transform.position, player.transform.position);
        if (dist <= radius)
        {
            // 爆炸伤害 = 接触伤害 × 2（对玩家），走新伤害公式（闪避 + 防御判定）
            float dmg = DamageCalculator.EnemyAttackDamage(
                ContactDamage * 2f, DamageType.Physical,
                player.DodgeChance, player.PhysDef, player.ElemDef);
            if (dmg > 0f)
            {
                player.Health?.TakeDamage(dmg);
                LogKit.Log($"[EnemySimple] {ConfigId} 爆炸！对玩家造成 {dmg} 伤害");
            }
        }
    }

    /// <summary>接触玩家造成伤害（2D 碰撞）：按攻击间隔结算一次全额伤害，走新伤害公式（闪避 + 防御判定）</summary>
    private void OnCollisionStay2D(Collision2D collision)
    {
        if (mConfig == null || mHealth == null || mHealth.IsDead) return;

        var player = collision.collider.GetComponent<PlayerController>();
        if (player == null || !player.IsAlive) return;

        // 攻击间隔：贴身期间每个敌人按间隔结算一次，避免每帧掉血、被多怪包围瞬间死亡
        if (Time.time - mLastAttackTime < AttackInterval) return;
        mLastAttackTime = Time.time;

        float dmg = DamageCalculator.EnemyAttackDamage(
            ContactDamage, DamageType.Physical,
            player.DodgeChance, player.PhysDef, player.ElemDef);
        if (dmg > 0f)
        {
            player.Health?.TakeDamage(dmg);
        }
    }

    private void Update()
    {
        // 死亡标记：在普通 Update 中执行销毁（此时不在 FSM 状态回调中，可安全 Dispose）
        if (mDeathPending)
        {
            mDeathPending = false;
            HandleDeath();
            return;
        }

        if (mAI != null && mHealth != null && !mHealth.IsDead)
        {
            mAI.Update();
        }

        // 空间网格：位置变化后更新所在格子（内部只在跨格时迁移，开销极小）
        SpatialGrid.UpdatePosition(this);
    }
}
