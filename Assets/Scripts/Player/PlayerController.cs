using UnityEngine;
using YokiFrame;

/// <summary>
/// 玩家控制器：读配置驱动移动，用 FSM 管理玩家状态，不承担任何显示逻辑。
/// 输入为鼠标右键移动与 WASD / 方向键。
/// </summary>
public class PlayerController : MonoBehaviour
{
    // ===== 状态机 =====
    private FSM<PlayerState> mFsm;
    private PlayerBlack mBlack;

    // ===== 运行时数据（数据驱动） =====
    private Rigidbody2D mRb;
    private Vector2 mMoveDir;

    // 角色身体渲染组件（用于按精灵实际尺寸做移动边界内缩，避免身体越出地图）
    private SpriteRenderer m_renderer;

    // 角色序列帧表现（按角色ID切换）
    private SequenceFrameAnimator mVisual;

    // 是否面向右（true = 面向右）。资源序列图默认朝左，"翻转精灵"即面向右；默认初始面向右
    private bool m_isFacingRight = true;

    // 判定水平移动方向的最小阈值（接近 0 视为无水平输入，保持当前朝向）
    private const float k_facingDeadZone = 0.01f;

    // 判定「正在移动」的输入阈值（低于该值视为静止，播放待机动画）
    private const float k_moveInputDeadZone = 0.0001f;

    // ===== 战斗属性（Step10 属性系统） =====
    /// <summary>基础属性（角色基础 + 战前装配菜品），战前确定</summary>
    private PlayerAttribute mBaseAttr;

    /// <summary>局内动态Buff加成（升级/烹饪/主动技能），战斗期间叠加</summary>
    private PlayerAttribute mBonusAttr;

    /// <summary>基础属性（只读，供属性聚合查询）</summary>
    public PlayerAttribute BaseAttr => mBaseAttr;

    /// <summary>局内动态加成（只读）</summary>
    public PlayerAttribute BonusAttr => mBonusAttr;

    /// <summary>最终战斗属性（基础 + 动态Buff；Attack 兼容旧 DamageBonus 绝对加成）</summary>
    public float MaxHp => mBaseAttr.MaxHp + mBonusAttr.MaxHp;
    public float Attack => mBaseAttr.Attack + mBonusAttr.Attack + DamageBonus;
    public float PhysDef => mBaseAttr.PhysDef + mBonusAttr.PhysDef;
    public float ElemDef => mBaseAttr.ElemDef + mBonusAttr.ElemDef;
    public float DodgeChance => mBaseAttr.DodgeChance + mBonusAttr.DodgeChance;
    public float CritChance => mBaseAttr.CritChance + mBonusAttr.CritChance;
    public float CritDamage => mBaseAttr.CritDamage + mBonusAttr.CritDamage;
    public float PhysDamageBonus => mBaseAttr.PhysDamageBonus + mBonusAttr.PhysDamageBonus;
    public float ElemDamageBonus => mBaseAttr.ElemDamageBonus + mBonusAttr.ElemDamageBonus;
    public float MoveSpeed => mBaseAttr.MoveSpeed + mBonusAttr.MoveSpeed;

    /// <summary>
    /// 基础攻击速度（次/秒）= 角色基础 + 武器/局外永久升级的平坦加值。
    /// 注意：局内攻速增益（升级卡/烹饪/限时Buff）**不走这条通道**，而是全部写进 AttackSpeedBonus 倍率。
    /// 因此直接显示本值会永远停在角色基础值（2.0），UI 请改用 <see cref="FinalAttackSpeed"/>。
    /// </summary>
    public float AttackSpeed => mBaseAttr.AttackSpeed + mBonusAttr.AttackSpeed;

    /// <summary>当前攻击力（旧字段，兼容 SkillSystem 绝对加成；Task5 后由 BonusAttr 替代）</summary>
    public float DamageBonus { get; set; } = 0f;

