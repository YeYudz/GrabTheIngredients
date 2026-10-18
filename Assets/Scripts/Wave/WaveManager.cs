using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 波次管理：以 FSM 驱动 Prep → Spawning → Clearing → 下一波 / BOSS 的循环。
/// 波次数据读 WaveConfig，刷怪走对象池；对外只广播波次开始 / 结束事件。
/// </summary>
public class WaveManager : MonoSingleton<WaveManager>
{
    private FSM<WaveState> mFsm;
    private WaveBlack mBlack;
    private bool mStarted;

    /// <summary>当前波次序号（1 起）</summary>
    public int CurrentWave { get; private set; } = 1;

    /// <summary>是否正在运行（Boss 战会暂停普通波次）</summary>
    public bool IsRunning { get; private set; }

    // ===== 波次击杀进度（HUD ProgressBar 数据源） =====

    /// <summary>本波总敌人数（Boss 波 = 1）</summary>
    public int WaveTotalEnemies { get; private set; }

    /// <summary>本波开始时已累计击杀数（用于计算本波击杀）</summary>
    private int mWaveStartKill;

    /// <summary>当前关卡的波次序列（旧模式：关卡内第 n 波 → WaveConfig.waveId）；为空时退回全局波次表</summary>
    private List<int> mWaveSequence;

    /// <summary>当前副本的阶段序列（阶段模式）；为 null 时使用旧 mWaveSequence 逻辑</summary>
    private List<DungeonPhase> mPhases;

    /// <summary>是否处于"阶段模式"（关卡配置了 phases）</summary>
    public bool IsPhaseMode => mPhases != null;

    /// <summary>当前阶段索引（0 起；旧模式恒为 0）</summary>
    public int CurrentPhaseIndex { get; private set; }

    /// <summary>阶段总数（旧模式为 1）</summary>
    public int PhaseCount => mPhases != null ? mPhases.Count : 1;

    /// <summary>当前阶段配置（旧模式返回 null）</summary>
    public DungeonPhase CurrentPhase =>
        mPhases != null && CurrentPhaseIndex >= 0 && CurrentPhaseIndex < mPhases.Count
            ? mPhases[CurrentPhaseIndex] : null;

    /// <summary>当前阶段ID（词缀作用域用；旧模式为空）</summary>
    public string CurrentPhaseId => CurrentPhase?.phaseId;

    /// <summary>本阶段总波次数（旧模式为本关全部波次）</summary>
    public int TotalWaves => mPhases != null
        ? (CurrentPhase?.waveIds?.Count ?? 0)
        : (mWaveSequence != null ? mWaveSequence.Count : (GameDataMgr.Instance.Waves?.Count ?? 0));

    /// <summary>当前阶段 BOSS 血量倍率（旧模式恒为 1）</summary>
    public float CurrentPhaseBossHpScale => CurrentPhase?.bossHpScale ?? 1f;

    /// <summary>当前波次对应的全局 waveId（词缀作用域用；取不到为 0）</summary>
    public int CurrentGlobalWaveId => GetWaveConfigAt(CurrentWave)?.waveId ?? 0;

    /// <summary>当前波次在本关内的累计序号（跨阶段连续递增，1 起；事件槽区间 / 成就统计使用）</summary>
    public int DungeonWaveOrdinal
    {
        get
        {
            if (mPhases == null) return CurrentWave;
            int sum = 0;
            for (int i = 0; i < CurrentPhaseIndex && i < mPhases.Count; i++)
                sum += mPhases[i]?.waveIds?.Count ?? 0;
            return sum + CurrentWave;
        }
    }

    /// <summary>当前副本ID（ApplyDungeon 时记录；断点保存/解锁判定使用）</summary>
    public string CurrentDungeonId { get; private set; }

    /// <summary>本波开始时的累计击杀数（波次进度条基准；断点恢复原样写回）</summary>
    public int WaveStartKill => mWaveStartKill;

    /// <summary>
    /// 断点续玩时是否发生了「BOSS 已清 → 跳进下一阶段/下一波」（一次性的，供恢复方重置阶段统计）。
    /// 存档停在阶段结算页时不会重打 BOSS，但上一阶段的统计计数还留在 BattleStatsCollector 里，
    /// 恢复方需要据此调 BeginStage()，否则新阶段的结算会带着上一阶段的数据。
    /// </summary>
    public bool RestoredPastClearedBoss { get; private set; }

    // ===== 断点续玩：运行时状态对外暴露（保存需要，恢复需要写回） =====

