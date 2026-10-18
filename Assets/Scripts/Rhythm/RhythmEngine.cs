using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 单个音符数据
/// </summary>
public class RhythmNote
{
    public int TrackIndex;          // 轨道索引
    public float ArrivalTime;       // 到达判定线的 unscaledTime（底边锚点触及判定线）
    public bool IsJudged;           // 是否已判定
    public string Judgment;         // "Perfect"/"Good"/"Miss"
    public GameObject Visual;       // 视觉对象（由 UI 层设置）
    public float DisappearTime;     // 完全越过判定线的时刻（Miss 后滑出此时刻，引擎据此移除/UI 隐藏）
    public float MissTime;          // Miss 判定时刻（松手漏接的长按音符从判定线钉住位置整体下滑用）

    // ===== 长按音符（阶段 holdBeats > 0 时启用） =====
    public bool IsHold;             // 是否为长按音符
    public float HoldDuration;      // 长按时长（秒）
    public bool IsHolding;          // head 已命中并按住中
    public string HeadJudgment;     // head 命中判定（Perfect/Good），撑到尾端按此计分
    public float HoldEndTime => ArrivalTime + HoldDuration;   // 长按尾端时刻
}

/// <summary>
/// 烹饪音游核心引擎（Step5，MonoSingleton）
/// 负责：音符生成、四阶段管理、判定逻辑、品质统计
/// 使用 Time.unscaledTime（音游期间 Time.timeScale=0）
/// </summary>
public class RhythmEngine : MonoSingleton<RhythmEngine>
{
    // ===== 配置 =====
    private RecipeConfig mRecipe;
    private BossConfig mBossConfig;
    private RhythmDifficulty mDifficulty;   // 当前难度档（按关卡递进）
    private float mBeatWindow;       // Perfect 判定窗口（秒，已按难度缩放）
    private float mFallDuration = 2f; // 音符下落时间（秒，已按难度缩放）
    private float mBaseBpm = 120f;    // 基准 BPM（菜谱优先，回退 BOSS）
    private float mCurrentBpm;        // 当前阶段实际 BPM（含难度与阶段倍率）
    private float mStageNoteInterval = 0.5f; // 当前阶段音符间隔（秒，由 BPM 派生）
    private float mStageHoldDuration;        // 当前阶段长按时长（秒，0=点击音符）
    private float mNoteFullCrossDuration = 0.06f; // 音符从触及判定线到完全越过所需时长（UI 层按实际几何上报）

    // ===== 判定宽容（2026-10-17 修"非操作性 Miss"） =====
    /// <summary>
    /// 迟到宽容窗口倍率：Miss 不在 ArrivalTime 立刻触发，而是再等 beatWindow × 本倍率。
    /// 旧行为取 0 —— 越过 ArrivalTime 一帧就 Miss，等于判定窗口只有 [-2W, +1帧]，
    /// 玩家按视觉压线按（本身还有半格音符的视觉偏差）必然吃到非操作性 Miss。
    /// 取 1.0 → 迟到宽容 100ms（easy 档 140ms），约 6 倍于残留的 1~2 帧延迟，足够兜住；
    /// 取 2.0 可与 Good 窗口完全对称 [-2W, +2W]，但 Miss 音符会一路滑出屏幕底部才消失，观感更差。
    /// </summary>
    private const float kLateGraceScale = 1f;

    /// <summary>
    /// 按键时间戳补偿（帧数）：按键发生在两帧之间，却只能在下一帧被读到、
    /// 并被记成"该帧起始时刻"，平均晚记半帧。减去半帧做无偏补偿（低帧率收益更明显，0=关闭）。
    /// 只作用于"按下"，不作用于"松开"（松开用原始时刻更宽容，避免长按被误判提前松手）。
    /// </summary>
    private const float kInputTimestampCompensation = 0.5f;

    private float mLateGrace;                // 当前迟到宽容窗口（秒）

    /// <summary>输入判定用的"现在"：补偿按键时间戳的半帧量化偏差</summary>
    private static float InputNow() => Time.unscaledTime - kInputTimestampCompensation * Time.unscaledDeltaTime;

