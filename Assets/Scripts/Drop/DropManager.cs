using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 掉落管理：订阅击杀事件后按配置掉落食材与经验宝石，数据来自 EnemyConfig / IngredientConfig。
/// 只广播拾取事件，不操作显示。
/// </summary>
public class DropManager : MonoSingleton<DropManager>
{
    /// <summary>食材最低掉落概率（保底 50%）：无论配置如何，实际掉率都不会低于此值</summary>
    private const float kMinIngredientDropChance = 0.5f;

    /// <summary>食材掉落物预制体（通用，用不同颜色/图标区分）</summary>
    [Header("掉落预制体（Resources 路径）")]
    [SerializeField] private string mIngredientDropPrefab = "Prefabs/Drops/Drop_Ingredient";
    [SerializeField] private string mExpGemPrefab = "Prefabs/Drops/Drop_Exp";

    /// <summary>食材拾取统计（供 UI 显示当前背包）</summary>
    public Dictionary<string, int> IngredientInventory { get; } = new();

    /// <summary>本局炒出的成品菜（"菜谱ID|星级" → 数量；状态面板背包展示，随 Reset 清空）</summary>
    public Dictionary<string, int> RunDishGains { get; } = new();

    /// <summary>当前击杀数（统计用）</summary>
    public int KillCount { get; private set; }

    /// <summary>局内拾取倍率（升级Buff"丰收祝福"等加成，1 = 无加成）</summary>
    public float DropMultiplierBonus { get; set; } = 1f;

    /// <summary>食材掉落物预制体 Resources 路径（池预热用）</summary>
    public string IngredientDropPrefabPath => mIngredientDropPrefab;

    /// <summary>经验宝石预制体 Resources 路径（池预热用）</summary>
    public string ExpGemPrefabPath => mExpGemPrefab;

    // ===== 连杀统计 =====

    /// <summary>连杀持续时间（秒），超时未击杀则中断连杀</summary>
    public float StreakDuration { get; private set; } = 3f;

    /// <summary>当前连杀数</summary>
    public int StreakCount { get; private set; }

    /// <summary>连杀剩余时间（秒），归零则中断</summary>
    public float StreakRemainTime { get; private set; }

