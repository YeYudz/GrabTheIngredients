using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 画中画炒菜判定引擎：纯点击颠勺的核心循环，世界不暂停（边打边炒）。
/// 计时统一用 Time.time，使其在 BOSS 音游 timeScale = 0 时自然停摆。
/// 命中率四档映射稀有度与局内增益；炒糊或「不炒」则施加手忙脚乱 debuff 且不扣食材。
/// 只广播 FieldCooking* 事件，展示由 FieldCookingPanel 订阅。
/// </summary>
public class FieldCookingEngine : MonoSingleton<FieldCookingEngine>
{
    /// <summary>颠勺判定专用按键（窗口内按下=成功颠勺；窗口外不响应）</summary>
    private const KeyCode k_flipKey = KeyCode.Space;

    // ===== 运行状态 =====
    public bool IsRunning { get; private set; }
    public CookingRecipeConfig Recipe { get; private set; }

    public int Combo { get; private set; }          // 当前连击（漏点即断）
    public int HintIndex { get; private set; }      // 已出现的提示数（1 起）
    public int HintTotal { get; private set; }      // 提示总数（步骤模式=ΣhintCount，精确）
    public bool HintActive { get; private set; }    // 当前是否有提示在窗口期内
    public float HintEndTime { get; private set; }  // 当前提示窗口截止时刻（Time.time）
    public float RunRemainRatio { get; private set; } // 剩余时长比例 0~1（UI 倒计时条）

    // 步骤模式状态（pipSteps 配置非空时启用：起锅-烧油-下菜-翻炒-倒水-调味）
    public int StepIndex { get; private set; } = -1;  // 当前步骤（0 起，-1=未开始）
    public int StepCount { get; private set; }        // 步骤总数（旧模式=0）

    private List<PipStepConfig> mSteps;     // 本次使用的步骤序列（菜品覆盖或全局默认）
    private bool mStepMode;                 // 是否启用步骤模式
    private float mStepStartAt;             // 当前步骤开始时刻
    private float mStepEndAt;               // 当前步骤截止时刻
    private int mStepHintShown;             // 当前步骤内已出现的提示数
    private float mStartedAt;       // 本次炒菜开始时刻
    private float mEndAt;           // 总时长截止时刻
    private float mNextHintAt;      // 下一个提示出现时刻（旧模式）
    private int mSuccessCount;      // 成功颠勺数
    private int mHintShownCount;    // 实际出现的提示数（结算命中率分母）

    // ===== 手忙脚乱 debuff（覆盖式 · 单层）=====
    // 语义：同时最多只有一层惩罚。重复失败只"刷新时长"（取较晚的结束时刻），惩罚更强时补差额。
    // 历史 bug：原先每次失败各扣一次攻速、各自起一个 30s 恢复协程；不操作时约 7s 就作废一次，
    //          惩罚持续 30s → 稳态叠加 4 层 ≈ -80% 攻速，表现为"没有任何操作时近战转速越玩越慢"。
    private float mFailDebuffValue;     // 当前已扣的攻速惩罚（0 = 未生效）
    private float mFailDebuffEndAt;     // 惩罚结束时刻（Time.time；timeScale=0 时随之停摆）
    private Coroutine mFailDebuffCo;    // 唯一的恢复协程（避免每次失败各起一个）

    // ===== 读取配置（JSON 优先，缺失时代码默认值兜底） =====
    private CookingRecipeConfigWrapper Cfg => GameDataMgr.Instance?.CookingRecipes;