    // ===== 运行状态 =====
    private bool mIsRunning;
    private int mCurrentStageIndex;
    private float mStageStartTime;
    private int mNotesSpawnedInStage;
    private float mSpawnTimer;

    // ===== 音符 =====
    private List<RhythmNote> mActiveNotes = new();

    /// <summary>生成音符时统计各轨道"被长按占用到何时"的复用缓冲（避免每次生成临时分配）</summary>
    private float[] mTrackBusyUntil = new float[8];

    /// <summary>谱面本身不可解（长按占满所有轨道）只告警一次</summary>
    private bool mWarnedHoldOverlap;

    // ===== 统计 =====
    private int mPerfectCount;
    private int mGoodCount;
    private int mMissCount;
    private int mCombo;
    private int mMaxCombo;
    private int mTotalNotes;

    /// <summary>当前阶段配置</summary>
    public RecipeStage CurrentStage => mRecipe?.stages != null && mCurrentStageIndex < mRecipe.stages.Count
        ? mRecipe.stages[mCurrentStageIndex] : null;

    /// <summary>当前活动音符（供 UI 层渲染）</summary>
    public List<RhythmNote> ActiveNotes => mActiveNotes;

    /// <summary>下落时间（秒），UI 层据此计算音符位置</summary>
    public float FallDuration => mFallDuration;

    /// <summary>是否运行中</summary>
    public bool IsRunning => mIsRunning;

    /// <summary>当前阶段索引</summary>
    public int CurrentStageIndex => mCurrentStageIndex;

    /// <summary>当前 Combo</summary>
    public int Combo => mCombo;

    /// <summary>统计信息</summary>
    public int PerfectCount => mPerfectCount;
    public int GoodCount => mGoodCount;
    public int MissCount => mMissCount;

    /// <summary>当前难度档（未配置时为 null）</summary>
    public RhythmDifficulty Difficulty => mDifficulty;

    /// <summary>当前阶段实际 BPM（含难度与阶段倍率，供 UI 展示）</summary>
    public float CurrentBpm => mCurrentBpm;

    /// <summary>当前阶段音符间隔（秒）</summary>
    public float StageNoteInterval => mStageNoteInterval;

    /// <summary>当前迟到宽容窗口（秒）：越过 ArrivalTime 后仍可命中的时长，0=不宽容</summary>
    public float LateGrace => mLateGrace;

    /// <summary>当前 Perfect 判定窗口（秒，单边）：|now-ArrivalTime| ≤ 本值 = Perfect</summary>
    public float BeatWindow => mBeatWindow;

    /// <summary>UI 层上报音符完全越过判定线所需时长（按实际轨道几何与透视映射反解，供 Miss 滑出移除时机用）</summary>
    public void SetNoteFullCrossDuration(float seconds)
    {
        if (seconds > 0.01f) mNoteFullCrossDuration = seconds;
    }

    /// <summary>阶段总数（供 UI 显示阶段 N/M）</summary>
    public int TotalStageCount => mRecipe?.stages?.Count ?? 0;

    protected override void Awake()
    {
        base.Awake();
        mBeatWindow = 0.1f; // 默认 100ms
    }