    /// <summary>当前波次状态机的状态（Prep/Spawning/Clearing/Boss）</summary>
    public WaveState CurrentState => mFsm != null ? mFsm.CurEnum : WaveState.Prep;

    /// <summary>Prep 状态剩余倒计时（秒；非 Prep 时为 0）</summary>
    public float PrepRemain { get; private set; }

    /// <summary>刷怪游标：当前正在生成的组索引（组内已发完则已指向下一组）</summary>
    public int SpawnGroupIndex { get; private set; }

    /// <summary>刷怪游标：当前组内已生成的个数</summary>
    public int SpawnIndexInGroup { get; private set; }

    /// <summary>本波刷怪是否已全部发完</summary>
    public bool SpawnFinished { get; private set; }

    /// <summary>恢复时跳过 Prep 原有倒计时的覆盖值（&lt;0 = 无覆盖，一次性）</summary>
    private float mRestorePrepRemain = -1f;

    /// <summary>恢复时刷怪协程的起始游标（一次性）</summary>
    private int mRestoreSpawnGroup;
    private int mRestoreSpawnIndex;

    /// <summary>恢复时存档里的「波次已停止（本局已结束）」标记（一次性）</summary>
    private bool mRestoreStoppedWaves;

    internal void SetPrepRemain(float v) => PrepRemain = Mathf.Max(0f, v);

    /// <summary>刷怪协程推进游标（每生成一只后调用，供断点保存读取）</summary>
    internal void SetSpawnCursor(int groupIndex, int indexInGroup, bool finished)
    {
        SpawnGroupIndex = Mathf.Max(0, groupIndex);
        SpawnIndexInGroup = Mathf.Max(0, indexInGroup);
        SpawnFinished = finished;
    }

    /// <summary>取出并清空"指定组/组内序号从哪开始"的恢复指令（协程开头调用一次）</summary>
    internal void ConsumeRestoreSpawnCursor(out int groupIndex, out int indexInGroup)
    {
        groupIndex = mRestoreSpawnGroup;
        indexInGroup = mRestoreSpawnIndex;
        mRestoreSpawnGroup = 0;
        mRestoreSpawnIndex = 0;
    }

    /// <summary>取出并清空 Prep 倒计时覆盖值（Prep 入口调用一次）</summary>
    internal bool ConsumeRestorePrepRemain(out float remain)
    {
        remain = mRestorePrepRemain;
        if (mRestorePrepRemain < 0f) return false;
        mRestorePrepRemain = -1f;
        return true;
    }

    /// <summary>本波击杀进度 0~1（Boss 波同样按清怪进度计算：开局 0 → 清完普通怪后满，BOSS 阶段保持满）</summary>
    public float WaveKillProgress
    {
        get
        {
            if (WaveTotalEnemies <= 0) return 0f;
            int killed = Mathf.Clamp((DropManager.Instance?.KillCount ?? 0) - mWaveStartKill, 0, WaveTotalEnemies);
            return (float)killed / WaveTotalEnemies;
        }
    }

    protected override void Awake()
    {
        base.Awake();
        mBlack = new WaveBlack { Manager = this };
        mFsm = new FSM<WaveState>("WaveSystem");
        mFsm.Add(WaveState.Prep, new WavePrepState(mFsm, mBlack));
        mFsm.Add(WaveState.Spawning, new WaveSpawningState(mFsm, mBlack));
        mFsm.Add(WaveState.Clearing, new WaveClearingState(mFsm, mBlack));
        mFsm.Add(WaveState.Boss, new WaveBossState(mFsm, mBlack));
    }

    /// <summary>开始波次循环（由 GameManager 在进入游戏时调用）</summary>
    public void StartWaves()
    {
        if (mStarted) return;
        mStarted = true;
        IsRunning = true;
        mFsm.Start(WaveState.Prep);
    }

    /// <summary>重置波次系统（重开游戏时调用）</summary>
    public void Reset()
    {
        // 先停止所有协程（旧 FSM 的 SpawnRoutine 等），避免 Dispose 后协程继续刷怪
        StopAllCoroutines();

        mStarted = false;
        IsRunning = false;
        CurrentWave = 1;
        CurrentPhaseIndex = 0;
        WaveTotalEnemies = 0;
        mWaveStartKill = 0;
        PrepRemain = 0f;
        SpawnGroupIndex = 0;
        SpawnIndexInGroup = 0;
        SpawnFinished = false;
        mRestorePrepRemain = -1f;
        mRestoreSpawnGroup = 0;
        mRestoreSpawnIndex = 0;
        mRestoreStoppedWaves = false;
        mPrepared = false;

        // FSM 无 Stop/Resume，Dispose 后重建
        mFsm?.Dispose();
        mFsm = new FSM<WaveState>("WaveSystem");
        mFsm.Add(WaveState.Prep, new WavePrepState(mFsm, mBlack));
        mFsm.Add(WaveState.Spawning, new WaveSpawningState(mFsm, mBlack));
        mFsm.Add(WaveState.Clearing, new WaveClearingState(mFsm, mBlack));
        mFsm.Add(WaveState.Boss, new WaveBossState(mFsm, mBlack));

        LogKit.Log("[WaveManager] 已重置");
    }

