using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 战斗数据统计：为阶段结算与整局结算提供快照。
/// 阶段计数随 BeginStage 清零、整局计数永不重置——两套计数并存，
/// 才能让「单阶段表现」与「整局累计」共用同一份数据来源。
/// 数值全部由既有事件推导，不侵入战斗逻辑；对外只产出 BattleStatsSnapshot。
/// </summary>
public class BattleStatsCollector : MonoSingleton<BattleStatsCollector>
{
    // ===== 阶段计数（BeginStage 清零） =====
    private float mStageDamage;
    private float mStageTime;
    private int mStageKills;
    private int mStageMaxStreak;
    private int mStageWavesCleared;
    private int mStageFieldCookCount;
    private readonly Dictionary<string, int> mStageIngredients = new();
    private string mBossQuality = "";          // 本阶段 BOSS 音游总评档位（S/A/B/C）
    private readonly List<string> mStageRewards = new(); // 本阶段收获（解锁菜谱/稀有食材/入背包/永久Buff）
    private readonly List<string> mFieldCookRewards = new(); // 本阶段画中画炒菜收获（入包成品菜，BOSS 结算时并入 Rewards）
    private AttrView mStageStartAttrs;          // 阶段开始时的玩家属性（计算本阶段属性提升）

    // ===== 整局计数（不重置） =====
    private float mRunDamage;
    private float mRunTime;
    private int mRunKills;
    private int mRunMaxStreak;
    private int mRunWavesCleared;
    private int mRunFieldCookCount;
    private int mBossesDefeated;
    private int mMaxLevel;
    private readonly Dictionary<string, int> mRunIngredients = new();

    /// <summary>阶段开始时的属性快照（计算属性提升）</summary>
    private struct AttrView
    {
        public float MaxHp, Attack, MoveSpeed, DodgeChance, CritChance, CritDamage,
                     PhysDef, ElemDef, PhysDamageBonus, ElemDamageBonus, AttackSpeedBonus;
    }

    protected override void Awake()
    {
        base.Awake();

        EventKit.Type.Register<GameEvents.DamageDealt>(OnDamageDealt)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.StreakChanged>(OnStreakChanged)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.IngredientDropped>(OnIngredientDropped)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.WaveCleared>(OnWaveCleared)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.PlayerLevelUp>(OnLevelUp)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BossDied>(OnBossDied)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.FieldCookingFinished>(OnFieldCookingFinished)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    // ===== 事件累计 =====

    private void OnDamageDealt(GameEvents.DamageDealt evt)
    {
        mStageDamage += evt.Damage;
        mRunDamage += evt.Damage;
    }

    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        mStageKills++;
        mRunKills++;
    }

    private void OnStreakChanged(GameEvents.StreakChanged evt)
    {
        if (evt.Count > mStageMaxStreak) mStageMaxStreak = evt.Count;
        if (evt.Count > mRunMaxStreak) mRunMaxStreak = evt.Count;
    }

    private void OnIngredientDropped(GameEvents.IngredientDropped evt)
    {
        if (string.IsNullOrEmpty(evt.IngredientId)) return;
        AddCount(mStageIngredients, evt.IngredientId);
        AddCount(mRunIngredients, evt.IngredientId);
    }

    private void OnWaveCleared(GameEvents.WaveCleared evt)
    {
        mStageWavesCleared++;
        mRunWavesCleared++;
    }

    private void OnLevelUp(GameEvents.PlayerLevelUp evt)
    {
        if (evt.NewLevel > mMaxLevel) mMaxLevel = evt.NewLevel;
    }

    private void OnBossDied(GameEvents.BossDied evt)
    {
        mBossesDefeated++;
    }

    private void OnFieldCookingFinished(GameEvents.FieldCookingFinished evt)
    {
        mStageFieldCookCount++;
        mRunFieldCookCount++;
    }

    private static void AddCount(Dictionary<string, int> dict, string id)
    {
        dict[id] = dict.TryGetValue(id, out var c) ? c + 1 : 1;
    }