    /// <summary>启动烹饪音游</summary>
    public void StartCooking(string recipeId)
    {
        mRecipe = GameDataMgr.Instance.GetRecipe(recipeId);
        if (mRecipe == null)
        {
            LogKit.Error($"[RhythmEngine] 找不到菜谱: {recipeId}");
            return;
        }

        // 难度档（按当前关卡递进；取不到则用全局默认档）
        mDifficulty = ResolveDifficulty();

        // 判定窗口：BOSS 配置为基准，再按难度缩放
        var bossId = mRecipe.correspondingBossId;
        mBossConfig = GameDataMgr.Instance.GetBoss(bossId);
        float baseBeatWindow = mBossConfig != null ? mBossConfig.beatWindowMs / 1000f : 0.1f;
        mBeatWindow = baseBeatWindow * (mDifficulty != null ? mDifficulty.beatWindowScale : 1f);
        // 迟到宽容：越过 ArrivalTime 后仍可命中的额外时长（0 会退化成"晚按一帧即 Miss"）
        mLateGrace = mBeatWindow * kLateGraceScale;

        // 音符下落时间：菜谱配置为基准，再按难度缩放
        float baseFall = mRecipe.fallDuration > 0.1f ? mRecipe.fallDuration : 2f;
        mFallDuration = baseFall * (mDifficulty != null ? mDifficulty.fallDurationScale : 1f);

        // 基准 BPM：菜谱优先 → BOSS → 120
        if (mRecipe.bpm > 1f) mBaseBpm = mRecipe.bpm;
        else if (mBossConfig != null && mBossConfig.bpm > 1f) mBaseBpm = mBossConfig.bpm;
        else mBaseBpm = 120f;

        // 重置统计
        mPerfectCount = 0;
        mGoodCount = 0;
        mMissCount = 0;
        mCombo = 0;
        mMaxCombo = 0;
        mTotalNotes = 0;
        mActiveNotes.Clear();
        mWarnedHoldOverlap = false;
        mCurrentStageIndex = 0;
        mIsRunning = true;

        StartStage(0);
        LogKit.Log($"[RhythmEngine] 烹饪音游开始：{mRecipe.recipeName}，阶段数={mRecipe.stages.Count}，" +
                   $"难度={mDifficulty?.name ?? "默认"}，基准BPM={mBaseBpm:F0}，判定窗口=±{mBeatWindow * 2000f:F0}ms，" +
                   $"迟到宽容=+{mLateGrace * 1000f:F0}ms");
    }

    /// <summary>按当前副本解析音游难度档（副本未配置则用全局默认档）</summary>
    private RhythmDifficulty ResolveDifficulty()
    {
        var wrapper = GameDataMgr.Instance.Rhythm;
        if (wrapper?.Rhythm == null) return null;

        string dungeonId = WaveManager.HasInstance ? WaveManager.Instance.CurrentDungeonId : null;
        var dungeon = GameDataMgr.Instance.GetDungeon(dungeonId) ?? GameDataMgr.Instance.CurrentDungeon;
        string diffId = dungeon != null && !string.IsNullOrEmpty(dungeon.rhythmDifficultyId)
            ? dungeon.rhythmDifficultyId
            : wrapper.Rhythm.defaultDifficultyId;

        var diff = wrapper.GetDifficulty(diffId);
        if (diff == null) diff = wrapper.GetDifficulty(wrapper.Rhythm.defaultDifficultyId);
        return diff;
    }

    /// <summary>
    /// 当前阶段音符间隔（秒）= 60 / 实际BPM × 每音符拍数
    /// 实际BPM = 基准BPM × 难度倍率 × 阶段倍率（阶段变速）
    /// </summary>
    private float ComputeStageInterval(RecipeStage stage)
    {
        if (stage == null) return 0.5f;

        float bpm = mBaseBpm;
        if (mDifficulty != null) bpm *= Mathf.Max(0.1f, mDifficulty.bpmScale);
        bpm *= Mathf.Max(0.1f, stage.bpmScale);
        mCurrentBpm = Mathf.Max(1f, bpm);

        float beats = stage.noteConfig != null && stage.noteConfig.beatsPerNote > 0f
            ? stage.noteConfig.beatsPerNote : 1f;
        return 60f / mCurrentBpm * beats;
    }

