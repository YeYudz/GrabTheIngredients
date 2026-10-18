using UnityEngine;
using YokiFrame;

/// <summary>
/// BOSS 控制器：追击玩家 + 周期碰撞伤害的简化 AI，并按配置初始化 BossHealth。
/// <para>
/// 表现层两件事也在这里驱动（逻辑/碰撞留在根节点，渲染在子节点 Visual 上）：
/// 1. 朝向：序列图默认朝右，向左走时水平翻转；
/// 2. 步伐：序列图本身只是「呼吸」级动作，靠抬身 + 落地挤压补出走动感，避免看起来像贴图平移。
/// </para>
/// </summary>
public class BossController : MonoBehaviour
{
    private BossHealth mHealth;
    private BossConfig mConfig;
    private Transform mPlayer;
    private float mAttackTimer;
    private float mAttackInterval = 1.5f;
    private float mContactDamage = 20f;

    [Header("表现 · 步伐起伏（作用在子节点 Visual，不动碰撞体）")]
    [Tooltip("抬升高度占精灵高度的比例（0.035 = 身高的 3.5%）。按比例给，改 BOSS 缩放不用重调")]
    [SerializeField, Range(0f, 0.2f)] private float m_stepLiftRatio = 0.035f;

    [Tooltip("每秒步数")]
    [SerializeField] private float m_stepPerSecond = 1.8f;

    [Tooltip("落地瞬间的垂直挤压比例（0.05 = 压扁 5%）")]
    [SerializeField, Range(0f, 0.3f)] private float m_stepSquash = 0.05f;

    [Tooltip("左右重心摆动幅度占身高的比例（0.015 = 1.5%）；0 = 不摆")]
    [SerializeField, Range(0f, 0.1f)] private float m_stepSwayRatio = 0.015f;

    [Tooltip("摆动时的倾斜角度（度），左右各一半；0 = 不倾斜")]
    [SerializeField, Range(0f, 6f)] private float m_stepTiltDegrees = 1.2f;

    [Tooltip("朝向判定死区：水平分量小于它不翻转，避免左右抖动")]
    [SerializeField] private float m_facingDeadZone = 0.02f;

    // 渲染根（子节点 Visual）：所有表现位移都只改它，根节点的位置/碰撞体不受影响
    private Transform mVisual;
    private Vector3 mVisualBasePos;
    private Vector3 mVisualBaseScale;
    private Quaternion mVisualBaseRotation;
    // 世界单位 → Visual 局部单位（Root 有缩放，直接用会放大/缩小过度）
    private float mWorldToLocalScale = 1f;
    // 精灵当前帧的世界高度（步伐抬升按它的比例算）
    private float mVisualWorldHeight = 1f;

    private SequenceFrameAnimator mAnimator;
    private SpriteRenderer mRenderer;
    private float mStepPhase;

    private void Awake()
    {
        mHealth = GetComponent<BossHealth>();

        mAnimator = GetComponentInChildren<SequenceFrameAnimator>(true);
        mVisual = mAnimator != null ? mAnimator.transform : null;
        if (mVisual != null && mVisual != transform)
        {
            mRenderer = mVisual.GetComponent<SpriteRenderer>();
            mVisualBasePos = mVisual.localPosition;
            mVisualBaseScale = mVisual.localScale;
            mVisualBaseRotation = mVisual.localRotation;
            float parentScale = mVisual.parent != null ? mVisual.parent.lossyScale.y : 1f;
            mWorldToLocalScale = Mathf.Max(0.0001f, Mathf.Abs(parentScale));
        }
        else
        {
            // 没有独立渲染子节点（旧预制体/占位 BOSS）：不做步伐位移，避免把碰撞体一起抬起来
            mVisual = null;
        }
    }

    /// <summary>初始化（由 WaveManager 在生成 BOSS 时调用）</summary>
    public void Init(string bossId)
    {
        mConfig = GameDataMgr.Instance.GetBoss(bossId);
        if (mConfig == null)
        {
            LogKit.Error($"[BossController] 找不到 BOSS 配置: {bossId}");
            return;
        }

        mHealth?.Init(mConfig);
        mPlayer = GameManager.Instance.Player?.transform;
        // 接触伤害使用独立配置的攻击力（不走 maxHp 比例，避免血量调大导致一击必杀）
        mContactDamage = mConfig.attack;

        // 保险：预制体上若把 m_playOnEnable 关掉或动画器被外部停过，这里恢复播放
        if (mAnimator != null)
        {
            mAnimator.SetMoving(true);
            mAnimator.Play();
        }

        // 精灵高度要在首帧应用之后量（Awake 里 Animator 的切图可能还没跑）
        CacheVisualMetrics();
    }

