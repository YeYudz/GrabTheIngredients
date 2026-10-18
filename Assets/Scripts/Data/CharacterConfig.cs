using System.Collections.Generic;

/// <summary>
/// 单套战斗序列图的切分/播放参数覆写（对应 SequenceFrameAnimator.SequenceClip）。
/// 数值写 0（或不写该字段）= 沿用预制体上的默认值，只有 >0 的字段才会覆盖。
/// </summary>
[System.Serializable]
public class SequenceClipConfig
{
    public bool isEnabled = true;   // 是否启用该套序列图（写 false 可关闭某套，例如只有移动图时关掉待机图）
    public int columns;             // 横向列数
    public int rows;                // 纵向行数
    public int frameCount;          // 实际播放帧数（网格末行不满时必须写真实帧数，否则会把空格子也播出去）
    public float framesPerSecond;   // 帧率
    public float playbackSpeed;     // 播放速度倍率
}

/// <summary>
/// 角色战斗序列图的网格与缩放参数（整块为空 = 完全沿用预制体上的默认值）。
/// 约定：移动图 = {角色ID}.png，待机图 = {角色ID}_idle.png，同放在 spriteSheetPath 文件夹下。
/// 新增角色只要在 CharacterConfig.json 里填好这张表即可，不用再动 Player 预制体。
/// </summary>
[System.Serializable]
public class CharacterSequenceConfig
{
    public float pixelsPerUnit;     // 单帧的像素/世界单位，决定角色在画面上的大小（按角色实际身高标定，保证不同角色等高）
    public float pivotY;            // 精灵轴心高度（0~1，0.5=帧中心）；0/不写=用预制体默认。各角色脚底留白不同，用它对齐地面
    public SequenceClipConfig move; // 移动图参数
    public SequenceClipConfig idle; // 待机图参数
}

/// <summary>
/// 角色基础属性配置（战前准备-选择角色）
/// 不同角色基础属性有差异，玩家战斗属性 = 角色基础 + 战前装配(武器+菜品) + 局内Buff
/// </summary>
public class CharacterConfig
{
    public string id;                // 角色唯一ID
    public string characterName;     // 角色名
    public string description;       // 角色描述
    public string iconPath;          // 头像 Resources 路径
    public string spriteSheetPath;   // 战斗角色序列图文件夹 Resources 路径（Art/Role/{角色ID}/，内含移动图 {ID}.png 与待机图 {ID}_idle.png）；空 = 该角色暂无专属序列图
    public CharacterSequenceConfig sequence;  // 序列图网格/缩放参数（为空 = 用 Player 预制体上的默认值）

    // ===== 基础属性 =====
    public float maxHp;              // 血量
    public float attack;             // 攻击力
    public float physDef;            // 物理防御力
    public float elemDef;            // 元素防御力
    public float moveSpeed;          // 移动速度
    public float attackSpeed;        // 攻击速度（每秒攻击次数，替代武器攻击间隔）
    public float dodgeChance;        // 闪避几率（0~1）
    public float critChance;         // 暴击率（0~1）
    public float critDamage;         // 暴击伤害倍率（1.5 = 150%）
    public float physDamageBonus;    // 物理伤害加成（0.1 = +10%）
    public float elemDamageBonus;    // 元素伤害加成（0.1 = +10%）

    // ===== 法力（技能资源，除闪避/武器主动技能外的所有主动技能使用） =====
    public float maxMana;            // 初始最大法力值（角色提供部分）
    public float manaRegen = 0f;     // 每秒法力回复

    // ===== 技能（被动 + 可选搭配） =====
    public string passiveEffectId;             // 唯一被动技能（引用 GameEffectConfig，常驻免佩戴）
    public List<string> skillIds = new();      // 角色专属主动技能池：只有该角色能装配，且一律需要商店购买解锁（拥有角色 ≠ 拥有其专属技能）
    public List<string> starterSkillIds = new(); // 角色附赠主动技能：已拥有该角色即默认解锁（不吃"需购买"规则），用于给初始角色留一套起手技能
    public string defaultWeaponId;             // 默认武器ID

    // ===== 商店 =====
    public int buyPrice;                    // 商店购买解锁价格（未拥有时可购买）
    public int sellPrice;                   // 商店出售价格（角色暂不可出售，预留）
}

public class CharacterConfigWrapper
{
    public int version = 1;
    public List<string> commonSkillIds = new();   // 共有可选主动技能池（所有角色通用）
    public List<CharacterConfig> Characters = new();

    private Dictionary<string, CharacterConfig> _dict;

    public CharacterConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, CharacterConfig>();
            foreach (var entry in Characters)
                if (!string.IsNullOrEmpty(entry?.id) && !_dict.ContainsKey(entry.id))
                    _dict.Add(entry.id, entry);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Characters?.Count ?? 0;
}
