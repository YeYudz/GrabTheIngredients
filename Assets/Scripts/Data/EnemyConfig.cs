using System.Collections.Generic;

/// <summary>
/// 敌人行为类型（决定 AI 逻辑）
/// </summary>
public enum EnemyBehavior
{
    SlowChase,     // 慢速追击（史莱姆系）
    FastCharge,    // 快速冲锋（哥布林系）
    Stationary,    // 静止不动（蘑菇系，死亡时爆炸）
    Boss           // BOSS 专用
}

/// <summary>
/// 敌人配置数据（食材拟态怪）
/// 对应文档第三节"怪物设计：食材拟态怪"
/// </summary>
public class EnemyConfig
{
    public string id;                           // 唯一ID
    public string enemyName;                    // 显示名称
    public float maxHp;                         // 血量
    public float speed;                         // 移动速度
    public float contactDamage;                 // 接触伤害
    public float attackInterval = 1f;           // 接触攻击间隔（秒；JSON 未配置时默认 1 秒一次）
    public EnemyBehavior behavior;              // 行为类型
    public float behaviorParam;                 // 行为参数（如冲锋距离/爆炸范围）

    // 掉落配置
    public string dropIngredientId;             // 主要掉落食材ID
    public int dropCount = 1;                   // 掉落数量
    public float dropChance = 0.5f;             // 食材掉落概率（0~1；运行时保底 50%，字段缺失时 LitJson 保留默认值）
    public float expReward = 1f;                // 经验值奖励（>0 即 100% 掉落经验宝石）

    // 精英标记（精英怪使用独立 EnemyConfig 条目，如 slime_jelly_elite）
    public bool isElite;                        // 是否精英（影响视觉标记与展示）

    // 外观
    public string prefabPath;                   // 预制体 Resources 路径
    public int skinColor;                       // 颜色索引（方便几何图形快速换色）

    // 序列图覆盖（与 iconPath 同口径：外观差异写数据，不复制预制体）
    // 约定：文件夹最后一段即图片基名，例如 Art/Enemy/Slime_Elite/ 读取 Slime_Elite.png；
    // 空 = 用预制体上的默认文件夹（即与普通怪同款外观）。预制体、网格参数、PPU 均无需改动。
    public string spriteSheetPath = "";         // 序列图文件夹 Resources 路径（不含扩展名）

    public string iconPath = "";                // 图鉴图标 Resources 路径（可空，空则图鉴隐藏）
    public string description = "";             // 图鉴描述（可空，空则图鉴用默认文案）
}

public class EnemyConfigWrapper
{
    public int version = 1;
    public List<EnemyConfig> Enemies = new();

    private Dictionary<string, EnemyConfig> _dict;

    public EnemyConfig GetById(string id)
    {
        if (_dict == null)
        {
            _dict = new Dictionary<string, EnemyConfig>();
            foreach (var e in Enemies)
                if (!string.IsNullOrEmpty(e?.id) && !_dict.ContainsKey(e.id))
                    _dict.Add(e.id, e);
        }
        return string.IsNullOrEmpty(id) ? null : (_dict.TryGetValue(id, out var c) ? c : null);
    }

    public int Count => Enemies?.Count ?? 0;
}
