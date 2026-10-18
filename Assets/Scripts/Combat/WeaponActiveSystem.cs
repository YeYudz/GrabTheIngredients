using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 武器主动技能系统：每把武器的 activeSkillId 独立成技，冷却与法力各自计算。
/// 对外接口一律收「武器技能槽序号 0~2」而非武器 id——空槽与无主动技能的武器
/// 因此不会让后续槽位串号。
/// 释放双轨：手动（HUD / 键盘）与自动（按槽位持久化的开关）。
/// 自动触发区分语义：回复类看血量阈值并加锁，攻击 / 增益类要求处于战斗状态，另设全局最小间隔防止瞬间空蓝。
/// 效果执行复用 ActiveSkillSystem 管线，冷却 / 法力 / 开关由本系统独立管理。
/// </summary>
public class WeaponActiveSystem : MonoSingleton<WeaponActiveSystem>
{
    /// <summary>单把武器的主动技能运行时数据</summary>
    private class WeaponActive
    {
        public int SlotIndex;               // 所属武器技能槽序号（0 起，对外接口一律用它定位）
        public string WeaponId;             // 所属武器 ID
        public ActiveSkillConfig Skill;     // 武器主动技能配置
        public float CooldownRemain;        // 冷却剩余（秒）
        public float AutoLockRemain;        // 回复类自动触发后的锁定剩余（秒，手动不受限）
        public bool AutoEnabled = true;     // 自动释放开关（按槽位独立，设置面板-游戏页签控制）
    }

    private const float kCombatWindow = 3f;      // 战斗状态窗口（秒）
    private const float kHealAutoLock = 60f;     // 回复类自动触发锁定（秒）
    private const float kHealHpThreshold = 0.3f; // 回复类自动触发血线（<30%）
    private const float kAutoGapMin = 1.5f;      // 全局自动间隔下限（秒）
    private const float kAutoGapMax = 2f;        // 全局自动间隔上限（秒）

    private readonly List<WeaponActive> mActives = new();
    private float mCombatRemain;    // 战斗状态剩余（秒）
    private float mAutoGapRemain;   // 全局自动间隔剩余（秒）

    /// <summary>已启用主动技能的武器数（HUD 调试用；对外定位一律用槽位序号，不要拿它当槽位数）</summary>
    public int Count => mActives.Count;

    /// <summary>该武器技能槽是否有可用主动技能（HUD/键盘触发前判空）</summary>
    public bool HasActiveAt(int slotIndex) => FindBySlot(slotIndex) != null;

    /// <summary>
    /// 标记进入战斗状态（3 秒窗口）：AutoAttack 命中/发射弹道、玩家受击时调用。
    /// 静态入口 + HasInstance 守卫，调用方无需关心单例是否存在。
    /// </summary>
    public static void NotifyCombat()
    {
        if (WeaponActiveSystem.HasInstance)
            WeaponActiveSystem.Instance.mCombatRemain = kCombatWindow;
    }

