#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// GM 调试控制台：仅编辑器与开发构建可用，F1 开关（打开时暂停，关闭恢复原流速）。
/// 用 IMGUI 覆盖层绘制，不依赖预制体与美术资源，可随时挂到任意构建上排查问题。
/// 由 GameManager 启动时创建并 DontDestroyOnLoad。
/// </summary>
public class GMConsole : MonoBehaviour
{
    public static GMConsole Instance { get; private set; }

    private bool mVisible;
    private float mPrevTimeScale = 1f;
    private int mTab;
    private Vector2 mScroll;
    private bool mGodMode;
    private string mLog = "";

    // ===== 输入参数 =====
    private int mCoinAmount = 1000;
    private int mIngredientCount = 5;
    private string mIngredientId = "tomato";
    private int mExpAmount = 50;
    private int mAttackBonus = 50;
    private string mRecipeId = "";
    private int mDishStar = 3;
    private string mBuffId = "";
    private string mSpawnEnemyId = "";
    private int mSpawnCount = 10;
    private int mJumpWave = 1;
    private string mUpgradeBuffId = "";
    private int mUpgradeBuffCount = 1;

    // ===== 常驻 =====
    public static void EnsureCreated()
    {
        if (Instance != null) return;
        var go = new GameObject("GMConsole");
        DontDestroyOnLoad(go);
        go.AddComponent<GMConsole>();
    }

    private void Awake()
    {
        Instance = this;
        mIngredientId = GetFirstIngredientId();
        mRecipeId = GetFirstRecipeId();
        mSpawnEnemyId = GetFirstEnemyId();
        mUpgradeBuffId = GetFirstBuffId();
        mJumpWave = 1;
    }

    private void Update()
    {
        // F1 开关
        if (Input.GetKeyDown(KeyCode.F1))
        {
            SetVisible(!mVisible);
        }
        // ESC 关闭
        if (mVisible && Input.GetKeyDown(KeyCode.Escape))
        {
            SetVisible(false);
        }

        // 无敌：每帧回满血量（GM 状态，非存档）
        if (mGodMode)
        {
            var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
            if (player != null && player.Health != null)
            {
                player.Health.Heal(player.Health.MaxHp + 1f);
                player.RestoreMana(99999f);
            }
        }
    }

    private void SetVisible(bool visible)
    {
        if (mVisible == visible) return;
        mVisible = visible;
        if (visible)
        {
            mPrevTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }
        else
        {
            Time.timeScale = mPrevTimeScale;
        }
    }

    // ===== 绘制 =====

    private void OnGUI()
    {
        if (!mVisible) return;

        float w = 960f, h = 640f;
        Rect win = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(win, GUIContent.none);

        GUILayout.BeginArea(new Rect(win.x + 10, win.y + 8, win.width - 20, win.height - 16));
        DrawHeader();
        DrawTabs();
        mScroll = GUILayout.BeginScrollView(mScroll);
        switch (mTab)
        {
            case 0: DrawMetaTab(); break;
            case 1: DrawPlayerTab(); break;
            case 2: DrawWaveTab(); break;
            case 3: DrawEnemyTab(); break;
            case 4: DrawWeaponTab(); break;
            case 5: DrawInfoTab(); break;
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("GM 控制台", new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold });
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("F1 关闭 (暂停)", GUILayout.Width(120)))
            SetVisible(false);
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    private void DrawTabs()
    {
        string[] tabs = { "局外 Meta", "玩家", "波次·Boss", "敌人", "武器·技能", "信息" };
        GUILayout.BeginHorizontal();
        for (int i = 0; i < tabs.Length; i++)
        {
            var style = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            bool active = mTab == i;
            if (active)
                style.normal.textColor = Color.yellow;
            if (GUILayout.Toggle(active, tabs[i], style, GUILayout.MinWidth(120)))
                mTab = i;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6);
    }