    /// <summary>断点恢复的已有 BOSS（RunSaveManager 传入，Boss 状态直接接管，避免重复生成）</summary>
    public BossHealth PendingRestoredBoss { get; set; }

    /// <summary>
    /// 断点恢复第一步：写入阶段 / 波次 / 击杀基准 / 刷怪游标，**不启动状态机**。
    /// 必须先做这一步再重建场上怪物——敌人的属性倍率（关卡难度 / 词缀 / 波次成长）
    /// 是按「当前阶段 + 当前波次」解析的，阶段没写回去就会按第 1 波的血量生成。
    /// </summary>
    public void PrepareRestore(RunSaveData snap)
    {
        if (snap == null) return;

        Reset();
        ApplyRestoreFields(snap);
        LogKit.Log($"[WaveManager] 断点恢复预备：第 {CurrentPhaseIndex + 1} 阶段 / 第 {CurrentWave} 波，游标=组{SpawnGroupIndex}#{SpawnIndexInGroup}");
    }

    /// <summary>
    /// 断点恢复第二步：场上的怪物与掉落物已重建完毕，按存档状态启动波次状态机
    /// （Prep 续倒计时 / Spawning 续刷怪游标 / Clearing 等清场 / Boss 接管）。
    /// </summary>
    public void RestoreRun(RunSaveData snap, BossHealth existingBoss = null)
    {
        if (snap == null) return;

        // 未走 PrepareRestore 时自洽（幂等，不重复 Reset）
        if (!mPrepared) { Reset(); ApplyRestoreFields(snap); }

        PendingRestoredBoss = existingBoss;

        // 目标状态纠正：有 BOSS → 一定在 Boss 波
        RestoredPastClearedBoss = false;
        var state = (WaveState)Mathf.Clamp(snap.waveState, 0, 3);
        if (existingBoss != null)
        {
            state = WaveState.Boss;
        }
        else if (state == WaveState.Boss)
        {
            // 存档时处于 Boss 状态但没有 BOSS 存活 = BOSS 已被击败、停在阶段结算页。
            // 奖励与结算数据在 BOSS 死亡时已发放，这里直接推进到下一阶段/下一波，避免重复打 BOSS 刷奖励。
            RestoredPastClearedBoss = true;
            state = AdvancePastClearedBoss(snap);
        }
        if (state == WaveState.Spawning && snap.spawnFinished) state = WaveState.Clearing;

        mStarted = true;
        IsRunning = !mRestoreStoppedWaves;
        mRestoreStoppedWaves = false;
        mFsm.Start(state);

        // 未被消费的恢复指令要清掉，避免影响后续正常波次
        mRestorePrepRemain = -1f;
        mRestoreSpawnGroup = 0;
        mRestoreSpawnIndex = 0;
        mPrepared = false;

        LogKit.Log($"[WaveManager] 断点恢复：第 {CurrentPhaseIndex + 1} 阶段 / 第 {CurrentWave} 波，状态={state}，" +
                   $"刷怪游标=组{SpawnGroupIndex}#{SpawnIndexInGroup}{(SpawnFinished ? "（已发完）" : "")}" +
                   (existingBoss != null ? "（BOSS 战中）" : ""));
    }

    /// <summary>是否已做过 PrepareRestore（避免 RestoreRun 再 Reset 一次）</summary>
    private bool mPrepared;