    /// <summary>开始某个阶段</summary>
    private void StartStage(int index)
    {
        if (mRecipe == null || index >= mRecipe.stages.Count)
        {
            FinishCooking();
            return;
        }

        mCurrentStageIndex = index;
        mStageStartTime = Time.unscaledTime;
        mNotesSpawnedInStage = 0;
        mSpawnTimer = 0f;

        var stage = mRecipe.stages[index];
        mStageNoteInterval = ComputeStageInterval(stage);

        // 长按时长 = holdBeats × 60 / 实际BPM（holdBeats <= 0 = 点击音符）
        float holdBeats = stage.noteConfig != null ? stage.noteConfig.holdBeats : 0f;
        mStageHoldDuration = holdBeats > 0f ? holdBeats * 60f / mCurrentBpm : 0f;

        LogKit.Log($"[RhythmEngine] 阶段 {index} 开始：{stage.stage}，音符数={stage.noteCount}，" +
                   $"轨道数={stage.noteConfig.trackCount}，BPM={mCurrentBpm:F0}，音符间隔={mStageNoteInterval:F3}s，" +
                   $"长按={mStageHoldDuration:F2}s");

        // 长按 ≥ 音符间隔 = 相邻长按在时间上必然交叠（同轨道更是按不过来），谱面本身有问题，先提示出来
        if (mStageHoldDuration > 0f && mStageHoldDuration >= mStageNoteInterval)
        {
            LogKit.Warning($"[RhythmEngine] 阶段 {index}（{stage.stage}）长按 {mStageHoldDuration:F3}s ≥ 音符间隔 {mStageNoteInterval:F3}s，" +
                           $"相邻长按会交叠；建议 holdBeats < beatsPerNote（当前 holdBeats={(stage.noteConfig != null ? stage.noteConfig.holdBeats : 0f)}）");
        }

        EventKit.Type.Send(new GameEvents.CookingStageStarted
        {
            Stage = stage.stage,
            StageIndex = index
        });
    }

    private void Update()
    {
        if (!mIsRunning) return;

        // 键盘输入：D=0, F=1, J=2, K=3（长按音符需监听松开）
        if (Input.GetKeyDown(KeyCode.D)) OnKeyPress(0);
        if (Input.GetKeyDown(KeyCode.F)) OnKeyPress(1);
        if (Input.GetKeyDown(KeyCode.J)) OnKeyPress(2);
        if (Input.GetKeyDown(KeyCode.K)) OnKeyPress(3);
        if (Input.GetKeyUp(KeyCode.D)) OnKeyRelease(0);
        if (Input.GetKeyUp(KeyCode.F)) OnKeyRelease(1);
        if (Input.GetKeyUp(KeyCode.J)) OnKeyRelease(2);
        if (Input.GetKeyUp(KeyCode.K)) OnKeyRelease(3);

        float now = Time.unscaledTime;
        var stage = CurrentStage;
        if (stage == null) { FinishCooking(); return; }

        // 1. 生成音符
        mSpawnTimer += Time.unscaledDeltaTime;
        float interval = mStageNoteInterval;
        while (mSpawnTimer >= interval && mNotesSpawnedInStage < stage.noteCount)
        {
            mSpawnTimer -= interval;
            SpawnNote(stage, now);
        }

        // 2. 判定扫描：长按撑满计分 / 越过判定线且迟到宽容窗口用尽即 Miss
        // 轨道按键实时状态（GetKey 而非 GetKeyDown）：
        // 长按接住不能只靠 GetKeyDown 一次性事件——前一长按撑满时按键未松，
        // 或玩家提前按下等音符来，GetKeyDown 都不会再触发，音符会漏记按住态
        bool dHeld = Input.GetKey(KeyCode.D);
        bool fHeld = Input.GetKey(KeyCode.F);
        bool jHeld = Input.GetKey(KeyCode.J);
        bool kHeld = Input.GetKey(KeyCode.K);

        for (int i = mActiveNotes.Count - 1; i >= 0; i--)
        {
            var note = mActiveNotes[i];
            if (note.IsJudged) continue;

            if (note.IsHold)
            {
                // 长按中：撑到尾端按 head 判定计分（每音符仅此一次计分）
                if (note.IsHolding)
                {
                    if (now >= note.HoldEndTime) JudgeNote(note, note.HeadJudgment);
                }
                else
                {
                    // 按住兜底：head 在窗口内且轨道按键正处于按下状态 → 自动接住
                    //（覆盖"前一长按撑满后按键未松直接接续"与"提前按下等音符来"）
                    bool held = note.TrackIndex switch
                    {
                        0 => dHeld,
                        1 => fHeld,
                        2 => jHeld,
                        _ => kHeld
                    };
                    if (held)
                    {
                        float headDiff = Mathf.Abs(note.ArrivalTime - InputNow());
                        if (headDiff <= mBeatWindow) BeginHold(note, "Perfect");
                        else if (headDiff <= mBeatWindow * 2f) BeginHold(note, "Good");
                    }

                    // 底部锚点（head）越过判定线且迟到宽容窗口用尽仍未接住 → Miss
                    //（宽容期内仍可按下接住，音符继续滑行至完全越过后消失）
                    if (!note.IsHolding && now > note.ArrivalTime + mLateGrace)
                    {
                        JudgeNote(note, "Miss");
                    }
                }
            }
            else if (now > note.ArrivalTime + mLateGrace)
            {
                // 点击音符：越过判定线且迟到宽容窗口用尽仍未判定即 Miss
                JudgeNote(note, "Miss");
            }
        }

        // 3. 阶段结束判定：所有音符已生成、全部判定且滑出（Miss 音符完全越过判定线）
        bool allSpawned = mNotesSpawnedInStage >= stage.noteCount;
        bool allGone = true;
        foreach (var n in mActiveNotes)
        {
            if (!n.IsJudged || now < n.DisappearTime) { allGone = false; break; }
        }

        if (allSpawned && allGone)
        {
            mActiveNotes.Clear();
            if (mCurrentStageIndex + 1 < mRecipe.stages.Count)
            {
                StartStage(mCurrentStageIndex + 1);
            }
            else
            {
                FinishCooking();
            }
        }
    }