    /// <summary>
    /// 攻速加成倍率（唯一写入方 = SkillSystem.RecalculatePlayerStats；0.45 = +45%，可负）。
    /// 实际攻速只在 AutoAttack / BossSpawner 消费，UI 展示请用 <see cref="FinalAttackSpeed"/>。
    /// </summary>
    public float AttackSpeedBonus { get; set; } = 0f;

    /// <summary>
    /// 最终攻击速度（次/秒）= 基础攻速 × (1 + 攻速加成)。
    /// 与 AutoAttack.cs / BossSpawner.cs 的实际消费公式完全一致（未含武器自身 attackSpeedScale，那是每把武器各自的差异）。
    /// </summary>
    public float FinalAttackSpeed => AttackSpeed * (1f + AttackSpeedBonus);

    /// <summary>玩家是否存活</summary>
    public bool IsAlive => mFsm != null && mFsm.CurEnum == PlayerState.Alive;

    /// <summary>血量（通用 Health 组件管理）</summary>
    public Health Health { get; private set; }

    // ===== 法力（技能资源，由角色+武器初始值决定；PlayerEffectController 管理增减） =====
    private float mMaxMana;
    private float mCurrentMana;
    private float mManaRegen;

    public float MaxMana => mMaxMana;
    public float CurrentMana => mCurrentMana;

    /// <summary>初始化法力（以角色基础属性为准 + 战前武器额外法力；在 ApplyBaseAttribute 之后调用）</summary>
    public void InitMana()
    {
        mMaxMana = mBaseAttr.MaxMana;
        mManaRegen = mBaseAttr.ManaRegen;

        // 战前武器提供的额外法力（RosterManager 槽位）
        if (RosterManager.HasInstance)
        {
            foreach (var wid in RosterManager.Instance.GetCurrentWeaponIds())
            {
                var w = GameDataMgr.Instance.GetWeapon(wid);
                if (w == null) continue;
                mMaxMana += w.maxMana;
                mManaRegen += w.manaRegen;
            }
        }

        mCurrentMana = mMaxMana;
        LogKit.Log($"[PlayerController] 法力初始化: {mCurrentMana:F0}/{mMaxMana:F0}");
    }

    /// <summary>增加最大法力（增益/限时）</summary>
    public void AddMaxMana(float amount) { mMaxMana = Mathf.Max(0f, mMaxMana + amount); }

    /// <summary>消耗法力（超出则消耗失败，返回是否足够）</summary>
    public bool SpendMana(float amount)
    {
        if (amount <= 0f) return true;
        if (mCurrentMana < amount) return false;
        mCurrentMana -= amount;
        return true;
    }

    /// <summary>回复法力（不超上限）</summary>
    public void RestoreMana(float amount)
    {
        if (amount <= 0f) return;
        mCurrentMana = Mathf.Min(mMaxMana, mCurrentMana + amount);
    }

    /// <summary>直接设置当前法力（断点恢复用，夹取到 0~上限）</summary>
    public void SetMana(float value)
    {
        mCurrentMana = Mathf.Clamp(value, 0f, mMaxMana);
    }

    /// <summary>法力逐帧回复</summary>
    private void UpdateManaRegen()
    {
        if (mManaRegen > 0f && mCurrentMana < mMaxMana)
            mCurrentMana = Mathf.Min(mMaxMana, mCurrentMana + mManaRegen * Time.deltaTime);
    }

    private void Awake()
    {
        mRb = GetComponent<Rigidbody2D>();
        Health = GetComponent<Health>();
        m_renderer = GetComponent<SpriteRenderer>();
        mVisual = GetComponent<SequenceFrameAnimator>();

        // 注册到 GameManager（跨模块访问的唯一入口）
        GameManager.Instance.RegisterPlayer(this);
    }