    /// <summary>
    /// 存档停在「BOSS 已击败、阶段结算未确认」时：把阶段/波次推进一位，返回应当进入的状态。
    /// 奖励结算已在 BOSS 死亡时完成，不重打 BOSS 才能避免刷奖励。
    /// </summary>
    private WaveState AdvancePastClearedBoss(RunSaveData snap)
    {
        if (mPhases != null)
        {
            if (CurrentPhaseIndex + 1 < mPhases.Count)
            {
                CurrentPhaseIndex++;
                CurrentWave = 1;
                LogKit.Log($"[WaveManager] 断点续玩：BOSS 已清，直接进入第 {CurrentPhaseIndex + 1} 阶段");
                return WaveState.Prep;
            }
        }
        else if (HasWave(CurrentWave + 1))
        {
            CurrentWave++;
            LogKit.Log($"[WaveManager] 断点续玩：BOSS 已清，直接进入第 {CurrentWave} 波");
            return WaveState.Prep;
        }

        // 已是最后一阶段/最后一波：本局实际已结束（正常情况下 CaptureCheckpoint 不会保存这种局）
        LogKit.Warning("[WaveManager] 断点续玩：BOSS 已清且没有后续阶段，停止波次");
        StopWaves();
        return WaveState.Clearing;
    }

    /// <summary>写入阶段/波次/击杀基准/刷怪游标/准备倒计时（不碰 FSM）</summary>
    private void ApplyRestoreFields(RunSaveData snap)
    {
        CurrentPhaseIndex = Mathf.Max(0, snap.phaseIndex);
        CurrentWave = Mathf.Max(1, snap.waveIndex);
        mWaveStartKill = Mathf.Max(0, snap.waveStartKill);
        WaveTotalEnemies = Mathf.Max(0, snap.waveTotalEnemies);

        // 刷怪游标 + Prep 倒计时（由对应状态的 OnEnter 一次性消费）
        mRestoreSpawnGroup = Mathf.Max(0, snap.spawnGroupIndex);
        mRestoreSpawnIndex = Mathf.Max(0, snap.spawnIndexInGroup);
        mRestorePrepRemain = Mathf.Max(0f, snap.prepRemain);

        SpawnGroupIndex = mRestoreSpawnGroup;
        SpawnIndexInGroup = mRestoreSpawnIndex;
        SpawnFinished = snap.spawnFinished;
        PrepRemain = mRestorePrepRemain;
        mRestoreStoppedWaves = snap.wavesStopped;

        mPrepared = true;
    }

    /// <summary>切换到下一波</summary>
    public void NextWave()
    {
        CurrentWave++;
        mFsm.Change(WaveState.Prep);
    }

    /// <summary>GM 调试：直接跳转到指定波次（当前阶段内；清空场上残留敌人后重新开始该波）</summary>
    public void JumpToWave(int wave)
    {
        if (wave < 1 || !HasWave(wave))
        {
            LogKit.Warning($"[WaveManager] 无法跳转到第 {wave} 波（不存在）");
            return;
        }

        // 清空场上残留敌人，避免与新波叠加
        var stragglers = new List<EnemySimple>(EnemySimple.AllEnemies);
        foreach (var e in stragglers)
            if (e != null) e.TakeDamage(999999f);

        CurrentWave = wave;
        mFsm.Change(WaveState.Prep);
        LogKit.Log($"[WaveManager] 已跳转到第 {wave} 波");
    }

    /// <summary>切换到 Boss 波（当前波结束后）</summary>
    public void TriggerBoss()
    {
        mFsm.Change(WaveState.Boss);
    }

    /// <summary>Boss 结束后恢复：阶段模式进入下一阶段，旧模式进入下一波</summary>
    public void ResumeAfterBoss()
    {
        if (mPhases != null) NextPhase();
        else NextWave();
    }

    /// <summary>是否存在下一个阶段（仅阶段模式有意义）</summary>
    public bool HasNextPhase() => mPhases != null && CurrentPhaseIndex + 1 < mPhases.Count;

    /// <summary>进入下一阶段（阶段内波次从 1 重新开始）；已是最后阶段则停止波次</summary>
    public void NextPhase()
    {
        if (mPhases == null) { NextWave(); return; }

        CurrentPhaseIndex++;
        if (CurrentPhaseIndex >= mPhases.Count)
        {
            StopWaves();
            return;
        }
        CurrentWave = 1;
        mFsm.Change(WaveState.Prep);
    }

    /// <summary>当前应生成的 BOSS ID：阶段模式取阶段声明的 bossId，缺省时取（末尾）BOSS 波的配置</summary>
    public string GetCurrentBossId()
    {
        if (mPhases != null)
        {
            if (!string.IsNullOrEmpty(CurrentPhase?.bossId)) return CurrentPhase.bossId;
        }
        return GetCurrentWaveConfig()?.bossId;
    }

    /// <summary>
    /// 当前是否应进入 Boss 战。
    /// 阶段模式：本阶段最后一波（= BOSS 波）清空后触发（该 BOSS 波本身可以带小怪）。
    /// 旧模式：当前波配置 triggersBoss。
    /// </summary>
    public bool ShouldTriggerBoss()
    {
        if (mPhases != null)
        {
            if (CurrentWave < TotalWaves) return false;
            return !string.IsNullOrEmpty(GetCurrentBossId());
        }
        return GetCurrentWaveConfig()?.triggersBoss ?? false;
    }

