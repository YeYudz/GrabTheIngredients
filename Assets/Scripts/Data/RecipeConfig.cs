using System.Collections.Generic;

/// <summary>
/// 食材需求项（LitJson 兼容：不能用 KeyValuePair）
/// </summary>
public class RequiredIngredient
{
    public string id;
    public int count;
}

/// <summary>
/// 烹饪阶段（对应文档"切菜→下锅→翻炒→调味"四阶段）
/// </summary>
public enum CookingStage
{
    Cut,        // 切菜：点击音符（D/F/J/K 单击）
    Heat,       // 下锅：长按音符（holdBeats > 0）
    Stir,       // 翻炒：点击音符（D/F/J/K 单击）
    Seasoning   // 调味：长按音符（holdBeats > 0）
}

/// <summary>
/// 烹饪音游单个阶段配置（一个菜品有多个阶段）
/// </summary>
public class RecipeStage
{
    public CookingStage stage;         // 对应哪个烹饪阶段
    public int noteCount;              // 该阶段音符数量
    public float bpmScale = 1f;        // 阶段内 BPM 倍率（>1 更快；用于四阶段速度起伏）
    public CookingStageNoteConfig noteConfig; // 该阶段音符类型配置
}

/// <summary>
/// 单个阶段内的音符生成配置（决定该阶段音游的"玩法形态"）
/// </summary>
public class CookingStageNoteConfig
{
    public int trackCount = 4;          // 轨道数量
    public float beatsPerNote = 1f;     // 每音符占多少拍（音符间隔 = 60/BPM × 该值）：1=四分音符, 2=二分, 0.5=八分
    public float holdBeats = 0f;        // 长按拍数（>0 = 该阶段全音符为长按，长按时长 = holdBeats × 60/实际BPM；0 = 点击音符）
}

/// <summary>
/// 菜品品质等级（对应音游判定结果）
/// </summary>
public enum RecipeQuality
{
    Failed = 0,    // 烹饪失败
    Common = 1,    // 普通菜品
    Legendary = 2  // 传说菜品
}

/// <summary>
/// 菜谱配置数据
/// 对应文档"菜谱系统（深度养成）"：基础菜谱 + 实验菜谱 + 稀有菜谱
/// </summary>
public class RecipeConfig
{
    public string id;                       // 菜谱唯一ID
    public string recipeName;               // 菜谱名称
    public string description;              // 菜品描述
    public string iconPath;                 // 立绘图 Resources 路径（成品菜/菜谱商店图标）
    public string correspondingBossId;      // 对应的BOSS ID

    // 所需食材（食材ID + 数量）
    public List<RequiredIngredient> requiredIngredients = new();

    // 烹饪音游参数
    public List<RecipeStage> stages = new();   // 四阶段配置（按阶段顺序）
    public float bpm = 120f;                   // 音游整体 BPM
    public float fallDuration = 2f;            // 音符从顶部下落到判定线的时间（秒，越小越难）

    // 品质判定：BOSS 音游加权总评（阈值在 RecipeConfigWrapper 全局节点，见 bossGradeSRatio 等）
    // Quality 枚举仅表达生死：S/A=Legendary（胜利）、B/C=Common（胜利）、0 音符/作废=Failed（复活）

    // Buff 效果（按品质数组索引）
    public List<BuffConfig> buffEffects = new(); // buffEffects[0] = Failed, [1] = Common, [2] = Legendary

    // 成品菜星级复杂增益（效果引擎）：装配时解析 star <= 菜品星级的全部条目并叠加
    // 星级无额外效果则沿用更低星级效果（天然继承）
    public List<StarEffectConfig> starEffects = new();

    // 解锁状态
    public bool isUnlocked = false;             // 是否解锁（传说菜谱默认 false）

    // ===== 商店 =====
    public int buyPrice;                        // 商店购买价（获得 ★1 成品菜，菜谱未解锁时同时解锁）
    public int sellPrice;                       // 成品菜出售单价（按星级翻倍：卖价 = sellPrice × 星级）
}

/// <summary>
/// 成品菜星级复杂增益档位
/// 装配时解析 star <= 菜品星级的全部条目并叠加（星级无额外效果则沿用更低星级）
/// </summary>
public class StarEffectConfig
{
    public int star;                 // 达到该星级激活
    public string effectId;          // 引用 GameEffectConfig 效果库
}

/// <summary>
/// Buff 效果配置（菜品品质决定最终效果）
/// 对应文档"菜品Buff反馈机制" + "Build 联动"
/// </summary>
public class BuffConfig
{
    public string buffId;
    public string buffName;
    public string buffType;            // 如 "Damage", "AttackSpeed", "Effect"（感电/燃烧）
    public float value;                // 效果数值
    public float duration = 0f;        // 持续时间（0 = 本局永久）
    public string description;         // 效果描述
    public bool isMetaBuff;            // 是否局外永久Buff
}

/// <summary>
/// 音游皮肤配置（RecipeConfigWrapper.rhythmSkin 节点，可缺省）：
/// 轨道区背景/滑轨背板/音符/判定线/按键背板可用艺术资源（Sprite）更替
/// 路径为 Resources 相对路径（省略 Resources/ 前缀与扩展名，如 "Art/Rhythm/note_0"）；
/// 字段留空或资源缺失 = 使用代码默认渲染（轨道色块/金色判定线），滑轨与音符仍叠加透视梯形
/// </summary>
public class RhythmSkinConfig
{
    public string trackBgSprite;          // 轨道区整体背景（铺满 TrackContainer 最底层）
    public string laneSprite;             // 滑轨背板（所有轨道统一）
    public List<string> laneSprites;      // 滑轨背板按轨道覆盖（索引=轨道号）
    public string noteSprite;             // 点击音符（所有轨道统一）
    public List<string> noteSprites;      // 点击音符按轨道覆盖
    public string holdNoteSprite;         // 长按音符（缺省回退 noteSprite）
    public List<string> holdNoteSprites;  // 长按音符按轨道覆盖
    public string judgeLineSprite;        // 判定线
    public string keySprite;              // 按键引导背板（所有轨道统一）
    public List<string> keySprites;       // 按键引导背板按轨道覆盖
    public string stagePillSprite;        // 阶段胶囊背板（TopBar，图与胶囊显示区等比即可免九宫格）
}

public class RecipeConfigWrapper
{
    public int version = 1;

    // ===== BOSS 音游加权总评阈值（全局；总评 = Perfect率×0.6 + 最高连击/总音符×0.4） =====
    public float bossGradeSRatio = 0.85f;       // S（成品菜 5 星）
    public float bossGradeARatio = 0.7f;        // A（4 星）
    public float bossGradeBRatio = 0.5f;        // B（3 星；低于则为 C，同为 3 星）

    // ===== 音游皮肤（可缺省；缺失或单项留空 = 代码默认渲染） =====
    public RhythmSkinConfig rhythmSkin;

    public List<RecipeConfig> Recipes = new();

    private Dictionary<string, RecipeConfig> _dict;

    public RecipeConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, RecipeConfig>();
            foreach (var r in Recipes)
                if (!string.IsNullOrEmpty(r?.id) && !_dict.ContainsKey(r.id))
                    _dict.Add(r.id, r);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public RecipeConfig GetByBossId(string bossId)
    {
        foreach (var r in Recipes)
            if (r != null && r.correspondingBossId == bossId) return r;
        return null;
    }

    public int Count => Recipes?.Count ?? 0;
}