    protected override void Awake()
    {
        base.Awake();

        // HUD 武器槽点击 → 手动释放；自动开关由设置面板（游戏页签）控制，走 WeaponAutoCastChanged
        EventKit.Type.Register<GameEvents.WeaponActiveRequested>(OnWeaponActiveRequested)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.WeaponAutoCastChanged>(OnWeaponAutoCastChanged)
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        // 受击也算战斗状态（发射/命中由 AutoAttack 直接上报）
        EventKit.Type.Register<GameEvents.PlayerDamaged>(OnPlayerDamaged)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    /// <summary>初始化武器主动技能（AutoAttack.SetWeaponSlots 调用，与武器槽位同源同序；槽位序号原样记下）</summary>
    public void Init(List<string> weaponIds)
    {
        mActives.Clear();
        mCombatRemain = 0f;
        mAutoGapRemain = 0f;

        if (weaponIds == null) return;

        for (int slot = 0; slot < weaponIds.Count; slot++)
        {
            var id = weaponIds[slot];
            if (string.IsNullOrEmpty(id)) continue;   // 空槽跳过
            var weapon = GameDataMgr.Instance.GetWeapon(id);
            if (weapon == null || string.IsNullOrEmpty(weapon.activeSkillId)) continue;

            var skill = GameDataMgr.Instance.GetActiveSkill(weapon.activeSkillId);
            if (skill == null)
            {
                LogKit.Warning($"[WeaponActiveSystem] 找不到武器主动技能配置: {weapon.activeSkillId}（武器 {id}）");
                continue;
            }
            mActives.Add(new WeaponActive
            {
                SlotIndex = slot,
                WeaponId = id,
                Skill = skill,
                // 自动释放开关 = 设置面板（游戏页签）里对应槽位的存档偏好，旧档无记录默认为开
                AutoEnabled = !MetaManager.HasInstance || MetaManager.Instance.IsAutoCastEnabled(slot)
            });
        }
        LogKit.Log($"[WeaponActiveSystem] 武器主动技能初始化 {mActives.Count} 个");
    }

    /// <summary>按武器技能槽序号取运行时条目（无 / 该槽没主动技能 → null）</summary>
    private WeaponActive FindBySlot(int slotIndex)
    {
        if (slotIndex < 0) return null;
        for (int i = 0; i < mActives.Count; i++)
            if (mActives[i].SlotIndex == slotIndex) return mActives[i];
        return null;
    }

    /// <summary>重置（回主菜单时调用）</summary>
    public void Reset()
    {
        mActives.Clear();
        mCombatRemain = 0f;
        mAutoGapRemain = 0f;
    }

    // ===== 查询接口（HUD 读取显示；参数一律是武器技能槽序号） =====

    /// <summary>按武器技能槽序号取武器主动技能配置（无 / 越界返回 null）</summary>
    public ActiveSkillConfig GetSkill(int slotIndex)
    {
        var wa = FindBySlot(slotIndex);
        return wa != null ? wa.Skill : null;
    }

    /// <summary>冷却剩余（秒）</summary>
    public float GetCooldownRemain(int slotIndex)
    {
        var wa = FindBySlot(slotIndex);
        return wa != null ? wa.CooldownRemain : 0f;
    }

    /// <summary>冷却总时长（秒）</summary>
    public float GetCooldownTotal(int slotIndex) => GetSkill(slotIndex)?.cooldown ?? 0f;

    /// <summary>自动释放开关状态（该槽没有主动技能时返回 false）</summary>
    public bool IsAutoEnabled(int slotIndex)
    {
        var wa = FindBySlot(slotIndex);
        return wa != null && wa.AutoEnabled;
    }

    /// <summary>当前法力是否够释放（缺蓝 → HUD 图标半透明）</summary>
    public bool IsManaEnough(int slotIndex)
    {
        var skill = GetSkill(slotIndex);
        if (skill == null) return true;
        float cost = GetManaCost(skill);
        if (cost <= 0f) return true;
        var player = GameManager.Instance?.Player;
        return player != null && player.CurrentMana >= cost;
    }

    /// <summary>实际法力消耗（与 ActiveSkillSystem 同规：×法力乘区；-1/0=不消耗）</summary>
    private static float GetManaCost(ActiveSkillConfig skill)
    {
        if (skill.manaCost <= 0) return 0f;
        var pec = GetPlayerEffect();
        return pec != null ? skill.manaCost * pec.ManaCostMultiplier : skill.manaCost;
    }

    /// <summary>取玩家效果控制器（禁用/法力乘区查询；无则 null）</summary>
    private static PlayerEffectController GetPlayerEffect()
    {
        var player = GameManager.Instance?.Player;
        return player != null ? player.GetComponent<PlayerEffectController>() : null;
    }

    // ===== 事件回调 =====

    private void OnWeaponActiveRequested(GameEvents.WeaponActiveRequested evt)
    {
        // 手动释放：不触发回复类 60 秒自动锁定
        TryCast(evt.WeaponIndex, isAuto: false);
    }

    /// <summary>设置面板（游戏页签）改了自动释放开关 → 按槽位序号同步（正/反局内都即时生效）</summary>
    private void OnWeaponAutoCastChanged(GameEvents.WeaponAutoCastChanged evt)
    {
        var wa = FindBySlot(evt.SlotIndex);
        if (wa == null) return;

        wa.AutoEnabled = evt.Enabled;
        LogKit.Log($"[WeaponActiveSystem] 技能槽 {evt.SlotIndex + 1}（{wa.WeaponId}）自动释放 = {(evt.Enabled ? "开" : "关")}（设置面板）");
    }

    private void OnPlayerDamaged(GameEvents.PlayerDamaged evt)
    {
        NotifyCombat();
    }

    // ===== 释放流程 =====

    /// <summary>
    /// 尝试释放武器主动（冷却/法力/禁用三重校验）。参数 = 武器技能槽序号。
    /// isAuto=true 走自动通道（回复类触发后锁定 60 秒）；false 手动（不受锁定限制）
    /// </summary>
    private bool TryCast(int slotIndex, bool isAuto)
    {
        var wa = FindBySlot(slotIndex);
        if (wa == null) return false;
        var skill = wa.Skill;
        if (skill == null) return false;

        // buff200：所有主动技能被禁用
        var pec = GetPlayerEffect();
        if (pec != null && pec.ActiveSkillsDisabled) return false;

        if (wa.CooldownRemain > 0f) return false;

        // 法力消耗（双约束之一；缺蓝点击不放）
        float cost = GetManaCost(skill);
        if (cost > 0f)
        {
            var player = GameManager.Instance?.Player;
            if (player == null || player.MaxMana <= 0f || !player.SpendMana(cost))
            {
                LogKit.Log($"[WeaponActiveSystem] 法力不足，无法释放 {skill.skillName}（需 {cost:F0}）");
                return false;
            }
        }

        // 执行效果（复用 ActiveSkillSystem 效果管线：临时Buff/伤害/回复等）
        if (ActiveSkillSystem.HasInstance)
            ActiveSkillSystem.Instance.ExecuteEffectOnly(skill);

        // 进入独立冷却
        wa.CooldownRemain = skill.cooldown;

        // 回复类自动触发 → 锁定 60 秒（手动释放不锁定）
        if (isAuto && IsHealType(skill))
            wa.AutoLockRemain = kHealAutoLock;

        // 广播释放成功 + 冷却变化（音效/特效/HUD 遮罩订阅）
        EventKit.Type.Send(new GameEvents.ActiveSkillReleased { SkillId = skill.id });
        EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
        {
            SkillId = skill.id,
            Remain = skill.cooldown,
            Total = skill.cooldown
        });

        LogKit.Log($"[WeaponActiveSystem] {(isAuto ? "自动" : "手动")}释放 技能槽{wa.SlotIndex + 1}（{wa.WeaponId}）主动: {skill.skillName}");
        return true;
    }