    /// <summary>生成一个音符</summary>
    private void SpawnNote(RecipeStage stage, float now)
    {
        int trackCount = Mathf.Max(1, stage.noteConfig.trackCount);
        float arrival = now + mFallDuration;
        var note = new RhythmNote
        {
            TrackIndex = PickTrack(trackCount, arrival),
            ArrivalTime = arrival,
            IsJudged = false,
            IsHold = mStageHoldDuration > 0f,
            HoldDuration = mStageHoldDuration,
            // 完全越过判定线时刻：底边触及判定线后滑出自身高度（长按音符尾端还需滑出长按段）
            // 迟到宽容窗口内音符仍在滑行，须一并计入，否则 Miss 判定前音符就先消失了
            DisappearTime = now + mFallDuration + mLateGrace + mNoteFullCrossDuration
                            + (mStageHoldDuration > 0f ? mStageHoldDuration : 0f)
        };
        mActiveNotes.Add(note);
        mNotesSpawnedInStage++;
        mTotalNotes++;
    }

    /// <summary>
    /// 为即将生成的音符挑一条「按键不冲突」的轨道。
    /// 长按在 [ArrivalTime, HoldEndTime] 内独占该轨道的按键：同轨道若已有长按未结束，
    /// 新音符即使压在判定线上也按不下去（键还在被占用，且 GetKeyDown 不会再触发），只能吃到 Miss；
    /// 两条长条视觉上也会首尾重合、分不清哪段还在按。故候选轨道 = 没有未结束长按（且留出收键→再按间隙）的轨道。
    /// </summary>
    private int PickTrack(int trackCount, float arrivalTime)
    {
        if (trackCount <= 1) return 0;

        if (mTrackBusyUntil == null || mTrackBusyUntil.Length < trackCount)
        {
            mTrackBusyUntil = new float[trackCount];
        }
        else
        {
            System.Array.Clear(mTrackBusyUntil, 0, mTrackBusyUntil.Length);
        }

        for (int i = 0; i < mActiveNotes.Count; i++)
        {
            var n = mActiveNotes[i];
            if (!n.IsHold || n.IsJudged) continue;
            if (n.TrackIndex < 0 || n.TrackIndex >= trackCount) continue;

            // 未判定的长按一律按"会一直占到 HoldEndTime"计（玩家随时可能接住），
            // 保守估计只会让新音符换轨，不会造成冲突
            if (n.HoldEndTime > mTrackBusyUntil[n.TrackIndex]) mTrackBusyUntil[n.TrackIndex] = n.HoldEndTime;
        }

        // 收键→再按需要一点间隙，否则新音符的判定窗口会与上一长按的结束时刻糊在一起
        float guard = Mathf.Max(mBeatWindow, 0.02f);

        int freeCount = 0;
        for (int i = 0; i < trackCount; i++)
        {
            if (mTrackBusyUntil[i] <= 0f || mTrackBusyUntil[i] + guard <= arrivalTime) freeCount++;
        }

        if (freeCount > 0)
        {
            int pick = Random.Range(0, freeCount);
            for (int i = 0; i < trackCount; i++)
            {
                if (mTrackBusyUntil[i] > 0f && mTrackBusyUntil[i] + guard > arrivalTime) continue;
                if (pick == 0) return i;
                pick--;
            }
        }

        // 兜底：所有轨道都被长按占满（只有长按 ≥ 2 倍音符间隔的谱面才会出现），取最早释放的那条
        int earliest = 0;
        for (int i = 1; i < trackCount; i++)
        {
            if (mTrackBusyUntil[i] < mTrackBusyUntil[earliest]) earliest = i;
        }
        if (!mWarnedHoldOverlap)
        {
            mWarnedHoldOverlap = true;
            LogKit.Warning($"[RhythmEngine] 长按占满全部 {trackCount} 条轨道，音符已无法避开重叠：" +
                           $"长按 {mStageHoldDuration:F2}s / 音符间隔 {mStageNoteInterval:F2}s（holdBeats 应小于 beatsPerNote）");
        }
        return earliest;
    }

