using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 对象池预热器：在战斗加载页期间按副本配置预先实例化怪物与掉落物，
/// 避开首次刷怪 / 掉落时的 Instantiate 抖动。
/// 预热规模只是下限（第一波怪全量、第一波掉落约三分之一），运行时仍按需扩容；
/// ⚠ 池对象是普通场景 GameObject，切场景即销毁，故必须在战场场景就绪后才预热。
/// </summary>
public static class PoolPrewarmer
{
    /// <summary>
    /// 第一波怪物总量的预热比例。
    /// 取 1.0：第一波正好刷在「加载页刚关掉、玩家最敏感」的时刻，必须全量预热，
    /// 否则超出的部分要在刷怪那几秒里逐只 Instantiate（这是开局掉帧的主因）。
    /// </summary>
    private const float kEnemyWaveFraction = 1f;

    /// <summary>第一波掉落物预计产出总量的预热比例（三分之一）</summary>
    private const float kDropWaveFraction = 1f / 3f;

    /// <summary>每帧最多实例化个数（分帧执行，避免加载页自身卡顿）</summary>
    private const int kInstancesPerFrame = 8;

    /// <summary>单个预热任务：预制体 Resources 路径 + 预热数量</summary>
    private struct PrewarmJob
    {
        public string PrefabPath;
        public int Count;
    }

    /// <summary>是否正在预热（WaveManager 第一波 Prep 期间等待此标记，确保预热完成后才正式开始刷怪）</summary>
    public static bool IsPrewarming { get; private set; }

    /// <summary>战斗预热协程（LoadingScreen 步骤）：分帧预热敌人 + 掉落物进对象池，上报子进度</summary>
    public static IEnumerator RunBattlePrewarm()
    {
        var jobs = CollectJobs();

        int total = 0;
        foreach (var job in jobs) total += job.Count;
        if (total <= 0) yield break;

        IsPrewarming = true;
        try
        {
            int done = 0;
            foreach (var job in jobs)
            {
                int remaining = job.Count;
                int target = 0;   // 该预制体的累计预热目标（跨分帧递增）
                while (remaining > 0)
                {
                    // 分帧实例化：Instantiate 集中在一帧会卡加载页
                    int chunk = Mathf.Min(kInstancesPerFrame, remaining);

                    // 注意：PoolFactory.Prewarm 的参数语义是「池应达到的总数」，
                    // 而不是「本次再补几个」。所以分帧时必须传累计目标 target；
                    // 若直接传 chunk，第二次调用起 missing = chunk - CurCount <= 0 会直接 return，
                    // 预热上限会被硬卡在 kInstancesPerFrame（曾经的 bug：配置要 25 只，实际只造 8 只）。
                    target += chunk;
                    PoolFactory.Prewarm(job.PrefabPath, target);

                    remaining -= chunk;
                    done += chunk;

                    LoadingScreen.ReportSubProgress((float)done / total);
                    yield return null;
                }
            }

            // 日志上报「实际入池数」而非目标数：Prewarm 的缺陷曾让日志与真实创建量严重不符
            var countedPaths = new HashSet<string>();
            int created = 0;
            foreach (var job in jobs)
            {
                if (job.PrefabPath == null || !countedPaths.Add(job.PrefabPath)) continue;
                created += PoolFactory.GetCachedCount(job.PrefabPath);
            }
            LogKit.Log($"[PoolPrewarmer] 战斗预热完成：{jobs.Count} 种预制体，目标 {total} 个，实际入池 {created} 个");
        }
        finally
        {
            // 无论中途如何退出（场景切换/销毁）都必须复位，否则波次 Prep 永久卡住
            IsPrewarming = false;
        }
    }

