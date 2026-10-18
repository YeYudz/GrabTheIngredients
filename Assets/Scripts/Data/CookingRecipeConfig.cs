using System.Collections.Generic;

/// <summary>
/// 战场炒菜效果类型（与 RecipeConfig 的局外加成菜品区分：这里是局内临时增益）
/// 可自由扩展，新增类型只需在 CookingEffectFactory 里加一个 case
/// </summary>
public enum CookingEffectType
{
    None,
    DamageBoost,        // 攻击力提升
    AttackSpeedBoost,   // 攻击速度提升
    MoveSpeedBoost,     // 移动速度提升
    AOEDamage,          // 范围伤害
    Heal,               // 回复生命
    Shield,             // 护盾（预留）
    SlowEnemies,        // 减速敌人
    Invincibility       // 无敌（预留）
}

/// <summary>
/// 画中画炒菜菜品配置（随机事件：局内临时增益 + 局外背包成品菜，与 RecipeConfig 区分）
/// 数据驱动：策划可独立调整效果数值，无需修改代码
/// </summary>
[System.Serializable]
public class CookingRecipeConfig
{
    public string recipeId;              // 菜品唯一标识
    public string recipeName;            // 菜品名称
    public string iconPath;              // 图标（Resources 路径）

    // 战斗效果配置（局内临时增益）
    public CookingEffectType effectType; // 效果类型
    public float effectValue;            // 效果数值（减速=速度倍率，如 0.4）
    public float effectDuration;         // 效果持续时间（秒，0=瞬时）
    public float effectRadius;           // 影响范围（AOE/减速时有效）

    // 局外背包映射：本次炒制对应的成品菜谱（RecipeConfig.id，可空=不入包）
    public string dishRecipeId;

    // 复杂增益（效果引擎）：引用 GameEffectConfig 效果库，品质倍率放大一次性数值
    // 定位：画中画炒菜是"限时小增益"，故请配 duration（秒）；本局常驻增益归升级3选1（UpgradeBuffSystem）
    public List<string> gameEffectIds = new();

    // 步骤序列覆盖：引用 wrapper.pipSteps 的 id（空/无效=使用默认全局流程）
    public List<string> pipStepIds;
}

/// <summary>
/// 画中画炒菜步骤配置：每步骤按时长推进，步骤内按 hintCount 均分出颠勺提示，实现演出与玩法融合。
/// icon / fxIcon 为不含扩展名的 Resources 路径，缺失时回退到 fallbackColor 纯色形状；
/// 特效图按「高度固定、宽度随图比例」绘制，出图只需保证竖向比例正确。
/// </summary>
[System.Serializable]
public class PipStepConfig
{
    public string id;            // 步骤唯一标识（菜品 pipStepIds 引用）
    public string name;          // 步骤名（UI 显示，如"起锅"）
    public string icon;          // 主图 Resources 路径（不含扩展名）
    public string anim;          // 入场动画类型（FlyIn/Pour/Drop/Toss/Sprinkle）
    public float duration = 2f;  // 步骤时长（秒）
    public int hintCount = 0;    // 步骤内颠勺提示次数（0=纯演出步骤）
    public string fallbackColor = "#FFFFFF"; // 图片缺失时兜底形状颜色（#RRGGBB）
    public string fxIcon = "";   // 特效图 Resources 路径（不含扩展名，空=纯色/主图兜底）
}

/// <summary>
/// 画中画炒菜配置（Wrapper，含随机触发参数 + 画中画颠勺参数 + 菜品列表）
/// 画中画参数可直接改 JSON 调整，无需改脚本
/// </summary>
public class CookingRecipeConfigWrapper
{
    public int version = 1;

    // 随机触发参数：累计击杀多少敌人触发一次画中画炒菜
    public int killTriggerThreshold = 400;

    // ===== 画中画颠勺参数（JSON 可调） =====
    public float pipDuration = 18f;          // 颠勺总时长（秒，约 6-10 次提示）
    public float pipHintIntervalMin = 1.5f;  // 提示出现间隔下限（秒）
    public float pipHintIntervalMax = 3f;    // 提示出现间隔上限（秒）
    public float pipHintWindow = 0.8f;       // 提示可判定窗口（秒，窗口内点击/按键=颠勺成功）
    public float pipFailDebuffDuration = 30f;// 失败惩罚时长（秒，手忙脚乱 debuff）
    public float pipFailDebuffAttackSpeed = 0.2f; // 手忙脚乱 debuff：攻速降低比例（0.2 = -20%）
    // 品质四档阈值（命中率=颠勺成功/总提示数）：S=4星 / A=3星 / B=2星 / C=1星
    public float qualitySRatio = 0.9f;       // S 品质线
    public float qualityARatio = 0.75f;      // A 品质线
    public float qualityBRatio = 0.5f;       // B 品质线
    public float qualityPassRatio = 0.3f;    // 失败线（低于此值=炒糊）

    // ===== 画中画美术资源（Resources 路径，不含扩展名；空=保持预制体/纯色兜底，旧表现完全不变） =====
    public string pipHintIcon = "";     // 颠勺提示图形（锅铲/火焰/箭头），覆盖 HintImage 的纯色闪烁方块
    public string pipBoardSprite = "";  // 小窗背板切片（9-slice），覆盖 PipWindow 的纯色矩形；
                                        // 配了图请在预制体里把 PipWindow 的 Image.color 改成纯白，并把 Image Type 设为 Sliced

    // ===== 画中画步骤演出（JSON 可调；非空时启用步骤模式，总时长=Σduration、总提示=ΣhintCount） =====
    public List<PipStepConfig> pipSteps = new();

    public List<CookingRecipeConfig> Recipes = new();

    private Dictionary<string, CookingRecipeConfig> _dict;
    private Dictionary<string, PipStepConfig> _stepDict;

    public CookingRecipeConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, CookingRecipeConfig>();
            foreach (var r in Recipes)
                if (!string.IsNullOrEmpty(r?.recipeId) && !_dict.ContainsKey(r.recipeId))
                    _dict.Add(r.recipeId, r);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    /// <summary>按 id 查询炒菜步骤配置（菜品 pipStepIds 引用用）</summary>
    public PipStepConfig GetStepById(string id)
    {
        if (_stepDict == null)
        {
            _stepDict = new Dictionary<string, PipStepConfig>();
            foreach (var s in pipSteps)
                if (!string.IsNullOrEmpty(s?.id) && !_stepDict.ContainsKey(s.id))
                    _stepDict.Add(s.id, s);
        }
        return string.IsNullOrEmpty(id) ? null : (_stepDict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Recipes?.Count ?? 0;
}