    private void Start()
    {
        if (Health == null)
        {
            Health = gameObject.AddComponent<Health>();
        }

        // Step10：计算战前基础属性（角色基础 + 装配菜品）
        // 当前用默认角色兜底；Task7/8 战前准备完成后由 RosterManager 通过 ApplyLoadout 覆盖
        var defaultCharacter = GetDefaultCharacter();
        ApplyBaseAttribute(PlayerAttributeAggregator.BuildBase(defaultCharacter, null));

        // 角色序列图按角色ID选择（战前准备完成后由 RosterManager 覆盖）
        ApplyCharacterVisual(defaultCharacter?.id);

        // 应用默认朝向：资源图默认朝左，初始面向右（翻转精灵），与 m_isFacingRight 初值保持一致
        if (mVisual != null) mVisual.SetFlipX(m_isFacingRight);

        // 玩家成长数据（经验/等级）自动挂载
        if (GetComponent<PlayerStats>() == null)
        {
            gameObject.AddComponent<PlayerStats>();
        }

        // 闪避（翻滚）技能（独立主动技能）：自动挂载，确保闪避冷却缩减(3选1增益)可被效果引擎查询
        if (GetComponent<DodgeSkill>() == null)
        {
            gameObject.AddComponent<DodgeSkill>();
        }

        InitStateMachine();
    }

    /// <summary>获取默认角色（配置第一个角色；无配置返回 null 由聚合器兜底默认值）</summary>
    private static CharacterConfig GetDefaultCharacter()
    {
        var characters = GameDataMgr.Instance.Characters?.Characters;
        if (characters != null && characters.Count > 0)
            return characters[0];
        return null;
    }

    /// <summary>
    /// 应用战前基础属性（角色+装配菜品），并重置血量。
    /// 由 Start 兜底调用；Task7/8 战前准备完成后由 RosterManager/PreparationManager 调用覆盖。
    /// </summary>
    public void ApplyBaseAttribute(PlayerAttribute attr)
    {
        mBaseAttr = attr;
        if (Health != null)
        {
            Health.Init(MaxHp);
        }
        // 法力随基础属性刷新（角色提供了最大法力/回复）
        InitMana();
        LogKit.Log($"[PlayerController] 基础属性已应用: HP={MaxHp} 攻击={Attack} 移速={MoveSpeed} 攻速={AttackSpeed}");
    }

    /// <summary>按角色ID切换战斗序列图（由 RosterManager 应用战前配置时调用）</summary>
    public void ApplyCharacterVisual(string characterId)
    {
        if (mVisual == null) return;
        mVisual.SetCharacter(characterId);
    }

    /// <summary>叠加局内动态Buff加成（升级/烹饪/主动技能调用；支持负值抵消）</summary>
    public void AddBonusAttribute(PlayerAttribute bonus)
    {
        mBonusAttr = mBonusAttr + bonus;
    }

    /// <summary>移除局内动态加成（数值工厂 Modifier 重算时先移除旧值再加新值）</summary>
    public void RemoveBonusAttribute(PlayerAttribute bonus)
    {
        mBonusAttr.MaxHp -= bonus.MaxHp;
        mBonusAttr.Attack -= bonus.Attack;
        mBonusAttr.PhysDef -= bonus.PhysDef;
        mBonusAttr.ElemDef -= bonus.ElemDef;
        mBonusAttr.MoveSpeed -= bonus.MoveSpeed;
        mBonusAttr.DodgeChance -= bonus.DodgeChance;
        mBonusAttr.CritChance -= bonus.CritChance;
        mBonusAttr.CritDamage -= bonus.CritDamage;
        mBonusAttr.PhysDamageBonus -= bonus.PhysDamageBonus;
        mBonusAttr.ElemDamageBonus -= bonus.ElemDamageBonus;
    }

    /// <summary>清空局内动态加成（重开/回主菜单时调用）</summary>
    public void ClearBonusAttribute()
    {
        mBonusAttr = default;
    }

    private void Update()
    {
        // 读取输入（纯业务，不渲染）
        mMoveDir = ReadInput();
        UpdateFacing(); // 按移动方向翻转角色精灵
        UpdateAnimationState(); // 按移动/静止切换移动与待机序列帧
        mFsm.Update();
        UpdateManaRegen();
    }

