using UnityEngine;

/// <summary>
/// 全局事件消息定义（模块间解耦的唯一通道）
/// 所有跨模块通信都走 EventKit.Type.Send<T>() / Register<T>()，禁止直接引用对方脚本
/// </summary>
namespace GameEvents
{
    // ===== 玩家相关 =====

    /// <summary>玩家受到伤害</summary>
    public struct PlayerDamaged { public float Damage; public float CurrentHp; public float MaxHp; }

    /// <summary>玩家死亡</summary>
    public struct PlayerDied { }

    /// <summary>玩家经验变化</summary>
    public struct PlayerExpChanged { public int Exp; public int ExpToNext; }

    /// <summary>玩家升级</summary>
    public struct PlayerLevelUp { public int NewLevel; }

    // ===== 攻击 / 战斗相关 =====

    /// <summary>子弹命中敌人（用于飘字/音效/击杀统计）</summary>
    public struct BulletHit { public Vector3 HitPoint; public float Damage; }

    /// <summary>玩家对目标造成伤害（表现层工厂订阅：飘字/受击闪白/震动）</summary>
    public struct DamageDealt
    {
        public Vector3 Position;
        public float Damage;
        public bool IsCrit;
        public GameObject Target; // 受击目标（用于闪白），可为空
        public bool FromEffectProjectile; // 是否来自效果弹（升级Buff/技能效果发射的弩箭等）：效果弹命中不再触发 OnHit 类效果，防止"弹触发弹"无限连锁
        public DamageType DamageType; // 物理/元素（飘字配色区分用，默认 Physical）
    }

    /// <summary>敌人被击杀（用于掉落、计数、死亡喷血表现；HitDirection=致死一击的受击方向）</summary>
    public struct EnemyKilled { public string EnemyId; public Vector3 Position; public Vector2 HitDirection; }

    /// <summary>特殊效果触发（爆炸/燃烧，供特效/音效订阅）</summary>
    public struct StatusEffectTriggered { public SkillEffectType EffectType; public Vector3 Position; public float Radius; }

    /// <summary>敌人波次相关（Step3 使用）</summary>
    public struct WaveStarted { public int WaveId; }

    /// <summary>波次清空</summary>
    public struct WaveCleared { public int WaveId; }

    /// <summary>连杀计数变化（DropManager 业务层统计，HUD 显示连杀数与计时条）</summary>
    public struct StreakChanged { public int Count; public float RemainTime; public float Duration; }

    /// <summary>武器槽装配变化（AutoAttack 上报：3 槽同时生效，无切换）</summary>
    public struct WeaponLoadoutChanged { }

    // ===== 资源 / 掉落 =====

    /// <summary>食材掉落（Step3 使用）</summary>
    public struct IngredientDropped { public string IngredientId; public Vector3 Position; }

    // ===== UI / 流程控制 =====

    /// <summary>主菜单点击开始游戏</summary>
    public struct GameStart { }

    /// <summary>主菜单点击"继续"（断点续玩）</summary>
    public struct ContinueRun { }

    /// <summary>GameOverPanel 点击"重新开始本局"（GameManager 强制重载 Battle 场景重开）</summary>
    public struct RestartRun { }

    /// <summary>主菜单点击返回主菜单</summary>
    public struct BackToMenu { }

    // ===== BOSS 战相关（Step5） =====

    /// <summary>BOSS 生成</summary>
    public struct BossSpawned { public string BossId; public float MaxHp; }

    /// <summary>BOSS 阶段切换（血量阈值触发）</summary>
    public struct BossPhaseChanged { public int PhaseIndex; public AttackType WeaknessType; }

    /// <summary>BOSS 破韧条满（进入虚弱状态，奖励状态：伤害加成）</summary>
    public struct BossBreakBarFull { public string BossId; }

    /// <summary>BOSS 血量/破韧条变化（事件驱动 HUD 血条，替代每帧轮询）</summary>
    public struct BossHealthChanged
    {
        public string BossId;
        public float CurrentHp;
        public float MaxHp;
        public float HpRatio;       // 0~1
        public float BreakBarRatio; // 0~1
    }

    /// <summary>BOSS 实际受到伤害（含弱点/虚弱倍率后的最终值，飘字显示用）</summary>
    public struct BossDamageApplied
    {
        public Vector3 Position;
        public float Damage;
        public bool IsCrit;          // 本次是否真的暴击（由伤害公式判定，不由伤害数值反推）
        public bool IsWeaknessHit;   // 是否打中当前阶段弱点（攻击属性匹配）
        public DamageType DamageType; // 物理/元素（飘字配色区分用）
    }

    /// <summary>BOSS 血量清空（触发烹饪音游阶段，不再依赖破韧/虚弱）</summary>
    public struct BossHpDepleted { public string BossId; }

    /// <summary>BOSS 死亡（烹饪音游胜利后）</summary>
    public struct BossDied { public string BossId; public Vector3 Position; }

    /// <summary>BOSS 复活（烹饪音游失败后）</summary>
    public struct BossRevived { public string BossId; public float CurrentHp; public float MaxHp; }

    // ===== 烹饪音游相关（Step5） =====

    /// <summary>烹饪音游阶段开始</summary>
    public struct CookingStageStarted { public CookingStage Stage; public int StageIndex; }

    /// <summary>单个音符判定结果（TrackIndex 供 UI 定位轨道特效）</summary>
    public struct CookingNoteJudged { public string Judgment; public int Combo; public int TrackIndex; }

