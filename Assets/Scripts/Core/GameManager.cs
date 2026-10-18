using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YokiFrame;
using GameUI;

/// <summary>
/// 游戏入口管理器：初始化配置与存档，用 FSM 驱动全局状态、场景切换与面板流程。
/// 面板只负责显示、本类只做数据与业务调度，两者之间仅通过事件与状态机交互。
/// </summary>
public class GameManager : MonoSingleton<GameManager>
{
    /// <summary>UI 面板预制体加载路径前缀（Resources 下的相对路径）</summary>
    private const string UI_PANEL_PATH_PREFIX = "Prefabs/UI";

    [Header("调试")]
    [Tooltip("勾选且当前场景为 Battle 时：直接进入战斗，跳过主菜单（方便调试）")]
    [SerializeField] private bool mDebugStartInBattle = false;

    /// <summary>游戏总状态机（YokiFrame FSM）</summary>
    private FSM<GameState> mFsm;

    /// <summary>状态机是否已启动（首次用 Start，之后用 Change）</summary>
    private bool mFsmStarted;

    /// <summary>下一局是否为"继续"（断点续玩标记，进入 Running 后消费）</summary>
    private bool mResumeRun;

    /// <summary>本局结算钱币是否已发放（一局仅发一次，防重复发币）</summary>
    private bool mRunCoinsAwarded;

    /// <summary>玩家引用（场景加载后由 PlayerController 注册）</summary>
    public PlayerController Player { get; private set; }

    /// <summary>当前波次（Step3 波次系统使用）</summary>
    public int CurrentWave { get; private set; } = 1;

    /// <summary>本局开始时间（Time.time，HUD 计时基准；断点恢复时回退为"波次开局的已用时长"）</summary>
    public float RunStartTime { get; private set; }

    /// <summary>设置本局开始时间（进入战斗 / 断点恢复时调用）</summary>
    public void MarkRunStart(float time) => RunStartTime = time;

    protected override void Awake()
    {
        base.Awake();

        // 调试：自动创建 GM 控制台（编辑器/开发构建；发布版条件编译自动剔除）
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GMConsole.EnsureCreated();
#endif

        // 1. 只做"加载页要立刻能显示"的准备：UIRoot（Canvas + CanvasScaler + 面板加载路径）
        //    重量级初始化（配置/存档/音频）挪到 Start 的加载流程里分步执行，避免 Awake 期间黑屏干等
        SetupUIRoot();

        // 3. 注册全局流程事件（只注册一次，避免状态重入时重复）
        EventKit.Type.Register<GameEvents.GameStart>(_ => StartCoroutine(StartBattleRoutine(false)))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.ContinueRun>(_ => StartBattleFromCheckpoint())
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.RestartRun>(_ => StartCoroutine(StartBattleRoutine(true)))
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BackToMenu>(_ => StartCoroutine(ReturnToMainMenuRoutine()))
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        // 阶段/整局结算流程（BOSS 胜利 + 结算页确认）
        EventKit.Type.Register<GameEvents.BossStageCleared>(OnBossStageCleared)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.StageResultConfirmed>(OnStageResultConfirmed)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.RunResultConfirmed>(OnRunResultConfirmed)
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        // 波次开始 → 自动落盘一次完整断点（崩溃/强杀也能续上；暂停退出时会再用"此刻"状态覆盖）
        EventKit.Type.Register<GameEvents.WaveStarted>(_ => RunSaveManager.AutoSaveWaveStart())
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        // 4. 初始化游戏总状态机（不立即启动，由 Start 按场景引导）
        mFsm = new FSM<GameState>("GameMain");
        var black = new GameBlack { Manager = this };
        mFsm.Add(GameState.Menu, new GameMenuState(mFsm, black));
        mFsm.Add(GameState.Running, new GameRunningState(mFsm, black));
        mFsm.Add(GameState.GameOver, new GameOverState(mFsm, black));

        LogKit.Log("[GameManager] 初始化完成");
    }

    // ===== 阶段/整局结算流程 =====

