using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 局外长期进度的业务层单例，统一维护跨局资产、解锁、统计与永久加成，不参与 UI 表现或单局状态。
/// 初始化由 GameManager 显式驱动并防重入；存档加载与默认值修复完成后才构建派生 Buff，保证依赖顺序稳定。
/// SaveDataModel/MetaSaveData 是可变状态的单一数据源，版本迁移、校验、备份与损坏恢复统一交给 SaveFactory。
/// 所有调用均位于 Unity 主线程；写盘只发生在明确提交点，避免逐帧 I/O。
/// </summary>
public class MetaManager : MonoSingleton<MetaManager>
{
    /// <summary>局外业务数据；加载后作为跨系统共享的可变状态源</summary>
    public MetaSaveData Data { get; private set; } = new MetaSaveData();

    /// <summary>存档根对象（含版本/时间戳/设置；设置读写走这里的 settings）</summary>
    public SaveDataModel Model { get; private set; }

    /// <summary>由永久 Buff ID 重建的运行时派生加成，不单独持久化</summary>
    public MetaBuffSystem BuffSystem { get; private set; } = new MetaBuffSystem();

    /// <summary>防止重复加载存档与重复构建派生状态</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>加载并修复局外存档，再按依赖顺序构建运行时派生状态；幂等</summary>
    public void Init()
    {
        if (IsLoaded) return;
        IsLoaded = true;

        // 恢复链由 SaveFactory 封装，业务层始终获得可用存档
        Model = SaveFactory.Load();
        Data = Model?.data ?? new MetaSaveData();
        if (Model == null)
        {
            Model = new SaveDataModel
            {
                saveVersion = SaveVersion.CURRENT_VERSION,
                data = Data,
                settings = new SettingsSaveData { musicVolume = 1f, sfxVolume = 1f }
            };
        }

                EnsureDefaultCharacters();

        // 技能赠送依赖角色拥有状态，初始化顺序不可反转
        EnsureDefaultSkills();

                EnsureDungeonDefaults();

        // Buff 是存档 ID 的运行时派生状态，默认值修复后再构建
        BuffSystem.BuildFromBuffIds(Data.permanentBuffIds);

        LogKit.Log($"[MetaManager] 存档加载完成: 菜谱解锁{Data.unlockedRecipeIds?.Count ?? 0}个, " +
                   $"永久Buff{Data.permanentBuffIds?.Count ?? 0}个, 总局数{Data.totalRuns}, 胜场{Data.victoryRuns}");
    }

    /// <summary>将版本、设置与业务数据合并为完整快照；备份和校验由 SaveFactory 统一处理</summary>
    public void Save()
    {
        SaveFactory.Save(new SaveDataModel
        {
            saveVersion = SaveVersion.CURRENT_VERSION,
            data = Data,
            settings = Model?.settings
        });
        LogKit.Log("[MetaManager] 存档已保存");
    }

    /// <summary>合并配置初始状态与存档进度判断菜谱解锁</summary>
    public bool IsRecipeUnlocked(string recipeId)
    {
        if (string.IsNullOrEmpty(recipeId)) return false;
                var recipe = GameDataMgr.Instance.GetRecipe(recipeId);
        return Data.IsRecipeUnlocked(recipeId) || (recipe != null && recipe.isUnlocked);
    }

    /// <summary>登记新的菜谱解锁并广播事件；重复解锁返回 false</summary>
    public bool UnlockRecipe(string recipeId)
    {
        if (string.IsNullOrEmpty(recipeId)) return false;
        if (Data.IsRecipeUnlocked(recipeId)) return false;

        Data.unlockedRecipeIds.Add(recipeId);
        EventKit.Type.Send(new GameEvents.RecipeUnlocked { RecipeId = recipeId });
        LogKit.Log($"[MetaManager] 菜谱已永久解锁: {recipeId}");
        return true;
    }

    /// <summary>筛选菜谱中标记为跨局生效的 Buff 配置</summary>
    public List<BuffConfig> GetMetaBuffsForRecipe(string recipeId)
    {
        var recipe = GameDataMgr.Instance.GetRecipe(recipeId);
        if (recipe?.buffEffects == null) return null;

        var result = new List<BuffConfig>();
        foreach (var buff in recipe.buffEffects)
        {
            if (buff != null && buff.isMetaBuff)
                result.Add(buff);
        }
        return result;
    }