    /// <summary>按输入状态切换移动 / 待机序列帧动画（死亡时视为静止，播放待机）</summary>
    private void UpdateAnimationState()
    {
        if (mVisual == null) return;

        bool isMoving = IsAlive && mMoveDir.sqrMagnitude > k_moveInputDeadZone;
        mVisual.SetMoving(isMoving);
    }

    /// <summary>根据水平移动方向翻转角色精灵（无水平分量时保持当前朝向）</summary>
    private void UpdateFacing()
    {
        if (mVisual == null) return;
        if (Mathf.Abs(mMoveDir.x) < k_facingDeadZone) return;

        // 资源序列图默认朝左：向右移动时才翻转精灵使其朝右，向左移动保持原图（朝左）
        bool isRight = mMoveDir.x > 0f;
        if (isRight == m_isFacingRight) return;

        m_isFacingRight = isRight;
        mVisual.SetFlipX(isRight);
    }

    private void FixedUpdate()
    {
        mFsm.FixedUpdate();
        ClampToArena(); // 固定场地：移动边界钳制
    }

    /// <summary>
    /// 把玩家位置钳制在 ArenaBounds 内，并按精灵半宽 / 半高内缩，保证「身体」而非中心点留在场内。
    /// 采用预测式钳制：物理步进发生在脚本回调之后，等越界再拉回会出现「先穿出再拉回」的往复位移，
    /// 叠加刚体插值后表现为贴边抖动——故用 p + vel×dt 预判，将要越界时钉住位置并清零该轴向外速度。
    /// </summary>
    private void ClampToArena()
    {
        var bounds = ArenaBounds.Current;
        if (bounds == null || mRb == null) return;

        // 身体内缩量：取精灵世界尺寸的一半（精灵未就绪时为 0，退化为仅钳制中心点）
        float marginX = 0f;
        float marginY = 0f;
        if (m_renderer != null && m_renderer.sprite != null)
        {
            Vector3 extents = m_renderer.bounds.extents;
            marginX = extents.x;
            marginY = extents.y;
        }

        float minX = bounds.MinX + marginX;
        float maxX = bounds.MaxX - marginX;
        float minY = bounds.MinY + marginY;
        float maxY = bounds.MaxY - marginY;

        Vector2 p = mRb.position;
        Vector2 vel = mRb.velocity;
        Vector2 nextPos = p;
        Vector2 nextVel = vel;
        float dt = Time.fixedDeltaTime;
        bool velChanged = false;

        // X 轴：已越界直接拉回；未越界但下一步将越界则预测式拦停在边界
        if (nextPos.x < minX)
        {
            nextPos.x = minX; if (nextVel.x < 0f) { nextVel.x = 0f; velChanged = true; }
        }
        else if (nextPos.x > maxX)
        {
            nextPos.x = maxX; if (nextVel.x > 0f) { nextVel.x = 0f; velChanged = true; }
        }
        else if (nextVel.x < 0f && nextPos.x + nextVel.x * dt < minX)
        {
            nextPos.x = minX; nextVel.x = 0f; velChanged = true;
        }
        else if (nextVel.x > 0f && nextPos.x + nextVel.x * dt > maxX)
        {
            nextPos.x = maxX; nextVel.x = 0f; velChanged = true;
        }

        // Y 轴同理
        if (nextPos.y < minY)
        {
            nextPos.y = minY; if (nextVel.y < 0f) { nextVel.y = 0f; velChanged = true; }
        }
        else if (nextPos.y > maxY)
        {
            nextPos.y = maxY; if (nextVel.y > 0f) { nextVel.y = 0f; velChanged = true; }
        }
        else if (nextVel.y < 0f && nextPos.y + nextVel.y * dt < minY)
        {
            nextPos.y = minY; nextVel.y = 0f; velChanged = true;
        }
        else if (nextVel.y > 0f && nextPos.y + nextVel.y * dt > maxY)
        {
            nextPos.y = maxY; nextVel.y = 0f; velChanged = true;
        }

        // 只在数值真正变化时才写回，避免无谓地写 position 干扰刚体插值
        if (velChanged) mRb.velocity = nextVel;
        if (nextPos != p) mRb.position = nextPos;
    }