    /// <summary>安全打开面板：预制体缺失/打开异常 → 返回 false（调用方降级继续，避免卡死）</summary>
    public static bool TryOpenPanel<T>(UILevel level, IUIData data = null) where T : UIPanel
    {
        string name = typeof(T).Name;
        if (Resources.Load<GameObject>(AssetKeys.PANEL_PREFIX + name) == null)
        {
            LogKit.Warning($"[GameManager] 面板预制体缺失，跳过: {name}");
            return false;
        }
        try
        {
            UIKit.OpenPanel<T>(level, data);
            return true;
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[GameManager] 面板打开失败 {name}: {e.Message}");
            return false;
        }
    }

    /// <summary>BOSS 战胜利（成功烹饪后）→ 冻结战斗 + 打开阶段结算页</summary>
    private void OnBossStageCleared(GameEvents.BossStageCleared evt)
    {
        // 最终 BOSS = 本副本通关 → 解锁下一关（写入局外存档）
        if (evt.IsFinal) UnlockNextDungeon();

        // 最终 BOSS 通关 = 本局结束 → 按波次/击杀/通关发放结算钱币（一局仅一次）
        if (evt.IsFinal) AwardRunCoinsOnce(true);

        Time.timeScale = 0f;
        UIKit.ClosePanel<HudPanel>();
        UIKit.ClosePanel<LevelUpPanel>();
        UIKit.ClosePanel<FieldCookingPanel>();

        var snapshot = BattleStatsCollector.HasInstance
            ? BattleStatsCollector.Instance.GetStageSnapshot()
            : new BattleStatsSnapshot();
        var data = new StageResultPanelData
        {
            Snapshot = snapshot,
            IsFinal = evt.IsFinal,
            Title = "阶段结算"
        };

        if (!TryOpenPanel<StageResultPanel>(UILevel.Pop, data))
        {
            // 无结算面板 → 直接按确认继续流程，避免卡死
            OnStageResultConfirmed(new GameEvents.StageResultConfirmed { IsFinal = evt.IsFinal });
            return;
        }
        LogKit.Log($"[GameManager] 阶段结算：IsFinal={evt.IsFinal}");
    }

    /// <summary>本副本通关 → 按配置顺序解锁下一关（写入局外存档）</summary>
    private static void UnlockNextDungeon()
    {
        string currentId = WaveManager.HasInstance
            ? WaveManager.Instance.CurrentDungeonId
            : MetaManager.Instance.SelectedDungeonId;

        var next = MetaManager.Instance.UnlockNextDungeon(currentId);
        if (next != null)
            LogKit.Log($"[GameManager] 已解锁新副本：{next.dungeonName}（{next.dungeonId}）");
    }

    /// <summary>阶段结算页确认：最终BOSS→整局结算；否则→继续游戏（下一阶段）。死亡已由 GameOverPanel 单独处理</summary>
    private void OnStageResultConfirmed(GameEvents.StageResultConfirmed evt)
    {
        UIKit.ClosePanel<StageResultPanel>();

        if (evt.IsFinal)
        {
            // 最终 BOSS → 额外展示整局结算页
            var snapshot = BattleStatsCollector.HasInstance
                ? BattleStatsCollector.Instance.GetStageSnapshot()
                : new BattleStatsSnapshot();
            snapshot.RunCoins = MetaManager.HasInstance ? MetaManager.Instance.LastRunCoinReward : 0;
            var data = new RunResultPanelData { Snapshot = snapshot };
            if (!TryOpenPanel<RunResultPanel>(UILevel.Pop, data))
            {
                OnRunResultConfirmed(new GameEvents.RunResultConfirmed());
            }
            return;
        }

        // 非最终 BOSS → 继续游戏（进入下一阶段），重新开始统计
        Time.timeScale = 1f;
        UIKit.OpenPanel<HudPanel>(UILevel.Hud);
        if (BattleStatsCollector.HasInstance) BattleStatsCollector.Instance.BeginStage();
        if (WaveManager.HasInstance) WaveManager.Instance.ResumeAfterBoss();
        LogKit.Log("[GameManager] 阶段结算确认，继续游戏");
    }

    /// <summary>整局结算页确认：记录本局数据 → 返回主菜单</summary>
    private void OnRunResultConfirmed(GameEvents.RunResultConfirmed evt)
    {
        UIKit.ClosePanel<RunResultPanel>();
        RecordRunAndSave(true);
        StartCoroutine(ReturnToMainMenuRoutine());
    }