    /// <summary>玩家按键（轨道索引）</summary>
    public void OnKeyPress(int trackIndex)
    {
        if (!mIsRunning) return;

        // 轨道数限制：当前阶段只有 N 条轨道时，超出范围的按键无效
        var stage = CurrentStage;
        if (stage == null || trackIndex >= stage.noteConfig.trackCount) return;

        float now = InputNow();
        // 找到该轨道上最接近判定线且未判定、未在长按中的音符
        RhythmNote closest = null;
        float minDiff = float.MaxValue;

        foreach (var note in mActiveNotes)
        {
            if (note.IsJudged || note.IsHolding || note.TrackIndex != trackIndex) continue;
            float diff = Mathf.Abs(note.ArrivalTime - now);
            if (diff < minDiff)
            {
                minDiff = diff;
                closest = note;
            }
        }

        if (closest == null) return;

        // 长按音符：head 命中后进入按住态（不计分，撑到尾端才按 head 判定计分）
        if (closest.IsHold)
        {
            if (minDiff <= mBeatWindow) BeginHold(closest, "Perfect");
            else if (minDiff <= mBeatWindow * 2f) BeginHold(closest, "Good");
            // else: 太早按，不判定（等音符过来）
            return;
        }

        // 点击音符判定
        if (minDiff <= mBeatWindow)
            JudgeNote(closest, "Perfect");
        else if (minDiff <= mBeatWindow * 2f)
            JudgeNote(closest, "Good");
        // else: 太早按，不判定（等音符过来）
    }

    /// <summary>长按音符 head 命中：记录判定进入按住态（此刻不计分，撑到尾端才计）</summary>
    private void BeginHold(RhythmNote note, string headJudgment)
    {
        note.IsHolding = true;
        note.HeadJudgment = headJudgment;
    }

    /// <summary>玩家松开按键（轨道索引）：长按中提前松手 → Miss</summary>
    public void OnKeyRelease(int trackIndex)
    {
        if (!mIsRunning) return;

        // 松手沿用原始帧时刻（不做半帧补偿）：松手越早越容易判 Miss，
        // 用原始时刻等于"往晚里算"，给玩家多一点宽容，不为难长按
        float now = Time.unscaledTime;
        foreach (var note in mActiveNotes)
        {
            // 只处理该轨道上正在按住且未计分的音符（撑满后由 Update 计分，此处只管提前松手）
            if (note.TrackIndex != trackIndex || !note.IsHolding || note.IsJudged) continue;
            if (now < note.HoldEndTime) JudgeNote(note, "Miss");
        }
    }