    /// <summary>烹饪音游结束（返回品质档位与生死判定，由 BossBattleManager 决定 BOSS 生死；Grade="S"/"A"/"B"/"C"，null=无音符失败）</summary>
    public struct CookingFinished { public RecipeQuality Quality; public string Grade; public int MaxCombo; }

    // ===== 主动技能相关（Step10） =====

    /// <summary>HUD 技能按钮点击上报（ActiveSkillSystem 订阅执行）</summary>
    public struct ActiveSkillRequested { public string SkillId; }

    /// <summary>主动技能释放成功（音效/特效订阅）</summary>
    public struct ActiveSkillReleased { public string SkillId; }

    /// <summary>主动技能冷却变化（HUD 刷新按钮遮罩）</summary>
    public struct ActiveSkillCooldownChanged { public string SkillId; public float Remain; public float Total; }

    /// <summary>武器主动技能释放请求（HUD 点击武器槽按钮；WeaponIndex = 武器槽序号，WeaponActiveSystem 订阅执行）</summary>
    public struct WeaponActiveRequested { public int WeaponIndex; }

    /// <summary>
    /// 武器主动「自动释放」开关变更（设置面板-游戏页签 → WeaponActiveSystem）。
    /// 按**武器技能槽序号**定位（0~2 = 界面上的技能槽 1~3，与 HUD 武器槽同序），
    /// 跨局持久化在 SettingsSaveData.autoCastOffSlots
    /// </summary>
    public struct WeaponAutoCastChanged { public int SlotIndex; public bool Enabled; }

    /// <summary>玩家选择了某个升级 Buff 项（LevelUpPanel 上报，UpgradeBuffSystem 生效，Step10）</summary>
    public struct UpgradeSelected { public string BuffId; }

    /// <summary>玩家施放闪避（翻滚）技能（DodgeSkill 上报，效果引擎 OnDodge 触发订阅）</summary>
    public struct DodgePerformed { public Vector2 Direction; }

    // ===== 画中画炒菜（Field Cooking：随机事件 + 画中画小窗颠勺） =====

    /// <summary>画中画炒菜触发（随机事件，播放提示音/弹出画中画小窗）</summary>
    public struct FieldCookingTriggered { public string RecipeId; public string RecipeName; public int HintCount; }

    /// <summary>颠勺提示出现（UI 高亮食材/锅铲+箭头，WindowSeconds 内点击/按键判定）</summary>
    public struct FieldCookingHintShown { public int HintIndex; public int HintTotal; public float WindowSeconds; }

    /// <summary>单次颠勺判定结果（"Hit"=成功/"Miss"=漏点，UI 显示判定飘字与 combo）</summary>
    public struct FieldCookingHintJudged { public string Judgment; public int Combo; }

    /// <summary>
    /// 画中画步骤切换（步骤模式专用，UI 播放步骤入场动画）
    /// Anim：FlyIn（锅飞入）/ Pour（油水倒入）/ Drop（食材落下）/ Toss（颠锅）/ Sprinkle（调味撒入）
    /// </summary>
    public struct FieldCookingStepChanged
    {
        public string StepId; public string StepName; public string Icon; public string Anim;
        public string FallbackColor; public int StepIndex; public int StepCount;
        public string FxIcon; // 特效图 Resources 路径（Pour/Drop/Sprinkle 用，空=主图或纯色兜底）
    }

    /// <summary>
    /// 画中画炒菜结束（效果/入包已处理，UI 清理）
    /// Quality："S"/"A"/"B"/"C"（命中率四档）/"炒糊"/"不炒"；DishStar=入局外背包星级（0=未入包）
    /// </summary>
    public struct FieldCookingFinished { public string RecipeId; public string Quality; public float Multiplier; public int DishStar; }

    // ===== 阶段/整局结算（每 BOSS 为一个阶段） =====

    /// <summary>BOSS 战胜利（成功烹饪后）：记录本阶段奖励并进入阶段结算（IsFinal=最终BOSS）</summary>
    public struct BossStageCleared { public bool IsFinal; }

    /// <summary>阶段结算页"继续"确认（IsFinal=最终BOSS，下一步为整局结算）</summary>
    public struct StageResultConfirmed { public bool IsFinal; }

    /// <summary>整局结算页"继续"确认（最终BOSS通关后，返回主菜单）</summary>
    public struct RunResultConfirmed { }

    // ===== 加载过渡（启动 / 场景切换） =====

    /// <summary>加载进度变化（LoadingScreen 业务层上报，LoadingPanel 显示层消费；Progress 0~1）</summary>
    public struct LoadingProgress { public float Progress; public string Tip; }

    // ===== 元游戏（成就 / 图鉴） =====

    /// <summary>获得金币（MetaManager.AddCoins 上报，成就累计）</summary>
    public struct MetaCoinEarned { public int Amount; }

    /// <summary>局外钱币变化（MetaManager 上报；Total=变化后总数，永久升级面板刷新余额）</summary>
    public struct MetaCoinsChanged { public int Total; }

    /// <summary>局外永久升级成功（MetaManager.TryUpgrade 上报；Id=升级项，Level=升级后等级）</summary>
    public struct MetaUpgradePurchased { public string Id; public int Level; }

    /// <summary>Boss 战后烹饪完成（BossBattleManager 上报，图鉴/成就记录）</summary>
    public struct BossCookingRecorded { public string RecipeId; public RecipeQuality Quality; public int MaxCombo; }

    /// <summary>成品菜谱解锁（MetaManager.UnlockRecipe 上报，图鉴标记 + 成就收集条件）</summary>
    public struct RecipeUnlocked { public string RecipeId; }
}
