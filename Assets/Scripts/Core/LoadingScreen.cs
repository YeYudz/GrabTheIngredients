using System;
using System.Collections;
using UnityEngine;
using YokiFrame;
using GameUI;

/// <summary>
/// 加载步骤描述：阶段提示 + 权重 + 具体工作。权重只表示相对占比，由 LoadingScreen 归一化。
/// </summary>
public sealed class LoadingStep
{
    public string Tip;          // 阶段提示文案（显示在加载页）
    public float Weight = 1f;   // 相对权重
    public Action Work;         // 同步工作（一帧内完成的初始化）
    public IEnumerator Routine; // 协程工作（需要跨帧，例如异步加载场景）

    /// <summary>同步步骤</summary>
    public static LoadingStep Sync(string tip, float weight, Action work)
        => new LoadingStep { Tip = tip, Weight = weight, Work = work };

    /// <summary>协程步骤（内部可用 LoadingScreen.ReportSubProgress 上报本步进度）</summary>
    public static LoadingStep Async(string tip, float weight, IEnumerator routine)
        => new LoadingStep { Tip = tip, Weight = weight, Routine = routine };
}

/// <summary>
/// 加载过渡（业务层）：按权重分步执行初始化/切场景，向 LoadingProgress 事件上报真实进度
/// - 真实加权：进度 = 已完成步骤权重和 + 当前步骤权重 × 步骤内进度
/// - 最短展示时长：整体耗时不足时补足，避免加载页一闪而过
/// - 面板缺失兜底：LoadingPanel 预制体不存在时流程照常执行（只是没有进度条）
/// 由 GameManager 用 StartCoroutine 驱动；显示层为 LoadingPanel（只订阅事件）
/// </summary>
public static class LoadingScreen
{
    /// <summary>默认最短展示时长（秒）</summary>
    public const float DEFAULT_MIN_DURATION = 0.8f;

    /// <summary>进度满后的停留时长，避免数字没走完就切场景</summary>
    private const float TAIL_HOLD = 0.4f;

    private static bool mRunning;
    private static float mBase;       // 当前步骤起始进度
    private static float mWeight;     // 当前步骤权重（已归一化）
    private static float mSub;        // 当前步骤内部进度 0~1
    private static string mCurrentTip;

    /// <summary>是否正在加载（防止重复触发）</summary>
    public static bool IsRunning => mRunning;

    /// <summary>
    /// 供协程步骤上报自身进度（0~1），会折算到总进度中。
    /// 仅在对应步骤执行期间有效。
    /// </summary>
    public static void ReportSubProgress(float local01) => mSub = Mathf.Clamp01(local01);

    /// <summary>执行加载流程（使用默认最短展示时长）</summary>
    public static IEnumerator Run(params LoadingStep[] steps) => Run(DEFAULT_MIN_DURATION, steps);

    /// <summary>执行加载流程</summary>
    /// <param name="minDuration">最短展示时长（秒，真实时间）</param>
    /// <param name="steps">加载步骤（按顺序执行）</param>
    public static IEnumerator Run(float minDuration, params LoadingStep[] steps)
    {
        if (mRunning)
        {
            LogKit.Warning("[LoadingScreen] 加载流程已在执行中，本次请求被忽略");
            yield break;
        }

        mRunning = true;
        try
        {
            float startTime = Time.unscaledTime;
            float totalWeight = 0f;
            if (steps != null)
            {
                for (int i = 0; i < steps.Length; i++)
                    totalWeight += Mathf.Max(0.0001f, steps[i]?.Weight ?? 1f);
            }
            if (totalWeight <= 0f) totalWeight = 1f;

            bool opened = GameManager.TryOpenPanel<LoadingPanel>(UILevel.AlwayTop);

            mBase = 0f; mWeight = 0f; mSub = 0f; mCurrentTip = "准备中…";
            Send(0f);
            yield return null; // 先让加载页渲染出来，避免黑屏干等

            float acc = 0f;
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    if (step == null) continue;

                    mBase = acc;
                    mWeight = Mathf.Max(0.0001f, step.Weight) / totalWeight;
                    mSub = 0f;
                    mCurrentTip = step.Tip;
                    Send(acc);

                    float stepStart = Time.unscaledTime;
                    if (step.Work != null)
                    {
                        // 同步步骤：单步异常不影响整体流程
                        // （yield 不能出现在带 catch 的 try 内，故协程步骤的异常由 DriveRoutine 内部隔离）
                        try
                        {
                            step.Work();
                        }
                        catch (Exception e)
                        {
                            LogKit.Exception(e);
                        }
                        mSub = 1f;
                    }
                    else if (step.Routine != null)
                    {
                        yield return DriveRoutine(step.Routine);
                    }

                    acc = mBase + mWeight;
                    Send(acc);
                    LogKit.Log($"[LoadingScreen] {step.Tip} 耗时 {(Time.unscaledTime - stepStart) * 1000f:F1} ms");
                    yield return null;
                }
            }

            // 最短展示时长：真实加载很快时补足，避免一闪而过（无加载页时不做无谓等待）
            while (opened && Time.unscaledTime - startTime < minDuration) yield return null;

            mCurrentTip = "准备完成";
            Send(1f);
            yield return new WaitForSecondsRealtime(TAIL_HOLD);

            if (opened) UIKit.ClosePanel<LoadingPanel>();
        }
        finally
        {
            mRunning = false;
        }
    }

    /// <summary>驱动协程步骤：每帧把"步骤内进度"折算为总进度上报（内部隔离异常）</summary>
    private static IEnumerator DriveRoutine(IEnumerator inner)
    {
        while (true)
        {
            Send(mBase + mWeight * mSub);

            bool moved;
            try
            {
                moved = inner.MoveNext();
            }
            catch (Exception e)
            {
                LogKit.Exception(e);
                moved = false;
            }
            if (!moved) yield break;

            yield return inner.Current;
        }
    }

    private static void Send(float progress)
        => EventKit.Type.Send(new GameEvents.LoadingProgress { Progress = progress, Tip = mCurrentTip });
}