    /// <summary>停止波次循环（最终 BOSS 被击败后调用）</summary>
    public void StopWaves()
    {
        IsRunning = false;
    }

    /// <summary>
    /// 应用副本配置（进入战斗前调用）：优先使用副本的阶段序列
    /// （每阶段 = 若干小怪波次 + 末尾 1 个 BOSS 波，BOSS 波也计入 waveIds）；
    /// 未配置阶段时退回 waveIds 序列；两者都没有则退回全局波次表（waveId == 波次序号）。
    /// </summary>
    public void ApplyDungeon(DungeonConfig dungeon)
    {
        CurrentDungeonId = dungeon?.dungeonId;
        CurrentPhaseIndex = 0;

        if (dungeon.HasPhases())
        {
            mPhases = new List<DungeonPhase>(dungeon.phases);
            mWaveSequence = null;
        }
        else
        {
            mPhases = null;
            mWaveSequence = (dungeon?.waveIds != null && dungeon.waveIds.Count > 0)
                ? new List<int>(dungeon.waveIds)
                : null;
        }

        LogKit.Log($"[WaveManager] 应用副本：{(string.IsNullOrEmpty(dungeon?.dungeonName) ? "（未配置，使用全局波次表）" : dungeon.dungeonName)}，" +
                   $"阶段数={PhaseCount}，本阶段波次={TotalWaves}");
    }

    /// <summary>获取当前波次配置</summary>
    public WaveConfig GetCurrentWaveConfig() => GetWaveConfigAt(CurrentWave);

    /// <summary>按阶段内波次序号（1 起）取波次配置；无关卡序列时退回全局波次表</summary>
    public WaveConfig GetWaveConfigAt(int waveIndex)
    {
        // 阶段模式：波次来自当前阶段的 waveIds
        if (mPhases != null)
        {
            var phase = CurrentPhase;
            if (phase?.waveIds == null) return null;
            int index = waveIndex - 1;
            if (index < 0 || index >= phase.waveIds.Count) return null;
            return GameDataMgr.Instance.GetWave(phase.waveIds[index]);
        }
        if (mWaveSequence != null)
        {
            int index = waveIndex - 1;
            if (index < 0 || index >= mWaveSequence.Count) return null;
            return GameDataMgr.Instance.GetWave(mWaveSequence[index]);
        }
        return GameDataMgr.Instance.GetWave(waveIndex);
    }

    /// <summary>指定波次序号是否存在（用于判断是否还有下一波）</summary>
    public bool HasWave(int waveIndex) => GetWaveConfigAt(waveIndex) != null;

    /// <summary>
    /// 波次准备时记录击杀进度基准（由 WavePrepState 调用）
    /// </summary>
    public void RecordWaveStart(WaveConfig config)
    {
        mWaveStartKill = DropManager.Instance?.KillCount ?? 0;
        WaveTotalEnemies = SumGroupCounts(config);
    }

    /// <summary>计算 spawnGroups 的敌人总数</summary>
    private static int SumGroupCounts(WaveConfig config)
    {
        if (config?.spawnGroups == null) return 0;
        int total = 0;
        foreach (var g in config.spawnGroups)
        {
            if (g != null) total += g.count;
        }
        return total;
    }

    private void Update()
    {
        if (IsRunning)
        {
            mFsm.Update();
        }
    }

    private void OnDestroy()
    {
        mFsm?.Dispose();
    }
}

/// <summary>波次状态枚举</summary>
public enum WaveState
{
    Prep,       // 准备（等待 prepTime）
    Spawning,   // 刷怪（依次生成 spawnGroups）
    Clearing,   // 清场（等待场上敌人清空）
    Boss        // Boss 波（Step5 接入，当前占位）
}

/// <summary>波次状态机共享黑板</summary>
public class WaveBlack
{
    public WaveManager Manager;
}

/// <summary>准备状态：等待 prepTime 后进入刷怪</summary>
public class WavePrepState : AbstractState<WaveState, WaveBlack>
{
    private float mTimer;

