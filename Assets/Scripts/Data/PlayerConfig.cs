/// <summary>
/// 玩家配置数据（保留升级经验参数；角色战斗属性已由 CharacterConfig + 武器/菜品提供）
/// </summary>
public class PlayerConfig
{
    // 升级参数
    public int expToNextLevel = 10;            // 第 1 次升级所需经验（也是等差段首项）
    public float expGrowthRate = 1.5f;         // 等比段每级倍率

    // ===== 分段经验曲线 =====
    /// <summary>
    /// 等差段级数（前 N 级用等差递增；&lt;=0 = 关闭分段，退回"全程等比"的老公式）。
    /// <para>
    /// 设计意图：前期（前 N 级）用等差让升级节奏线性可预期、开局快速建立构筑；
    /// 之后切等比，把难度曲线拉到"越往后越需要滚雪球"，避免全程等比那种前期过密/后期过疏。
    /// </para>
    /// </summary>
    public int expLinearLevels = 0;

    /// <summary>等差段公差（每一级比上一级多要多少经验）</summary>
    public int expLinearStep = 0;

    // ===== 近战环带模型（转圈攻击） =====
    public float meleeOrbitRadius = 2f;        // 基础轴枢半径（环带内缘，固定不随范围成长移动）
    public float meleePivotOffset = 0.5f;      // 轴枢外移量（武器沿径向向外偏移；视觉与判定环带同步外移）
    public int meleeTicksPerLap = 4;           // 转速参数：一圈耗时 = 该值/攻速 秒（判定已改为刃身胶囊每帧检测，每圈每目标一次）
    public float meleeMaxOuterRadius = 10f;    // 环带外缘硬上限（防刃长成长后穿屏）

    // ===== 攻速上限（安全阀） =====
    /// <summary>
    /// 攻速加成上限（1.2 = 最多 +120%）。最终攻速 = 角色攻速 × (1 + min(加成, 本值))。
    /// 近战转速 = meleeTicksPerLap / 攻速 且按帧采样刃身 OBB，攻速过高会同时导致转速失控与单帧转角漏判，
    /// 本值因此兼作数值安全阀。超限部分在面板汇总时截断，故增益到期回退不会漂移。
    /// </summary>
    public float maxAttackSpeedBonus = 1.2f;

    /// <summary>
    /// 攻速加成下限（-0.6 = 最多 -60%）。最终攻速 = 角色攻速 × (1 + max(加成, 本值))。
    /// 攻速是唯一会被高频负向修改的属性，任何一处扣减未配对都会累积成持续变慢，
    /// 本值兜住下限，保证手感不会崩到不可玩。
    /// </summary>
    public float minAttackSpeedBonus = -0.6f;
}

public class PlayerConfigWrapper
{
    public int version = 1;
    public PlayerConfig Player = new();
}