    /// <summary>判定一个音符</summary>
    private void JudgeNote(RhythmNote note, string judgment)
    {
        note.IsJudged = true;
        note.Judgment = judgment;

        if (judgment == "Miss")
        {
            // Miss 时刻：仅"已接住又提前松手"的长按音符需要它——从判定线钉住位置整体下滑
            //（从未接住的长按让它在 UI 层沿轨道自然滑行，不要从线上方跳回判定线）
            if (note.IsHolding) note.MissTime = Time.unscaledTime;
            note.IsHolding = false;
        }
        else
        {
            // 命中即时消失，不拖慢阶段切换
            note.DisappearTime = Time.unscaledTime;
        }

        switch (judgment)
        {
            case "Perfect":
                mPerfectCount++;
                mCombo++;
                break;
            case "Good":
                mGoodCount++;
                mCombo++;
                break;
            case "Miss":
                mMissCount++;
                mCombo = 0;
                break;
        }
        if (mCombo > mMaxCombo) mMaxCombo = mCombo;

        EventKit.Type.Send(new GameEvents.CookingNoteJudged
        {
            Judgment = judgment,
            Combo = mCombo,
            TrackIndex = note.TrackIndex
        });
    }

    /// <summary>结束音游，计算品质并发送事件</summary>
    private void FinishCooking()
    {
        if (!mIsRunning) return;
        mIsRunning = false;
        mActiveNotes.Clear();

        string grade = CalculateGrade();
        RecipeQuality quality = CalculateQuality(grade);
        LogKit.Log($"[RhythmEngine] 烹饪结束！Perfect={mPerfectCount}, Good={mGoodCount}, Miss={mMissCount}, 最高连招={mMaxCombo}, 总评={grade}, 品质={quality}");

        EventKit.Type.Send(new GameEvents.CookingFinished { Quality = quality, Grade = grade, MaxCombo = mMaxCombo });
    }

    /// <summary>
    /// 加权总评：score = Perfect率×0.6 + 最高连击/总音符×0.4 → S/A/B/C
    /// 阈值取 RecipeConfigWrapper 全局配置，缺省 0.85/0.7/0.5；0 音符返回 null（=失败）
    /// </summary>
    private string CalculateGrade()
    {
        if (mTotalNotes == 0) return null;

        float perfectRatio = (float)mPerfectCount / mTotalNotes;
        float comboRatio = (float)mMaxCombo / mTotalNotes;
        float score = perfectRatio * 0.6f + comboRatio * 0.4f;

        var wrapper = GameDataMgr.Instance != null ? GameDataMgr.Instance.Recipes : null;
        float sLine = wrapper != null && wrapper.bossGradeSRatio > 0f ? wrapper.bossGradeSRatio : 0.85f;
        float aLine = wrapper != null && wrapper.bossGradeARatio > 0f ? wrapper.bossGradeARatio : 0.7f;
        float bLine = wrapper != null && wrapper.bossGradeBRatio > 0f ? wrapper.bossGradeBRatio : 0.5f;

        if (score >= sLine) return "S";
        if (score >= aLine) return "A";
        if (score >= bLine) return "B";
        return "C";
    }

    /// <summary>品质档位 → 生死判定（S/A=传说、B/C=普通、null=失败）</summary>
    private RecipeQuality CalculateQuality(string grade)
    {
        if (string.IsNullOrEmpty(grade)) return RecipeQuality.Failed;
        return grade == "S" || grade == "A" ? RecipeQuality.Legendary : RecipeQuality.Common;
    }

    /// <summary>外部强制结束（如玩家取消）</summary>
    public void ForceStop()
    {
        mIsRunning = false;
        mActiveNotes.Clear();
    }
}