    public WavePrepState(FSM<WaveState> fsm, WaveBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        var config = mBlack.Manager.GetCurrentWaveConfig();

        // 断点恢复：沿用存档里的剩余倒计时；击杀基准与"波次开始"事件已在存档里，不重放
        if (mBlack.Manager.ConsumeRestorePrepRemain(out float restoredRemain))
        {
            mTimer = restoredRemain;
            mBlack.Manager.SetPrepRemain(mTimer);
            LogKit.Log($"[WaveManager] 第 {mBlack.Manager.CurrentWave} 波继续准备倒计时 {mTimer:F1}s（断点恢复）");
            return;
        }

        mTimer = config?.prepTime ?? 3f;
        mBlack.Manager.SetPrepRemain(mTimer);
        mBlack.Manager.SetSpawnCursor(0, 0, false); // 新一波：刷怪游标归零（断点保存读的就是它）

        // 记录本波击杀进度基准（HUD ProgressBar 数据源）
        mBlack.Manager.RecordWaveStart(config);

        // 广播波次开始事件（UI 显示"第 X 波"）
        EventKit.Type.Send(new GameEvents.WaveStarted { WaveId = mBlack.Manager.DungeonWaveOrdinal });
        LogKit.Log($"[WaveManager] 第 {mBlack.Manager.CurrentWave} 波开始，{mTimer}s 后刷怪");
    }

    protected override void OnUpdate()
    {
        // 对象池预热进行中暂不倒数：确保 LoadingPanel 预加载完成后才正式开始刷怪
        if (PoolPrewarmer.IsPrewarming) return;

        mTimer -= Time.deltaTime;
        mBlack.Manager.SetPrepRemain(mTimer);
        if (mTimer <= 0f)
        {
            mFSM.Change(WaveState.Spawning);
        }
    }
}

/// <summary>刷怪状态：按 spawnGroups 依次生成敌人（协程）</summary>
public class WaveSpawningState : AbstractState<WaveState, WaveBlack>
{
    private Coroutine mSpawnCoroutine;
    private bool mFinished;

    public WaveSpawningState(FSM<WaveState> fsm, WaveBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        var config = mBlack.Manager.GetCurrentWaveConfig();
        mFinished = false;
        mSpawnCoroutine = mBlack.Manager.StartCoroutine(SpawnRoutine(config));
    }

    private IEnumerator SpawnRoutine(WaveConfig config)
    {
        var wrapper = GameDataMgr.Instance?.Waves;
        int waveId = config?.waveId ?? 0;

        // 生成速度曲线：同一波内速度恒定，故每波只分配 1 个 WaitForSeconds（保持原零分配优化）
        float pacingInterval = wrapper != null ? wrapper.ResolveSpawnInterval(waveId) : 0f;
        float pacingRate = pacingInterval > 0f ? 1f / pacingInterval : 0f;

        // 同屏存活上限：防"生成速率 > 击杀速率"导致敌人无限堆积
        int maxAlive = wrapper != null ? wrapper.ResolveMaxAlive(waveId) : 0;
        if (maxAlive <= 0) maxAlive = int.MaxValue;

        // 断点恢复：从存档游标继续（之前已发出的怪已由 RunSaveManager 重建在场上，不重复生成）
        mBlack.Manager.ConsumeRestoreSpawnCursor(out int resumeGroup, out int resumeIndex);
        if (resumeGroup > 0 || resumeIndex > 0)
            LogKit.Log($"[WaveManager] 断点恢复：第 {mBlack.Manager.CurrentWave} 波从组 {resumeGroup} 第 {resumeIndex} 只继续刷怪");

        if (config != null && config.spawnGroups != null)
        {
            for (int gi = 0; gi < config.spawnGroups.Count; gi++)
            {
                if (gi < resumeGroup) continue; // 之前的组已全部生成完毕

                var group = config.spawnGroups[gi];
                if (group == null) continue;

                // 优先用生成速度曲线；未配置时回退到组内 spawnInterval（向后兼容旧配置）
                float interval = pacingInterval > 0f ? pacingInterval : group.spawnInterval;
                var spawnWait = interval > 0f ? new WaitForSeconds(interval) : null;

                int from = gi == resumeGroup ? Mathf.Clamp(resumeIndex, 0, group.count) : 0;

                for (int i = from; i < group.count; i++)
                {
                    // 达到同屏上限 → 等待清场（超时兜底放行并告警，避免波次软锁）
                    if (EnemySimple.AllEnemies.Count >= maxAlive)
                    {
                        float waited = 0f;
                        while (EnemySimple.AllEnemies.Count >= maxAlive)
                        {
                            waited += Time.deltaTime;
                            if (waited >= 30f)
                            {
                                LogKit.Warning($"[WaveManager] 第 {mBlack.Manager.CurrentWave} 波同屏已达上限 {maxAlive} 且 30s 未缓解，放行继续刷怪");
                                break;
                            }
                            yield return null;
                        }
                    }

                    SpawnEnemy(group.enemyId);
                    mBlack.Manager.SetSpawnCursor(gi, i + 1, false); // 断点游标：本组已发 i+1 只
                    if (spawnWait != null) yield return spawnWait;
                    else yield return null;
                }

                mBlack.Manager.SetSpawnCursor(gi + 1, 0, false); // 本组发完 → 游标指向下一组
            }
        }
        mFinished = true;
        mBlack.Manager.SetSpawnCursor(config?.spawnGroups?.Count ?? 0, 0, true);
        LogKit.Log($"[WaveManager] 第 {mBlack.Manager.CurrentWave} 波刷怪完成"
                   + (pacingRate > 0f ? $"（生成速度 {pacingRate:F2} 只/秒，间隔 {pacingInterval:F3}s，同屏上限 {maxAlive}）" : ""));
    }

