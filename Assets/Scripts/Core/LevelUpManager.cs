using System.Collections.Generic;
using UnityEngine;
using YokiFrame;
using GameUI;

/// <summary>
/// 升级流程管理：订阅升级事件后暂停游戏、抽 3 个升级 Buff 并打开选卡面板，
/// 收到选择结果再交给 UpgradeBuffSystem 生效。
/// 暂停与恢复由本类成对发起，避免各面板自行改 timeScale 导致状态泄漏。
/// </summary>
public class LevelUpManager : MonoSingleton<LevelUpManager>
{
    private bool mIsSelecting;
    private bool mInited;

    /// <summary>
    /// 本局已获得次数（buffId → 次数）。用于按 BuffUpgradeConfig.maxStack 过滤升级池。
    /// <para>
    /// 修复前：抽取只在本轮的 3 张卡之间去重，**跨等级不去重** → 同一张卡可被反复抽到，
    /// 导致"攻速 +60%"这类卡线性叠加，近战转速随之被无限推高（越玩越快）。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, int> mPickedCount = new();

    /// <summary>当前 3 选 1 面板里正在展示的卡（断点保存用；选完清空）</summary>
    private readonly List<string> mPendingBuffIds = new();

    /// <summary>断点保存：导出「已获得次数」与「未选完的 3 选 1」卡片</summary>
    public void ExportSelectionState(List<BuffPickCountSave> counts, List<string> pendingIds)
    {
        if (counts != null)
        {
            counts.Clear();
            foreach (var kv in mPickedCount)
                counts.Add(new BuffPickCountSave { id = kv.Key, count = kv.Value });
        }
        if (pendingIds != null)
        {
            pendingIds.Clear();
            if (mIsSelecting) pendingIds.AddRange(mPendingBuffIds);
        }
    }

    /// <summary>
    /// 断点恢复：还原「已获得次数」（maxStack 过滤，避免续玩后又抽到已满的卡），
    /// 并暂存未选完的 3 选 1 —— 面板由 <see cref="ReopenPendingSelection"/> 在 HUD 打开后再弹，
    /// 避免面板先于 HUD 打开被覆盖。
    /// </summary>
    public void RestoreSelectionState(List<BuffPickCountSave> counts, List<string> pendingIds)
    {
        mPickedCount.Clear();
        if (counts != null)
        {
            foreach (var c in counts)
            {
                if (c == null || string.IsNullOrEmpty(c.id)) continue;
                mPickedCount[c.id] = Mathf.Max(0, c.count);
            }
        }

        mPendingBuffIds.Clear();
        if (pendingIds != null)
        {
            foreach (var id in pendingIds)
                if (!string.IsNullOrEmpty(id)) mPendingBuffIds.Add(id);
        }
    }

    /// <summary>断点恢复：把暂存的待选 3 选 1 重新弹出来（无待选则返回 false）</summary>
    public bool ReopenPendingSelection()
    {
        if (mIsSelecting || mPendingBuffIds.Count == 0) return false;

        var buffs = new List<BuffUpgradeConfig>();
        foreach (var id in mPendingBuffIds)
        {
            var cfg = GameDataMgr.Instance.GetBuffUpgrade(id);
            if (cfg != null) buffs.Add(cfg);
        }
        if (buffs.Count == 0)
        {
            LogKit.Warning("[LevelUpManager] 断点恢复：待选升级卡配置缺失，跳过重开面板");
            mPendingBuffIds.Clear();
            return false;
        }

        mIsSelecting = true;
        Time.timeScale = 0f;
        UIKit.OpenPanel<LevelUpPanel>(UILevel.Pop, new LevelUpPanelData { Buffs = buffs.ToArray() });
        LogKit.Log($"[LevelUpManager] 断点恢复：重新打开 3 选 1（{buffs.Count} 张）");
        return true;
    }

    /// <summary>显式初始化：触发单例创建并注册事件（防重入）</summary>
    public void Init()
    {
        if (mInited) return;
        mInited = true;

        EventKit.Type.Register<GameEvents.PlayerLevelUp>(OnPlayerLevelUp)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.UpgradeSelected>(OnUpgradeSelected)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        LogKit.Log("[LevelUpManager] 初始化完成");
    }

    /// <summary>重置升级流程状态与已选计数（重开游戏时调用）</summary>
    public void Reset()
    {
        mIsSelecting = false;
        mPickedCount.Clear();
        mPendingBuffIds.Clear();
        LogKit.Log("[LevelUpManager] 已重置");
    }

    /// <summary>玩家升级 → 弹出升级Buff选择面板</summary>
    private void OnPlayerLevelUp(GameEvents.PlayerLevelUp evt)
    {
        if (mIsSelecting) return;
        mIsSelecting = true;

        // 暂停游戏
        Time.timeScale = 0f;

        // 随机抽取 3 个升级 Buff（按权重）
        BuffUpgradeConfig[] buffs = PickRandomBuffs(3);
        if (buffs == null || buffs.Length == 0)
        {
            LogKit.Warning("[LevelUpManager] 没有可用升级Buff，跳过选择");
            FinishSelection();
            return;
        }

        // 记录当前待选卡（断点保存/恢复用）
        mPendingBuffIds.Clear();
        foreach (var b in buffs)
            if (b != null && !string.IsNullOrEmpty(b.id)) mPendingBuffIds.Add(b.id);

        // 打开升级面板，传入 Buff 数据
        var data = new LevelUpPanelData { Buffs = buffs };
        UIKit.OpenPanel<LevelUpPanel>(UILevel.Pop, data);
    }