    /// <summary>死亡结算：打开 GameOverPanel（玩家阵亡，显示整局统计 + 双按钮）；预制体缺失则直接回主菜单</summary>
    public void ShowDeathResult()
    {
        // 阵亡 = 本局结束 → 按波次/击杀发放结算钱币（一局仅一次）
        AwardRunCoinsOnce(false);

        var snapshot = BattleStatsCollector.HasInstance
            ? BattleStatsCollector.Instance.GetStageSnapshot()
            : new BattleStatsSnapshot();
        snapshot.RunCoins = MetaManager.HasInstance ? MetaManager.Instance.LastRunCoinReward : 0;
        var data = new GameOverPanelData { Snapshot = snapshot };
        if (!TryOpenPanel<GameOverPanel>(UILevel.Pop, data))
        {
            StartCoroutine(ReturnToMainMenuRoutine());
        }
    }

    /// <summary>本局结束发币（按波次/击杀/通关；一局仅发一次，结果写入 MetaManager.LastRunCoinReward）</summary>
    private void AwardRunCoinsOnce(bool victory)
    {
        if (mRunCoinsAwarded) return;
        mRunCoinsAwarded = true;
        if (!MetaManager.HasInstance) return;

        int waves = WaveManager.HasInstance ? WaveManager.Instance.DungeonWaveOrdinal : 1;
        int kills = DropManager.HasInstance ? DropManager.Instance.KillCount : 0;
        MetaManager.Instance.AwardRunCoins(waves, kills, victory);
    }

    private void Start()
    {
        // 启动引导：从任意场景启动都进入正确流程
        // 调试模式：Battle 场景启动 → 只做初始化后直接开战
        if (mDebugStartInBattle && SceneManager.GetActiveScene().name == SceneNames.Battle)
        {
            StartCoroutine(DebugBattleRoutine());
            return;
        }

        // 正式流程：加载页过渡 → 主菜单场景 → Menu 状态
        StartCoroutine(BootRoutine());
    }

    // ===== 加载流程（LoadingScreen 驱动：分步初始化 + 异步切场景 + 进度上报） =====

    /// <summary>正式启动：配置/存档/音频 → 异步进主菜单（全程由加载页过渡）</summary>
    private IEnumerator BootRoutine()
    {
        yield return LoadingScreen.Run(1.2f,
            LoadingStep.Sync("正在加载配置…", 0.25f, () => GameDataMgr.Instance.ReloadConfigs()),
            LoadingStep.Sync("正在读取存档…", 0.15f, () =>
            {
                MetaManager.Instance.Init();
                // 元游戏（成就/图鉴）：配置已就绪，初始化常驻系统并载入存档
                RecipeAtlasManager.Instance.LoadFromSave();
                AchievementManager.Instance.LoadFromSave();
                // 画面设置：必须在存档加载之后应用（分辨率 / 窗口模式 / 渲染效果 / 垂直同步 / 亮度）
                VideoSettingsManager.Init();
            }),
            LoadingStep.Sync("正在准备音频…", 0.10f, () => AudioManager.Instance.Init()),
            LoadingStep.Async("正在进入厨房…", 0.50f, SwitchSceneRoutine(SceneNames.MainMenu, GameState.Menu)));

        LogKit.Log("[GameManager] 启动完成");
    }

    /// <summary>调试模式：Battle 场景启动 → 只做初始化，直接进入 Running</summary>
    private IEnumerator DebugBattleRoutine()
    {
        mRunCoinsAwarded = false;   // 新一局开始：重置发币标记

        yield return LoadingScreen.Run(0.5f,
            LoadingStep.Sync("正在加载配置…", 0.35f, () => GameDataMgr.Instance.ReloadConfigs()),
            LoadingStep.Sync("正在读取存档…", 0.15f, () =>
            {
                MetaManager.Instance.Init();
                RecipeAtlasManager.Instance.LoadFromSave();
                AchievementManager.Instance.LoadFromSave();
                VideoSettingsManager.Init();
            }),
            LoadingStep.Sync("正在准备音频…", 0.1f, () => AudioManager.Instance.Init()),
            LoadingStep.Sync("正在开始战斗…", 0.2f, () => ChangeState(GameState.Running)),
            LoadingStep.Async("正在预加载战场对象…", 0.2f, PoolPrewarmer.RunBattlePrewarm()));

        LogKit.Log("[GameManager] 调试模式：直接进入战斗");
    }

