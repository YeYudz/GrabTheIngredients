using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 角色 / 战前配置管理器（业务层单例）：战前配置（角色 / 武器 / 装配菜品 / 技能组）的唯一读写方，
/// 配置持久化在 MetaManager.Preparation，进入游戏时由本类把配置落到玩家身上。
/// 只提供查询与配置接口、不操作显示，使战前配置与 UI 共用同一数据源且互不知晓。
/// </summary>
public class RosterManager : MonoSingleton<RosterManager>
{
    /// <summary>战前配置存档入口</summary>
    public PreparationSaveData Preparation => MetaManager.Instance?.Preparation;

    /// <summary>装配菜品数量上限</summary>
    public const int MaxEquippedDishes = 3;

    /// <summary>可选主动技能槽位数量（技能栏 = N 个可选技能；角色被动常驻、不占槽位）</summary>
    public const int MaxSelectableSkills = 2;

    // ===== 查询（存档未配置时逐级兜底） =====

    /// <summary>当前角色ID（空则默认第一个拥有角色/第一个角色）</summary>
    public string GetCurrentCharacterId()
    {
        if (!string.IsNullOrEmpty(Preparation.characterId)) return Preparation.characterId;

        // 兜底 1：第一个已拥有角色
        var owned = MetaManager.Instance.Data.ownedCharacters;
        if (owned != null && owned.Count > 0) return owned[0];

        // 兜底 2：配置里的第一个角色
        var characters = GameDataMgr.Instance.Characters?.Characters;
        if (characters != null && characters.Count > 0) return characters[0].id;
        return null;
    }

    /// <summary>当前角色配置</summary>
    public CharacterConfig GetCurrentCharacter()
    {
        return GameDataMgr.Instance.GetCharacter(GetCurrentCharacterId());
    }

    /// <summary>当前武器槽位列表（空则兜底角色默认武器）</summary>
    public List<string> GetCurrentWeaponIds()
    {
        if (Preparation.weaponIds != null && Preparation.weaponIds.Count > 0)
            return Preparation.weaponIds;

        // 兜底：角色默认武器
        var character = GetCurrentCharacter();
        if (character != null && !string.IsNullOrEmpty(character.defaultWeaponId))
            return new List<string> { character.defaultWeaponId };
        return new List<string> { "knife" };
    }

    /// <summary>当前武器槽位存档值（不做兜底：空列表 = 未配置，供战前准备页显示/编辑）</summary>
    public List<string> GetStoredWeaponIds()
    {
        return Preparation.weaponIds ?? new List<string>();
    }

    /// <summary>写入武器槽位列表；null 视为清空</summary>
    public void SetWeaponIds(List<string> weaponIds)
    {
        Preparation.weaponIds = weaponIds ?? new List<string>();
        LogKit.Log($"[RosterManager] 战前装备武器槽位 {Preparation.weaponIds.Count} 个");
    }

    /// <summary>当前装配菜品</summary>
    public List<EquippedDish> GetEquippedDishes()
    {
        return Preparation.equippedDishes ?? new List<EquippedDish>();
    }

    /// <summary>
    /// 已装配的具体实例ID集合（装配精确到实例）。
    /// 旧档装配项未绑实例（InstanceId 为空）时不计入：宁可不标也不误标，保证背包侧自洽。
    /// </summary>
    public HashSet<string> GetEquippedInstanceIds()
    {
        var result = new HashSet<string>();
        var dishes = GetEquippedDishes();
        if (dishes == null) return result;
        foreach (var d in dishes)
            if (!string.IsNullOrEmpty(d.InstanceId)) result.Add(d.InstanceId);
        return result;
    }

    /// <summary>
    /// 本局技能栏 = 玩家选择的可选技能（最多 MaxSelectableSkills 个），供 ActiveSkillSystem / HUD 使用。
    /// 武器主动技能不在此列（每武器独立主动由 WeaponActiveSystem 管理）；不做兜底：槽位为空即不装备，只剩被动。
    /// </summary>
    public List<string> GetCurrentSkillIds()
    {
        var result = new List<string>();

        // 存档可选槽：空槽与重复项跳过，超出上限的截断
        var stored = Preparation.skillIds;
        if (stored != null)
        {
            int selectable = 0;
            for (int i = 0; i < stored.Count && selectable < MaxSelectableSkills; i++)
            {
                if (string.IsNullOrEmpty(stored[i]) || result.Contains(stored[i])) continue;
                result.Add(stored[i]);
                selectable++;
            }
        }
        return result;
    }

