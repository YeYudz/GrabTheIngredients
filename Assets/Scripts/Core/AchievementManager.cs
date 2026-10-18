using System.Collections.Generic;
using UnityEngine;
using YokiFrame;
using GameUI;

/// <summary>
/// 成就系统（常驻单例）：订阅事件、按配置评估条件、累计进度并解锁发奖。
/// 每个成就的条件为 AND 组合；解锁弹窗走队列，预制体缺失时只记日志、不中断流程。
/// ⚠ LoadFromSave 须在 MetaManager.Init 完成后由启动流程显式调用。
/// </summary>
public class AchievementManager : MonoSingleton<AchievementManager>
{
    private readonly Dictionary<string, AchievementConfig> mConfigs = new();
    private readonly Dictionary<string, AchievementSave> mProgress = new(); // 存档镜像
    private readonly Queue<AchievementConfig> mPopupQueue = new();          // 解锁弹窗队列
    private int mTotalPoints;

    /// <summary>累计成就点数</summary>
    public int TotalPoints => mTotalPoints;

    /// <summary>成就配置列表（UI 遍历用）</summary>
    public IReadOnlyCollection<AchievementConfig> Configs => mConfigs.Values;

    protected override void Awake()
    {
        base.Awake();
        BuildConfigs();
        Subscribe();
    }

    // ===== 初始化 =====

    private void BuildConfigs()
    {
        mConfigs.Clear();
        var list = GameDataMgr.Instance.Achievements?.Achievements;
        if (list == null) return;
        foreach (var a in list)
        {
            if (a == null || string.IsNullOrEmpty(a.achievementId)) continue;
            if (!mConfigs.ContainsKey(a.achievementId)) mConfigs.Add(a.achievementId, a);
        }
    }

    /// <summary>从局外存档载入成就进度（MetaManager.Init 完成后调用）</summary>
    public void LoadFromSave()
    {
        var data = MetaManager.Instance.Data;
        mProgress.Clear();
        if (data.achievementProgress != null)
        {
            foreach (var p in data.achievementProgress)
            {
                if (p != null && !string.IsNullOrEmpty(p.achievementId)) mProgress[p.achievementId] = p;
            }
        }
        mTotalPoints = data.totalAchievementPoints;
        LogKit.Log($"[成就] 加载完成：{mConfigs.Count} 条成就配置，已解锁 {GetUnlockedCount()} 个，累计 {mTotalPoints} 点");
    }