    /// <summary>
    /// 异步切换到目标场景并进入目标状态（作为加载步骤执行）。
    /// 已在目标场景且未要求强制重载时，跳过场景加载直接切状态。
    /// 场景加载进度经 LoadingScreen.ReportSubProgress 折算到总进度。
    /// </summary>
    private IEnumerator SwitchSceneRoutine(string sceneName, GameState targetState, bool forceReload = false)
    {
        Time.timeScale = 1f;

        // 切场景前清空玩家引用（旧场景玩家将随场景卸载销毁）
        ClearPlayer();

        if (forceReload || SceneManager.GetActiveScene().name != sceneName)
        {
            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            while (op != null && !op.isDone)
            {
                // Unity 激活场景前的进度上限为 0.9，归一化后作为本步内部进度
                LoadingScreen.ReportSubProgress(Mathf.Clamp01(op.progress / 0.9f));
                yield return null;
            }
            yield return null; // 等一帧，让新场景对象 Awake/Start + PlayerController 注册
        }

        ChangeState(targetState);
    }

    /// <summary>启动/切换状态机状态（首次用 Start，之后用 Change）</summary>
    private void ChangeState(GameState state)
    {
        if (!mFsmStarted)
        {
            mFsmStarted = true;
            mFsm.Start(state);
        }
        else
        {
            mFsm.Change(state);
        }
    }

    /// <summary>
    /// 开始一局战斗：清理局内系统 → 异步加载 Battle 场景 → 进入 Running（加载页过渡）
    /// reloadScene = true 时强制重载场景（用于"重开游戏"重置玩家/敌人/掉落物）
    /// </summary>
    private IEnumerator StartBattleRoutine(bool reloadScene)
    {
        mRunCoinsAwarded = false;   // 新一局开始：重置发币标记

        yield return LoadingScreen.Run(0.8f,
            LoadingStep.Sync("正在收拾装备…", 0.2f, () =>
            {
                // 开新局（非续玩）→ 丢掉上一局残留的断点，避免崩溃后被"继续"送回旧局
                if (!mResumeRun) RunSaveManager.Clear();

                // 关闭主菜单流程面板（战前准备/背包/商店）
                UIKit.ClosePanel<PreparationPanel>();
                UIKit.ClosePanel<InventoryPanel>();
                UIKit.ClosePanel<ShopPanel>();
                UIKit.ClosePanel<DungeonPanel>();

                DisposeInGameSystems();
            }),
            LoadingStep.Async("正在进入战场…", 0.6f, SwitchSceneRoutine(SceneNames.Battle, GameState.Running, reloadScene)),
            // 战场场景就绪后预热对象池（怪物/掉落物）：池对象属于场景，必须在切场景后预热，否则会被场景卸载销毁
            // 正式刷怪/掉落时直接复用池内对象，避免开局 Instantiate 掉帧
            LoadingStep.Async("正在预加载战场对象…", 0.2f, PoolPrewarmer.RunBattlePrewarm()));
    }

    /// <summary>主菜单"继续"：有断点则标记续玩并进入战斗，无断点回主菜单</summary>
    private void StartBattleFromCheckpoint()
    {
        if (!RunSaveManager.HasSave())
        {
            LogKit.Warning("[GameManager] 无可恢复的对局断点");
            StartCoroutine(ReturnToMainMenuRoutine());
            return;
        }
        mResumeRun = true;
        StartCoroutine(StartBattleRoutine(false));
    }

    /// <summary>消费续玩标记（GameRunningState 判断是否恢复断点）</summary>
    public bool ConsumeResumeFlag()
    {
        bool v = mResumeRun;
        mResumeRun = false;
        return v;
    }

    // ===== 断点自动保存（退出/切后台时） =====