    /// <summary>
    /// 初始化玩家 FSM 状态机
    /// Alive: 可移动、可被攻击
    /// Dead: 死亡，广播事件
    /// </summary>
    private void InitStateMachine()
    {
        mFsm = new FSM<PlayerState>("Player");
        mBlack = new PlayerBlack { Controller = this };

        mFsm.Add(PlayerState.Alive, new PlayerAliveState(mFsm, mBlack));
        mFsm.Add(PlayerState.Dead, new PlayerDeadState(mFsm, mBlack));
        mFsm.Start(PlayerState.Alive);

        // 监听死亡事件（EventKit 解耦：Health 只管扣血，这里决定游戏状态）
        EventKit.Type.Register<GameEvents.PlayerDied>(OnPlayerDied).UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    private void OnPlayerDied(GameEvents.PlayerDied evt)
    {
        if (mFsm.CurEnum == PlayerState.Alive)
        {
            mFsm.Change(PlayerState.Dead);
        }
    }

    /// <summary>读取输入（WASD / 方向键 / 右键移动）</summary>
    private Vector2 ReadInput()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        // 鼠标右键：向鼠标位置移动
        if (Input.GetMouseButton(1))
        {
            Vector2 dir = (Camera.main.ScreenToWorldPoint(Input.mousePosition) - transform.position);
            if (dir.sqrMagnitude > 0.1f)
                return dir.normalized;
        }
        return new Vector2(h, v).normalized;
    }

    /// <summary>由状态机调用的移动逻辑（Alive 状态），移速取聚合属性</summary>
    public void HandleMove()
    {
        if (mRb != null)
        {
            mRb.velocity = mMoveDir * MoveSpeed;
        }
    }

    /// <summary>停止移动（Dead 状态）</summary>
    public void StopMove()
    {
        if (mRb != null)
        {
            mRb.velocity = Vector2.zero;
        }
    }

    /// <summary>对外暴露当前朝向（自动攻击用）</summary>
    public Vector2 GetFacingDirection()
    {
        return mMoveDir == Vector2.zero ? Vector2.right : mMoveDir;
    }

    /// <summary>依据最大生命重新初始化血量（增益改变 MaxHp 后调用），并刷新法力上限到新配置</summary>
    public void RefreshToMaxHp()
    {
        if (Health != null) Health.Init(MaxHp);
        // 同步 HUD
        EventKit.Type.Send(new GameEvents.PlayerDamaged
        {
            Damage = 0f,
            CurrentHp = Health != null ? Health.CurrentHp : MaxHp,
            MaxHp = MaxHp
        });
    }
}

/// <summary>玩家状态枚举</summary>
public enum PlayerState
{
    Alive,   // 存活（可移动）
    Dead     // 死亡
}

/// <summary>玩家状态机共享黑板</summary>
public class PlayerBlack
{
    public PlayerController Controller;
}

/// <summary>存活状态：处理移动</summary>
public class PlayerAliveState : AbstractState<PlayerState, PlayerBlack>
{
    public PlayerAliveState(FSM<PlayerState> fsm, PlayerBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        LogKit.Log("[PlayerState] Alive");
    }

    protected override void OnUpdate()
    {
        mBlack.Controller.HandleMove();
    }
}

/// <summary>死亡状态：停止移动</summary>
public class PlayerDeadState : AbstractState<PlayerState, PlayerBlack>
{
    public PlayerDeadState(FSM<PlayerState> fsm, PlayerBlack black) : base(fsm, black) { }

    protected override void OnEnter()
    {
        LogKit.Log("[PlayerState] Dead");
        mBlack.Controller.StopMove();
        GameManager.Instance.GameOver();
    }
}
