using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 烹饪图鉴（常驻单例）：记录成品菜的收集与烹饪历史。
/// 图鉴项由配置在运行时构建、无需单独配表，持久化沿用 MetaSaveData 的 List 形式；
/// 对外只提供查询接口，记录动作由订阅烹饪 / 解锁事件自动完成。
/// ⚠ LoadFromSave 须在 MetaManager.Init 之后调用；CookingRecipeConfig 只是画中画炒菜的资源表，
/// 其产出经 dishRecipeId 映射为成品菜后才计入图鉴。
/// </summary>
public class RecipeAtlasManager : MonoSingleton<RecipeAtlasManager>
{
    public const int SOURCE_BATTLEFIELD = 0;   // 发现来源：画中画炒菜产出（经 dishRecipeId 映射为成品菜）
    public const int SOURCE_BOSS_RECIPE = 1;   // 发现来源：成品菜谱（RecipeConfig，Boss 音游产出 / 商店解锁）

    /// <summary>图鉴项（配置镜像，运行时构建）</summary>
    public sealed class AtlasItem
    {
        public string Id;
        public string Name;
        public string IconPath;
        public int Source;
        public string Description;
    }

    private readonly List<AtlasItem> mItems = new();
    private readonly Dictionary<string, RecipeAtlasEntry> mEntries = new(); // 存档镜像（运行时索引）

    /// <summary>图鉴总菜品数</summary>
    public int TotalCount => mItems.Count;

    /// <summary>已解锁菜品数</summary>
    public int UnlockedCount { get; private set; }

    /// <summary>烹饪全局统计</summary>
    public CookingStatisticsData Stats { get; private set; } = new CookingStatisticsData();

    /// <summary>图鉴项列表（UI 遍历用）</summary>
    public IReadOnlyList<AtlasItem> Items => mItems;

    protected override void Awake()
    {
        base.Awake();

        BuildItems();

        EventKit.Type.Register<GameEvents.FieldCookingFinished>(OnFieldCookingFinished)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BossCookingRecorded>(OnBossCookingRecorded)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.RecipeUnlocked>(OnRecipeUnlocked)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.DishCollected>(OnDishCollected)
            .UnRegisterWhenGameObjectDestroyed(gameObject);

        LogKit.Log($"[图鉴] 初始化：共 {TotalCount} 道成品菜");
    }

    // ===== 图鉴项构建（运行时从配置生成） =====

    private void BuildItems()
    {
        mItems.Clear();

        // 成品菜谱（RecipeConfig）：图鉴"菜品"唯一来源，局外作为"饰品"装备使用
        // CookingRecipeConfig（画中画炒菜资源）不构建图鉴项，其产出经 dishRecipeId 映射为下面的成品菜
        var recipes = GameDataMgr.Instance.Recipes;
        if (recipes != null)
        {
            foreach (var r in recipes.Recipes)
            {
                if (r == null || string.IsNullOrEmpty(r.id)) continue;
                mItems.Add(new AtlasItem
                {
                    Id = r.id,
                    Name = r.recipeName,
                    IconPath = r.iconPath,
                    Source = SOURCE_BOSS_RECIPE,
                    Description = r.description
                });
            }
        }
    }

    // ===== 存档读写 =====