    /// <summary>
    /// 可自由搭配的主动技能池（战斗准备页 + 技能槽清洗共用）= 共有池 ∪ 已解锁技能。
    /// 角色专属技能只有归属角色能装配，别的角色的专属技能即使已购买也不进本角色的池。
    /// 不做兜底：已解锁技能为空即只剩共有池。
    /// </summary>
    public List<string> GetSelectableSkillPool()
    {
        var pool = new List<string>();

        // 1) 共有池：与角色无关的基线池
        var wrapper = GameDataMgr.Instance.Characters;
        if (wrapper?.commonSkillIds != null)
            foreach (var s in wrapper.commonSkillIds)
                if (!string.IsNullOrEmpty(s) && !pool.Contains(s)) pool.Add(s);

        // 2) 已解锁技能（购买 / 角色附赠）：专属技能只归其归属角色；配置里已不存在的 id 跳过，
        //    否则旧档残留的死技能会被 SanitizeSkillIds 判为合法而留在装备槽上
        var character = GetCurrentCharacter();
        if (MetaManager.HasInstance && MetaManager.Instance.Data?.unlockedSkillIds != null)
            foreach (var s in MetaManager.Instance.Data.unlockedSkillIds)
                if (!string.IsNullOrEmpty(s) && !pool.Contains(s)
                    && GameDataMgr.Instance.GetActiveSkill(s) != null
                    && IsUsableByCharacter(s, character?.id))
                    pool.Add(s);

        return pool;
    }

    /// <summary>
    /// 技能归属的专属角色ID（null = 非角色专属：共有池 / 武器主动 / 无归属）。
    /// 归属取 CharacterConfig.skillIds，一个技能只允许有一个归属角色。
    /// </summary>
    public static string GetExclusiveOwner(string skillId)
    {
        if (string.IsNullOrEmpty(skillId)) return null;

        var characters = GameDataMgr.Instance.Characters?.Characters;
        if (characters == null) return null;

        foreach (var c in characters)
        {
            if (c?.skillIds == null) continue;
            if (c.skillIds.Contains(skillId)) return c.id;
        }
        return null;
    }

    /// <summary>
    /// 该技能能否被指定角色装配：非角色专属（共有池 / 武器主动）人人可用，
    /// 角色专属则只有归属角色可用。
    /// </summary>
    public static bool IsUsableByCharacter(string skillId, string characterId)
    {
        var owner = GetExclusiveOwner(skillId);
        return owner == null || owner == characterId;
    }

    /// <summary>
    /// 技能是否"已解锁"（= 玩家真的拥有它）。<b>全工程统一口径</b>：图鉴、商店等一切解锁判定都必须走这里。
    /// 判定来源（任一满足）：已解锁记录 MetaManager.Data.unlockedSkillIds（购买 / 角色附赠）、
    /// 共有池 commonSkillIds、已拥有武器的主动技能 WeaponConfig.activeSkillId（随武器一起获得）。
    /// 角色专属池 CharacterConfig.skillIds <b>不在</b>其中：拥有角色 ≠ 拥有其专属技能，专属技能同样要购买后才写入 unlockedSkillIds；
    /// "专属技能只有该角色能装配"是另一件事，由 GetSelectableSkillPool / IsUsableByCharacter 负责，不要混进解锁判定。
    /// 不直接用 MetaManager.IsSkillUnlocked：它只读 unlockedSkillIds，而共有池与武器主动技能永远不会写进去，
    /// 用它当条件会把这些固有内容判为未解锁（图鉴技能分类曾因此全部只显示锁定图）。
    /// </summary>
    public static bool IsSkillAvailable(string skillId)
    {
        if (string.IsNullOrEmpty(skillId)) return false;

        var meta = MetaManager.HasInstance ? MetaManager.Instance : null;
        var gdm = GameDataMgr.Instance;
        var chars = gdm?.Characters;

        // 已解锁记录（商店购买 / 角色附赠）
        if (meta != null && meta.IsSkillUnlocked(skillId)) return true;

        // 共有池：与角色无关，视为默认解锁
        if (chars?.commonSkillIds != null && chars.commonSkillIds.Contains(skillId)) return true;

        // 已拥有武器的主动技能
        var weapons = gdm?.Weapons?.Weapons;
        if (weapons != null)
        {
            foreach (var w in weapons)
            {
                if (w == null || w.activeSkillId != skillId) continue;
                if (meta != null && meta.GetWeaponCount(w.id) > 0) return true;   // 武器未拥有 → 其主动技能也未解锁
            }
        }

        return false;
    }

