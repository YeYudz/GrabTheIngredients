using UnityEngine;
using YokiFrame;

/// <summary>
/// 画中画炒菜触发调度：触发来源由副本配置决定——累计击杀达到阈值，或由事件槽掷骰触发。
/// 与进行中的 BOSS 音游、炒菜互斥；触发后只广播事件，具体流程由 FieldCookingEngine 接管。
/// </summary>
public class FieldCookingDirector : MonoSingleton<FieldCookingDirector>
{
    private int mKillsSinceTrigger;

    /// <summary>距下次触发还差的击杀数（断点保存/恢复用）</summary>
    public int KillsSinceTrigger
    {
        get => mKillsSinceTrigger;
        set => mKillsSinceTrigger = Mathf.Max(0, value);
    }

    protected override void Awake()
    {
        base.Awake();
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        // 事件槽模式：击杀不触发，改由 DungeonEventDirector 按副本配置掷骰触发
        if (TriggerMode == DungeonCookingTriggerMode.EventSlot) return;

        mKillsSinceTrigger++;
        if (mKillsSinceTrigger < GameDataMgr.Instance.GetCookingTriggerThreshold()) return;
        if (!TryTriggerNow()) return;

        mKillsSinceTrigger = 0;
    }

    /// <summary>
    /// 尝试立即触发一次战场炒菜（击杀阈值路径与关卡事件槽路径共用）。
    /// 返回是否真的启动了炒菜（条件不满足时返回 false，调用方可自行决定是否重试）。
    /// </summary>
    public bool TryTriggerNow()
    {
        if (!CanStartCooking()) return false;

        var recipe = PickRandomRecipe();
        if (recipe == null) return false;

        FieldCookingEngine.Instance.StartCooking(recipe);
        return true;
    }

    /// <summary>当前触发来源（取自当前副本配置；无副本配置时沿用击杀阈值）</summary>
    private static DungeonCookingTriggerMode TriggerMode
        => GameDataMgr.Instance.CurrentDungeon?.fieldCookingTrigger ?? DungeonCookingTriggerMode.KillThreshold;

    /// <summary>是否可启动（炒菜/BOSS音游互斥 + 玩家存活）</summary>
    private static bool CanStartCooking()
    {
        // 炒菜进行中不重复触发
        if (FieldCookingEngine.HasInstance && FieldCookingEngine.Instance.IsRunning) return false;
        // 与 BOSS 音游互斥（BOSS 音游期间 timeScale=0）
        if (RhythmEngine.HasInstance && RhythmEngine.Instance.IsRunning) return false;

        var player = GameManager.Instance?.Player;
        return player != null && player.IsAlive;
    }

    /// <summary>从已解锁菜谱池随机抽一道（当前全量随机，解锁系统后续接入）</summary>
    private CookingRecipeConfig PickRandomRecipe()
    {
        var wrapper = GameDataMgr.Instance.CookingRecipes;
        if (wrapper == null || wrapper.Count == 0) return null;
        return wrapper.Recipes[Random.Range(0, wrapper.Count)];
    }

    /// <summary>重置（重开/回主菜单时调用）</summary>
    public void Reset()
    {
        mKillsSinceTrigger = 0;
    }
}