    /// <summary>收集预热任务：敌人/掉落物均按副本第一波配置折算（当前选中副本 + 断点续玩副本）</summary>
    private static List<PrewarmJob> CollectJobs()
    {
        var jobs = new List<PrewarmJob>();
        var countPerPath = new Dictionary<string, int>();

        // 副本解析：当前选中 + 默认副本 + 断点记录的副本，按引用去重后一并收集
        var dungeons = new HashSet<DungeonConfig>
        {
            GameDataMgr.Instance.GetDungeon(MetaManager.Instance.SelectedDungeonId),
            GameDataMgr.Instance.CurrentDungeon
        };
        if (RunSaveManager.HasSave())
            dungeons.Add(GameDataMgr.Instance.GetDungeon(RunSaveManager.Load()?.dungeonId));
        dungeons.Remove(null);

        // 1. 敌人：第一波刷怪量的一半，按预制体路径累计（去重后合并）
        foreach (var dungeon in dungeons)
            CollectEnemyJobs(dungeon, countPerPath);

        foreach (var pair in countPerPath)
        {
            jobs.Add(new PrewarmJob { PrefabPath = pair.Key, Count = pair.Value });
        }

        // 2. 掉落物：第一波预计产出总量的三分之一
        CollectDropJobs(dungeons, jobs);

        return jobs;
    }

    /// <summary>收集第一波敌人的预热任务：每类取刷怪量（kEnemyWaveFraction），同预制体路径累计</summary>
    private static void CollectEnemyJobs(DungeonConfig dungeon, Dictionary<string, int> countPerPath)
    {
        var firstWave = GetFirstWave(dungeon);
        if (firstWave?.spawnGroups == null) return;

        foreach (var group in firstWave.spawnGroups)
        {
            if (group == null || string.IsNullOrEmpty(group.enemyId) || group.count <= 0) continue;

            var config = GameDataMgr.Instance.GetEnemy(group.enemyId);
            if (config == null || string.IsNullOrEmpty(config.prefabPath)) continue;

            // 按类型分摊并向上取整；精英/普通怪共用预制体时按路径累计
            int prewarmCount = Mathf.CeilToInt(group.count * kEnemyWaveFraction);
            countPerPath.TryGetValue(config.prefabPath, out int current);
            countPerPath[config.prefabPath] = current + prewarmCount;
        }
    }

    /// <summary>收集掉落物预热任务：按第一波预计产出总量（食材按 dropCount、经验宝石每杀 1 个）取三分之一（向上取整）</summary>
    private static void CollectDropJobs(HashSet<DungeonConfig> dungeons, List<PrewarmJob> jobs)
    {
        if (!DropManager.HasInstance) return;

        int ingredientTotal = 0;
        int expGemTotal = 0;

        foreach (var dungeon in dungeons)
        {
            var firstWave = GetFirstWave(dungeon);
            if (firstWave?.spawnGroups == null) continue;

            foreach (var group in firstWave.spawnGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.enemyId) || group.count <= 0) continue;

                var config = GameDataMgr.Instance.GetEnemy(group.enemyId);
                if (config == null) continue;

                ingredientTotal += group.count * Mathf.Max(0, config.dropCount);
                if (config.expReward > 0f) expGemTotal += group.count;
            }
        }

        if (ingredientTotal > 0)
        {
            jobs.Add(new PrewarmJob
            {
                PrefabPath = DropManager.Instance.IngredientDropPrefabPath,
                Count = Mathf.CeilToInt(ingredientTotal * kDropWaveFraction)
            });
        }

        if (expGemTotal > 0)
        {
            jobs.Add(new PrewarmJob
            {
                PrefabPath = DropManager.Instance.ExpGemPrefabPath,
                Count = Mathf.CeilToInt(expGemTotal * kDropWaveFraction)
            });
        }
    }

    /// <summary>
    /// 取副本的第一波配置：阶段模式取第一个有效阶段的第一个 waveId；
    /// 否则取旧字段 waveIds 第一项；未配置副本退回全局波次表（与 WaveManager 的回退规则一致）
    /// </summary>
    private static WaveConfig GetFirstWave(DungeonConfig dungeon)
    {
        if (dungeon != null)
        {
            if (dungeon.HasPhases())
            {
                foreach (var phase in dungeon.phases)
                {
                    if (phase?.waveIds == null || phase.waveIds.Count == 0) continue;
                    return GameDataMgr.Instance.GetWave(phase.waveIds[0]);
                }
                return null;
            }

            if (dungeon.waveIds != null && dungeon.waveIds.Count > 0)
                return GameDataMgr.Instance.GetWave(dungeon.waveIds[0]);
        }

        return GameDataMgr.Instance.GetWave(1);
    }
}