    /// <summary>启动一次画中画炒菜（由 FieldCookingDirector 触发时调用）</summary>
    public void StartCooking(CookingRecipeConfig recipe)
    {
        if (recipe == null || IsRunning) return;

        Recipe = recipe;
        Combo = 0;
        HintIndex = 0;
        HintActive = false;
        HintEndTime = 0f;
        RunRemainRatio = 1f;
        mSuccessCount = 0;
        mHintShownCount = 0;

        // 解析步骤序列：菜品 pipStepIds 覆盖 → 全局默认 pipSteps → 均无效走旧模式
        mSteps = ResolveSteps(recipe);
        mStepMode = mSteps != null && mSteps.Count > 0;
        StepIndex = -1;
        StepCount = mStepMode ? mSteps.Count : 0;

        float now = Time.time;
        mStartedAt = now;
        if (mStepMode)
        {
            // 步骤模式：总时长=Σduration，总提示=ΣhintCount（精确）
            float total = 0f;
            int hints = 0;
            foreach (var s in mSteps) { total += Mathf.Max(0.1f, s.duration); hints += Mathf.Max(0, s.hintCount); }
            mEndAt = now + total;
            HintTotal = Mathf.Max(1, hints);
            BeginStep(0, now); // 立即进入第一步（演出开始）
        }
        else
        {
            // 旧模式：随机间隔调度
            mEndAt = now + (Cfg?.pipDuration ?? 18f);
            HintTotal = EstimateHintTotal();
            mNextHintAt = now + NextHintInterval();
        }
        IsRunning = true;

        // "灶台就绪"提示音由 AudioManager 订阅 FieldCookingTriggered 播放

        EventKit.Type.Send(new GameEvents.FieldCookingTriggered
        {
            RecipeId = recipe.recipeId,
            RecipeName = recipe.recipeName,
            HintCount = HintTotal
        });
        LogKit.Log($"[FieldCooking] 画中画炒菜开始：{recipe.recipeName}" +
                   (mStepMode ? $"（步骤模式 {StepCount} 步，共 {HintTotal} 次颠勺提示）" : $"（预估 {HintTotal} 次颠勺提示）"));
    }

    /// <summary>解析本次炒菜的步骤序列（菜品 pipStepIds 覆盖优先，无效引用自动跳过）</summary>
    private List<PipStepConfig> ResolveSteps(CookingRecipeConfig recipe)
    {
        var cfg = Cfg;
        if (cfg == null) return null;

        // 菜品级覆盖
        if (recipe.pipStepIds != null && recipe.pipStepIds.Count > 0)
        {
            var custom = new List<PipStepConfig>();
            foreach (var id in recipe.pipStepIds)
            {
                var step = cfg.GetStepById(id);
                if (step != null) custom.Add(step);
            }
            if (custom.Count > 0) return custom;
        }

        // 全局默认流程
        return cfg.pipSteps != null && cfg.pipSteps.Count > 0 ? cfg.pipSteps : null;
    }

    /// <summary>进入指定步骤：广播步骤切换（UI 播放入场演出）并重置步骤内提示调度</summary>
    private void BeginStep(int index, float now)
    {
        StepIndex = index;
        var step = mSteps[index];
        mStepStartAt = now;
        mStepEndAt = now + Mathf.Max(0.1f, step.duration);
        mStepHintShown = 0;

        EventKit.Type.Send(new GameEvents.FieldCookingStepChanged
        {
            StepId = step.id,
            StepName = step.name,
            Icon = step.icon,
            Anim = step.anim,
            FallbackColor = step.fallbackColor,
            FxIcon = step.fxIcon,
            StepIndex = index,
            StepCount = StepCount
        });
    }

    /// <summary>颠勺判定：窗口期内点击小窗/按专用按键 → 成功；窗口外不响应</summary>
    public void OnFlipInput()
    {
        if (!IsRunning || !HintActive) return;

        HintActive = false;
        mSuccessCount++;
        Combo++;

        // 成功后安排下一次提示（仅旧模式；步骤模式按均分时刻自然继续）
        if (!mStepMode) mNextHintAt = Time.time + NextHintInterval();

        EventKit.Type.Send(new GameEvents.FieldCookingHintJudged { Judgment = "Hit", Combo = Combo });
    }