    /// <summary>玩家选择了升级Buff → 生效并恢复</summary>
    private void OnUpgradeSelected(GameEvents.UpgradeSelected evt)
    {
        if (!mIsSelecting) return;

        UpgradeBuffSystem.Instance.ApplyBuff(evt.BuffId);

        // 本局已选计数（供抽取池按 maxStack 过滤）
        if (!string.IsNullOrEmpty(evt.BuffId))
            mPickedCount[evt.BuffId] = mPickedCount.TryGetValue(evt.BuffId, out var n) ? n + 1 : 1;

        // 图鉴"增益"分类：记录历史选择过的升级Buff（持久化）
        if (MetaManager.HasInstance)
            MetaManager.Instance.RecordBuffSelected(evt.BuffId);

        FinishSelection();
    }

    /// <summary>结束本次选择，恢复游戏</summary>
    private void FinishSelection()
    {
        mIsSelecting = false;
        mPendingBuffIds.Clear();
        Time.timeScale = 1f;
        UIKit.ClosePanel<LevelUpPanel>();
    }

    /// <summary>每阶抽取权重（索引=阶级：1/2/3；对应 3选1 文档权重）</summary>
    private static readonly float[] sTierWeights = { 0f, 0.65f, 0.25f, 0.10f };

    /// <summary>从配置中先按阶级权重选池，再按池内卡权重随机抽取 count 个升级Buff（本局内不重复，且不超过各卡 maxStack）</summary>
    private BuffUpgradeConfig[] PickRandomBuffs(int count)
    {
        var wrapper = GameDataMgr.Instance.Buffs;
        if (wrapper == null || wrapper.Buffs == null || wrapper.Buffs.Count == 0) return null;

        // 按阶级分池（已抽满 maxStack 的卡不进池）
        var pools = new Dictionary<int, List<BuffUpgradeConfig>>();
        int skipped = 0;
        foreach (var b in wrapper.Buffs)
        {
            if (b == null || string.IsNullOrEmpty(b.id)) continue;
            if (PickedCount(b.id) >= MaxStackOf(b)) { skipped++; continue; }
            int t = Mathf.Clamp(b.tier, 1, 3);
            if (!pools.TryGetValue(t, out var list))
            {
                list = new List<BuffUpgradeConfig>();
                pools[t] = list;
            }
            list.Add(b);
        }
        if (skipped > 0)
            LogKit.Log($"[LevelUpManager] 升级池已排除 {skipped} 张（本局已达 maxStack）");

        var result = new List<BuffUpgradeConfig>();
        // 每抽先选池，再在对应池内按权重抽取（依据阶级权重，但跳过已抽空的阶级）
        var drawn = new List<BuffUpgradeConfig>();
        for (int i = 0; i < count; i++)
        {
            // 当前剩余卡按阶级分池（去掉本轮已抽走的）
            var remaining = new Dictionary<int, List<BuffUpgradeConfig>>();
            foreach (var kv in pools)
            {
                var rest = new List<BuffUpgradeConfig>();
                foreach (var b in kv.Value)
                    if (!drawn.Contains(b)) rest.Add(b);
                if (rest.Count > 0) remaining[kv.Key] = rest;
            }
            if (remaining.Count == 0) break;

            var tier = PickTierPool(remaining);
            var pool = remaining[tier];
            var picked = PickByWeight(pool);
            if (picked == null) break;
            result.Add(picked);
            drawn.Add(picked);
        }
        // 池子被抽空（45 张卡全拿满）时允许少于 count 张，调用方按实际数量显示
        if (result.Count < count)
            LogKit.Warning($"[LevelUpManager] 可用升级Buff不足：需要 {count} 张，实际 {result.Count} 张");
        return result.ToArray();
    }

    /// <summary>本局该卡已获得次数</summary>
    private int PickedCount(string buffId)
        => mPickedCount.TryGetValue(buffId, out var n) ? n : 0;

    /// <summary>该卡的获取上限（<=0 视为 1，避免误配成"永远抽不到"）</summary>
    private static int MaxStackOf(BuffUpgradeConfig b) => b.maxStack > 0 ? b.maxStack : 1;

    /// <summary>先按阶级权重随机选出一个非空阶级（越高阶越稀有）</summary>
    private static int PickTierPool(Dictionary<int, List<BuffUpgradeConfig>> pools)
    {
        float total = 0f;
        foreach (var kv in pools)
            total += TierWeight(kv.Key);
        float roll = Random.Range(0f, total);
        float acc = 0f;
        foreach (var kv in pools)
        {
            acc += TierWeight(kv.Key);
            if (roll <= acc) return kv.Key;
        }
        // 兜底：取第一个非空阶级
        foreach (var kv in pools) return kv.Key;
        return 1;
    }

    private static float TierWeight(int tier)
    {
        return tier > 0 && tier < sTierWeights.Length ? sTierWeights[tier] : sTierWeights[1];
    }

    /// <summary>按权重随机抽取一个（权重越大越容易出）</summary>
    private static BuffUpgradeConfig PickByWeight(List<BuffUpgradeConfig> pool)
    {
        float total = 0f;
        foreach (var b in pool)
            total += Mathf.Max(0.01f, b.weight);

        float roll = Random.Range(0f, total);
        float acc = 0f;
        foreach (var b in pool)
        {
            acc += Mathf.Max(0.01f, b.weight);
            if (roll <= acc) return b;
        }
        return pool[pool.Count - 1];
    }
}