    /// <summary>量一次渲染节点的世界高度，供步伐抬升按比例换算</summary>
    private void CacheVisualMetrics()
    {
        if (mVisual == null || mRenderer == null) return;

        float h = mRenderer.bounds.size.y;
        if (h > 0.0001f) mVisualWorldHeight = h;
    }

    private void Update()
    {
        if (mHealth == null || mHealth.IsDead) return;
        if (mPlayer == null)
        {
            mPlayer = GameManager.Instance.Player?.transform;
            return;
        }

        // 追击玩家（虚弱状态原地不动，给玩家稳定输出的奖励窗口）
        bool moving = false;
        if (!mHealth.IsWeakened)
        {
            Vector3 dir = (mPlayer.position - transform.position).normalized;
            float speed = mConfig?.moveSpeed ?? 2f;
            transform.position += dir * speed * Time.deltaTime;

            moving = Mathf.Abs(dir.x) > 1e-4f;
            ApplyFacing(dir.x);
        }

        UpdateStepMotion(moving);

        // 周期攻击（接触伤害）
        mAttackTimer -= Time.deltaTime;
        if (mAttackTimer <= 0f)
        {
            mAttackTimer = mAttackInterval;
            // 简单处理：靠近玩家时造成伤害
            float dist = Vector3.Distance(transform.position, mPlayer.position);
            if (dist < 2.5f)
            {
                var playerHealth = mPlayer.GetComponent<Health>();
                if (playerHealth != null)
                {
                    // 走统一伤害公式：闪避判定 + 按伤害类型扣对应防御（保底1）
                    var pc = mPlayer.GetComponent<PlayerController>();
                    float damage = DamageCalculator.EnemyAttackDamage(
                        mContactDamage,
                        mConfig?.damageType ?? DamageType.Physical,
                        pc != null ? pc.DodgeChance : 0f,
                        pc != null ? pc.PhysDef : 0f,
                        pc != null ? pc.ElemDef : 0f);
                    playerHealth.TakeDamage(damage);
                }
            }
        }
    }

    /// <summary>按水平移动方向翻转朝向（序列图默认朝右 → 向左走时 flipX = true）</summary>
    private void ApplyFacing(float dirX)
    {
        if (Mathf.Abs(dirX) < m_facingDeadZone) return;

        bool faceLeft = dirX < 0f;
        if (mAnimator != null) mAnimator.SetFlipX(faceLeft);
        else if (mRenderer != null) mRenderer.flipX = faceLeft;
    }

    /// <summary>
    /// 程序化步伐：抬身（0 → 峰值 → 0）配合落地挤压，让「只有呼吸帧」的序列图也读得出走动节奏。
    /// 停止移动时平滑回位，虚弱/死亡不会突然弹一下。
    /// </summary>
    private void UpdateStepMotion(bool moving)
    {
        if (mVisual == null) return;

        if (moving)
        {
            mStepPhase += Time.deltaTime * Mathf.Max(0.01f, m_stepPerSecond);

            float cycle = Mathf.Repeat(mStepPhase, 1f);
            float lift = Mathf.Sin(cycle * Mathf.PI);                // 0 = 脚落地，1 = 抬到最高
            float squash = m_stepSquash * (1f - lift);               // 落地那一瞬压扁，抬身时回弹

            // 世界 → Visual 局部：Root 有缩放，位移要按比例折算，否则会放大好几十倍
            float heightWorld = mVisualWorldHeight;
            float bobLocal = lift * heightWorld * m_stepLiftRatio / mWorldToLocalScale;
            float swayLocal = Mathf.Sin(cycle * Mathf.PI * 2f) * heightWorld * m_stepSwayRatio / mWorldToLocalScale;

            mVisual.localPosition = mVisualBasePos + new Vector3(swayLocal, bobLocal, 0f);
            mVisual.localScale = new Vector3(
                mVisualBaseScale.x * (1f + squash * 0.5f),
                mVisualBaseScale.y * (1f - squash),
                mVisualBaseScale.z);
            mVisual.localRotation = mVisualBaseRotation * Quaternion.Euler(0f, 0f,
                -Mathf.Sin(cycle * Mathf.PI * 2f) * m_stepTiltDegrees);
        }
        else
        {
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            mVisual.localPosition = Vector3.Lerp(mVisual.localPosition, mVisualBasePos, k);
            mVisual.localScale = Vector3.Lerp(mVisual.localScale, mVisualBaseScale, k);
            mVisual.localRotation = Quaternion.Slerp(mVisual.localRotation, mVisualBaseRotation, k);

            if ((mVisual.localPosition - mVisualBasePos).sqrMagnitude < 1e-8f)
            {
                mVisual.localPosition = mVisualBasePos;
                mVisual.localScale = mVisualBaseScale;
                mVisual.localRotation = mVisualBaseRotation;
                mStepPhase = 0f;
            }
        }
    }
}