    private void Update()
    {
        // 专用按键判定（BOSS 音游 timeScale=0 时时间停摆，按键也屏蔽避免误判）
        bool bossMinigame = RhythmEngine.HasInstance && RhythmEngine.Instance.IsRunning;
        if (Input.GetKeyDown(k_flipKey) && !bossMinigame) OnFlipInput();

        if (!IsRunning) return;
        if (bossMinigame) return; // 音游期间冻结战场节奏（Time.deltaTime 已为 0，双保险）

        // ===== 1. 总时长到：当前提示若仍开着按漏点计，随后结算 =====
        if (Time.time >= mEndAt)
        {
            if (HintActive) JudgeMiss();
            FinishCooking(isAbandoned: false);
            return;
        }

        // ===== 2. 步骤推进（步骤模式：按时长推进，总时长到自然结算） =====
        if (mStepMode)
        {
            // 窗口结束仍未点击 → 漏点（combo 断）
            if (HintActive && Time.time >= HintEndTime)
            {
                JudgeMiss();
            }
            else if (Time.time >= mStepEndAt && StepIndex < StepCount - 1)
            {
                BeginStep(StepIndex + 1, Time.time);
            }
            // 步骤内提示调度：hintCount 均分步骤时长 + 轻微随机抖动
            else if (!HintActive && StepIndex >= 0)
            {
                var step = mSteps[StepIndex];
                int hints = Mathf.Max(0, step.hintCount);
                if (mStepHintShown < hints)
                {
                    float slotAt = mStepStartAt + Mathf.Max(0.1f, step.duration) * (mStepHintShown + 0.5f) / hints;
                    if (Time.time >= slotAt) ShowHint();
                }
            }
        }
        // ===== 3. 旧模式提示调度 =====
        else
        {
            if (HintActive)
            {
                // 窗口结束仍未点击 → 漏点（combo 断）
                if (Time.time >= HintEndTime) JudgeMiss();
            }
            else if (Time.time >= mNextHintAt)
            {
                ShowHint();
            }
        }

        // 剩余时长比例（UI 倒计时条）
        float total = Mathf.Max(0.1f, mEndAt - mStartedAt);
        RunRemainRatio = Mathf.Clamp01((mEndAt - Time.time) / total);
    }

    /// <summary>出现一次颠勺提示（UI 高亮食材/锅铲+箭头）</summary>
    private void ShowHint()
    {
        HintIndex++;
        mHintShownCount++;
        if (mStepMode) mStepHintShown++; // 步骤内计数推进（决定下一个均分时刻）
        HintActive = true;
        HintEndTime = Time.time + (Cfg?.pipHintWindow ?? 0.8f);

        EventKit.Type.Send(new GameEvents.FieldCookingHintShown
        {
            HintIndex = HintIndex,
            HintTotal = HintTotal,
            WindowSeconds = Cfg?.pipHintWindow ?? 0.8f
        });
    }

    /// <summary>漏点判定：combo 断 + 广播；第一次提示就漏掉 = 完全没理睬 = "不炒"作废</summary>
    private void JudgeMiss()
    {
        HintActive = false;
        Combo = 0;
        bool firstHintMissed = mSuccessCount == 0 && mHintShownCount == 1;
        if (!mStepMode) mNextHintAt = Time.time + NextHintInterval(); // 仅旧模式重排
        EventKit.Type.Send(new GameEvents.FieldCookingHintJudged { Judgment = "Miss", Combo = 0 });

        // "不炒"：第一次颠勺提示都没接住（给出过机会但完全没反应）→ 作废
        // 注意不能用"触发后固定秒数"判定：步骤模式下第一次提示最早在演出步骤之后才出现
        if (firstHintMissed)
        {
            LogKit.Log("[FieldCooking] 第一次颠勺提示未响应，本次机会作废（不炒）");
            FinishCooking(isAbandoned: true);
        }
    }

    /// <summary>下一次提示的间隔（随机区间）</summary>
    private float NextHintInterval()
    {
        var cfg = Cfg;
        float min = cfg != null && cfg.pipHintIntervalMin > 0f ? cfg.pipHintIntervalMin : 1.5f;
        float max = cfg != null && cfg.pipHintIntervalMax > min ? cfg.pipHintIntervalMax : min + 0.5f;
        return Random.Range(min, max);
    }