    /// <summary>从局外存档载入图鉴进度（MetaManager.Init 完成后调用）</summary>
    public void LoadFromSave()
    {
        var data = MetaManager.Instance.Data;
        mEntries.Clear();

        // 图鉴只承认成品菜（RecipeConfig）条目，旧存档中残留的画中画炒菜条目（fc_*）一律丢弃
        var catalog = new HashSet<string>();
        foreach (var item in mItems) catalog.Add(item.Id);

        if (data.recipeCollection != null)
        {
            foreach (var e in data.recipeCollection)
            {
                if (e == null || string.IsNullOrEmpty(e.recipeId)) continue;
                if (!catalog.Contains(e.recipeId)) continue; // 清理历史遗留的非图鉴菜品条目
                mEntries[e.recipeId] = e;
            }
        }

        // 成品菜谱解锁状态与局外存档对齐（Boss 胜利解锁 / 商店购买）
        if (data.unlockedRecipeIds != null)
        {
            foreach (var id in data.unlockedRecipeIds)
            {
                if (string.IsNullOrEmpty(id) || !catalog.Contains(id)) continue;
                if (!mEntries.TryGetValue(id, out var e))
                {
                    e = new RecipeAtlasEntry { recipeId = id, source = SOURCE_BOSS_RECIPE };
                    mEntries[id] = e;
                }
                e.isUnlocked = true;
                if (string.IsNullOrEmpty(e.discoveredSource)) e.discoveredSource = "unlock";
            }
        }

        // 未记录的图鉴项补空条目（未解锁）
        foreach (var item in mItems)
        {
            if (!mEntries.ContainsKey(item.Id))
                mEntries[item.Id] = new RecipeAtlasEntry { recipeId = item.Id, source = item.Source };
        }

        Stats = data.cookingStats ?? new CookingStatisticsData();
        RecalcCounts();
    }

    /// <summary>写回局外存档并保存（烹饪/解锁频率低，直接整体保存）</summary>
    private void Flush()
    {
        var data = MetaManager.Instance.Data;
        data.recipeCollection.Clear();
        data.recipeCollection.AddRange(mEntries.Values);
        data.cookingStats = Stats;
        MetaManager.Instance.Save();
    }

    private void RecalcCounts()
    {
        UnlockedCount = 0;
        foreach (var e in mEntries.Values)
            if (e.isUnlocked) UnlockedCount++;
        if (Stats != null) Stats.recipeUnlockedCount = UnlockedCount;
    }

    // ===== 事件订阅 =====

    private void OnFieldCookingFinished(GameEvents.FieldCookingFinished evt)
    {
        // 画中画炒菜本身不是图鉴菜品（CookingRecipeConfig 仅配置画中画资源）
        // 其产出经 dishRecipeId 映射为成品菜（RecipeConfig）后，按成品菜记入图鉴
        string dishId = ResolveDishFromFieldCooking(evt.RecipeId);
        if (string.IsNullOrEmpty(dishId)) return;

        bool cooked = evt.Quality == "S" || evt.Quality == "A"
            || evt.Quality == "B" || evt.Quality == "C";
        RecordCooking(dishId, SOURCE_BATTLEFIELD, cooked, 0);
    }

    /// <summary>画中画炒菜菜品 id → 对应成品菜谱 id（无映射/映射失效返回 null）</summary>
    private static string ResolveDishFromFieldCooking(string cookingRecipeId)
    {
        if (string.IsNullOrEmpty(cookingRecipeId)) return null;

        string dishId = GameDataMgr.Instance?.GetCookingRecipe(cookingRecipeId)?.dishRecipeId;
        if (string.IsNullOrEmpty(dishId)) return null;
        return GameDataMgr.Instance.GetRecipe(dishId) != null ? dishId : null;
    }

    private void OnBossCookingRecorded(GameEvents.BossCookingRecorded evt)
        => RecordCooking(evt.RecipeId, SOURCE_BOSS_RECIPE, evt.Quality != RecipeQuality.Failed, evt.MaxCombo);

    private void OnRecipeUnlocked(GameEvents.RecipeUnlocked evt)
    {
        if (string.IsNullOrEmpty(evt.RecipeId) || GetItem(evt.RecipeId) == null) return;

        if (!mEntries.TryGetValue(evt.RecipeId, out var entry))
        {
            entry = new RecipeAtlasEntry { recipeId = evt.RecipeId, source = SOURCE_BOSS_RECIPE };
            mEntries[evt.RecipeId] = entry;
        }
        if (!entry.isUnlocked)
        {
            entry.isUnlocked = true;
            entry.discoveredSource = "unlock";
        }
        RecalcCounts();
        Flush();
    }

