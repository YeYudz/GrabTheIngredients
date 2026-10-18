using UnityEngine;
using YokiFrame;

/// <summary>
/// 敌人行为状态，与 EnemyBehavior 配置一一映射。
/// </summary>
public enum EnemyAiState
{
    Chase,        // 慢速追击（史莱姆系）
    Charge,       // 快速冲锋（哥布林系）
    Stationary,   // 静止不动（蘑菇系，死亡爆炸）
    Dead          // 死亡
}

/// <summary>敌人 AI 状态机共享黑板</summary>
public class EnemyBlack
{
    public EnemySimple Enemy;
    public Transform Player;
}

/// <summary>
/// 敌人 AI 状态机（YokiFrame FSM）
/// 行为逻辑完全数据驱动：根据 EnemyConfig.behavior 映射初始状态
/// 生命周期：每个敌人实例只构造一次（池化复用时由 Reset 复位，不重建 FSM 与状态对象）
/// </summary>
public class EnemyAI
{
    // FSM 诊断名用常量：原来用 $"EnemyAI_{ConfigId}" 会给每只怪都分配一个新字符串
    // （Editor 下还会逐个登记到 FsmKitRegistry）；同名实例在注册表里本就是独立记录，可安全复用。
    private const string kFsmName = "EnemyAI";

    private readonly FSM<EnemyAiState> mFsm;
    private readonly EnemyBlack mBlack;

    public EnemyAI(EnemySimple enemy, Transform player)
    {
        mBlack = new EnemyBlack { Enemy = enemy, Player = player };
        mFsm = new FSM<EnemyAiState>(kFsmName);

        mFsm.Add(EnemyAiState.Chase, new EnemyChaseState(mFsm, mBlack));
        mFsm.Add(EnemyAiState.Charge, new EnemyChargeState(mFsm, mBlack));
        mFsm.Add(EnemyAiState.Stationary, new EnemyStationaryState(mFsm, mBlack));
        mFsm.Add(EnemyAiState.Dead, new EnemyDeadState(mFsm, mBlack));
    }

    /// <summary>根据行为类型启动对应状态</summary>
    public void Start(EnemyBehavior behavior)
    {
        switch (behavior)
        {
            case EnemyBehavior.SlowChase: mFsm.Start(EnemyAiState.Chase); break;
            case EnemyBehavior.FastCharge: mFsm.Start(EnemyAiState.Charge); break;
            case EnemyBehavior.Stationary: mFsm.Start(EnemyAiState.Stationary); break;
            default: mFsm.Start(EnemyAiState.Chase); break;
        }
    }

    /// <summary>
    /// 池化复用复位：更新玩家引用并把状态机重新拉回配置指定的初始状态。
    /// 必须先 End()——FSM.Start 在 Running 阶段是 no-op，而上一局的敌人可能停在 Dead 状态。
    /// 复用同一实例可省掉「每次生成重建 FSM + 4 个状态对象 + 黑板」的开销。
    /// </summary>
    public void Reset(EnemyBehavior behavior, Transform player)
    {
        mBlack.Player = player;
        mFsm.End();
        Start(behavior);
    }

    public void Update() => mFsm.Update();
    public void FixedUpdate() => mFsm.FixedUpdate();

    /// <summary>切到死亡状态（由 EnemySimple.HandleDeath 调用）</summary>
    public void ToDead() => mFsm.Change(EnemyAiState.Dead);

    public void Dispose() => mFsm.Dispose();
}

/// <summary>慢速追击状态（史莱姆系）</summary>
public class EnemyChaseState : AbstractState<EnemyAiState, EnemyBlack>
{
    public EnemyChaseState(FSM<EnemyAiState> fsm, EnemyBlack black) : base(fsm, black) { }

    protected override void OnUpdate()
    {
        if (mBlack.Player == null || mBlack.Enemy == null) return;
        mBlack.Enemy.MoveTowardPlayer(mBlack.Player.position, mBlack.Enemy.Speed);
    }
}

/// <summary>快速冲锋状态（哥布林系）：朝玩家冲一段距离后回到追击</summary>
public class EnemyChargeState : AbstractState<EnemyAiState, EnemyBlack>
{
    private float mChargeTimer;

    public EnemyChargeState(FSM<EnemyAiState> fsm, EnemyBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        // 冲锋时间 = 行为参数（秒），结束后回到追击
        mChargeTimer = mBlack.Enemy.BehaviorParam;
        LogKit.Log($"[EnemyAI] {mBlack.Enemy.ConfigId} 开始冲锋 {mChargeTimer}s");
    }

    protected override void OnUpdate()
    {
        if (mBlack.Player == null || mBlack.Enemy == null) return;

        // 冲锋速度 = 基础速度 × 3
        mBlack.Enemy.MoveTowardPlayer(mBlack.Player.position, mBlack.Enemy.Speed * 3f);

        mChargeTimer -= Time.deltaTime;
        if (mChargeTimer <= 0f)
        {
            mFSM.Change(EnemyAiState.Chase);
        }
    }
}

/// <summary>静止状态（蘑菇系）：不动，死亡时爆炸</summary>
public class EnemyStationaryState : AbstractState<EnemyAiState, EnemyBlack>
{
    public EnemyStationaryState(FSM<EnemyAiState> fsm, EnemyBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        // 静止：速度强制为 0（Player 可能为空，做空检查）
        if (mBlack.Player != null)
            mBlack.Enemy.MoveTowardPlayer(mBlack.Player.position, 0f);
    }
}

/// <summary>死亡状态：只做标记，实际销毁由 EnemySimple.Update 处理（避免 FSM 重入崩溃）</summary>
public class EnemyDeadState : AbstractState<EnemyAiState, EnemyBlack>
{
    public EnemyDeadState(FSM<EnemyAiState> fsm, EnemyBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        // 只标记死亡，不在状态回调内销毁对象（FSM Change 生命周期中禁止嵌套修改）
        mBlack.Enemy.MarkDeathPending();
    }
}