    private void Subscribe()
    {
        EventKit.Type.Register<GameEvents.EnemyKilled>(_ => AddStat(AchievementConditionType.TotalKills, 1))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.WaveCleared>(evt => AddStat(AchievementConditionType.WaveReached, evt.WaveId))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BossDied>(_ => AddStat(AchievementConditionType.BossKills, 1))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.FieldCookingFinished>(evt =>
        {
            AddStat(AchievementConditionType.BattlefieldCookingTriggered, 1);
            AddStat(AchievementConditionType.TotalCooking, 1);
            if (evt.Quality == "S") AddStat(AchievementConditionType.PerfectCooking, 1);
        }).UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BossCookingRecorded>(evt =>
        {
            AddStat(AchievementConditionType.TotalCooking, 1);
            // 历史最高连招（Boss 音游）：取更大值
            SetStat(AchievementConditionType.MaxComboChain, evt.MaxCombo);
        }).UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.MetaCoinEarned>(evt => AddStat(AchievementConditionType.TotalGoldEarned, evt.Amount))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.RecipeUnlocked>(_ => RefreshCollectionStat())
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    // ===== 条件推进 =====

    /// <summary>增量推进条件（+amount）</summary>
    private void AddStat(AchievementConditionType type, int amount) => UpdateCondition(type, amount, false);

    /// <summary>绝对值设置条件（用于收集数这类"当前值"语义）</summary>
    private void SetStat(AchievementConditionType type, int value) => UpdateCondition(type, value, true);

    /// <summary>图鉴收集数变化 → 刷新收集类条件并检查解锁</summary>
    private void RefreshCollectionStat()
    {
        int count = RecipeAtlasManager.HasInstance ? RecipeAtlasManager.Instance.UnlockedCount : 0;
        SetStat(AchievementConditionType.RecipeCollectionCount, count);
    }

    /// <summary>一局结束（GameManager.RecordRunAndSave 调用）</summary>
    public void OnRunEnded() => AddStat(AchievementConditionType.GamesPlayed, 1);

    private void UpdateCondition(AchievementConditionType type, int value, bool isAbsolute)
    {
        bool anyChanged = false;
        bool anyUnlocked = false;

        foreach (var cfg in mConfigs.Values)
        {
            if (cfg.conditions == null || cfg.conditions.Count == 0) continue;

            var progress = GetOrCreateSave(cfg.achievementId);
            if (progress.isUnlocked) continue;

            bool changed = false;
            bool allDone = true;
            for (int i = 0; i < cfg.conditions.Count; i++)
            {
                var cond = cfg.conditions[i];
                int target = EffectiveTarget(cfg, cond);
                int current = progress.conditionCurrent[i];

                if (cond.type == type)
                {
                    int next = isAbsolute ? value : current + value;
                    if (next > current)
                    {
                        progress.conditionCurrent[i] = next;
                        current = next;
                        changed = true;
                    }
                }

                if (current < target) allDone = false;
            }

            if (changed) anyChanged = true;
            if (allDone && !progress.isUnlocked)
            {
                Unlock(cfg, progress);
                anyUnlocked = true;
            }
        }

        if (anyChanged || anyUnlocked) SaveProgress();
    }

    /// <summary>目标值：收集类 targetValue >= 999 视为"收集全部"</summary>
    private int EffectiveTarget(AchievementConfig cfg, AchievementCondition cond)
    {
        if (cond.type == AchievementConditionType.RecipeCollectionCount && cond.targetValue >= 999)
            return RecipeAtlasManager.HasInstance ? RecipeAtlasManager.Instance.TotalCount : 0;
        return cond.targetValue;
    }

    // ===== 解锁 / 奖励 / 弹窗 =====

    /// <summary>解锁：标记已解锁 + 加成就点数 + 存档 + 弹窗（奖励需在面板手动领取，见 ClaimReward）</summary>
    private void Unlock(AchievementConfig cfg, AchievementSave progress)
    {
        progress.isUnlocked = true;
        progress.unlockTimestamp = SaveVersion.NowUnix();
        mTotalPoints += cfg.pointValue;

        var data = MetaManager.Instance.Data;
        if (!data.unlockedAchievementIds.Contains(cfg.achievementId))
            data.unlockedAchievementIds.Add(cfg.achievementId);
        data.totalAchievementPoints = mTotalPoints;
        MetaManager.Instance.Save();

        // 奖励不在解锁时自动发放，改由玩家在成就面板点击领取（ClaimReward）
        EnqueuePopup(cfg);
        LogKit.Log($"[成就] 解锁：{cfg.title}（{cfg.achievementId}）+{cfg.pointValue} 点，奖励待领取");
    }

    // ===== 奖励领取 =====

    /// <summary>是否已领取奖励</summary>
    public bool IsClaimed(string id)
        => mProgress.TryGetValue(id, out var p) && p.claimed;

    /// <summary>是否可领取（已解锁且未领取）</summary>
    public bool CanClaim(string id)
        => IsUnlocked(id) && !IsClaimed(id);

    /// <summary>领取奖励：发放成就奖励并标记已领取（返回是否成功）</summary>
    public bool ClaimReward(string id)
    {
        if (!CanClaim(id)) return false;
        if (!mProgress.TryGetValue(id, out var progress)) return false;

        var cfg = mConfigs.TryGetValue(id, out var c) ? c : null;
        if (cfg == null) return false;

        progress.claimed = true;
        DistributeRewards(cfg);
        SaveProgress();
        LogKit.Log($"[成就] 已领取奖励：{cfg.title}（{cfg.achievementId}）");
        return true;
    }

    private void DistributeRewards(AchievementConfig cfg)
    {
        if (cfg.rewards == null) return;
        foreach (var r in cfg.rewards)
        {
            if (r == null) continue;
            switch (r.type)
            {
                case AchievementRewardType.Gold:
                    MetaManager.Instance.AddCoins(r.amount);
                    break;
                case AchievementRewardType.Ingredient:
                    if (!string.IsNullOrEmpty(r.rewardId))
                        MetaManager.Instance.AddIngredient(r.rewardId, r.amount);
                    break;
                case AchievementRewardType.RecipeUnlock:
                    if (!string.IsNullOrEmpty(r.rewardId))
                        MetaManager.Instance.UnlockRecipe(r.rewardId);
                    break;
                // CharacterUnlock / WeaponUnlock 预留
            }
        }
    }

    private void EnqueuePopup(AchievementConfig cfg)
    {
        mPopupQueue.Enqueue(cfg);
        if (mPopupQueue.Count == 1) ShowNextPopup();
    }

    private void ShowNextPopup()
    {
        while (mPopupQueue.Count > 0)
        {
            var cfg = mPopupQueue.Peek();
            if (GameManager.TryOpenPanel<AchievementPopupPanel>(UILevel.Pop, new AchievementPopupPanelData { Config = cfg }))
                return;
            mPopupQueue.Dequeue(); // 面板缺失 → 跳过
        }
    }

    /// <summary>弹窗收尾时由 AchievementPopupPanel 调用（弹出下一个）</summary>
    public void OnPopupClosed()
    {
        if (mPopupQueue.Count > 0) mPopupQueue.Dequeue();
        ShowNextPopup();
    }

    // ===== 存档 =====

    private AchievementSave GetOrCreateSave(string id)
    {
        if (mProgress.TryGetValue(id, out var p)) return p;

        var cfg = mConfigs[id];
        p = new AchievementSave { achievementId = id };
        for (int i = 0; i < (cfg.conditions?.Count ?? 0); i++) p.conditionCurrent.Add(0);
        mProgress[id] = p;
        return p;
    }

    private void SaveProgress()
    {
        var data = MetaManager.Instance.Data;
        data.achievementProgress.Clear();
        data.achievementProgress.AddRange(mProgress.Values);
        data.totalAchievementPoints = mTotalPoints;
        MetaManager.Instance.Save();
    }

    // ===== 查询接口（UI） =====

    public AchievementConfig GetConfig(string id)
        => mConfigs.TryGetValue(id, out var c) ? c : null;

    public AchievementSave GetProgress(string id)
        => mProgress.TryGetValue(id, out var p) ? p : null;

    public bool IsUnlocked(string id)
        => mProgress.TryGetValue(id, out var p) && p.isUnlocked;

    public int GetUnlockedCount()
    {
        int n = 0;
        foreach (var p in mProgress.Values) if (p.isUnlocked) n++;
        return n;
    }
}