    /// <summary>成品菜入包 → 图鉴按星级收集（炒菜产出/合成/商店等所有入包路径的统一上报点）</summary>
    private void OnDishCollected(GameEvents.DishCollected evt)
        => RecordStarCollected(evt.RecipeId, evt.Star);

    // ===== 记录接口 =====

    /// <summary>记录一次成功烹饪（首次烹饪自动解锁该菜品；maxCombo>0 时回写最高连招）</summary>
    public void RecordCooking(string recipeId, int source, bool perfect, int maxCombo = 0)
    {
        // 只记录图鉴承认的成品菜（RecipeConfig）；非图鉴 id 直接忽略，避免产生无对应条目的脏数据
        if (string.IsNullOrEmpty(recipeId) || GetItem(recipeId) == null) return;

        if (!mEntries.TryGetValue(recipeId, out var entry))
        {
            entry = new RecipeAtlasEntry { recipeId = recipeId, source = source };
            mEntries[recipeId] = entry;
        }

        if (!entry.isUnlocked)
        {
            entry.isUnlocked = true;
            entry.discoveredSource = "first_cook";
        }
        entry.cookCount++;
        if (perfect) entry.perfectCount++;
        if (maxCombo > entry.maxCombo) entry.maxCombo = maxCombo;
        entry.lastCookTimestamp = SaveVersion.NowUnix();

        // 全局统计
        Stats ??= new CookingStatisticsData();
        Stats.totalCookingCount++;
        if (perfect) Stats.totalPerfectCount++;
        if (source == SOURCE_BATTLEFIELD) Stats.battlefieldCookingCount++;
        else Stats.bossCookingCount++;
        if (entry.cookCount == 1) Stats.differentRecipesCooked++;
        if (maxCombo > Stats.maxComboChain) Stats.maxComboChain = maxCombo;

        RecalcCounts();
        Flush();
    }

    /// <summary>
    /// 记录获得某星级的成品菜（图鉴按星级分格解锁；去重，首次收集才写盘）。
    /// 拿到过任意星级的成品菜即视为该菜品已解锁。
    /// </summary>
    public void RecordStarCollected(string recipeId, int star)
    {
        if (string.IsNullOrEmpty(recipeId) || star < 1 || star > 5) return;
        if (GetItem(recipeId) == null) return; // 只记录图鉴承认的成品菜（RecipeConfig）

        if (!mEntries.TryGetValue(recipeId, out var entry))
        {
            entry = new RecipeAtlasEntry { recipeId = recipeId, source = SOURCE_BATTLEFIELD };
            mEntries[recipeId] = entry;
        }

        if (!entry.isUnlocked) entry.isUnlocked = true;

        entry.collectedStars ??= new List<int>();
        if (entry.collectedStars.Contains(star)) return;
        entry.collectedStars.Add(star);
        entry.collectedStars.Sort();

        RecalcCounts();
        Flush();
    }

    // ===== 查询接口（UI / 成就） =====

    /// <summary>图鉴条目（无记录返回 null）</summary>
    public RecipeAtlasEntry GetEntry(string recipeId)
        => mEntries.TryGetValue(recipeId, out var e) ? e : null;

    /// <summary>菜品是否已在图鉴解锁（首次烹饪或成品菜谱解锁）</summary>
    public bool IsUnlocked(string recipeId)
        => mEntries.TryGetValue(recipeId, out var e) && e.isUnlocked;

    /// <summary>某星级成品菜是否已收集过（图鉴按星级分格的解锁依据）</summary>
    public bool IsStarCollected(string recipeId, int star)
        => mEntries.TryGetValue(recipeId, out var e)
           && e.collectedStars != null && e.collectedStars.Contains(star);

    /// <summary>取图鉴项（配置镜像）</summary>
    public AtlasItem GetItem(string recipeId)
    {
        foreach (var it in mItems)
            if (it.Id == recipeId) return it;
        return null;
    }
}