    private void OnApplicationPause(bool paused)
    {
        if (paused) TryAutoSaveRun();
    }

    private void OnApplicationQuit()
    {
        TryAutoSaveRun();
    }

    /// <summary>对局进行中（Running 且玩家存在）才保存断点</summary>
    private void TryAutoSaveRun()
    {
        if (mFsm != null && mFsm.CurEnum == GameState.Running && Player != null)
        {
            if (RunSaveManager.CaptureCheckpoint())
                LogKit.Log("[GameManager] 对局断点已自动保存（可主菜单继续）");
        }
    }

    /// <summary>返回主菜单：清理局内系统 → 关闭流程面板 → 异步加载 MainMenu 场景 → 进入 Menu（加载页过渡）</summary>
    private IEnumerator ReturnToMainMenuRoutine()
    {
        yield return LoadingScreen.Run(0.5f,
            LoadingStep.Sync("正在整理厨房…", 0.25f, () =>
            {
                // 关闭主菜单流程面板（战前准备/背包/商店），避免跨场景残留
                UIKit.ClosePanel<PreparationPanel>();
                UIKit.ClosePanel<InventoryPanel>();
                UIKit.ClosePanel<ShopPanel>();
                UIKit.ClosePanel<DungeonPanel>();

                DisposeInGameSystems();
            }),
            LoadingStep.Async("正在返回主菜单…", 0.75f, SwitchSceneRoutine(SceneNames.MainMenu, GameState.Menu)));
    }

    /// <summary>
    /// 初始化 UIRoot：
    /// 1) 设置 CanvasScaler（Scale With Screen Size 1920x1080 Match=1）
    /// 2) 替换面板预制体加载路径为 Prefabs/UI
    /// </summary>
    private void SetupUIRoot()
    {
        // 强制创建 UIRoot（会自动创建 Canvas + CanvasScaler）
        var root = UIRoot.Instance;
        var canvas = root.Canvas;
        if (canvas == null)
        {
            LogKit.Warning("[GameManager] UIRoot.Canvas 为空，跳过初始化");
            return;
        }

        // 设置 CanvasScaler
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // 1 = 完全匹配高度
            LogKit.Log("[GameManager] UIRoot CanvasScaler 已设置: ScaleWithScreenSize 1920x1080 Match=1");
        }