    private void Update()
    {
        // 活跃战斗时间（timeScale>0 才累加；结算暂停/音游暂停不计）
        if (Time.timeScale > 0f)
        {
            mStageTime += Time.deltaTime;
            mRunTime += Time.deltaTime;
        }
    }

    // ===== 阶段生命周期 =====

    /// <summary>开始新阶段：清零阶段计数 + 快照当前玩家属性（开局/每 BOSS 胜利后调用）</summary>
    public void BeginStage()
    {
        mStageDamage = 0f;
        mStageTime = 0f;
        mStageKills = 0;
        mStageMaxStreak = 0;
        mStageWavesCleared = 0;
        mStageFieldCookCount = 0;
        mStageIngredients.Clear();
        mBossQuality = "";
        mStageRewards.Clear();
        mFieldCookRewards.Clear();

        mStageStartAttrs = CaptureAttrs();
    }

    /// <summary>记录本阶段 BOSS 收获（由 BossBattleManager 在 BOSS 胜利时调用）</summary>
    public void RecordBossRewards(string quality, List<string> rewards)
    {
        mBossQuality = quality;
        mStageRewards.Clear();
        if (rewards != null) mStageRewards.AddRange(rewards);
    }

    /// <summary>记录本阶段画中画炒菜入包收获（由 FieldCookingEngine 结算时调用，阶段结算收获页签展示）</summary>
    public void RecordFieldCookingReward(string text)
    {
        if (!string.IsNullOrEmpty(text)) mFieldCookRewards.Add(text);
    }

    // ===== 快照 =====

    /// <summary>结算快照（阶段 + 整局，三个结算面板共用）</summary>
    public BattleStatsSnapshot GetStageSnapshot()
    {
        // 属性快照各取一次（原先每个字段都调一次 CaptureAttrs，白跑 11 次）
        var start = mStageStartAttrs;
        var now = CaptureAttrs();

        return new BattleStatsSnapshot
        {
            // 阶段
            StageIndex = WaveManager.HasInstance ? WaveManager.Instance.CurrentPhaseIndex + 1 : 1,
            StageCount = WaveManager.HasInstance ? WaveManager.Instance.PhaseCount : 1,
            StageDamage = mStageDamage,
            StageDps = mStageTime > 0f ? mStageDamage / mStageTime : 0f,
            StageKills = mStageKills,
            StageMaxStreak = mStageMaxStreak,
            StageTime = mStageTime,
            StageWavesCleared = mStageWavesCleared,
            StageFieldCookCount = mStageFieldCookCount,
            BossQuality = mBossQuality,
            Rewards = MergeRewards(),
            StageIngredients = SortedCopy(mStageIngredients),
            // 属性提升（本阶段变化量）
            // 注意：绝对值属性走 Diff，比率属性（闪避/暴击/暴伤/物元加成/攻速）必须走 DiffPercent
            DeltaMaxHp = Diff(start.MaxHp, now.MaxHp),
            DeltaAttack = Diff(start.Attack, now.Attack),
            DeltaMoveSpeed = Diff(start.MoveSpeed, now.MoveSpeed),
            DeltaDodgeChance = DiffPercent(start.DodgeChance, now.DodgeChance),
            DeltaCritChance = DiffPercent(start.CritChance, now.CritChance),
            DeltaCritDamage = DiffPercent(start.CritDamage, now.CritDamage),
            DeltaPhysDef = Diff(start.PhysDef, now.PhysDef),
            DeltaElemDef = Diff(start.ElemDef, now.ElemDef),
            DeltaPhysDamageBonus = DiffPercent(start.PhysDamageBonus, now.PhysDamageBonus),
            DeltaElemDamageBonus = DiffPercent(start.ElemDamageBonus, now.ElemDamageBonus),
            DeltaAttackSpeed = DiffPercent(start.AttackSpeedBonus, now.AttackSpeedBonus),
            // 整局
            RunDamage = mRunDamage,
            RunDps = mRunTime > 0f ? mRunDamage / mRunTime : 0f,
            RunKills = mRunKills,
            RunMaxStreak = mRunMaxStreak,
            RunTime = mRunTime,
            RunWavesCleared = mRunWavesCleared,
            RunFieldCookCount = mRunFieldCookCount,
            BossesDefeated = mBossesDefeated,
            MaxLevel = mMaxLevel,
            RunIngredients = SortedCopy(mRunIngredients)
        };
    }