    /// <summary>当前技能组存档值（不做兜底：空列表 = 未配置，供战前准备页显示/编辑）</summary>
    public List<string> GetStoredSkillIds()
    {
        return Preparation.skillIds ?? new List<string>();
    }

    /// <summary>
    /// 清洗技能槽存档：只保留「当前角色可选池」内的技能（去重 + 截断到 MaxSelectableSkills）。
    /// 必须清洗：技能池由数据配置 + 商店解锁决定，版本迭代后旧档会残留已不在池内的ID（改名/删除/换角色/槽位上限变化），
    /// 不清掉的话准备面板会显示列表里根本没有的技能，进战斗还会真的装备上。
    /// 有变更时自动落盘（与 EnsureEquippedDishInstances 的自愈风格一致）。
    /// </summary>
    /// <returns>是否发生了裁剪</returns>
    public bool SanitizeSkillIds()
    {
        var stored = Preparation.skillIds;
        if (stored == null || stored.Count == 0) return false;

        var pool = GetSelectableSkillPool();
        var cleaned = new List<string>();
        var seen = new HashSet<string>();

        foreach (var id in stored)
        {
            if (cleaned.Count >= MaxSelectableSkills) break; // 超出槽位上限的旧数据直接丢弃
            if (string.IsNullOrEmpty(id)) continue;
            if (!pool.Contains(id)) continue;                // 已不在可选池（旧档残留 / 换了角色）
            if (!seen.Add(id)) continue;
            cleaned.Add(id);
        }

        if (cleaned.Count == stored.Count)
        {
            bool same = true;
            for (int i = 0; i < cleaned.Count; i++)
                if (cleaned[i] != stored[i]) { same = false; break; }
            if (same) return false;
        }

        LogKit.Log($"[RosterManager] 技能槽清洗：{stored.Count} → {cleaned.Count}（剔除不在当前可选池中的技能）");
        Preparation.skillIds = cleaned;
        SavePreparation();
        return true;
    }

    // ===== 战前配置修改 =====