        // 替换面板预制体加载路径为 Prefabs/UI
        SetupPanelPathPrefix(root, UI_PANEL_PATH_PREFIX);
    }

    /// <summary>
    /// 通过反射替换 UIKitController 的 PanelLoader，使面板预制体从指定路径加载。
    /// UIRoot.PrefabPathPrefix 和 UIKitController.SetLoader 均为 internal，需反射访问。
    /// </summary>
    private void SetupPanelPathPrefix(UIRoot root, string pathPrefix)
    {
        // 1. 反射获取 UIRoot.Controller（internal 属性）
        var controllerProp = typeof(UIRoot).GetProperty(
            "Controller", BindingFlags.Instance | BindingFlags.NonPublic);
        if (controllerProp == null)
        {
            LogKit.Warning("[GameManager] 找不到 UIRoot.Controller 属性，路径设置失败");
            return;
        }

        var controller = controllerProp.GetValue(root);
        if (controller == null)
        {
            LogKit.Warning("[GameManager] UIRoot.Controller 为空，路径设置失败");
            return;
        }

        // 2. 反射调用 UIKitController.SetLoader(IPanelLoader)（internal 方法）
        var setLoaderMethod = controller.GetType().GetMethod(
            "SetLoader", BindingFlags.Instance | BindingFlags.NonPublic);
        if (setLoaderMethod == null)
        {
            LogKit.Warning("[GameManager] 找不到 SetLoader 方法，路径设置失败");
            return;
        }

        // 3. 创建新的 ResKitPanelLoader，路径前缀为 Prefabs/UI
        var newLoader = new ResKitPanelLoader(pathPrefix, false);
        setLoaderMethod.Invoke(controller, new object[] { newLoader });

        LogKit.Log($"[GameManager] UI 面板加载路径已设置为: Resources/{pathPrefix}/{{面板名}}.prefab");
    }

    /// <summary>由 PlayerController 在场景中注册自身</summary>
    public void RegisterPlayer(PlayerController player)
    {
        Player = player;
    }

    /// <summary>清空玩家引用（场景切换/重开时调用，避免访问已销毁对象）</summary>
    public void ClearPlayer()
    {
        Player = null;
    }

    /// <summary>游戏结束（玩家死亡时由事件触发）</summary>
    public void GameOver()
    {
        mFsm.Change(GameState.GameOver);
    }

    /// <summary>记录本局数据并保存局外存档（Meta）；同时清除对局断点（本局已结束）</summary>
    public void RecordRunAndSave(bool victory)
    {
        // 一局正常结束（胜利/失败）→ 清除断点，"继续"按钮不再可用
        RunSaveManager.Clear();

        // 成就：一局结束（GamesPlayed +1）
        if (AchievementManager.HasInstance) AchievementManager.Instance.OnRunEnded();

        if (!MetaManager.HasInstance) return;

        int kills = DropManager.HasInstance ? DropManager.Instance.KillCount : 0;
        int wave = WaveManager.HasInstance ? WaveManager.Instance.DungeonWaveOrdinal : 1;
        MetaManager.Instance.RecordRunEnd(victory, wave, kills);

        // Step10：本局未消耗食材返还局外食材背包（炒菜消耗后再返还剩余）
        if (DropManager.HasInstance)
        {
            MetaManager.Instance.RefundUnusedIngredients(DropManager.Instance.IngredientInventory);
        }

        MetaManager.Instance.Save();
    }

    /// <summary>
    /// 销毁局内玩法系统（生命周期：进入游戏时由 GameRunningState 创建，离开游戏时销毁）
    /// 局外常驻系统（MetaManager/AudioManager/RosterManager）不在此销毁。
    /// 注意用 Dispose() 而非 Instance.xxx()：避免在主菜单阶段触发单例创建。
    /// </summary>
    public void DisposeInGameSystems()
    {
        // 局内玩法系统：销毁实例（无实例时 Dispose 为 no-op）
        WaveManager.Dispose();
        DropManager.Dispose();
        LevelUpManager.Dispose();
        SkillSystem.Dispose();
        ActiveSkillSystem.Dispose();
        UpgradeBuffSystem.Dispose();
        BossBattleManager.Dispose();
        RhythmEngine.Dispose();
        CombatFeedbackFactory.Dispose();
        // 升级粒子特效（随战斗结束销毁）
        LevelUpParticleEffect.Dispose();
        // 战场炒菜（随机事件/判定/效果）
        FieldCookingDirector.Dispose();
        FieldCookingEngine.Dispose();
        CookingEffectFactory.Dispose();
        // 副本事件槽调度
        DungeonEventDirector.Dispose();
        // 战斗数据统计（阶段/整局结算）
        BattleStatsCollector.Dispose();

        // 清空空间网格（敌人已随池回收 OnDisable 注销，此处为防御性清理）
        SpatialGrid.Clear();

        // 局外常驻系统状态复位（不销毁）
        RosterManager.Instance.Reset();

        // 清空对象池（敌人、掉落物、子弹等）
        PoolFactory.ClearAll();

        LogKit.Log("[GameManager] 局内玩法系统已销毁，回到局外状态");
    }

    private void Update()
    {
        mFsm.Update();
    }

    /// <summary>游戏结束时不销毁（跨场景保持）</summary>
    private void OnDestroy()
    {
        mFsm?.Dispose();
    }
}

/// <summary>游戏总状态枚举</summary>
public enum GameState
{
    Menu,      // 主菜单
    Running,   // 正常游玩
    GameOver   // 游戏结束（玩家死亡，进入阶段结算）
}

/// <summary>游戏状态机共享黑板</summary>
public class GameBlack
{
    public GameManager Manager;
}