    protected override void Awake()
    {
        base.Awake();

        // 订阅敌人击杀事件（EventKit 解耦）
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    private void Update()
    {
        // 连杀计时递减：归零则中断连杀
        if (StreakCount > 0)
        {
            StreakRemainTime -= Time.deltaTime;
            if (StreakRemainTime <= 0f)
            {
                StreakRemainTime = 0f;
                StreakCount = 0;
                EventKit.Type.Send(new GameEvents.StreakChanged
                {
                    Count = 0,
                    RemainTime = 0f,
                    Duration = StreakDuration
                });
            }
        }
    }

    /// <summary>
    /// 显式初始化：触发单例创建并注册事件。
    /// MonoSingleton 首次访问 Instance 才创建，因此需在进入游戏时主动调用一次。
    /// </summary>
    public void Init()
    {
        LogKit.Log("[DropManager] 初始化完成，掉落系统已就绪");
    }

    /// <summary>重置掉落统计（重开游戏时调用）</summary>
    public void Reset()
    {
        KillCount = 0;
        IngredientInventory.Clear();
        RunDishGains.Clear();
        StreakCount = 0;
        StreakRemainTime = 0f;
        DropMultiplierBonus = 1f;
        LogKit.Log("[DropManager] 已重置");
    }

    /// <summary>断点恢复：还原击杀数/食材背包/拾取倍率/连杀/本局成品菜</summary>
    public void RestoreRun(int kills, List<IngredientStack> inventory, float dropMultiplier,
        int streakCount = 0, float streakRemain = 0f, List<RunDishStackSave> dishGains = null)
    {
        KillCount = Mathf.Max(0, kills);
        DropMultiplierBonus = Mathf.Max(0f, dropMultiplier);
        StreakCount = Mathf.Max(0, streakCount);
        StreakRemainTime = Mathf.Max(0f, streakRemain);
        IngredientInventory.Clear();
        if (inventory != null)
        {
            foreach (var s in inventory)
            {
                if (s == null || string.IsNullOrEmpty(s.id) || s.count <= 0) continue;
                IngredientInventory[s.id] = s.count;
            }
        }
        RunDishGains.Clear();
        if (dishGains != null)
        {
            foreach (var d in dishGains)
            {
                if (d == null || string.IsNullOrEmpty(d.recipeId) || d.count <= 0) continue;
                RunDishGains[$"{d.recipeId}|{Mathf.Clamp(d.star, 1, 5)}"] = d.count;
            }
        }
        LogKit.Log($"[DropManager] 断点恢复：击杀 {KillCount}，食材 {IngredientInventory.Count} 种，成品菜 {RunDishGains.Count} 种，连杀 {StreakCount}");
    }

    /// <summary>
    /// 记录本局炒出的成品菜（画中画炒菜 / BOSS 音游结算时调用，与局外背包 AddDish 同步记账）。
    /// 只做局内展示数据，不写入局外背包。
    /// </summary>
    public void AddRunDish(string recipeId, int star, int count = 1)
    {
        if (string.IsNullOrEmpty(recipeId) || count <= 0) return;
        string key = $"{recipeId}|{Mathf.Clamp(star, 1, 5)}";
        RunDishGains.TryGetValue(key, out int c);
        RunDishGains[key] = c + count;
    }

    /// <summary>敌人被击杀 → 连杀 + 掉落食材 + 经验</summary>
    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        KillCount++;

        // 连杀计数：重置计时，并发事件供 HUD 显示
        StreakCount++;
        StreakRemainTime = StreakDuration;
        EventKit.Type.Send(new GameEvents.StreakChanged
        {
            Count = StreakCount,
            RemainTime = StreakRemainTime,
            Duration = StreakDuration
        });

        var enemyConfig = GameDataMgr.Instance.GetEnemy(evt.EnemyId);
        if (enemyConfig == null)
        {
            LogKit.Warning($"[DropManager] 找不到敌人配置: {evt.EnemyId}");
            return;
        }

        // 敌人属性倍率（关卡难度 × 作用域词缀修饰器）：影响掉落数量与经验
        var statScale = EnemyStatResolver.Resolve(evt.EnemyId);

        // 1. 概率掉落食材（配置概率保底 50%；命中后按数量生成，含局外永久Buff的食材获取率加成）
        if (!string.IsNullOrEmpty(enemyConfig.dropIngredientId) && RollIngredientDrop(enemyConfig.dropChance))
        {
            int baseCount = Mathf.Max(0, Mathf.RoundToInt(enemyConfig.dropCount * statScale.Drop));
            int count = GetDropCountWithMeta(baseCount);
            for (int i = 0; i < count; i++)
            {
                SpawnIngredientDrop(enemyConfig.dropIngredientId, evt.Position);
            }
        }

        // 2. 掉落经验宝石（100% 必掉，只要配置了经验奖励）
        if (enemyConfig.expReward > 0f)
        {
            SpawnExpGem(enemyConfig.expReward * statScale.Exp, evt.Position);
        }
    }

    /// <summary>掷食材掉落骰：概率保底 50%、上限 100%（配置 ≥1 直接必掉，避免浮点边界漏掉）</summary>
    private bool RollIngredientDrop(float configuredChance)
    {
        float chance = Mathf.Clamp(configuredChance, kMinIngredientDropChance, 1f);
        return chance >= 1f || Random.value < chance;
    }

    /// <summary>生成食材掉落物（对象池）</summary>
    private void SpawnIngredientDrop(string ingredientId, Vector3 pos)
    {
        var go = PoolFactory.Get(mIngredientDropPrefab, pos + RandomOffset(), Quaternion.identity);
        var drop = go.GetComponent<DropItem>();
        if (drop != null)
        {
            drop.SetupIngredient(ingredientId);
        }
    }

    /// <summary>外部调用：生成指定食材掉落物（BOSS 死亡掉落稀有食材用）</summary>
    public void SpawnIngredient(string ingredientId, Vector3 pos)
    {
        SpawnIngredientDrop(ingredientId, pos);
    }

    /// <summary>生成经验宝石（对象池）</summary>
    private void SpawnExpGem(float expValue, Vector3 pos)
    {
        var go = PoolFactory.Get(mExpGemPrefab, pos + RandomOffset(), Quaternion.identity);
        var drop = go.GetComponent<DropItem>();
        if (drop != null)
        {
            drop.SetupExp(Mathf.RoundToInt(expValue));
        }
    }