    /// <summary>登记唯一永久 Buff，并立即重建运行时加成</summary>
    public void AddPermanentBuff(string buffId)
    {
        if (string.IsNullOrEmpty(buffId) || Data.HasBuff(buffId)) return;

        Data.permanentBuffIds.Add(buffId);
        BuffSystem.BuildFromBuffIds(Data.permanentBuffIds);
        LogKit.Log($"[MetaManager] 永久Buff已添加: {buffId}");
    }

    /// <summary>首次选中局内 Buff 时写入图鉴进度并立即保存</summary>
    public void RecordBuffSelected(string buffId)
    {
        if (string.IsNullOrEmpty(buffId)) return;
        if (Data.selectedBuffIds != null && Data.selectedBuffIds.Contains(buffId)) return;

        if (Data.selectedBuffIds == null) Data.selectedBuffIds = new System.Collections.Generic.List<string>();
        Data.selectedBuffIds.Add(buffId);
        Save();
        LogKit.Log($"[MetaManager] 已记录选择的升级Buff: {buffId}");
    }

        public bool HasSelectedBuff(string buffId)
    {
        return Data.selectedBuffIds != null && Data.selectedBuffIds.Contains(buffId);
    }

    /// <summary>累加局次与胜场，并按本局结果刷新历史峰值</summary>
    public void RecordRunEnd(bool victory, int wave, int kills)
    {
        Data.totalRuns++;
        if (victory) Data.victoryRuns++;
        if (wave > Data.bestWave) Data.bestWave = wave;
        if (kills > Data.bestKills) Data.bestKills = kills;

        LogKit.Log($"[MetaManager] 本局记录: 胜利={victory}, 波次={wave}, 击杀={kills}, " +
                   $"累计{Data.totalRuns}局/{Data.victoryRuns}胜");
    }

    /// <summary>清除持久化局外进度，重建默认状态并立即保存（测试入口）</summary>
    public void ResetAllMeta()
    {
        SaveFactory.DeleteAll();
        Data = new MetaSaveData();
        BuffSystem.BuildFromBuffIds(null);
        EnsureDefaultCharacters();
        EnsureDefaultSkills();
        EnsureDungeonDefaults();
        Save();
        LogKit.Log("[MetaManager] 局外存档已重置");
    }

    // 副本进度

    /// <summary>选中副本；存档为空时回退配置默认值</summary>
    public string SelectedDungeonId
    {
        get
        {
            if (!string.IsNullOrEmpty(Data?.selectedDungeonId)) return Data.selectedDungeonId;
            return GameDataMgr.Instance.CurrentDungeon?.dungeonId;
        }
    }

    /// <summary>合并存档进度与配置初始状态判断副本解锁</summary>
    public bool IsDungeonUnlocked(string dungeonId)
    {
        if (string.IsNullOrEmpty(dungeonId)) return false;
        if (Data?.unlockedDungeonIds != null && Data.unlockedDungeonIds.Contains(dungeonId)) return true;

        var cfg = GameDataMgr.Instance.GetDungeon(dungeonId);
        return cfg != null && cfg.isUnlocked;
    }

    /// <summary>仅允许选中已解锁副本；状态变化时立即保存</summary>
    public bool SelectDungeon(string dungeonId)
    {
        if (!IsDungeonUnlocked(dungeonId)) return false;
        if (Data.selectedDungeonId == dungeonId) return true;

        Data.selectedDungeonId = dungeonId;
        Save();
        LogKit.Log($"[MetaManager] 已选择副本: {dungeonId}");
        return true;
    }

    /// <summary>登记新的副本解锁并立即保存；重复解锁返回 false</summary>
    public bool UnlockDungeon(string dungeonId)
    {
        if (string.IsNullOrEmpty(dungeonId) || Data == null) return false;

        Data.unlockedDungeonIds ??= new List<string>();
        if (Data.unlockedDungeonIds.Contains(dungeonId)) return false;

        Data.unlockedDungeonIds.Add(dungeonId);
        Save();
        LogKit.Log($"[MetaManager] 副本已解锁: {dungeonId}");
        return true;
    }