    private static void HLine()
    {
        GUILayout.Space(2);
        GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(2));
        GUILayout.Space(2);
    }

    // ===== 局外 Meta =====

    private void DrawMetaTab()
    {
        GUILayout.Label("—— 钱币 / 食材 ——", Bold());
        GUILayout.BeginHorizontal();
        GUILayout.Label("数量:", GUILayout.Width(50));
        mCoinAmount = IntField(mCoinAmount, 120);
        if (GUILayout.Button("加钱币", GUILayout.Width(100))) Meta_AddCoins(mCoinAmount);
        if (GUILayout.Button("减钱币", GUILayout.Width(100))) Meta_AddCoins(-mCoinAmount);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("食材ID:", GUILayout.Width(70));
        mIngredientId = GUILayout.TextField(mIngredientId, GUILayout.Width(140));
        mIngredientCount = IntField(mIngredientCount, 80);
        if (GUILayout.Button("加入背包", GUILayout.Width(100))) Meta_AddIngredient(mIngredientId, mIngredientCount);
        if (GUILayout.Button("全部食材+10", GUILayout.Width(120))) Meta_AddAllIngredients(10);
        GUILayout.EndHorizontal();
        HLine();

        GUILayout.Label("—— 成品菜 / 菜谱 ——", Bold());
        GUILayout.BeginHorizontal();
        GUILayout.Label("菜谱ID:", GUILayout.Width(70));
        mRecipeId = GUILayout.TextField(mRecipeId, GUILayout.Width(200));
        mDishStar = IntField(mDishStar, 60);
        if (GUILayout.Button("加成品菜 ★", GUILayout.Width(120))) Meta_AddDish(mRecipeId, mDishStar, 1);
        if (GUILayout.Button("解锁菜谱", GUILayout.Width(100))) Meta_UnlockRecipe(mRecipeId);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("解锁全部菜谱", GUILayout.Width(130))) Meta_UnlockAllRecipes();
        if (GUILayout.Button("解锁全部关卡", GUILayout.Width(130))) Meta_UnlockAllDungeons();
        if (GUILayout.Button("解锁全部角色", GUILayout.Width(130))) Meta_UnlockAllCharacters();
        if (GUILayout.Button("补全武器库存", GUILayout.Width(130))) Meta_EnsureAllWeapons();
        GUILayout.EndHorizontal();
        HLine();

        GUILayout.Label("—— 永久 Buff ——", Bold());
        GUILayout.BeginHorizontal();
        GUILayout.Label("BuffID:", GUILayout.Width(70));
        mBuffId = GUILayout.TextField(mBuffId, GUILayout.Width(200));
        if (GUILayout.Button("添加永久Buff", GUILayout.Width(130))) Meta_AddPermanentBuff(mBuffId);
        if (GUILayout.Button("列全部BuffID", GUILayout.Width(130))) mLog = CollectBuffIds();
        GUILayout.EndHorizontal();
        HLine();

        GUILayout.Label("—— 危险操作 ——", Bold());
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("重置全部局外存档", GUILayout.Width(180)))
        {
            if (MetaManager.HasInstance) MetaManager.Instance.ResetAllMeta();
            mLog = "局外存档已重置";
        }
        if (GUILayout.Button("保存存档", GUILayout.Width(100)))
        {
            if (MetaManager.HasInstance) MetaManager.Instance.Save();
            mLog = "存档已保存";
        }
        GUILayout.EndHorizontal();

        DrawLog();
    }

    // ===== 玩家 =====

    private void DrawPlayerTab()
    {
        var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
        if (player == null)
        {
            GUILayout.Label("当前不在战斗（无玩家对象）", Bold(Color.yellow));
            return;
        }

        GUILayout.Label($"—— 状态：HP {player.Health?.CurrentHp:F0}/{player.Health?.MaxHp:F0}  蓝 {player.CurrentMana:F0}/{player.MaxMana:F0}  " +
                        $"攻 {player.Attack:F0}  攻速 {player.AttackSpeed:F2}  移速 {player.MoveSpeed:F0}  暴击率 {player.CritChance:P0}", Wrap());

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("满血满蓝", GUILayout.Width(110))) Player_HealFull(player);
        if (GUILayout.Button("杀光场上敌人", GUILayout.Width(130))) KillAllEnemies();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("加经验:", GUILayout.Width(60));
        mExpAmount = IntField(mExpAmount, 80);
        if (GUILayout.Button("加经验", GUILayout.Width(90))) Player_AddExp(player, mExpAmount);
        if (GUILayout.Button("直接升级", GUILayout.Width(90))) Player_AddExp(player, Mathf.Max(1, mExpAmount * 10));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("加攻击:", GUILayout.Width(60));
        mAttackBonus = IntField(mAttackBonus, 80);
        if (GUILayout.Button("+攻击", GUILayout.Width(90))) Player_AddAttack(player, mAttackBonus);
        if (GUILayout.Button("+移速5", GUILayout.Width(90))) Player_AddMoveSpeed(player, 5f);
        if (GUILayout.Button("+攻速0.1", GUILayout.Width(100))) Player_AddAttackSpeed(player, 0.1f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        mGodMode = GUILayout.Toggle(mGodMode, "无敌（锁血）", GUILayout.Width(130));
        if (mGodMode) GUI.backgroundColor = Color.yellow;
        if (GUILayout.Button("满血", GUILayout.Width(80))) player.Health?.Heal(99999f);
        GUI.backgroundColor = Color.white;
        GUILayout.EndHorizontal();
        HLine();

        // 局内升级 Buff 批量添加（3选1 池，UpgradeBuffSystem.ApplyBuff）
        GUILayout.Label("—— 局内 Buff 批量添加（3选1 池）——", Bold());
        GUILayout.BeginHorizontal();
        GUILayout.Label("BuffID:", GUILayout.Width(60));
        mUpgradeBuffId = GUILayout.TextField(mUpgradeBuffId, GUILayout.Width(170));
        GUILayout.Label("次数:", GUILayout.Width(50));
        mUpgradeBuffCount = IntField(mUpgradeBuffCount, 60);
        if (GUILayout.Button("批量添加", GUILayout.Width(100))) ApplyUpgradeBuffBatch();
        if (GUILayout.Button("列全部Buff", GUILayout.Width(120))) mLog = CollectUpgradeBuffIds();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("随机加10个Buff", GUILayout.Width(140))) ApplyRandomUpgradeBuffs(10);
        GUILayout.EndHorizontal();

        // 玩家属性查看
        GUILayout.Label("—— 最终属性（Base + Bonus）——", Bold());
        var b = player.BaseAttr;
        var bn = player.BonusAttr;
        GUILayout.Label($"物理防御 {player.PhysDef:F1}  元素防御 {player.ElemDef:F1}  闪避 {player.DodgeChance:P0}  暴伤 {player.CritDamage:F2}", Wrap());
        GUILayout.Label($"物伤加成 {player.PhysDamageBonus:P0}  元伤加成 {player.ElemDamageBonus:P0}  (Base HP {b.MaxHp:F0} / Bonus {bn.MaxHp:F0})", Wrap());

        DrawLog();
    }

    // ===== 波次 / Boss =====

    private void DrawWaveTab()
    {
        var wm = WaveManager.HasInstance ? WaveManager.Instance : null;
        if (wm == null)
        {
            GUILayout.Label("当前不在战斗（无波次管理器）", Bold(Color.yellow));
            return;
        }

        GUILayout.Label($"当前：阶段 {wm.CurrentPhaseIndex + 1}/{wm.PhaseCount}  波次 {wm.CurrentWave}/{wm.TotalWaves}  " +
                        $"运行中 {wm.IsRunning}  Boss ID {wm.GetCurrentBossId()}", Wrap());

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("下一波", GUILayout.Width(110))) wm.NextWave();
        if (GUILayout.Button("触发Boss战", GUILayout.Width(130))) wm.TriggerBoss();
        if (GUILayout.Button("下一阶段", GUILayout.Width(110))) wm.NextPhase();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("跳转到波次:", GUILayout.Width(90));
        mJumpWave = IntField(mJumpWave, 60);
        if (GUILayout.Button("跳转", GUILayout.Width(70))) wm.JumpToWave(mJumpWave);
        if (GUILayout.Button("跳Boss波", GUILayout.Width(100))) wm.JumpToWave(wm.TotalWaves);
        GUILayout.EndHorizontal();
        HLine();

        GUILayout.Label("—— 战场炒菜 ——", Bold());
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("立即触发战场炒菜", GUILayout.Width(160)))
        {
            bool ok = FieldCookingDirector.HasInstance && FieldCookingDirector.Instance.TryTriggerNow();
            mLog = ok ? "战场炒菜已触发" : "触发失败（互斥/玩家不在场）";
        }
        GUILayout.EndHorizontal();
        HLine();

        GUILayout.Label("—— 结算流程 ——", Bold());
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("结束本局(胜利)", GUILayout.Width(140)))
        {
            if (GameManager.HasInstance) GameManager.Instance.RecordRunAndSave(true);
            mLog = "已记录胜利并保存";
        }
        if (GUILayout.Button("强制死亡结算", GUILayout.Width(140)))
        {
            var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
            if (player != null) player.Health?.TakeDamage(999999f);
        }
        if (GUILayout.Button("返回主菜单", GUILayout.Width(120)))
        {
            EventKitSendBackToMenu();
        }
        GUILayout.EndHorizontal();

        DrawLog();
    }

    // ===== 敌人 =====

    private void DrawEnemyTab()
    {
        GUILayout.Label($"场上敌人数量：{EnemySimple.AllEnemies.Count}", Wrap());

        GUILayout.BeginHorizontal();
        GUILayout.Label("敌人ID:", GUILayout.Width(70));
        mSpawnEnemyId = GUILayout.TextField(mSpawnEnemyId, GUILayout.Width(200));
        mSpawnCount = IntField(mSpawnCount, 60);
        if (GUILayout.Button("生成", GUILayout.Width(70))) SpawnEnemy(mSpawnEnemyId, mSpawnCount);
        GUILayout.EndHorizontal();

        GUILayout.Label("—— 快速生成（按配置敌人）——", Bold());
        var enemies = GameDataMgr.Instance.Enemies?.Enemies;
        if (enemies != null)
        {
            GUILayout.BeginHorizontal();
            int cnt = 0;
            foreach (var e in enemies)
            {
                if (e == null || string.IsNullOrEmpty(e.id)) continue;
                if (GUILayout.Button(e.id, GUILayout.Width(170)))
                {
                    SpawnEnemy(e.id, mSpawnCount);
                    mSpawnEnemyId = e.id;
                }
                if (++cnt % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("清除全部敌人", GUILayout.Width(140))) KillAllEnemies();
        GUILayout.EndHorizontal();

        DrawLog();
    }

    // ===== 武器 / 技能 =====

    private void DrawWeaponTab()
    {
        var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
        if (player == null)
        {
            GUILayout.Label("当前不在战斗（无玩家对象）", Bold(Color.yellow));
            return;
        }

        var auto = player.GetComponent<AutoAttack>();
        GUILayout.Label("—— 武器槽（3 槽同时生效，无切换）——", Bold());
        if (auto != null)
        {
            GUILayout.Label($"生效武器数: {auto.WeaponCount}", Wrap());
        }
        else
        {
            GUILayout.Label("玩家缺少 AutoAttack 组件", Wrap());
        }
        HLine();

        // 战前装配查看
        GUILayout.Label("—— 战前装配 ——", Bold());
        var roster = RosterManager.HasInstance ? RosterManager.Instance : null;
        if (roster != null)
        {
            GUILayout.Label($"角色: {roster.GetCurrentCharacterId()}   武器: {string.Join(", ", roster.GetCurrentWeaponIds().ToArray())}", Wrap());
            var skills = roster.GetCurrentSkillIds();
            GUILayout.Label($"技能组: {string.Join(", ", skills.ToArray())}", Wrap());
        }
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("重新装配战前配置", GUILayout.Width(160)))
        {
            if (roster != null) { roster.ApplyLoadoutToPlayer(); mLog = "已重新装配战前配置"; }
        }
        if (GUILayout.Button("应用局外永久Buff", GUILayout.Width(160)))
        {
            PermanentBuffApplier.ApplyPermanentBuffToPlayer();
            mLog = "已应用永久Buff";
        }
        GUILayout.EndHorizontal();

        DrawLog();
    }

    // ===== 信息 =====

    private void DrawInfoTab()
    {
        GUILayout.Label("—— 游戏状态 ——", Bold());
        var gm = GameManager.HasInstance ? GameManager.Instance : null;
        var meta = MetaManager.HasInstance ? MetaManager.Instance : null;
        var data = meta?.Data;

        GUILayout.Label($"玩家存在: {gm?.Player != null}   当前波次: {gm?.CurrentWave}   运行时间基准: {gm?.RunStartTime:F1}", Wrap());
        GUILayout.Label($"金币: {data?.coins}   局外食材: {data?.ingredientBackpack?.Count} 种   成品菜: {data?.dishInventory?.Count} 个", Wrap());
        GUILayout.Label($"已解锁菜谱: {data?.unlockedRecipeIds?.Count}   已解锁角色: {data?.ownedCharacters?.Count}   " +
                        $"永久Buff: {data?.permanentBuffIds?.Count}   已解锁副本: {data?.unlockedDungeonIds?.Count}", Wrap());
        GUILayout.Label($"累计局数: {data?.totalRuns}   胜场: {data?.victoryRuns}   最高波次: {data?.bestWave}   最高击杀: {data?.bestKills}", Wrap());

        HLine();
        GUILayout.Label("—— 配置数量 ——", Bold());
        GUILayout.Label($"角色 {GameDataMgr.Instance.Characters?.Count ?? 0}  武器 {GameDataMgr.Instance.Weapons?.Count ?? 0}  " +
                        $"敌人 {GameDataMgr.Instance.Enemies?.Count ?? 0}  食材 {GameDataMgr.Instance.Ingredients?.Count ?? 0}  " +
                        $"菜谱 {GameDataMgr.Instance.Recipes?.Count ?? 0}  战场菜 {GameDataMgr.Instance.CookingRecipes?.Count ?? 0}  " +
                        $"副本 {GameDataMgr.Instance.Dungeons?.Count ?? 0}  成就 {GameDataMgr.Instance.Achievements?.Count ?? 0}  " +
                        $"Buff {GameDataMgr.Instance.Buffs?.Count ?? 0}", Wrap());
        GUILayout.Label($"主动技能 {GameDataMgr.Instance.ActiveSkills?.Count ?? 0}  效果库 {GameDataMgr.Instance.GameEffects?.Count ?? 0}  " +
                        $"敌人词缀 {GameDataMgr.Instance.EnemyModifiers?.Count ?? 0}", Wrap());

        DrawLog();
    }

    // ===== 操作实现（局外） =====

    private void Meta_AddCoins(int amount)
    {
        if (!MetaManager.HasInstance) return;
        if (amount >= 0) MetaManager.Instance.AddCoins(amount);
        else MetaManager.Instance.TrySpendCoins(-amount);
        mLog = $"金币 -> {MetaManager.Instance.Coins}";
    }

    private void Meta_AddIngredient(string id, int count)
    {
        if (!MetaManager.HasInstance || string.IsNullOrEmpty(id)) return;
        MetaManager.Instance.AddIngredient(id, count);
        mLog = $"食材 {id} +{count}（当前 {MetaManager.Instance.GetIngredientCount(id)}）";
    }

    private void Meta_AddAllIngredients(int count)
    {
        var list = GameDataMgr.Instance.Ingredients?.Ingredients;
        if (list == null || !MetaManager.HasInstance) return;
        int n = 0;
        foreach (var ing in list)
        {
            if (ing == null || string.IsNullOrEmpty(ing.id)) continue;
            MetaManager.Instance.AddIngredient(ing.id, count);
            n++;
        }
        mLog = $"已为 {n} 种食材各 +{count}";
    }

    private void Meta_AddDish(string recipeId, int star, int count)
    {
        if (!MetaManager.HasInstance || string.IsNullOrEmpty(recipeId)) return;
        MetaManager.Instance.AddDish(recipeId, Mathf.Clamp(star, 1, 5), count);
        mLog = $"成品菜 {recipeId} ★{star} ×{count} 已入背包";
    }

    private void Meta_UnlockRecipe(string recipeId)
    {
        if (!MetaManager.HasInstance || string.IsNullOrEmpty(recipeId)) return;
        mLog = MetaManager.Instance.UnlockRecipe(recipeId)
            ? $"菜谱已解锁: {recipeId}"
            : $"解锁失败/已解锁: {recipeId}";
    }

    private void Meta_UnlockAllRecipes()
    {
        var list = GameDataMgr.Instance.Recipes?.Recipes;
        if (list == null || !MetaManager.HasInstance) return;
        int n = 0;
        foreach (var r in list)
        {
            if (r == null || string.IsNullOrEmpty(r.id)) continue;
            if (MetaManager.Instance.UnlockRecipe(r.id)) n++;
        }
        mLog = $"已解锁 {n} 道菜谱";
    }

    private void Meta_UnlockAllDungeons()
    {
        var list = GameDataMgr.Instance.Dungeons?.Dungeons;
        if (list == null || !MetaManager.HasInstance) return;
        int n = 0;
        foreach (var s in list)
        {
            if (s == null || string.IsNullOrEmpty(s.dungeonId)) continue;
            if (MetaManager.Instance.UnlockDungeon(s.dungeonId)) n++;
        }
        mLog = $"已解锁 {n} 个关卡";
    }

    private void Meta_UnlockAllCharacters()
    {
        if (!MetaManager.HasInstance) return;
        var list = GameDataMgr.Instance.Characters?.Characters;
        if (list == null) return;
        int n = 0;
        foreach (var c in list)
        {
            if (c == null || string.IsNullOrEmpty(c.id)) continue;
            if (!MetaManager.Instance.Data.ownedCharacters.Contains(c.id))
            {
                MetaManager.Instance.Data.ownedCharacters.Add(c.id);
                n++;
            }
        }
        MetaManager.Instance.Save();
        mLog = $"已解锁 {n} 个角色";
    }

    private void Meta_EnsureAllWeapons()
    {
        if (!MetaManager.HasInstance) return;
        var inv = MetaManager.Instance.GetWeaponInventory(); // 触发默认武器补全
        mLog = $"武器库存: {inv.Count} 把";
    }

    private void Meta_AddPermanentBuff(string buffId)
    {
        if (!MetaManager.HasInstance || string.IsNullOrEmpty(buffId)) return;
        MetaManager.Instance.AddPermanentBuff(buffId);
        mLog = $"永久Buff 已添加: {buffId}";
    }

    // ===== 操作实现（玩家 / 敌人 / 波次） =====

    private static void Player_HealFull(PlayerController player)
    {
        player.Health?.Heal(999999f);
        player.RestoreMana(99999f);
    }

    private static void Player_AddExp(PlayerController player, int amount)
    {
        var stats = player.GetComponent<PlayerStats>();
        if (stats != null) stats.AddExp(amount);
    }

    private static void Player_AddAttack(PlayerController player, float amount)
    {
        player.AddBonusAttribute(new PlayerAttribute { Attack = amount });
    }

    private static void Player_AddMoveSpeed(PlayerController player, float amount)
    {
        player.AddBonusAttribute(new PlayerAttribute { MoveSpeed = amount });
    }

    private static void Player_AddAttackSpeed(PlayerController player, float amount)
    {
        player.AddBonusAttribute(new PlayerAttribute { AttackSpeed = amount });
    }

    private static void KillAllEnemies()
    {
        var list = new List<EnemySimple>(EnemySimple.AllEnemies);
        foreach (var e in list)
        {
            if (e != null) e.TakeDamage(999999f);
        }
    }

    private void SpawnEnemy(string enemyId, int count)
    {
        var config = GameDataMgr.Instance.GetEnemy(enemyId);
        if (config == null)
        {
            mLog = $"找不到敌人配置: {enemyId}";
            return;
        }
        var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
        Vector3 center = player ? player.transform.position : Vector3.zero;
        int spawned = 0;
        for (int i = 0; i < Mathf.Clamp(count, 1, 200); i++)
        {
            Vector3 pos = center + new Vector3(Random.Range(-6f, 6f), Random.Range(-6f, 6f), 0f);
            var go = PoolFactory.Get(config.prefabPath, pos, Quaternion.identity);
            var enemy = go.GetComponent<EnemySimple>();
            if (enemy != null)
            {
                enemy.Init(enemyId);
                spawned++;
            }
        }
        mLog = $"已生成 {spawned} 个敌人: {enemyId}";
    }

    private static void EventKitSendBackToMenu()
    {
        YokiFrame.EventKit.Type.Send(new GameEvents.BackToMenu());
    }

    // ===== 工具 =====

    private static string GetFirstIngredientId()
    {
        var list = GameDataMgr.Instance.Ingredients?.Ingredients;
        return list != null && list.Count > 0 && list[0] != null ? list[0].id : "tomato";
    }

    private static string GetFirstRecipeId()
    {
        var list = GameDataMgr.Instance.Recipes?.Recipes;
        return list != null && list.Count > 0 && list[0] != null ? list[0].id : "";
    }

    private static string GetFirstEnemyId()
    {
        var list = GameDataMgr.Instance.Enemies?.Enemies;
        return list != null && list.Count > 0 && list[0] != null ? list[0].id : "";
    }

    private static string GetFirstBuffId()
    {
        var list = GameDataMgr.Instance.Buffs?.Buffs;
        return list != null && list.Count > 0 && list[0] != null ? list[0].id : "";
    }

    /// <summary>批量应用指定局内升级 Buff（3选1 池，UpgradeBuffSystem.ApplyBuff ×N）</summary>
    private void ApplyUpgradeBuffBatch()
    {
        if (!UpgradeBuffSystem.HasInstance || string.IsNullOrEmpty(mUpgradeBuffId))
        {
            mLog = "未在战斗中或 BuffID 为空";
            return;
        }
        var config = GameDataMgr.Instance.GetBuffUpgrade(mUpgradeBuffId);
        if (config == null)
        {
            mLog = $"找不到升级Buff配置: {mUpgradeBuffId}";
            return;
        }
        int n = Mathf.Clamp(mUpgradeBuffCount, 1, 99);
        for (int i = 0; i < n; i++) UpgradeBuffSystem.Instance.ApplyBuff(mUpgradeBuffId);
        mLog = $"已批量应用 Buff {mUpgradeBuffId}（{config.buffName}）×{n}";
    }

    /// <summary>随机应用 N 个升级 Buff（测试成长曲线）</summary>
    private void ApplyRandomUpgradeBuffs(int count)
    {
        var list = GameDataMgr.Instance.Buffs?.Buffs;
        if (list == null || list.Count == 0 || !UpgradeBuffSystem.HasInstance)
        {
            mLog = "无可用 Buff 或未在战斗中";
            return;
        }
        int n = Mathf.Clamp(count, 1, 99);
        for (int i = 0; i < n; i++)
        {
            var b = list[Random.Range(0, list.Count)];
            if (b != null && !string.IsNullOrEmpty(b.id)) UpgradeBuffSystem.Instance.ApplyBuff(b.id);
        }
        mLog = $"已随机应用 {n} 个升级Buff";
    }

    /// <summary>列出全部局内升级 Buff ID（3选1 池，带 tier）</summary>
    private string CollectUpgradeBuffIds()
    {
        var list = GameDataMgr.Instance.Buffs?.Buffs;
        if (list == null || list.Count == 0) return "无升级Buff配置";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var b in list)
        {
            if (b == null || string.IsNullOrEmpty(b.id)) continue;
            parts.Add($"{b.id}(T{b.tier})");
        }
        return "Buff(" + parts.Count + "): " + string.Join(", ", parts);
    }

    private string CollectBuffIds()
    {
        var recipes = GameDataMgr.Instance.Recipes?.Recipes;
        if (recipes == null) return "无菜谱";
        var set = new HashSet<string>();
        foreach (var r in recipes)
        {
            if (r?.buffEffects == null) continue;
            foreach (var b in r.buffEffects)
            {
                if (b != null && b.isMetaBuff && !string.IsNullOrEmpty(b.buffId))
                    set.Add(b.buffId);
            }
        }
        return set.Count > 0 ? "metaBuff: " + string.Join(", ", set) : "无 metaBuff 配置";
    }

    private void DrawLog()
    {
        if (string.IsNullOrEmpty(mLog)) return;
        GUILayout.Space(6);
        GUI.color = Color.yellow;
        GUILayout.Label("[结果] " + mLog, Wrap());
        GUI.color = Color.white;
    }

    // ===== 小工具 =====

    private static int IntField(int value, float width)
    {
        string s = GUILayout.TextField(value.ToString(), GUILayout.Width(width));
        if (int.TryParse(s, out int v)) return v;
        return value;
    }

    private static GUIStyle Bold() => new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };

    private static GUIStyle Bold(Color c)
    {
        var s = Bold();
        s.normal.textColor = c;
        return s;
    }

    private static GUIStyle Wrap() => new GUIStyle(GUI.skin.label) { wordWrap = true };
}
#endif