    /// <summary>生成单个敌人（对象池）</summary>
    private void SpawnEnemy(string enemyId)
    {
        var enemyConfig = GameDataMgr.Instance.GetEnemy(enemyId);
        if (enemyConfig == null)
        {
            LogKit.Error($"[WaveManager] 找不到敌人配置: {enemyId}");
            return;
        }

        // 生成位置：玩家周围随机环形
        Vector3 pos = GetSpawnPosition();
        var go = PoolFactory.Get(enemyConfig.prefabPath, pos, Quaternion.identity);
        var enemy = go.GetComponent<EnemySimple>();
        if (enemy != null)
        {
            enemy.Init(enemyId);
        }
    }

    /// <summary>生成位置：距玩家一定半径的随机点（半径从 WaveConfig 读取）</summary>
    private Vector3 GetSpawnPosition()
    {
        var config = mBlack.Manager.GetCurrentWaveConfig();
        float min = config?.spawnRadiusMin ?? 6f;
        float max = config?.spawnRadiusMax ?? 9f;

        var player = GameManager.Instance.Player;
        Vector3 center = player ? player.transform.position : Vector3.zero;
        float radius = Random.Range(min, max);
        float angle = Random.Range(0f, Mathf.PI * 2f);
        return center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    protected override void OnUpdate()
    {
        // 刷怪完成后进入清场
        if (mFinished)
        {
            mFSM.Change(WaveState.Clearing);
        }
    }

    protected override void OnExit()
    {
        if (mSpawnCoroutine != null)
        {
            mBlack.Manager.StopCoroutine(mSpawnCoroutine);
            mSpawnCoroutine = null;
        }
    }
}

/// <summary>清场状态：等待场上敌人全部清空</summary>
public class WaveClearingState : AbstractState<WaveState, WaveBlack>
{
    // 清场超时：超过该时长仍有敌人存活，说明有敌人被遗留远处够不着
    // （典型：静止蘑菇生成后永不动，玩家跑远后自动攻击够不到）→ 传送到玩家附近，避免软锁
    private const float StuckTimeout = 20f;

    private float mStuckTimer;

    public WaveClearingState(FSM<WaveState> fsm, WaveBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        mStuckTimer = 0f;
        LogKit.Log($"[WaveManager] 第 {mBlack.Manager.CurrentWave} 波清场中...（剩余敌人 {EnemySimple.AllEnemies.Count}）");
    }

    protected override void OnUpdate()
    {
        // 场上还有敌人：计时等待，超时则把遗留敌人传送到玩家附近
        if (EnemySimple.AllEnemies.Count > 0)
        {
            mStuckTimer += Time.deltaTime;
            if (mStuckTimer >= StuckTimeout)
            {
                TeleportStragglersToPlayer();
                mStuckTimer = 0f;
            }
            return;
        }

        // 场上没有敌人 → 本波结束
        // 广播波次清空事件
        EventKit.Type.Send(new GameEvents.WaveCleared { WaveId = mBlack.Manager.CurrentWave });
        LogKit.Log($"[WaveManager] 阶段 {mBlack.Manager.CurrentPhaseIndex + 1} 第 {mBlack.Manager.CurrentWave} 波清空！");

        // 阶段模式：阶段内还有波次 → 下一波；否则本阶段打完 → 有 BOSS 进 Boss，无 BOSS 进下一阶段
        if (mBlack.Manager.IsPhaseMode)
        {
            if (mBlack.Manager.HasWave(mBlack.Manager.CurrentWave + 1))
                mBlack.Manager.NextWave();
            else if (mBlack.Manager.ShouldTriggerBoss())
                mFSM.Change(WaveState.Boss);
            else
                mBlack.Manager.NextPhase();
            return;
        }

        var config = mBlack.Manager.GetCurrentWaveConfig();
        if (config != null && config.triggersBoss)
        {
            // 进入 Boss 波
            mFSM.Change(WaveState.Boss);
        }
        else
        {
            // 下一波
            mBlack.Manager.NextWave();
        }
    }