/// <summary>主菜单状态：打开主菜单面板，等待玩家点击开始</summary>
public class GameMenuState : AbstractState<GameState, GameBlack>
{
    public GameMenuState(FSM<GameState> fsm, GameBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        LogKit.Log("[GameState] Menu");
        Time.timeScale = 1f;

        // 进入主菜单 = 上一局结束，销毁局内玩法系统、清除残留（不触发创建，生命周期严格）
        mBlack.Manager.DisposeInGameSystems();

        // 关闭战斗相关面板（防御：跨场景残留）
        UIKit.ClosePanel<HudPanel>();
        UIKit.ClosePanel<LevelUpPanel>();
        UIKit.ClosePanel<CookingPanel>();
        UIKit.ClosePanel<FieldCookingPanel>();
        UIKit.ClosePanel<StageResultPanel>();
        UIKit.ClosePanel<RunResultPanel>();
        // 关闭主菜单流程面板，避免遮挡主菜单
        UIKit.ClosePanel<PreparationPanel>();
        UIKit.ClosePanel<InventoryPanel>();
        UIKit.ClosePanel<ShopPanel>();
        UIKit.ClosePanel<DungeonPanel>();

        // 切换主菜单 BGM（MainMenuBgm1）
        if (AudioManager.HasInstance)
        {
            AudioManager.Instance.PlayMenuBgm();
        }

        UIKit.OpenPanel<MainMenuPanel>(UILevel.Common);
    }

    protected override void OnExit()
    {
        UIKit.ClosePanel<MainMenuPanel>();
    }
}

/// <summary>运行中状态：启动波次 + 打开 HUD</summary>
public class GameRunningState : AbstractState<GameState, GameBlack>
{
    public GameRunningState(FSM<GameState> fsm, GameBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        LogKit.Log("[GameState] Running");
        Time.timeScale = 1f;

        // 本局计时基准（断点恢复时会被回退为"该波开局的已用时长"）
        mBlack.Manager.MarkRunStart(Time.time);

        // 0. 先清空局内加成状态
        //    SkillSystem 是 DamageBonus/AttackSpeedBonus 的唯一写入方，必须"先清空再装配"，
        //    否则会把装配（成品菜星级效果）写入的追加加成一并清掉
        SkillSystem.Instance.Reset();
        UpgradeBuffSystem.Instance.Reset();

        // 1. 先应用战前配置 + 局外永久Buff（玩家属性就绪：供波次开局快照捕获正确属性）
        RosterManager.Instance.ApplyLoadoutToPlayer();
        PermanentBuffApplier.ApplyPermanentBuffToPlayer();

        // 2. 创建局内玩法系统（生命周期：进入游戏创建，离开游戏由 DisposeInGameSystems 销毁）
        DropManager.Instance.Init();
        LevelUpManager.Instance.Init();
        // BOSS 战协调器：确保单例创建并注册 BossHpDepleted/CookingFinished 事件
        var _ = BossBattleManager.Instance;

        // P1：战斗表现工厂（飘字/震动/闪白）
        CombatFeedbackFactory.Instance.Init();

        // 升级粒子特效（表现层：订阅 PlayerLevelUp，从天而降的彩色粒子）
        LevelUpParticleEffect.EnsureCreated();

        // 战场炒菜触发调度（随机事件，需在 Running 期间存活）
        var _fc = FieldCookingDirector.Instance;

        // 副本事件槽调度（订阅 WaveStarted，按副本配置掷骰触发随机事件）
        var _se = DungeonEventDirector.Instance;

        // 副本选择：断点续玩优先用断点记录的副本，否则用存档里选中的副本
        bool resuming = mBlack.Manager.ConsumeResumeFlag() && RunSaveManager.HasSave();
        RunSaveData checkpoint = resuming ? RunSaveManager.Load() : null;
        if (resuming && !RunSaveManager.IsCompatible(checkpoint))
        {
            // 旧版本/坏档：直接丢弃并按新局开打，避免按错误语义恢复出一个错乱战场
            RunSaveManager.Clear();
            checkpoint = null;
            resuming = false;
        }

        string dungeonId = checkpoint?.dungeonId;
        if (string.IsNullOrEmpty(dungeonId)) dungeonId = MetaManager.Instance.SelectedDungeonId;

        // 应用副本（波次序列 + 事件槽）；未配置副本时退回全局波次表（兼容旧行为）
        var dungeon = GameDataMgr.Instance.GetDungeon(dungeonId) ?? GameDataMgr.Instance.CurrentDungeon;
        WaveManager.Instance.ApplyDungeon(dungeon);
        DungeonEventDirector.Instance.ApplyDungeon(dungeon);

        // 副本背景：Arena 按副本配置的 BackGround 字段切换背景图（Start 里已自行兜底解析，这里做权威同步）
        if (ArenaBackground.Current != null) ArenaBackground.Current.ApplyDungeonBackground(dungeon);

        // 战斗数据统计（阶段/整局结算数据源）+ 开始第一阶段的统计
        var _stats = BattleStatsCollector.Instance;
        _stats.BeginStage();

        // 3. 启动波次 / 断点续玩
        //    续玩时**不先 StartWaves**：由 RestoreCheckpoint 按存档状态（Prep 倒计时/刷怪游标/清场/BOSS）
        //    直接接管，避免先起第 1 波又被打断（旧实现会因此产生"空刷"与进度错位）。
        if (resuming && checkpoint != null)
        {
            RunSaveManager.RestoreCheckpoint(checkpoint);
        }
        else
        {
            WaveManager.Instance.StartWaves();
        }

        // 关闭主菜单相关面板（跨场景残留清理）
        UIKit.ClosePanel<MainMenuPanel>();
        UIKit.ClosePanel<PreparationPanel>();
        UIKit.ClosePanel<InventoryPanel>();
        UIKit.ClosePanel<ShopPanel>();
        UIKit.ClosePanel<DungeonPanel>();

        // 切换战斗 BGM（BattleBgm1 / BattleBgm2 随机）
        if (AudioManager.HasInstance)
        {
            AudioManager.Instance.PlayBattleBgm();
        }

        // 打开 HUD 面板 + 画中画炒菜小窗面板（面板预制体缺失时降级：仅缺小窗 UI，判定逻辑仍正常）
        UIKit.OpenPanel<HudPanel>(UILevel.Hud);
        GameManager.TryOpenPanel<FieldCookingPanel>(UILevel.Pop);

        // 断点续玩：HUD 就绪后再补弹需要 UI 的恢复项（音游进行中 / 未选完的 3 选 1）
        if (resuming) RunSaveManager.ResumeDeferredPanels();
    }
}

