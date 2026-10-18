using System.Collections.Generic;
using UnityEngine;
using YokiFrame;
using GameUI;

/// <summary>
/// BOSS 战流程协调器：把「战斗 → 烹饪音游 → 结果处理」串成一条链路。
/// 血量清空只冻结战斗并转入音游，胜负由音游品质裁定——BOSS 的生死因此不取决于血量；
/// 破韧则退化为虚弱增伤状态，与音游解耦。
/// </summary>
public class BossBattleManager : MonoSingleton<BossBattleManager>
{
    private BossHealth mCurrentBoss;
    private CookingPanel mCookingPanel;
    private string mCurrentRecipeId;

    protected override void Awake()
    {
        base.Awake();

        EventKit.Type.Register<GameEvents.BossSpawned>(OnBossSpawned)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.BossHpDepleted>(OnBossHpDepleted)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        EventKit.Type.Register<GameEvents.CookingFinished>(OnCookingFinished)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    /// <summary>当前音游菜谱（断点保存"音游进行中"时记录，恢复后原样重开）</summary>
    public string CurrentRecipeId => mCurrentRecipeId;

    /// <summary>
    /// 断点恢复：音游进行中被中断（BOSS 血量已空）→ 直接重开音游面板接着玩。
    /// 不走 OnBossHpDepleted（那里会重新抽菜谱并再扣一次食材）。
    /// </summary>
    public void RestoreCookingInProgress(string recipeId, BossHealth boss)
    {
        if (boss == null) return;
        mCurrentBoss = boss;
        if (!string.IsNullOrEmpty(recipeId)) mCurrentRecipeId = recipeId;
        else if (string.IsNullOrEmpty(mCurrentRecipeId))
        {
            var cfg = GameDataMgr.Instance.GetBoss(boss.BossId);
            if (cfg != null) mCurrentRecipeId = cfg.recipeId;
        }

        Time.timeScale = 0f;
        OpenCookingPanel();
        LogKit.Log($"[BossBattleManager] 断点恢复：重开烹饪音游（菜谱 {mCurrentRecipeId}）");
    }

    /// <summary>BOSS 生成时缓存引用 + 记录菜谱 ID</summary>
    private void OnBossSpawned(GameEvents.BossSpawned evt)
    {
        // 取本次生成的 BOSS：优先 BossHealth.Current（Init 写入 / Die 清空）。
        // 不能直接 FindObjectOfType —— 上一阶段的 BOSS 在 Die() 后是延时 0.5s 销毁，
        // 而阶段结算期间 Time.timeScale=0 会让这个延时被冻结，跨阶段的瞬间场景里可能同时存在两只。
        mCurrentBoss = BossHealth.Current != null ? BossHealth.Current : FindObjectOfType<BossHealth>();
        if (mCurrentBoss != null)
        {
            var config = GameDataMgr.Instance.GetBoss(evt.BossId);
            if (config != null)
                mCurrentRecipeId = config.recipeId;
        }
        LogKit.Log($"[BossBattleManager] BOSS 生成: {evt.BossId}，菜谱: {mCurrentRecipeId}");
    }

    /// <summary>BOSS 血量清空 → 冻结战斗 → 确定本次要做的菜 → 打开烹饪音游</summary>
    private void OnBossHpDepleted(GameEvents.BossHpDepleted evt)
    {
        LogKit.Log("[BossBattleManager] BOSS 血量清空，启动烹饪音游");

        // Step10：从候选池选出本次要做的菜（BOSS 菜必出 + 本局食材凑齐的其它菜随机）
        PickRecipeForCooking();

        // 冻结战斗
        Time.timeScale = 0f;

        // 打开烹饪音游面板
        OpenCookingPanel();
    }

    /// <summary>
    /// Step10 炒菜候选池：
    ///   BOSS 对应菜谱必出（食材不足自动补足，保证音游可玩）+ 本局食材凑齐的其它菜谱（随机抽1）
    /// 选定后消耗对应食材各×1，结果存入 mCurrentRecipeId
    /// </summary>
    private void PickRecipeForCooking()
    {
        var candidates = new List<string>();

        // 1. BOSS 菜必出（不足自动补足）
        var bossRecipe = GameDataMgr.Instance.GetRecipe(mCurrentRecipeId);
        if (bossRecipe != null)
        {
            if (!DropManager.Instance.CanAffordRecipe(bossRecipe))
            {
                foreach (var req in bossRecipe.requiredIngredients)
                {
                    if (req == null) continue;
                    int lack = req.count - DropManager.Instance.GetIngredientCount(req.id);
                    if (lack > 0)
                        DropManager.Instance.AddInGameIngredient(req.id, lack);
                }
                LogKit.Log("[BossBattleManager] BOSS 菜食材不足，已自动补足");
            }
            candidates.Add(mCurrentRecipeId);
        }

        // 2. 本局食材凑齐的其它菜谱
        var recipes = GameDataMgr.Instance.Recipes?.Recipes;
        if (recipes != null)
        {
            foreach (var r in recipes)
            {
                if (r == null || string.IsNullOrEmpty(r.id)) continue;
                if (r.id == mCurrentRecipeId) continue;
                if (DropManager.Instance.CanAffordRecipe(r))
                    candidates.Add(r.id);
            }
        }

        if (candidates.Count == 0)
        {
            LogKit.Warning("[BossBattleManager] 候选池为空，使用 BOSS 菜");
            return;
        }

        // 从候选池随机抽 1 个作为本次要做的菜
        string picked = candidates[Random.Range(0, candidates.Count)];
        if (picked != mCurrentRecipeId)
        {
            LogKit.Log($"[BossBattleManager] 随机出菜: {picked}（候选 {candidates.Count} 个，BOSS菜={mCurrentRecipeId}）");
            mCurrentRecipeId = picked;
        }

        // 消耗食材
        var recipe = GameDataMgr.Instance.GetRecipe(mCurrentRecipeId);
        if (recipe != null)
        {
            DropManager.Instance.ConsumeRecipeIngredients(recipe);
            LogKit.Log($"[BossBattleManager] 本次炒菜: {recipe.recipeName}，已消耗对应食材");
        }
    }

    /// <summary>打开烹饪音游面板（预制体存在用 UIKit，否则动态创建）</summary>
    private void OpenCookingPanel()
    {
        var data = new CookingPanelData { RecipeId = mCurrentRecipeId };

        // 检查预制体是否存在
        var prefab = Resources.Load<GameObject>(AssetKeys.PANEL_PREFIX + "CookingPanel");
        if (prefab != null)
        {
            UIKit.OpenPanel<CookingPanel>(UILevel.Pop, data);
            return;
        }

        // 预制体不存在 → 动态创建面板实例
        LogKit.Log("[BossBattleManager] CookingPanel 预制体不存在，动态创建面板");
        var canvas = UIRoot.Instance?.Canvas;
        if (canvas == null)
        {
            LogKit.Error("[BossBattleManager] UIRoot.Canvas 为空，无法创建音游面板");
            return;
        }

        var go = new GameObject("CookingPanel", typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        mCookingPanel = go.AddComponent<CookingPanel>();
        mCookingPanel.ManualOpen(data);
    }

    /// <summary>关闭烹饪音游面板</summary>
    private void CloseCookingPanel()
    {
        if (mCookingPanel != null)
        {
            mCookingPanel.ManualClose();
            Destroy(mCookingPanel.gameObject);
            mCookingPanel = null;
        }
        else
        {
            // 预制体方式打开的，用 UIKit 关闭
            UIKit.ClosePanel<CookingPanel>();
        }
    }

    /// <summary>烹饪音游结束 → 根据品质决定 BOSS 生死</summary>
    private void OnCookingFinished(GameEvents.CookingFinished evt)
    {
        LogKit.Log($"[BossBattleManager] 烹饪结束，品质={evt.Quality}");

        // 关闭音游面板
        CloseCookingPanel();

        // 恢复战斗
        Time.timeScale = 1f;

        if (mCurrentBoss == null)
        {
            LogKit.Error("[BossBattleManager] 当前 BOSS 引用为空");
            return;
        }

        var recipe = GameDataMgr.Instance.GetRecipe(mCurrentRecipeId);
        if (recipe == null)
        {
            LogKit.Error($"[BossBattleManager] 找不到菜谱: {mCurrentRecipeId}");
            return;
        }

        switch (evt.Quality)
        {
            case RecipeQuality.Common:
            case RecipeQuality.Legendary:
                // 胜利：本局临时 Buff（非meta项，metaBuff 仅 Legendary 解锁）+ 成品菜按总评定星入局外背包
                // 总评定星：S=5金 / A=4紫 / B、C=3蓝
                int dishStar = evt.Grade == "S" ? 5 : evt.Grade == "A" ? 4 : 3;
                ApplyRecipeBuff(recipe, evt.Quality);
                MetaManager.Instance.AddDish(recipe.id, dishStar);
                // 记入本局背包（状态面板「背包」页签展示本局收获）
                if (DropManager.HasInstance)
                    DropManager.Instance.AddRunDish(recipe.id, dishStar);
                // 记入本阶段收获（阶段结算收获页签的成品菜物品格）
                if (BattleStatsCollector.HasInstance)
                    BattleStatsCollector.Instance.RecordDishReward(recipe.id, dishStar, 1);
                BossVictory(evt.Quality, evt.Grade, evt.MaxCombo, dishStar);
                break;

            case RecipeQuality.Failed:
            default:
                // 失败：无任何奖励，BOSS 复活
                mCurrentBoss.Revive();
                break;
        }
    }

    /// <summary>
    /// 应用菜谱 Buff（Step10 品质分流）：
    ///   非 meta 项 → 本局临时加成（Common/Legendary 均应用）
    ///   isMetaBuff 项 → 仅 Legendary 时解锁为局外永久 Buff
    /// </summary>
    private void ApplyRecipeBuff(RecipeConfig recipe, RecipeQuality quality)
    {
        if (recipe.buffEffects == null) return;

        foreach (var buff in recipe.buffEffects)
        {
            if (buff == null || string.IsNullOrEmpty(buff.buffType)) continue;
            if (buff.buffType == "None") continue;

            // 局外永久Buff：仅传说时解锁
            if (buff.isMetaBuff)
            {
                if (quality == RecipeQuality.Legendary && !string.IsNullOrEmpty(buff.buffId))
                {
                    MetaManager.Instance.AddPermanentBuff(buff.buffId);
                }
                continue;
            }

            // 本局临时加成（按类型分发到玩家属性）
            ApplyInRunBuff(buff);
        }
    }

    /// <summary>把单个菜品本局Buff应用到玩家（Damage/MoveSpeed/Dodge/Crit等）</summary>
    private void ApplyInRunBuff(BuffConfig buff)
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        switch (buff.buffType)
        {
            case "Damage":
                // 绝对攻击加成（兼容 SkillSystem 烹饪Buff记录）
                SkillSystem.Instance.ApplyCookingBuff(buff.value);
                break;
            case "MoveSpeed":
                player.AddBonusAttribute(new PlayerAttribute { MoveSpeed = buff.value });
                break;
            case "Dodge":
                player.AddBonusAttribute(new PlayerAttribute { DodgeChance = buff.value });
                break;
            case "CritChance":
                player.AddBonusAttribute(new PlayerAttribute { CritChance = buff.value });
                break;
            case "CritDamage":
                player.AddBonusAttribute(new PlayerAttribute { CritDamage = buff.value });
                break;
            case "BurnPower":
            case "ExplosionPower":
                // 化学反应（兼容旧 Build 系统；技能移除后恒 None，保留无害）
                SkillEffectType linked = buff.buffType == "BurnPower" ? SkillEffectType.Burn : SkillEffectType.Explosion;
                if (buff.value > 0f)
                    SkillSystem.Instance.ApplyCookingEffectBuff(linked, buff.value);
                break;
            default:
                LogKit.Log($"[BossBattleManager] 未处理的菜品Buff类型: {buff.buffType}");
                break;
        }

        LogKit.Log($"[BossBattleManager] 菜品本局Buff生效: {buff.buffName} ({buff.buffType} +{buff.value})");
    }

    /// <summary>
    /// BOSS 战胜利：解锁菜谱 + 掉稀有食材 + 记录本阶段收获 → 进入阶段结算（不再直接结束游戏）
    /// 阶段结算由 GameManager 订阅 BossStageCleared 打开面板；最终 BOSS 额外进入整局结算
    /// </summary>
    private void BossVictory(RecipeQuality quality, string grade, int maxCombo, int dishStar)
    {
        var bossConfig = GameDataMgr.Instance.GetBoss(mCurrentBoss.BossId);
        var rewards = new List<string>();

        // 永久解锁当前 BOSS 对应菜谱（BOSS 菜必出，作为 BOSS 额外奖励，不受炒菜随机影响）
        if (bossConfig != null && !string.IsNullOrEmpty(bossConfig.recipeId))
        {
            MetaManager.Instance.UnlockRecipe(bossConfig.recipeId);
            var r = GameDataMgr.Instance.GetRecipe(bossConfig.recipeId);
            rewards.Add($"解锁菜谱：{r?.recipeName ?? bossConfig.recipeId}");
        }

        // 掉落稀有食材
        if (bossConfig != null && !string.IsNullOrEmpty(bossConfig.dropRareIngredientId))
        {
            DropManager.Instance.SpawnIngredient(bossConfig.dropRareIngredientId, mCurrentBoss.transform.position);
            var ing = GameDataMgr.Instance.GetIngredient(bossConfig.dropRareIngredientId);
            rewards.Add($"获得稀有食材：{ing?.ingredientName ?? bossConfig.dropRareIngredientId}");
        }

        // 成品菜入背包（按总评定星，入账已在上层处理，这里只记录收获文案）
        if (quality != RecipeQuality.Failed)
        {
            var recipe = GameDataMgr.Instance.GetRecipe(mCurrentRecipeId);
            if (recipe != null) rewards.Add($"成品菜入背包：{recipe.recipeName} ★{dishStar}");
            if (quality == RecipeQuality.Legendary && HasMetaBuff(recipe)) rewards.Add("解锁局外永久Buff（传说奖励）");
        }

        // 记录本阶段收获到统计收集器（结算页展示）
        if (BattleStatsCollector.HasInstance)
        {
            BattleStatsCollector.Instance.RecordBossRewards(GradeText(grade), rewards);
        }

        // 图鉴/成就：Boss 战后烹饪完成（成功进入结算即视为完成一次成品菜烹饪）
        if (!string.IsNullOrEmpty(mCurrentRecipeId))
        {
            EventKit.Type.Send(new GameEvents.BossCookingRecorded
            {
                RecipeId = mCurrentRecipeId,
                Quality = quality,
                MaxCombo = maxCombo
            });
        }

        // BOSS 死亡
        mCurrentBoss.Die();

        // 进入阶段结算（GameManager 订阅处理：非最终→继续游戏；最终→整局结算→胜利）
        // ⚠「是否最终 BOSS」必须分模式判定，不能直接用 BossConfig.isFinal：阶段模式以「已是最后一个阶段」为唯一依据，
        // 因为同一 BOSS 会跨副本跨阶段复用，让 isFinal 参与判定会把非终阶段误判为通关、结算后直接回主菜单；
        // 只有旧模式（无 phases）才回落到 isFinal。
        bool isFinal;
        if (WaveManager.HasInstance && WaveManager.Instance.IsPhaseMode)
            isFinal = !WaveManager.Instance.HasNextPhase();
        else
            isFinal = bossConfig != null && bossConfig.isFinal;

        EventKit.Type.Send(new GameEvents.BossStageCleared { IsFinal = isFinal });
    }

    /// <summary>总评档位中文（结算页展示）</summary>
    private static string GradeText(string grade)
        => grade == "S" ? "传说" : grade == "A" ? "卓越" : grade == "B" ? "优良" : grade == "C" ? "普通" : "失败";

    /// <summary>菜谱是否含局外永久Buff（传说解锁）</summary>
    private static bool HasMetaBuff(RecipeConfig recipe)
    {
        if (recipe?.buffEffects == null) return false;
        foreach (var b in recipe.buffEffects)
            if (b != null && b.isMetaBuff) return true;
        return false;
    }
}