    /// <summary>把遗留的敌人传送到玩家附近（保持可击杀，不直接秒杀）</summary>
    private void TeleportStragglersToPlayer()
    {
        var player = GameManager.Instance.Player;
        if (player == null) return;

        var stragglers = new List<EnemySimple>(EnemySimple.AllEnemies);
        foreach (var enemy in stragglers)
        {
            if (enemy == null) continue;
            float angle = Random.Range(0f, Mathf.PI * 2f);
            Vector3 offset = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * 4f;
            enemy.transform.position = player.transform.position + offset;
        }
        LogKit.Log($"[WaveManager] 清场超时，传送 {stragglers.Count} 个遗留敌人到玩家附近");
    }
}

/// <summary>Boss 波状态（Step5：生成 BOSS 并等待其被击败）</summary>
public class WaveBossState : AbstractState<WaveState, WaveBlack>
{
    private bool mBossDefeated;
    private bool mClearedBroadcast; // 波次清空是否已广播（防结算暂停期间重复广播）
    private BossController mBoss;

    public WaveBossState(FSM<WaveState> fsm, WaveBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        mBossDefeated = false;
        mClearedBroadcast = false;

        // 断点恢复：已有 BOSS 直接接管（血量/破韧已由断点还原），不再重新生成
        var restored = mBlack.Manager.PendingRestoredBoss;
        if (restored != null)
        {
            mBlack.Manager.PendingRestoredBoss = null;
            mBoss = restored.GetComponent<BossController>();
            EventKit.Type.Register<GameEvents.BossDied>(OnBossDied);
            LogKit.Log($"[WaveManager] Boss 战恢复：{restored.BossId}");
            return;
        }

        string bossId = mBlack.Manager.GetCurrentBossId();

        if (string.IsNullOrEmpty(bossId))
        {
            LogKit.Error("[WaveManager] Boss 波缺少 bossId 配置（阶段模式看 phase.bossId，旧模式看 WaveConfig.bossId）");
            mBlack.Manager.ResumeAfterBoss();
            return;
        }

        // 生成 BOSS（场景上方中央）
        var player = GameManager.Instance.Player;
        Vector3 spawnPos = player ? player.transform.position + new Vector3(0f, 5f, 0f) : new Vector3(0f, 5f, 0f);
        mBoss = BossSpawner.Spawn(bossId, spawnPos, mBlack.Manager.CurrentPhaseBossHpScale);

        // 监听 BOSS 死亡事件
        EventKit.Type.Register<GameEvents.BossDied>(OnBossDied);
        LogKit.Log($"[WaveManager] Boss 战开始：{bossId}");
    }

    private void OnBossDied(GameEvents.BossDied evt)
    {
        mBossDefeated = true;
        // 立即注销，防止场景重开后残留旧监听器
        EventKit.Type.UnRegister<GameEvents.BossDied>(OnBossDied);
    }

    protected override void OnUpdate()
    {
        // BOSS 已击败：只广播一次波次清空；是否继续/停止由阶段结算流程（GameManager）控制
        // （避免结算暂停期间每帧重发 WaveCleared 导致统计重复计数）
        if (mBossDefeated && !mClearedBroadcast)
        {
            mClearedBroadcast = true;
            EventKit.Type.Send(new GameEvents.WaveCleared { WaveId = mBlack.Manager.CurrentWave });
            LogKit.Log($"[WaveManager] Boss 被击败，阶段 {mBlack.Manager.CurrentPhaseIndex + 1} / 第 {mBlack.Manager.CurrentWave} 波清空！");

            // 无下一阶段/下一波（最终 BOSS）→ 停止波次循环，等待结算结束回主菜单
            bool isLast = mBlack.Manager.IsPhaseMode
                ? !mBlack.Manager.HasNextPhase()
                : !mBlack.Manager.HasWave(mBlack.Manager.CurrentWave + 1);
            if (isLast)
            {
                mBlack.Manager.StopWaves();
            }
            // 还有后续（非最终 BOSS）→ 不自动推进，由阶段结算确认后 ResumeAfterBoss()
        }
    }

    protected override void OnExit()
    {
        EventKit.Type.UnRegister<GameEvents.BossDied>(OnBossDied);
    }
}