    /// <summary>提示总数估算（按平均间隔；仅 UI 进度参考，结算用实际出现数）</summary>
    private int EstimateHintTotal()
    {
        var cfg = Cfg;
        float min = cfg != null && cfg.pipHintIntervalMin > 0f ? cfg.pipHintIntervalMin : 1.5f;
        float max = cfg != null && cfg.pipHintIntervalMax > min ? cfg.pipHintIntervalMax : min + 0.5f;
        float avg = (min + max) * 0.5f + (cfg?.pipHintWindow ?? 0.8f);
        float duration = cfg?.pipDuration ?? 18f;
        return Mathf.Max(1, Mathf.RoundToInt(duration / avg));
    }

    /// <summary>结算：命中率 → 品质 → 局内增益 + 局外背包 + 失败 debuff</summary>
    private void FinishCooking(bool isAbandoned)
    {
        if (!IsRunning) return;
        IsRunning = false;
        HintActive = false;

        var cfg = Cfg;
        var recipe = Recipe;

        float hitRatio = mHintShownCount > 0 ? (float)mSuccessCount / mHintShownCount : 0f;
        float sLine = cfg?.qualitySRatio ?? 0.9f;
        float aLine = cfg?.qualityARatio ?? 0.75f;
        float bLine = cfg?.qualityBRatio ?? 0.5f;
        float passLine = cfg?.qualityPassRatio ?? 0.3f;

        // 品质：不炒直接判失败；炒糊按命中率；S/A/B/C 四档映射稀有度 4/3/2/1 星
        string quality;
        float qualityMult;
        if (isAbandoned || hitRatio < passLine) { quality = isAbandoned ? "不炒" : "炒糊"; qualityMult = 0f; }
        else if (hitRatio >= sLine) { quality = "S"; qualityMult = 1.8f; }
        else if (hitRatio >= aLine) { quality = "A"; qualityMult = 1.5f; }
        else if (hitRatio >= bLine) { quality = "B"; qualityMult = 1.3f; }
        else { quality = "C"; qualityMult = 1.2f; }

        LogKit.Log($"[FieldCooking] 结算：{recipe?.recipeName} 品质={quality} 命中={mSuccessCount}/{mHintShownCount} " +
                   $"(Perfect率={hitRatio:P0}) combo峰值={Combo}");

        // ===== 失败：手忙脚乱 debuff（攻速降低），不扣食材 =====
        int dishStar = 0;
        if (qualityMult <= 0f)
        {
            ApplyFailDebuff(cfg);
        }
        else
        {
            // ===== 成功：局内临时增益（品质倍率放大） =====
            if (recipe != null && CookingEffectFactory.HasInstance)
            {
                CookingEffectFactory.Instance.ApplyEffect(recipe, qualityMult);
            }

            // ===== 成功：菜品按档位入局外背包（C=1星 白 / B=2星 绿 / A=3星 蓝 / S=4星 紫） =====
            dishStar = quality == "S" ? 4 : quality == "A" ? 3 : quality == "B" ? 2 : 1;
            bool added = false;
            if (recipe != null && !string.IsNullOrEmpty(recipe.dishRecipeId)
                && MetaManager.HasInstance
                && GameDataMgr.Instance.GetRecipe(recipe.dishRecipeId) != null)
            {
                MetaManager.Instance.AddDish(recipe.dishRecipeId, dishStar, 1);
                added = true;

                // 记入本阶段收获（阶段结算收获页签展示）
                if (BattleStatsCollector.HasInstance)
                {
                    BattleStatsCollector.Instance.RecordFieldCookingReward(
                        $"画中画炒菜：{recipe.recipeName}（{quality}）→ {recipe.dishRecipeId} ★{dishStar} 入背包");
                }
            }
            if (!added) dishStar = 0; // 无映射/映射失效时不入包
        }

        Combo = 0;
        EventKit.Type.Send(new GameEvents.FieldCookingFinished
        {
            RecipeId = recipe?.recipeId,
            Quality = quality,
            Multiplier = qualityMult,
            DishStar = dishStar
        });

        Recipe = null;
    }