    /// <summary>选择角色</summary>
    public void SetCharacter(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)) return;
        Preparation.characterId = characterId;

        // 换角色不重置武器：武器与角色解耦
        LogKit.Log($"[RosterManager] 战前选择角色: {characterId}");
    }

    /// <summary>装备武器（旧单武器接口兼容：写回第一槽）</summary>
    public void SetWeapon(string weaponId)
    {
        if (string.IsNullOrEmpty(weaponId)) return;
        var ids = GetCurrentWeaponIds();
        if (ids.Count > 0) ids[0] = weaponId;
        else ids.Add(weaponId);
        Preparation.weaponIds = ids;
        LogKit.Log($"[RosterManager] 战前装备武器: {weaponId}");
    }

    /// <summary>设置装配菜品（上限 MaxEquippedDishes）</summary>
    public void SetEquippedDishes(List<EquippedDish> dishes)
    {
        if (dishes == null) return;
        Preparation.equippedDishes = dishes;
        LogKit.Log($"[RosterManager] 战前装配菜品 {dishes.Count} 个");
    }

    /// <summary>
    /// 迁移：为旧档装配项（InstanceId 为空）从背包补绑一个"同组合且未被占用"的实例，使装配精确到实例。
    /// 无可用实例时保持为空（背包按组合回退兼容）；打开背包 / 战前准备时调用。
    /// </summary>
    public void EnsureEquippedDishInstances()
    {
        var dishes = GetEquippedDishes();
        if (dishes == null || dishes.Count == 0) return;
        if (!MetaManager.HasInstance) return;

        var taken = new HashSet<string>();
        foreach (var d in dishes)
            if (!string.IsNullOrEmpty(d.InstanceId)) taken.Add(d.InstanceId);

        var inventory = MetaManager.Instance.GetDishInventory();
        bool changed = false;
        for (int i = 0; i < dishes.Count; i++)
        {
            var d = dishes[i];
            if (string.IsNullOrEmpty(d.RecipeId) || !string.IsNullOrEmpty(d.InstanceId)) continue;

            foreach (var inst in inventory)
            {
                if (inst == null || inst.recipeId != d.RecipeId || inst.star != d.Star) continue;
                if (taken.Contains(inst.instanceId)) continue;

                d.InstanceId = inst.instanceId;
                taken.Add(inst.instanceId);
                changed = true;
                break;
            }
            dishes[i] = d;
        }

        if (changed) SavePreparation();
    }

    /// <summary>写入技能组；null 视为清空</summary>
    public void SetSkillIds(List<string> skillIds)
    {
        Preparation.skillIds = skillIds ?? new List<string>();
        LogKit.Log($"[RosterManager] 战前选择技能组 {Preparation.skillIds.Count} 个");
    }

    /// <summary>保存战前配置到存档</summary>
    public void SavePreparation()
    {
        MetaManager.Instance.Save();
    }

    // ===== 应用到战斗 =====

    /// <summary>进入游戏时调用：把战前配置应用到玩家（属性/武器/主动技能）</summary>
    public void ApplyLoadoutToPlayer()
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        // 技能槽自愈：剔除旧档里已不在可选池的技能，避免装备到"列表里没有"的技能
        SanitizeSkillIds();

        var character = GetCurrentCharacter();
        player.ApplyBaseAttribute(PlayerAttributeAggregator.BuildBase(character, GetCurrentWeaponIds()));
        player.ApplyCharacterVisual(character?.id);

        var autoAttack = player.GetComponent<AutoAttack>();
        if (autoAttack != null)
        {
            // 武器槽最多 3 把且同时生效，无切换
            autoAttack.SetWeaponSlots(GetCurrentWeaponIds());
        }

        ActiveSkillSystem.Instance.Init(GetCurrentSkillIds());

        // 星级效果：combine 叠加 / 替换，装配后作为玩家被动或条件效果
        ApplyDishEffects(player);

        // 角色唯一被动常驻，免佩戴
        ApplyCharacterPassive(player, character);

        LogKit.Log($"[RosterManager] 战前配置已应用到玩家: 角色={GetCurrentCharacterId()}, 武器={GetCurrentWeaponIds().Count}把, " +
                   $"菜品={GetEquippedDishes().Count}, 技能={GetCurrentSkillIds().Count}");
    }

    /// <summary>应用角色唯一被动技能（引用 GameEffectConfig 效果库，常驻/条件触发）</summary>
    private static void ApplyCharacterPassive(PlayerController player, CharacterConfig character)
    {
        if (character == null || string.IsNullOrEmpty(character.passiveEffectId)) return;

        var effect = GameDataMgr.Instance.GetEffect(character.passiveEffectId);
        if (effect == null)
        {
            LogKit.Warning($"[RosterManager] 未找到角色被动效果: {character.passiveEffectId}（角色 {character.characterName}）");
            return;
        }
        PlayerEffectController.GetOrAdd(player)?.ApplyEffect(effect);
    }

    /// <summary>把装配成品菜的星级复杂增益应用到玩家效果控制器</summary>
    private static void ApplyDishEffects(PlayerController player)
    {
        var dishes = RosterManager.Instance.GetEquippedDishes();
        if (dishes == null || dishes.Count == 0) return;

        var effectCtrl = PlayerEffectController.GetOrAdd(player);

        foreach (var dish in dishes)
        {
            if (string.IsNullOrEmpty(dish.RecipeId)) continue;
            var recipe = GameDataMgr.Instance.GetRecipe(dish.RecipeId);
            if (recipe?.starEffects == null || recipe.starEffects.Count == 0) continue;

            var ids = ResolveStarEffects(recipe, dish.Star);
            foreach (var id in ids)
            {
                var cfg = GameDataMgr.Instance.GetEffect(id);
                if (cfg == null)
                {
                    LogKit.Warning($"[RosterManager] 未找到效果配置: {id}（菜谱 {recipe.recipeName}）");
                    continue;
                }
                effectCtrl.ApplyEffect(cfg);
            }
        }
    }

    /// <summary>按星级解析效果链：取 star &lt;= 装配星级的全部条目叠加（星级无额外效果则沿用更低星级）</summary>
    public static List<string> ResolveStarEffects(RecipeConfig recipe, int star)
    {
        var result = new List<string>();
        foreach (var se in recipe.starEffects)
        {
            if (se == null || se.star > star) continue;
            if (!string.IsNullOrEmpty(se.effectId) && !result.Contains(se.effectId))
                result.Add(se.effectId);
        }
        return result;
    }

    /// <summary>重置：战前配置保留在存档，不清理</summary>
    public void Reset()
    {
        LogKit.Log("[RosterManager] 已重置（战前配置保留在存档）");
    }
}