/// <summary>把局外永久Buff应用到玩家（血量上限乘算到基础属性 + 触发SkillSystem重算伤害/攻速）</summary>
public static class PermanentBuffApplier
{
    public static void ApplyPermanentBuffToPlayer()
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        var meta = MetaManager.Instance.BuffSystem;
        if (meta.MaxHpBonusPercent > 0f)
        {
            var baseAttr = player.BaseAttr;
            baseAttr.MaxHp = baseAttr.MaxHp * (1f + meta.MaxHpBonusPercent);
            player.ApplyBaseAttribute(baseAttr);
        }

        // 触发 SkillSystem 重算：把永久 Damage/AttackSpeed 加成写入玩家
        if (SkillSystem.HasInstance)
        {
            SkillSystem.Instance.RefreshPlayerStats();
        }
    }
}

/// <summary>
/// 游戏结束状态：暂停 + 记录本局数据 + 打开 GameOverPanel（整局统计 + 重新开始/返回主菜单）
/// </summary>
public class GameOverState : AbstractState<GameState, GameBlack>
{
    public GameOverState(FSM<GameState> fsm, GameBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        LogKit.Log("[GameState] GameOver");
        Time.timeScale = 0f; // 暂停游戏

        // 记录本局数据 + 保存局外存档（Meta，失败局）
        mBlack.Manager.RecordRunAndSave(false);

        // 关闭 HUD 和升级面板
        UIKit.ClosePanel<HudPanel>();
        UIKit.ClosePanel<LevelUpPanel>();
        UIKit.ClosePanel<FieldCookingPanel>();

        // 打开死亡结算页（GameOverPanel：整局统计 + 重新开始/返回主菜单；预制体缺失则直接回主菜单）
        mBlack.Manager.ShowDeathResult();
    }

    protected override void OnExit()
    {
        UIKit.ClosePanel<GameOverPanel>();
        UIKit.ClosePanel<StageResultPanel>();
        UIKit.ClosePanel<RunResultPanel>();
    }
}
