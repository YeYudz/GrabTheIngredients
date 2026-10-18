using UnityEngine;
using YokiFrame;

/// <summary>
/// 玩家成长数据：经验 / 等级 / 升级，升级曲线读 PlayerConfig。
/// 对外只广播经验与升级事件，不含 UI 逻辑。
/// </summary>
public class PlayerStats : MonoBehaviour
{
    private PlayerConfig mConfig;

    /// <summary>当前经验</summary>
    public int Exp { get; private set; }

    /// <summary>当前等级</summary>
    public int Level { get; private set; } = 1;

    /// <summary>
    /// 升级所需经验（按当前等级计算）。分段公式：
    /// <para>
    /// ① 前 expLinearLevels 级（Level ≤ N）：等差 = expToNextLevel + (Level-1) × expLinearStep
    /// ② 之后：等比 = (等差段最后一次需求) × expGrowthRate^(Level - N)
    /// </para>
    /// expLinearLevels ≤ 0 时退回"全程等比"的老公式（与旧配置完全兼容）。
    /// </summary>
    public int ExpToNext
    {
        get
        {
            if (mConfig == null) return 10;

            int level = Mathf.Max(1, Level);
            int linearLevels = mConfig.expLinearLevels;

            // 等差段
            if (linearLevels > 0 && level <= linearLevels)
                return Mathf.Max(1, mConfig.expToNextLevel + (level - 1) * mConfig.expLinearStep);

            // 等比段：以等差段末尾需求为基数（无等差段时即第 1 级需求）
            int baseLevel = linearLevels > 0 ? linearLevels : 1;
            int baseNeed = Mathf.Max(1, mConfig.expToNextLevel + (baseLevel - 1) * mConfig.expLinearStep);
            return Mathf.Max(1, Mathf.RoundToInt(baseNeed * Mathf.Pow(mConfig.expGrowthRate, level - baseLevel)));
        }
    }

    /// <summary>当前等级经验进度（0~1，供 UI 显示）</summary>
    public float ExpRatio => (float)Exp / ExpToNext;

    private void Start()
    {
        mConfig = GameDataMgr.Instance.Player?.Player ?? new PlayerConfig();
    }

    /// <summary>断点恢复：直接设置等级/经验（不触发升级流程），并广播经验刷新</summary>
    public void Restore(int level, int exp)
    {
        Level = Mathf.Max(1, level);
        Exp = Mathf.Max(0, exp);
        EventKit.Type.Send(new GameEvents.PlayerExpChanged
        {
            Exp = Exp,
            ExpToNext = ExpToNext
        });
    }

    /// <summary>增加经验（由 ExpGem 拾取调用）</summary>
    public void AddExp(int amount)
    {
        if (amount <= 0) return;

        Exp += amount;

        // 广播经验变化（UI 更新经验条）
        EventKit.Type.Send(new GameEvents.PlayerExpChanged
        {
            Exp = Exp,
            ExpToNext = ExpToNext
        });

        // 升级判定
        while (Exp >= ExpToNext)
        {
            Exp -= ExpToNext;
            Level++;
            OnLevelUp();
        }
    }

    /// <summary>升级处理</summary>
    private void OnLevelUp()
    {
        LogKit.Log($"[PlayerStats] 升级！等级 {Level}，需求经验 {ExpToNext}");

        // 广播升级事件（Step4 升级选技能界面订阅）
        EventKit.Type.Send(new GameEvents.PlayerLevelUp { NewLevel = Level });

        // 广播更新后的经验条（扣除升级消耗后）
        EventKit.Type.Send(new GameEvents.PlayerExpChanged
        {
            Exp = Exp,
            ExpToNext = ExpToNext
        });
    }
}