    /// <summary>按配置顺序解锁后继关卡并保存；无可解锁项时返回 null</summary>
    public DungeonConfig UnlockNextDungeon(string currentDungeonId)
    {
        var list = GameDataMgr.Instance.Dungeons?.Dungeons;
        if (list == null || list.Count == 0) return null;

        int index = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null && list[i].dungeonId == currentDungeonId) { index = i; break; }
        }
        if (index < 0 || index + 1 >= list.Count) return null;

        var next = list[index + 1];
        if (next == null || IsDungeonUnlocked(next.dungeonId)) return null;

        UnlockDungeon(next.dungeonId);
        return next;
    }

    /// <summary>以配置初始状态补齐副本解锁与选择；幂等</summary>
    private void EnsureDungeonDefaults()
    {
        Data.unlockedDungeonIds ??= new List<string>();

                var list = GameDataMgr.Instance.Dungeons?.Dungeons;
        if (list != null)
        {
            foreach (var s in list)
            {
                if (s == null || string.IsNullOrEmpty(s.dungeonId)) continue;
                if (s.isUnlocked && !Data.unlockedDungeonIds.Contains(s.dungeonId))
                    Data.unlockedDungeonIds.Add(s.dungeonId);
            }
        }

        if (string.IsNullOrEmpty(Data.selectedDungeonId))
            Data.selectedDungeonId = GameDataMgr.Instance.CurrentDungeon?.dungeonId;
    }

    // 局外资产与制作

        public int Coins => Data?.coins ?? 0;

    /// <summary>最近一局发放的钱币，仅供运行期结算展示，不持久化</summary>
    public int LastRunCoinReward { get; private set; }

    /// <summary>增加局外钱币并广播余额变化；持久化由上层提交点负责</summary>
    public void AddCoins(int amount)
    {
        if (Data == null || amount <= 0) return;
        Data.coins += amount;
        EventKit.Type.Send(new GameEvents.MetaCoinEarned { Amount = amount });
        EventKit.Type.Send(new GameEvents.MetaCoinsChanged { Total = Data.coins });
        LogKit.Log($"[MetaManager] 钱币 +{amount}，当前 {Data.coins}");
    }

    /// <summary>余额足够时扣除钱币并广播变化；不自动保存</summary>
    public bool TrySpendCoins(int amount)
    {
        if (Data == null || Data.coins < amount) return false;
        Data.coins -= amount;
        EventKit.Type.Send(new GameEvents.MetaCoinsChanged { Total = Data.coins });
        return true;
    }

        public int GetIngredientCount(string id)
    {
        if (Data?.ingredientBackpack == null || string.IsNullOrEmpty(id)) return 0;
        foreach (var s in Data.ingredientBackpack)
            if (s != null && s.id == id) return s.count;
        return 0;
    }

        public void AddIngredient(string id, int count)
    {
        if (Data?.ingredientBackpack == null || string.IsNullOrEmpty(id) || count <= 0) return;
        foreach (var s in Data.ingredientBackpack)
        {
            if (s != null && s.id == id)
            {
                s.count += count;
                return;
            }
        }
        Data.ingredientBackpack.Add(new IngredientStack { id = id, count = count });
    }

    /// <summary>库存充足时扣除食材，数量归零则移除条目</summary>
    public bool TryRemoveIngredient(string id, int count)
    {
        if (Data?.ingredientBackpack == null || string.IsNullOrEmpty(id) || count <= 0) return false;
        foreach (var s in Data.ingredientBackpack)
        {
            if (s != null && s.id == id)
            {
                if (s.count < count) return false;
                s.count -= count;
                if (s.count <= 0) Data.ingredientBackpack.Remove(s);
                return true;
            }
        }
        return false;
    }

    /// <summary>按配置规则结算食材出售，扣除库存后将收益计入钱币</summary>
    public int SellIngredient(string id, int count)
    {
        var config = GameDataMgr.Instance.GetIngredient(id);
        if (config == null || count <= 0) return 0;
        if (!TryRemoveIngredient(id, count)) return 0;

        float multiplier = GameDataMgr.Instance.GetCraftRules().ingredientSellMultiplier;
        int gain = Mathf.RoundToInt(config.sellPrice * multiplier * count);
        AddCoins(gain);
        LogKit.Log($"[MetaManager] 出售 {id} ×{count}，获得 {gain} 钱币");
        return gain;
    }

    /// <summary>将单局剩余食材归还局外背包；不负责清空来源库存</summary>
    public void RefundUnusedIngredients(Dictionary<string, int> inGameInventory)
    {
        if (Data?.ingredientBackpack == null || inGameInventory == null) return;

        foreach (var kv in inGameInventory)
        {
            if (kv.Value > 0)
                AddIngredient(kv.Key, kv.Value);
        }
        LogKit.Log($"[MetaManager] 本局 {inGameInventory.Count} 种未消耗食材已返还局外背包");
    }

    /// <summary>返回成品菜实例列表的浅拷贝，并在读取前修复旧档实例 ID</summary>
    public List<DishInstance> GetDishInventory()
    {
        if (Data?.dishInventory == null) return new List<DishInstance>();
        EnsureDishInstanceIds();
        return new List<DishInstance>(Data.dishInventory);
    }

        public int GetDishCount(string recipeId, int star)
    {
        if (Data?.dishInventory == null || string.IsNullOrEmpty(recipeId)) return 0;
        int count = 0;
        foreach (var d in Data.dishInventory)
            if (d != null && d.recipeId == recipeId && d.star == star) count++;
        return count;
    }

    /// <summary>按实例而非堆叠数量入库，确保每份成品菜拥有独立 ID</summary>
    public void AddDish(string recipeId, int star, int count = 1)
    {
        if (Data?.dishInventory == null || string.IsNullOrEmpty(recipeId) || count <= 0) return;
        for (int i = 0; i < count; i++)
        {
            Data.dishInventory.Add(new DishInstance
            {
                instanceId = System.Guid.NewGuid().ToString("N"),
                recipeId = recipeId,
                star = star
            });
        }
        LogKit.Log($"[MetaManager] 成品菜入背包: {recipeId} ★{star} ×{count}");
    }

    /// <summary>以实例库存执行同类同星合成，规则由 CraftConfig 驱动</summary>
    public bool CombineDishes(string recipeId, int star)
    {
        var rules = GameDataMgr.Instance.GetCraftRules();
        if (string.IsNullOrEmpty(recipeId) || star < 1 || star >= rules.combineMaxStar) return false;
        if (GetDishCount(recipeId, star) < rules.combineRequiredCount) return false;

        if (!TryConsumeDish(recipeId, star, rules.combineRequiredCount)) return false;
        AddDish(recipeId, star + 1, 1);
        LogKit.Log($"[MetaManager] 合成成功: {recipeId} ★{star} ×{rules.combineRequiredCount} → ★{star + 1}");
        return true;
    }

    /// <summary>仅在库存足量时移除匹配实例，避免部分扣除</summary>
    public bool TryConsumeDish(string recipeId, int star, int count)
    {
        if (Data?.dishInventory == null || string.IsNullOrEmpty(recipeId) || count <= 0) return false;
        if (GetDishCount(recipeId, star) < count) return false;

        int removed = 0;
        for (int i = Data.dishInventory.Count - 1; i >= 0 && removed < count; i--)
        {
            var d = Data.dishInventory[i];
            if (d != null && d.recipeId == recipeId && d.star == star)
            {
                Data.dishInventory.RemoveAt(i);
                removed++;
            }
        }
        return removed == count;
    }

    /// <summary>为旧档中缺失 ID 的菜品实例补齐唯一标识；读取库存时惰性迁移</summary>
    private void EnsureDishInstanceIds()
    {
        if (Data?.dishInventory == null) return;
        foreach (var d in Data.dishInventory)
        {
            if (d != null && string.IsNullOrEmpty(d.instanceId))
            {
                d.instanceId = System.Guid.NewGuid().ToString("N");
            }
        }
    }

    // 武器库存

    /// <summary>返回武器实例列表的浅拷贝，并在首次读取时补默认库存与旧档 ID</summary>
    public List<WeaponInstance> GetWeaponInventory()
    {
        if (Data?.weaponInventory == null) return new List<WeaponInstance>();
        EnsureDefaultWeapons();
        EnsureWeaponInstanceIds();
        return new List<WeaponInstance>(Data.weaponInventory);
    }

    /// <summary>仅在库存为空时补初始武器，避免后续新增配置被旧档自动拥有</summary>
    private void EnsureDefaultWeapons()
    {
        if (Data?.weaponInventory == null || Data.weaponInventory.Count > 0) return;

        // 默认拥有的武器（其余武器需玩家在商店购买解锁；
        // 至少保留一把可购买武器：sauce_sprayer 已移出默认集合，改为商店上架）
        var defaults = new HashSet<string> { "knife", "pan", "pepper_gun" };

        var weapons = GameDataMgr.Instance.Weapons?.Weapons;
        if (weapons == null) return;
        foreach (var w in weapons)
        {
            if (w == null || string.IsNullOrEmpty(w.id) || !defaults.Contains(w.id)) continue;
            Data.weaponInventory.Add(new WeaponInstance
            {
                instanceId = System.Guid.NewGuid().ToString("N"),
                weaponId = w.id
            });
        }
        LogKit.Log($"[MetaManager] 初始武器库存补齐：{Data.weaponInventory.Count} 把（默认武器）");
    }

    /// <summary>迁移：为缺失 id 的武器实例补 instanceId</summary>
    private void EnsureWeaponInstanceIds()
    {
        if (Data?.weaponInventory == null) return;
        foreach (var w in Data.weaponInventory)
        {
            if (w != null && string.IsNullOrEmpty(w.instanceId))
            {
                w.instanceId = System.Guid.NewGuid().ToString("N");
            }
        }
    }

    // ===== 商店：出售 / 购买 =====

    /// <summary>指定武器拥有数量（实例数）</summary>
    public int GetWeaponCount(string weaponId)
    {
        if (Data?.weaponInventory == null || string.IsNullOrEmpty(weaponId)) return 0;
        int c = 0;
        foreach (var w in Data.weaponInventory)
            if (w != null && w.weaponId == weaponId) c++;
        return c;
    }

    /// <summary>消耗武器（移除 count 个匹配实例，返回是否成功）</summary>
    private bool TryConsumeWeapon(string weaponId, int count)
    {
        if (Data?.weaponInventory == null || string.IsNullOrEmpty(weaponId) || count <= 0) return false;
        if (GetWeaponCount(weaponId) < count) return false;

        int removed = 0;
        for (int i = Data.weaponInventory.Count - 1; i >= 0 && removed < count; i--)
        {
            if (Data.weaponInventory[i] != null && Data.weaponInventory[i].weaponId == weaponId)
            {
                Data.weaponInventory.RemoveAt(i);
                removed++;
            }
        }
        return removed == count;
    }

    /// <summary>出售成品菜：扣除 count 个实例，卖价 = sellPrice × 星级 × 规则倍率，返回获得钱数</summary>
    public int SellDish(string recipeId, int star, int count)
    {
        var recipe = GameDataMgr.Instance.GetRecipe(recipeId);
        if (recipe == null || star < 1 || count <= 0) return 0;
        if (!TryConsumeDish(recipeId, star, count)) return 0;

        float multiplier = GameDataMgr.Instance.GetCraftRules().dishSellStarMultiplier;
        int gain = Mathf.RoundToInt(recipe.sellPrice * star * multiplier * count);
        AddCoins(gain);
        LogKit.Log($"[MetaManager] 出售成品菜 {recipeId} ★{star} ×{count}，获得 {gain} 钱币");
        return gain;
    }

    /// <summary>出售武器：扣除 count 个实例，卖价 = sellPrice × 规则倍率，返回获得钱数</summary>
    public int SellWeapon(string weaponId, int count)
    {
        var cfg = GameDataMgr.Instance.GetWeapon(weaponId);
        if (cfg == null || count <= 0) return 0;
        if (!TryConsumeWeapon(weaponId, count)) return 0;

        float multiplier = GameDataMgr.Instance.GetCraftRules().weaponSellMultiplier;
        int gain = Mathf.RoundToInt(cfg.sellPrice * multiplier * count);
        AddCoins(gain);
        LogKit.Log($"[MetaManager] 出售武器 {weaponId} ×{count}，获得 {gain} 钱币");
        return gain;
    }

    /// <summary>购买角色：花钱解锁（未拥有且钱够时）</summary>
    public bool BuyCharacter(string id)
    {
        var cfg = GameDataMgr.Instance.GetCharacter(id);
        if (cfg == null || cfg.buyPrice <= 0 || HasCharacter(id)) return false;
        if (!TrySpendCoins(cfg.buyPrice)) return false;

        Data.ownedCharacters.Add(id);
        LogKit.Log($"[MetaManager] 购买角色 {id}，花费 {cfg.buyPrice} 钱币");
        return true;
    }

    /// <summary>购买武器：花钱新增一把实例（未拥有且钱够时）</summary>
    public bool BuyWeapon(string id)
    {
        var cfg = GameDataMgr.Instance.GetWeapon(id);
        if (cfg == null || cfg.buyPrice <= 0 || GetWeaponCount(id) > 0) return false;
        if (!TrySpendCoins(cfg.buyPrice)) return false;

        Data.weaponInventory.Add(new WeaponInstance
        {
            instanceId = System.Guid.NewGuid().ToString("N"),
            weaponId = id
        });
        LogKit.Log($"[MetaManager] 购买武器 {id}，花费 {cfg.buyPrice} 钱币");
        return true;
    }

    /// <summary>
    /// 购买菜品：花钱获得 1 个 ★1 成品菜（菜谱未解锁时同时永久解锁）。
    /// 菜品同类可合成（可堆叠耗材），已解锁仍可重复购买；唯一物品（角色/武器/技能）由各自 Buy 方法拦截
    /// </summary>
    public bool BuyRecipe(string id)
    {
        var cfg = GameDataMgr.Instance.GetRecipe(id);
        if (cfg == null || cfg.buyPrice <= 0) return false;
        if (!TrySpendCoins(cfg.buyPrice)) return false;

        UnlockRecipe(id);   // 已解锁时内部直接返回，不影响本次购买
        AddDish(id, 1, 1);
        LogKit.Log($"[MetaManager] 购买菜品 {id}，花费 {cfg.buyPrice} 钱币（获得 ★1 成品菜）");
        return true;
    }

    /// <summary>主动技能是否已购买解锁</summary>
    public bool IsSkillUnlocked(string id)
    {
        return Data?.unlockedSkillIds != null && !string.IsNullOrEmpty(id) && Data.unlockedSkillIds.Contains(id);
    }

    /// <summary>购买技能：花钱解锁（未解锁且钱够时）</summary>
    public bool BuySkill(string id)
    {
        var cfg = GameDataMgr.Instance.GetActiveSkill(id);
        if (cfg == null || cfg.buyPrice <= 0 || IsSkillUnlocked(id)) return false;
        if (!TrySpendCoins(cfg.buyPrice)) return false;

        Data.unlockedSkillIds.Add(id);
        LogKit.Log($"[MetaManager] 购买技能 {id}，花费 {cfg.buyPrice} 钱币");
        return true;
    }

    /// <summary>是否拥有角色</summary>
    public bool HasCharacter(string id)
    {
        return Data?.ownedCharacters != null && !string.IsNullOrEmpty(id) && Data.ownedCharacters.Contains(id);
    }

    /// <summary>初始拥有角色（首次进入时补默认角色）</summary>
    public void EnsureDefaultCharacters()
    {
        if (Data?.ownedCharacters == null) return;
        if (Data.ownedCharacters.Count == 0)
        {
            // 默认拥有第一个角色
            var characters = GameDataMgr.Instance.Characters?.Characters;
            if (characters != null && characters.Count > 0)
            {
                Data.ownedCharacters.Add(characters[0].id);
            }
        }
    }

    /// <summary>
    /// 角色附赠技能兜底（幂等）：把「已拥有角色」的 CharacterConfig.starterSkillIds 写入已解锁技能。
    /// 为什么需要：角色专属技能（skillIds）一律要商店购买才解锁，拥有角色不等于拥有其技能；
    /// 但初始角色若不附赠两个技能，新档将只剩共有池可用。starterSkillIds 就是这份"角色自带"清单。
    /// </summary>
    public void EnsureDefaultSkills()
    {
        if (Data?.unlockedSkillIds == null) return;

        var characters = GameDataMgr.Instance.Characters?.Characters;
        if (characters == null) return;

        int added = 0;
        foreach (var c in characters)
        {
            if (c == null || c.starterSkillIds == null) continue;
            if (!HasCharacter(c.id)) continue;          // 未拥有该角色 → 其附赠技能也不生效
            foreach (var s in c.starterSkillIds)
            {
                if (string.IsNullOrEmpty(s) || Data.unlockedSkillIds.Contains(s)) continue;
                Data.unlockedSkillIds.Add(s);
                added++;
            }
        }

        if (added > 0) LogKit.Log($"[MetaManager] 补角色附赠技能 {added} 个");
    }

    /// <summary>战前配置存档（读写入口）</summary>
    public PreparationSaveData Preparation => Data?.preparation ?? new PreparationSaveData();

    // ===== 游戏设置：武器主动「自动释放」偏好（设置面板-游戏页签） =====
    // 说明：与音量同源，读写 SaveDataModel.settings（跨局持久化）。键为**武器技能槽序号**
    //       （0 = 界面上的「技能槽1」），与设置面板的 3 个固定开关、HUD 武器槽、AutoAttack 的武器槽同序同源。

    /// <summary>武器技能槽数量（设置面板的 3 个自动释放开关 / HUD 的 3 个武器槽 / AutoAttack 的 3 槽）</summary>
    public const int WeaponSkillSlotCount = 3;

    /// <summary>存档设置对象（未加载存档时返回 null）</summary>
    public SettingsSaveData Settings => Model?.settings;

    /// <summary>武器技能槽的自动释放是否开启（缺省 = 开；存储的是"关闭列表"；槽位越界一律视为开）</summary>
    public bool IsAutoCastEnabled(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= WeaponSkillSlotCount) return true;
        var off = Model?.settings?.autoCastOffSlots;
        return off == null || !off.Contains(slotIndex);
    }

    /// <summary>
    /// 设置某个武器技能槽的自动释放（写存档 + 广播 WeaponAutoCastChanged，运行中的 WeaponActiveSystem 即时生效）。
    /// 无变化时直接返回，避免每次点开面板都写盘。
    /// </summary>
    public void SetAutoCastEnabled(int slotIndex, bool enabled)
    {
        if (slotIndex < 0 || slotIndex >= WeaponSkillSlotCount || Model == null) return;

        if (Model.settings == null) Model.settings = new SettingsSaveData();
        var off = Model.settings.autoCastOffSlots ??= new List<int>();

        if (enabled)
        {
            if (!off.Remove(slotIndex)) return;      // 本来就是开启 → 无变化
        }
        else
        {
            if (off.Contains(slotIndex)) return;
            off.Add(slotIndex);
        }

        Save();
        EventKit.Type.Send(new GameEvents.WeaponAutoCastChanged { SlotIndex = slotIndex, Enabled = enabled });
        LogKit.Log($"[MetaManager] 技能槽 {slotIndex + 1} 自动释放 = {(enabled ? "开" : "关")}");
    }

    // ===== 局外永久升级（MetaUpgrade：钱币购买永久属性加成，只做加值） =====

    /// <summary>查询某升级项当前等级（未升级/未配置返回 0）</summary>
    public int GetUpgradeLevel(string id)
    {
        if (Data?.metaUpgradeLevels == null || string.IsNullOrEmpty(id)) return 0;
        foreach (var l in Data.metaUpgradeLevels)
            if (l != null && l.id == id) return l.level;
        return 0;
    }

    /// <summary>升到下一级所需花费（未配置或已满级返回 -1）</summary>
    public int GetUpgradeCost(string id)
    {
        var cfg = GameDataMgr.Instance.GetMetaUpgrade()?.GetById(id);
        if (cfg == null || cfg.maxLevel <= 0) return -1;

        int level = GetUpgradeLevel(id);
        if (level >= cfg.maxLevel) return -1;

        // 价格曲线：升到 L+1 级花费 = round(baseCost × (1 + costGrowth)^L)（0→1 级 = baseCost）
        return Mathf.RoundToInt(cfg.baseCost * Mathf.Pow(1f + cfg.costGrowth, level));
    }

    /// <summary>是否可升级（未满级 且 钱币足够）</summary>
    public bool CanUpgrade(string id)
    {
        int cost = GetUpgradeCost(id);
        return cost >= 0 && Coins >= cost;
    }

    /// <summary>尝试升级：扣币 + 等级+1 + 保存 + 上报（返回是否成功）</summary>
    public bool TryUpgrade(string id)
    {
        if (Data == null || string.IsNullOrEmpty(id)) return false;

        var cfg = GameDataMgr.Instance.GetMetaUpgrade()?.GetById(id);
        if (cfg == null) return false;

        int cost = GetUpgradeCost(id);
        if (cost < 0) return false;                 // 已满级
        if (!TrySpendCoins(cost)) return false;     // 钱币不足

        Data.metaUpgradeLevels ??= new List<MetaUpgradeLevel>();
        MetaUpgradeLevel entry = null;
        foreach (var l in Data.metaUpgradeLevels)
            if (l != null && l.id == id) { entry = l; break; }
        if (entry == null)
        {
            entry = new MetaUpgradeLevel { id = id, level = 0 };
            Data.metaUpgradeLevels.Add(entry);
        }
        entry.level++;

        Save();
        EventKit.Type.Send(new GameEvents.MetaUpgradePurchased { Id = id, Level = entry.level });
        LogKit.Log($"[MetaManager] 永久升级 {id} → Lv{entry.level}，花费 {cost} 钱币");
        return true;
    }

    /// <summary>聚合所有永久升级为属性加成（PlayerAttributeAggregator.BuildBase 叠加；只做加值）</summary>
    public PlayerAttribute BuildMetaUpgradeAttribute()
    {
        var attr = new PlayerAttribute();
        var entries = GameDataMgr.Instance.GetMetaUpgrade()?.Entries;
        if (entries == null) return attr;

        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrEmpty(e.id)) continue;
            int level = GetUpgradeLevel(e.id);
            if (level <= 0) continue;

            ApplyMetaUpgradeAttr(ref attr, e.attrKey, GetUpgradeTotalValue(e, level));
        }
        return attr;
    }

    /// <summary>
    /// 累计 L 级的总增量 = Σ valuePerLevel × (1 + valueGrowth × (i-1))（i=1..L）
    /// 等差数列求和化简：valuePerLevel × (L + valueGrowth × L(L-1)/2)
    /// </summary>
    private static float GetUpgradeTotalValue(MetaUpgradeEntry entry, int level)
    {
        return entry.valuePerLevel * (level + entry.valueGrowth * level * (level - 1) / 2f);
    }

    /// <summary>按 attrKey 把加值写入对应属性字段（只做加值，无百分比）</summary>
    private static void ApplyMetaUpgradeAttr(ref PlayerAttribute attr, string attrKey, float value)
    {
        switch (attrKey)
        {
            case "MaxHp":            attr.MaxHp += value; break;
            case "Attack":           attr.Attack += value; break;
            case "PhysDef":          attr.PhysDef += value; break;
            case "ElemDef":          attr.ElemDef += value; break;
            case "MoveSpeed":        attr.MoveSpeed += value; break;
            case "AttackSpeed":      attr.AttackSpeed += value; break;
            case "DodgeChance":      attr.DodgeChance += value; break;
            case "CritChance":       attr.CritChance += value; break;
            case "CritDamage":       attr.CritDamage += value; break;
            case "PhysDamageBonus":  attr.PhysDamageBonus += value; break;
            case "ElemDamageBonus":  attr.ElemDamageBonus += value; break;
            case "MaxMana":          attr.MaxMana += value; break;
            case "ManaRegen":        attr.ManaRegen += value; break;
            default:
                LogKit.Warning($"[MetaManager] 未知升级属性键: {attrKey}");
                break;
        }
    }

    /// <summary>局末结算发币：按波次/击杀/胜利计算并发放，返回本局获得钱币（写入 LastRunCoinReward）</summary>
    public int AwardRunCoins(int waves, int kills, bool victory)
    {
        // 发币规则统一由 MetaShopConfig.coinSources 提供（单一数据源，原 MetaUpgradeConfig.reward 已移除）
        var economy = GameDataMgr.Instance.GetMetaShop();
        int perWave = economy.GetCoinSourceValue("perWave");
        int perKill = economy.GetCoinSourceValue("perKill");
        int victoryBonus = economy.GetCoinSourceValue("victoryBonus");

        int amount = Mathf.Max(0, waves) * perWave
                   + Mathf.Max(0, kills) * perKill
                   + (victory ? victoryBonus : 0);

        LastRunCoinReward = amount;
        if (amount > 0) AddCoins(amount);
        LogKit.Log($"[MetaManager] 局末发币：波次{waves}×{perWave} + 击杀{kills}×{perKill}" +
                   $"{(victory ? $" + 通关{victoryBonus}" : "")} = {amount}");
        return amount;
    }
}