    /// <summary>回复类技能判定（自动触发走血线规则而非战斗状态）</summary>
    private static bool IsHealType(ActiveSkillConfig skill)
        => skill.effectType == ActiveSkillEffectType.Heal
            || skill.effectType == ActiveSkillEffectType.HealPercent
            || skill.effectType == ActiveSkillEffectType.TimedKillHeal;

    private void Update()
    {
        float dt = Time.deltaTime;
        if (mCombatRemain > 0f) mCombatRemain = Mathf.Max(0f, mCombatRemain - dt);
        if (mAutoGapRemain > 0f) mAutoGapRemain = Mathf.Max(0f, mAutoGapRemain - dt);

        bool inCombat = mCombatRemain > 0f;
        var player = GameManager.Instance?.Player;
        float hpRatio = player != null && player.Health != null && player.MaxHp > 0f
            ? player.Health.CurrentHp / player.MaxHp
            : 1f;

        for (int i = 0; i < mActives.Count; i++)
        {
            var wa = mActives[i];

            // 冷却递减（到 0 时广播，HUD 遮罩清零）
            if (wa.CooldownRemain > 0f)
            {
                wa.CooldownRemain = Mathf.Max(0f, wa.CooldownRemain - dt);
                if (wa.CooldownRemain <= 0f)
                {
                    EventKit.Type.Send(new GameEvents.ActiveSkillCooldownChanged
                    {
                        SkillId = wa.Skill.id,
                        Remain = 0f,
                        Total = wa.Skill.cooldown
                    });
                }
            }

            // 自动触发锁定递减
            if (wa.AutoLockRemain > 0f) wa.AutoLockRemain = Mathf.Max(0f, wa.AutoLockRemain - dt);

            // 自动释放（开关开启 + 未锁定 + 全局间隔 + 触发条件满足）
            if (!wa.AutoEnabled || wa.AutoLockRemain > 0f || mAutoGapRemain > 0f) continue;

            // 回复类：血量 < 30%（不要求战斗状态）；攻击/增益类：战斗状态
            bool trigger = IsHealType(wa.Skill) ? hpRatio < kHealHpThreshold : inCombat;
            if (!trigger) continue;

            if (TryCast(wa.SlotIndex, isAuto: true))
                mAutoGapRemain = Random.Range(kAutoGapMin, kAutoGapMax);
        }
    }
}
