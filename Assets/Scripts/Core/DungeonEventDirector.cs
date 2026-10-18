using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 副本事件槽调度：订阅 WaveStarted，按当前副本的 eventSlots 配置掷骰并派发随机事件。
/// 概率 / 波次区间 / 一次性 / 冷却 四重过滤；派发失败不消耗次数，下波可重掷。
/// 尚无消费者的槽类型只记日志，不预埋空事件以免留下死数据。
/// </summary>
public class DungeonEventDirector : MonoSingleton<DungeonEventDirector>
{
    private DungeonConfig mDungeon;
    private readonly HashSet<string> mFiredOnce = new();
    private readonly Dictionary<string, int> mLastFireWave = new();

    protected override void Awake()
    {
        base.Awake();
        EventKit.Type.Register<GameEvents.WaveStarted>(OnWaveStarted)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    /// <summary>应用副本配置（进入战斗 / 断点恢复时调用，同时清空触发记录）</summary>
    public void ApplyDungeon(DungeonConfig dungeon)
    {
        mDungeon = dungeon;
        mFiredOnce.Clear();
        mLastFireWave.Clear();
    }

    private void OnWaveStarted(GameEvents.WaveStarted evt)
    {
        var slots = mDungeon?.eventSlots;
        if (slots == null || slots.Count == 0) return;

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!CanRoll(slot, evt.WaveId)) continue;
            if (Random.value > slot.probability) continue;   // 掷骰未中
            if (!Dispatch(slot, evt.WaveId)) continue;       // 派发失败 → 不消耗次数

            mLastFireWave[slot.eventId] = evt.WaveId;
            if (slot.oncePerDungeon) mFiredOnce.Add(slot.eventId);
        }
    }

    /// <summary>是否允许掷骰（波次区间 / 一次性 / 冷却）；波次为"本关累计波次序号"（跨阶段连续）</summary>
    private bool CanRoll(DungeonEventSlot slot, int waveId)
    {
        if (slot == null || string.IsNullOrEmpty(slot.eventId)) return false;
        if (waveId < slot.minWave || waveId > slot.maxWave) return false;
        if (slot.oncePerDungeon && mFiredOnce.Contains(slot.eventId)) return false;
        if (slot.cooldownWaves > 0
            && mLastFireWave.TryGetValue(slot.eventId, out int lastWave)
            && waveId - lastWave <= slot.cooldownWaves) return false;
        return true;
    }

    /// <summary>派发事件槽，返回是否成功执行</summary>
    private bool Dispatch(DungeonEventSlot slot, int waveId)
    {
        switch (slot.kind)
        {
            case DungeonEventKind.BattlefieldCooking:
                bool triggered = FieldCookingDirector.HasInstance
                    && FieldCookingDirector.Instance.TryTriggerNow();
                if (triggered)
                    LogKit.Log($"[DungeonEventDirector] 事件槽触发战场炒菜：{slot.eventId}（第 {waveId} 波）");
                return triggered;

            default:
                LogKit.Log($"[DungeonEventDirector] 事件槽 {slot.eventId}（类型 {slot.kind}）尚无消费者，已跳过");
                return false;
        }
    }
}