    /// <summary>绝对值属性差值（血量/攻击/移速/防御）</summary>
    private static float Diff(float start, float now) => Mathf.Round(now - start);

    /// <summary>
    /// 比率属性差值（闪避/暴击率/暴击伤害/物元伤害加成/攻速加成）。
    /// 这类字段存的是比率（0.45 = +45%），必须先 ×100 再取整，
    /// 否则 0.45 会被 Round 抹成 0、再被面板的 "&lt;0.01 则跳过" 过滤掉 —— 表现为该行永远不显示。
    /// 返回值单位 = 百分点，与结算面板的 "%" 后缀配套。
    /// </summary>
    private static float DiffPercent(float start, float now) => Mathf.Round((now - start) * 100f);

    /// <summary>收获列表合并：BOSS 奖励在前 + 画中画炒菜入包收获在后</summary>
    private List<string> MergeRewards()
    {
        var merged = new List<string>(mStageRewards);
        merged.AddRange(mFieldCookRewards);
        return merged;
    }

    private static List<IngredientStack> SortedCopy(Dictionary<string, int> dict)
    {
        var list = new List<IngredientStack>();
        foreach (var kv in dict)
        {
            if (kv.Value <= 0) continue;
            list.Add(new IngredientStack { id = kv.Key, count = kv.Value });
        }
        list.Sort((a, b) => b.count.CompareTo(a.count));
        return list;
    }

    /// <summary>快照玩家当前战斗属性（基础+动态加成+攻速/伤害加成）</summary>
    private AttrView CaptureAttrs()
    {
        var p = GameManager.Instance?.Player;
        if (p == null) return default;

        return new AttrView
        {
            MaxHp = p.MaxHp,
            Attack = p.Attack,
            MoveSpeed = p.MoveSpeed,
            DodgeChance = p.DodgeChance,
            CritChance = p.CritChance,
            CritDamage = p.CritDamage,
            PhysDef = p.PhysDef,
            ElemDef = p.ElemDef,
            PhysDamageBonus = p.PhysDamageBonus,
            ElemDamageBonus = p.ElemDamageBonus,
            AttackSpeedBonus = p.AttackSpeedBonus
        };
    }

    /// <summary>断点保存：导出阶段 + 整局全部计数（续玩后结算页「整局数据」才完整）</summary>
    public RunStatsSave Export()
    {
        var s = new RunStatsSave
        {
            stageDamage = mStageDamage,
            stageTime = mStageTime,
            stageKills = mStageKills,
            stageMaxStreak = mStageMaxStreak,
            stageWavesCleared = mStageWavesCleared,
            stageFieldCookCount = mStageFieldCookCount,
            bossQuality = mBossQuality ?? "",
            stageRewards = new List<string>(mStageRewards),
            fieldCookRewards = new List<string>(mFieldCookRewards),
            stageIngredients = SortedCopy(mStageIngredients),
            runDamage = mRunDamage,
            runTime = mRunTime,
            runKills = mRunKills,
            runMaxStreak = mRunMaxStreak,
            runWavesCleared = mRunWavesCleared,
            runFieldCookCount = mRunFieldCookCount,
            bossesDefeated = mBossesDefeated,
            maxLevel = mMaxLevel,
            runIngredients = SortedCopy(mRunIngredients),
        };
        return s;
    }