    /// <summary>外部调用：在指定位置生成经验宝石（断点恢复地上掉落物用，不再叠加随机偏移）</summary>
    public void SpawnExpGemAt(int expValue, Vector3 pos)
    {
        var go = PoolFactory.Get(mExpGemPrefab, pos, Quaternion.identity);
        var drop = go.GetComponent<DropItem>();
        if (drop != null) drop.SetupExp(Mathf.Max(1, expValue));
    }

    /// <summary>外部调用：在指定位置生成食材掉落物（断点恢复地上掉落物用，不再叠加随机偏移）</summary>
    public void SpawnIngredientAt(string ingredientId, Vector3 pos)
    {
        if (string.IsNullOrEmpty(ingredientId)) return;
        var go = PoolFactory.Get(mIngredientDropPrefab, pos, Quaternion.identity);
        var drop = go.GetComponent<DropItem>();
        if (drop != null) drop.SetupIngredient(ingredientId);
    }

    /// <summary>随机偏移（避免多个掉落物重叠）</summary>
    private Vector3 RandomOffset()
    {
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float radius = Random.Range(0.2f, 0.5f);
        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    /// <summary>计算实际掉落数量（局内拾取倍率 × 局外永久Buff食材获取率，随机取整）</summary>
    private int GetDropCountWithMeta(int baseCount)
    {
        if (baseCount <= 0) return 0;

        float rate = DropMultiplierBonus; // 局内升级Buff（丰收祝福等）
        if (MetaManager.HasInstance)
            rate *= (1f + MetaManager.Instance.BuffSystem.IngredientDropRateBonusPercent);

        if (rate <= 0f) return baseCount;

        // 额外数量 = 基础 × 总倍率，取随机整数（保证长期期望值正确且单次波动自然）
        float total = baseCount * rate;
        int count = Mathf.FloorToInt(total);
        if (Random.value < total - count)
            count++;
        return count;
    }

    /// <summary>食材被拾取（由 DropItem 调用，更新背包）</summary>
    public void OnIngredientPicked(string ingredientId, Vector3 position)
    {
        if (string.IsNullOrEmpty(ingredientId)) return;

        IngredientInventory.TryGetValue(ingredientId, out int count);
        IngredientInventory[ingredientId] = count + 1;

        // 广播食材拾取事件（UI 显示背包 + Step4 预加工系统）
        EventKit.Type.Send(new GameEvents.IngredientDropped
        {
            IngredientId = ingredientId,
            Position = position
        });
    }

    // ===== Step10 本局食材消耗（炒菜用） =====

    /// <summary>本局食材库存查询</summary>
    public int GetIngredientCount(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        return IngredientInventory.TryGetValue(id, out int c) ? c : 0;
    }

    /// <summary>直接增加本局食材库存（BOSS菜食材不足时自动补足用）</summary>
    public void AddInGameIngredient(string id, int count)
    {
        if (string.IsNullOrEmpty(id) || count <= 0) return;
        IngredientInventory.TryGetValue(id, out int c);
        IngredientInventory[id] = c + count;
    }

    /// <summary>本局食材是否足以制作某菜谱</summary>
    public bool CanAffordRecipe(RecipeConfig recipe)
    {
        if (recipe?.requiredIngredients == null) return true;
        foreach (var req in recipe.requiredIngredients)
        {
            if (req == null) continue;
            if (GetIngredientCount(req.id) < req.count) return false;
        }
        return true;
    }

    /// <summary>制作菜谱：消耗对应食材各×1（返回是否成功）</summary>
    public bool ConsumeRecipeIngredients(RecipeConfig recipe)
    {
        if (!CanAffordRecipe(recipe)) return false;

        foreach (var req in recipe.requiredIngredients)
        {
            if (req == null) continue;
            IngredientInventory.TryGetValue(req.id, out int c);
            IngredientInventory[req.id] = Mathf.Max(0, c - req.count);
            if (IngredientInventory[req.id] == 0) IngredientInventory.Remove(req.id);
        }
        return true;
    }

    /// <summary>扣除本局食材（战场炒菜失败惩罚等；扣到 0 移除条目，返回是否实际扣除）</summary>
    public bool RemoveInGameIngredient(string id, int count)
    {
        if (string.IsNullOrEmpty(id) || count <= 0) return false;
        if (!IngredientInventory.TryGetValue(id, out int c) || c <= 0) return false;

        int next = Mathf.Max(0, c - count);
        if (next == 0) IngredientInventory.Remove(id);
        else IngredientInventory[id] = next;
        return true;
    }
}
