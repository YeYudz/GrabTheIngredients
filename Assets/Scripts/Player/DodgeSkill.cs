using UnityEngine;
using YokiFrame;

/// <summary>
/// 闪避：向当前移动方向位移一段距离，无无敌帧。
/// 独立于武器主动技能与两个自由技能槽，冷却可被效果引擎动态修正。
/// 施放后广播 DodgePerformed，供 OnDodge 类触发器消费。
/// </summary>
public class DodgeSkill : MonoBehaviour
{
    /// <summary>闪避位移距离</summary>
    public float dashDistance = 4f;

    /// <summary>闪避位移持续时间（秒）</summary>
    public float dashDuration = 0.18f;

    /// <summary>基础冷却时间（秒）</summary>
    public float baseCooldown = 8f;

    /// <summary>
    /// 闪避冷却缩减硬上限（0.9 = 最多减 90%）。
    /// 唯一真相：`SetCooldownReduction` 的夹取与效果引擎的 UI 显示口径都引用本常量，避免两处各写一个 0.9。
    /// </summary>
    public const float MaxCooldownReduction = 0.9f;

    /// <summary>冷却缩减来源（由效果引擎写入：闪避冷却缩减百分比0~1）</summary>
    private float mCooldownReduction;

    private float mCooldownRemain;
    private float mDashTimer;
    private Vector2 mDashDir;
    private bool mIsDashing;
    private PlayerController mPlayer;
    private Rigidbody2D mRb;

    /// <summary>冷却剩余（HUD 显示用）</summary>
    public float CooldownRemain => mCooldownRemain;

    /// <summary>冷却总时长（含缩减）</summary>
    public float CooldownTotal => baseCooldown * (1f - mCooldownReduction);

    /// <summary>设置闪避冷却缩减（效果引擎写入，夹到 MaxCooldownReduction）</summary>
    public void SetCooldownReduction(float reduction)
    {
        mCooldownReduction = Mathf.Clamp(reduction, 0f, MaxCooldownReduction);
    }

    /// <summary>设置冷却剩余（断点恢复用）</summary>
    public void SetCooldownRemain(float remain)
    {
        mCooldownRemain = Mathf.Max(0f, remain);
    }

    private void Awake()
    {
        mPlayer = GetComponent<PlayerController>();
        mRb = GetComponent<Rigidbody2D>();
    }

    private void Update()
    {
        if (mCooldownRemain > 0f) mCooldownRemain = Mathf.Max(0f, mCooldownRemain - Time.deltaTime);

        // 闪避冲刺位移
        if (mIsDashing && mPlayer != null && mRb != null)
        {
            mRb.velocity = mDashDir * (dashDistance / Mathf.Max(0.01f, dashDuration));
            mDashTimer -= Time.deltaTime;
            if (mDashTimer <= 0f)
            {
                mIsDashing = false;
            }
            return;
        }

        // 施放：闪避键（默认左 Shift，可在设置面板 Control 页签改绑；存活且不在冷却）；HUD 技能按钮通过 TryDodge() 触发
        if (KeyBindingManager.IsDown(GameInputAction.Dodge)
            && mPlayer != null && mPlayer.IsAlive && mCooldownRemain <= 0f)
        {
            TryDodge();
        }
    }

    /// <summary>施放闪避（HUD 技能按钮 / 闪避键输入共用；存活且不在冷却时生效）</summary>
    public void TryDodge()
    {
        if (mPlayer == null || !mPlayer.IsAlive || mCooldownRemain > 0f) return;
        BeginDash();
    }

    private void BeginDash()
    {
        if (mPlayer == null) return;

        Vector2 dir = mPlayer.GetFacingDirection();
        mDashDir = dir;
        mDashTimer = dashDuration;
        mIsDashing = true;
        mCooldownRemain = CooldownTotal;

        // 广播闪避事件（效果引擎 OnDodge 触发器消费）
        EventKit.Type.Send(new GameEvents.DodgePerformed { Direction = dir });
        LogKit.Log($"[DodgeSkill] 闪避 → 方向 {dir}");
    }
}