    /// <summary>
    /// 断点恢复：写回阶段 + 整局计数。
    /// 阶段属性基准重取"当前属性"，即续玩后的「属性提升」从此刻开始算（续玩前的提升已在存档的数值里）。
    /// </summary>
    public void Import(RunStatsSave s)
    {
        if (s == null) return;

        mStageDamage = s.stageDamage;
        mStageTime = s.stageTime;
        mStageKills = s.stageKills;
        mStageMaxStreak = s.stageMaxStreak;
        mStageWavesCleared = s.stageWavesCleared;
        mStageFieldCookCount = s.stageFieldCookCount;
        mBossQuality = s.bossQuality ?? "";
        mStageRewards.Clear();
        if (s.stageRewards != null) mStageRewards.AddRange(s.stageRewards);
        mFieldCookRewards.Clear();
        if (s.fieldCookRewards != null) mFieldCookRewards.AddRange(s.fieldCookRewards);

        mStageIngredients.Clear();
        FillCounts(mStageIngredients, s.stageIngredients);

        mRunDamage = s.runDamage;
        mRunTime = s.runTime;
        mRunKills = s.runKills;
        mRunMaxStreak = s.runMaxStreak;
        mRunWavesCleared = s.runWavesCleared;
        mRunFieldCookCount = s.runFieldCookCount;
        mBossesDefeated = s.bossesDefeated;
        mMaxLevel = s.maxLevel;

        mRunIngredients.Clear();
        FillCounts(mRunIngredients, s.runIngredients);

        mStageStartAttrs = CaptureAttrs();

        LogKit.Log($"[BattleStatsCollector] 断点恢复：整局伤害 {mRunDamage:F0}，击杀 {mRunKills}，时长 {mRunTime:F0}s，已清波次 {mRunWavesCleared}");
    }

    private static void FillCounts(Dictionary<string, int> dict, List<IngredientStack> list)
    {
        if (list == null) return;
        foreach (var it in list)
        {
            if (it == null || string.IsNullOrEmpty(it.id) || it.count <= 0) continue;
            dict[it.id] = it.count;
        }
    }

    /// <summary>重置（重开/回主菜单时调用）</summary>
    public void Reset()
    {
        mStageDamage = mRunDamage = 0f;
        mStageTime = mRunTime = 0f;
        mStageKills = mRunKills = 0;
        mStageMaxStreak = mRunMaxStreak = 0;
        mStageWavesCleared = mRunWavesCleared = 0;
        mStageFieldCookCount = mRunFieldCookCount = 0;
        mBossesDefeated = 0;
        mMaxLevel = 0;
        mStageIngredients.Clear();
        mRunIngredients.Clear();
        mStageRewards.Clear();
        mBossQuality = "";
    }
}

/// <summary>结算数据快照（阶段 + 整局，面板展示用）</summary>
public class BattleStatsSnapshot
{
    // ===== 阶段 =====
    public int StageIndex = 1;                       // 本阶段序号（1 起；旧模式恒为 1）
    public int StageCount = 1;                       // 阶段总数（旧模式恒为 1）
    public float StageDamage;
    public float StageDps;
    public int StageKills;
    public int StageMaxStreak;
    public float StageTime;
    public int StageWavesCleared;
    public int StageFieldCookCount;
    public string BossQuality;                       // BOSS 音游品质（死亡结算为空）
    public List<string> Rewards = new();             // 本阶段收获
    public List<IngredientStack> StageIngredients = new();

    // ===== 属性提升（本阶段变化量） =====
    public float DeltaMaxHp, DeltaAttack, DeltaMoveSpeed, DeltaDodgeChance, DeltaCritChance,
                 DeltaCritDamage, DeltaPhysDef, DeltaElemDef, DeltaPhysDamageBonus,
                 DeltaElemDamageBonus, DeltaAttackSpeed;

    // ===== 整局 =====
    public float RunDamage;
    public float RunDps;
    public int RunKills;
    public int RunMaxStreak;
    public float RunTime;
    public int RunWavesCleared;
    public int RunFieldCookCount;
    public int BossesDefeated;
    public int MaxLevel;
    public int RunCoins;                             // 本局结算获得钱币（局末按波次/击杀发放；由 GameManager 写入）
    public List<IngredientStack> RunIngredients = new();
}