    /// <summary>
    /// 失败惩罚：手忙脚乱 debuff（攻速降低 N 秒后恢复）。
    /// **覆盖式单层**：已有惩罚时不叠加，只刷新结束时刻（取较晚者）；若新惩罚更强则补差额。
    /// 这样"连续失败"的强度上限恒为 1 层，转速不会被时间累积拖垮。
    /// </summary>
    private void ApplyFailDebuff(CookingRecipeConfigWrapper cfg)
    {
        float duration = cfg?.pipFailDebuffDuration ?? 30f;
        float value = cfg?.pipFailDebuffAttackSpeed ?? 0.2f;
        if (duration <= 0f || value <= 0f) return;

        if (!SkillSystem.HasInstance)
        {
            LogKit.Warning("[FieldCooking] SkillSystem 不存在，失败 debuff 未生效");
            return;
        }

        float now = Time.time;
        float newEnd = now + duration;

        if (mFailDebuffValue <= 0f)
        {
            // 无惩罚在身：直接扣一层
            SkillSystem.Instance.AddExtraAttackSpeedBonus(-value);
            mFailDebuffValue = value;
            mFailDebuffEndAt = newEnd;
        }
        else
        {
            // 已有惩罚：只刷新时长；更强才补差额（不重复扣、不叠层）
            if (value > mFailDebuffValue)
            {
                SkillSystem.Instance.AddExtraAttackSpeedBonus(-(value - mFailDebuffValue));
                mFailDebuffValue = value;
            }
            mFailDebuffEndAt = Mathf.Max(mFailDebuffEndAt, newEnd);
        }

        // 只有一个常驻恢复协程；到期统一回退（不依赖"每次失败一个协程"的配对）
        if (mFailDebuffCo == null) mFailDebuffCo = StartCoroutine(FailDebuffCo());
        LogKit.Log($"[FieldCooking] 手忙脚乱！攻速 -{mFailDebuffValue:P0}（覆盖式），{mFailDebuffEndAt - now:F0} 秒后恢复");
    }

    /// <summary>失败惩罚恢复协程（唯一实例）：到统一结束时刻后一次性回退已扣量</summary>
    private IEnumerator FailDebuffCo()
    {
        // Time.time 在 timeScale=0（BOSS 音游）时停摆，与惩罚计时口径一致，不会误判到期
        while (mFailDebuffValue > 0f && Time.time < mFailDebuffEndAt) yield return null;

        if (mFailDebuffValue > 0f)
        {
            if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(mFailDebuffValue);
            mFailDebuffValue = 0f;
        }
        mFailDebuffEndAt = 0f;
        mFailDebuffCo = null;
    }

    /// <summary>立即清除手忙脚乱惩罚（重开/重置时调用，回退已扣攻速，避免跨局残留）</summary>
    private void ClearFailDebuff()
    {
        if (mFailDebuffCo != null)
        {
            StopCoroutine(mFailDebuffCo);
            mFailDebuffCo = null;
        }
        if (mFailDebuffValue > 0f)
        {
            if (SkillSystem.HasInstance) SkillSystem.Instance.AddExtraAttackSpeedBonus(mFailDebuffValue);
            mFailDebuffValue = 0f;
        }
        mFailDebuffEndAt = 0f;
    }

    /// <summary>强制停止（结算页/重开时调用）</summary>
    public void ForceStop()
    {
        IsRunning = false;
        HintActive = false;
        Recipe = null;
        Combo = 0;
        mStepMode = false;
        mSteps = null;
        StepIndex = -1;
        StepCount = 0;
    }

    /// <summary>重置（重开/回主菜单时调用）</summary>
    public void Reset()
    {
        ForceStop();
        ClearFailDebuff();   // 跨局不残留已扣攻速
        RunRemainRatio = 0f;
    }
}
