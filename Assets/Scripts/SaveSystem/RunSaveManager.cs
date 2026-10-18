using System.Collections.Generic;
using System.IO;
using LitJson;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 对局断点：捕获、恢复并持久化一局进行中的状态（persistentDataPath/saves/run.json，与局外存档独立）。
/// <para>
/// v2 语义 = **真正的「继续」**：保存瞬间的时间/血量/法力/等级/场上每只怪的位置与血量/地上每个掉落物/
/// 刷怪游标/BOSS 破韧与音游进度全部落盘，续玩后原样接上（不再「回到波次开头」）。
/// </para>
/// 保存时机：暂停→退出本局、切后台、退出应用、以及**每波开始时自动落盘**（崩溃韧性）。
/// </summary>
public static class RunSaveManager
{
    private const string SAVE_DIR = "saves";
    private const string FILE_NAME = "run.json";

    /// <summary>
    /// 断点存档结构版本（结构变更时 +1，旧档由 IsCompatible 丢弃而不是错误恢复）。
    /// ⚠ 放在 RunSaveManager 而不是 RunSaveData：LitJson 反射会枚举 RunSaveData 的
    /// public const/static 字段，常量放那边会导致「读档抛 Cannot set a constant field → 坏档被清」。
    /// </summary>
    private const int SAVE_VERSION = 2;

    private static string SavePath => Path.Combine(Application.persistentDataPath, SAVE_DIR, FILE_NAME);

    /// <summary>是否正在恢复断点（恢复期间抑制自动存档，避免恢复过程反过来写坏档）</summary>
    private static bool mRestoring;

    /// <summary>恢复期间为 true</summary>
    public static bool IsRestoring => mRestoring;

    // ===== 持久化 =====

    public static void Save(RunSaveData snap)
    {
        try
        {
            if (snap == null) return;
            snap.version = SAVE_VERSION;

            string dir = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 原子写：先写临时文件再替换，避免写一半崩溃留下半截 JSON
            string tmp = SavePath + ".tmp";
            File.WriteAllText(tmp, JsonMapper.ToJson(snap));
            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(tmp, SavePath);

            Debug.Log("[RunSave] 断点已保存");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[RunSave] 断点保存失败: {ex.Message}");
        }
    }

    public static RunSaveData Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return null;
            string json = File.ReadAllText(SavePath);
            // 旧断点兼容：副本字段改名（stageId → dungeonId），LitJson 不识别旧键会静默丢失
            json = json.Replace("\"stageId\"", "\"dungeonId\"");
            var data = JsonMapper.ToObject<RunSaveData>(json);
            if (data == null) Clear();
            return data;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[RunSave] 断点读取失败（已丢弃坏档）: {ex.Message}");
            Clear();
            return null;
        }
    }

    /// <summary>存档结构是否与当前版本兼容（旧版断点直接丢弃，避免按错误语义恢复）</summary>
    public static bool IsCompatible(RunSaveData snap)
    {
        if (snap == null) return false;
        if (snap.version < SAVE_VERSION)
        {
            Debug.LogWarning($"[RunSave] 断点版本过旧（{snap.version} < {SAVE_VERSION}），已丢弃");
            return false;
        }
        // 基础属性缺失（空 PlayerAttribute）→ 恢复出来会是个 0 血角色，直接判为坏档
        if (snap.baseAttr.MaxHp <= 0f || snap.waveIndex < 1)
        {
            Debug.LogWarning("[RunSave] 断点数据不完整（基础属性/波次异常），已丢弃");
            return false;
        }
        return true;
    }

    public static bool HasSave() => File.Exists(SavePath);

    public static void Clear()
    {
        try
        {
            if (File.Exists(SavePath)) File.Delete(SavePath);
            string tmp = SavePath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[RunSave] 断点删除失败: {ex.Message}");
        }
    }

    // ===== 捕获 =====

    /// <summary>每波开始时自动落盘（崩溃/强杀也能回到本波开头，而不是丢掉整局）</summary>
    public static void AutoSaveWaveStart()
    {
        if (mRestoring) return;
        var snap = BuildSnapshot();
        if (snap == null) return;
        Save(snap);
    }

    /// <summary>
    /// 断点保存（退出本局/暂停/切后台/退出应用）：落盘**当前瞬间**的完整状态。
    /// 时间、血量、场上怪物与掉落物、刷怪游标、BOSS 进度全部按此刻记录。
    /// </summary>
    public static bool CaptureCheckpoint()
    {
        if (mRestoring) return false;
        var snap = BuildSnapshot();
        if (snap == null) return false;

        // 本局已结束（最终 BOSS 清完、波次循环已停）→ 不写断点，并清掉旧档，避免"继续"把玩家送回已通关的局
        if (snap.wavesStopped)
        {
            Clear();
            Debug.Log("[RunSave] 本局已结束，不保存断点");
            return false;
        }

        Save(snap);
        return true;
    }

    /// <summary>汇总当前各系统状态为一份完整快照（不落盘）</summary>
    private static RunSaveData BuildSnapshot()
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return null;

        var snap = new RunSaveData
        {
            saveTimestamp = SaveVersion.NowUnix(),
            baseAttr = player.BaseAttr,
            bonusAttr = player.BonusAttr,
            playerHp = player.Health != null ? player.Health.CurrentHp : player.MaxHp,
            healthMax = player.Health != null ? player.Health.MaxHp : player.MaxHp,
            posX = player.transform.position.x,
            posY = player.transform.position.y,
            mana = player.CurrentMana,
        };

        // BonusAttr 剔除「由恢复流程重新写入」的部分，避免双算：
        //  1) 升级Buff Modifier + 效果引擎属性 → RestoreModifiers 重算/重放时重新写入
        //  2) 主动技能临时Buff → RestoreTempBuffs 重新叠加
        if (UpgradeBuffSystem.HasInstance && UpgradeBuffSystem.Instance.HasAppliedBonus)
        {
            snap.bonusAttr = snap.bonusAttr - UpgradeBuffSystem.Instance.GetLastBonus();
        }
        if (ActiveSkillSystem.HasInstance)
        {
            snap.bonusAttr = snap.bonusAttr - ActiveSkillSystem.Instance.GetTempBuffBonus();
        }

        // 等级/经验
        var stats = player.GetComponent<PlayerStats>();
        if (stats != null)
        {
            snap.playerLevel = stats.Level;
            snap.playerExp = stats.Exp;
        }

        // 闪避冷却
        var dodge = player.GetComponent<DodgeSkill>();
        if (dodge != null) snap.dodgeCooldown = dodge.CooldownRemain;

        // 效果引擎瞬态防御状态（护盾/免疫/格挡）
        var effectCtrl = player.GetComponent<PlayerEffectController>();
        if (effectCtrl != null)
        {
            snap.shield = effectCtrl.Shield;
            snap.immunityRemain = effectCtrl.ImmunityRemain;
            snap.blockCharges = effectCtrl.BlockCharges;
        }

        // SkillSystem（烹饪Buff/特殊效果，效果数据扁平化存储）
        if (SkillSystem.HasInstance)
        {
            snap.cookingDamageBonus = SkillSystem.Instance.CookingDamageBonus;
            var effect = SkillSystem.Instance.ActiveEffect;
            snap.effectType = (int)effect.type;
            snap.effectParam = effect.param;
            snap.effectDuration = effect.duration;
            snap.effectPowerBonus = SkillSystem.Instance.GetEffectPowerBonus();
        }

        // 主动技能冷却/临时Buff（按此刻的剩余时间续算）+ 限时战斗修饰
        if (ActiveSkillSystem.HasInstance)
        {
            snap.skillCooldowns = ActiveSkillSystem.Instance.GetCooldownSnapshot();
            snap.tempBuffs = ActiveSkillSystem.Instance.GetTempBuffSnapshot();
            snap.skillMods = ActiveSkillSystem.Instance.ExportCombatMods();
        }

        // 波次运行时（波次/阶段/刷怪游标/准备倒计时）
        if (WaveManager.HasInstance)
        {
            var wm = WaveManager.Instance;
            snap.dungeonId = wm.CurrentDungeonId;
            snap.phaseIndex = wm.CurrentPhaseIndex;
            snap.waveIndex = wm.CurrentWave;
            snap.waveState = (int)wm.CurrentState;
            snap.prepRemain = wm.PrepRemain;
            snap.waveStartKill = wm.WaveStartKill;
            snap.waveTotalEnemies = wm.WaveTotalEnemies;
            snap.spawnGroupIndex = wm.SpawnGroupIndex;
            snap.spawnIndexInGroup = wm.SpawnIndexInGroup;
            snap.spawnFinished = wm.SpawnFinished;
            snap.wavesStopped = !wm.IsRunning;   // 最终 BOSS 已清 → 本局结束，不再可续玩
        }

        // 本局已用时长（HUD 计时基准；Time.time 受 timeScale 影响，暂停时为冻结值）
        if (GameManager.Instance != null)
            snap.runElapsed = Mathf.Max(0f, Time.time - GameManager.Instance.RunStartTime);

        // 掉落（击杀数 = 此刻累计值）
        if (DropManager.HasInstance)
        {
            var dm = DropManager.Instance;
            snap.killCount = dm.KillCount;
            snap.dropMultiplierBonus = dm.DropMultiplierBonus;
            snap.streakCount = dm.StreakCount;
            snap.streakRemain = dm.StreakRemainTime;
            foreach (var kv in dm.IngredientInventory)
            {
                if (kv.Value > 0)
                    snap.ingredientInventory.Add(new IngredientStack { id = kv.Key, count = kv.Value });
            }
        }

        // 升级Buff Modifier 快照 + 复杂增益ID + 增益卡历史 + 抽取计数 + 未选完的 3 选 1
        if (UpgradeBuffSystem.HasInstance)
        {
            snap.buffModifiers = UpgradeBuffSystem.Instance.GetModifiers();
            snap.upgradeEffectIds = UpgradeBuffSystem.Instance.GetSelectedEffectIds();
            snap.selectedBuffIds = UpgradeBuffSystem.Instance.GetSelectedBuffIds();
        }
        if (LevelUpManager.HasInstance)
            LevelUpManager.Instance.ExportSelectionState(snap.pickedBuffCounts, snap.pendingUpgradeIds);

        // 场上怪物（位置 + 血量 + 移速倍率）
        foreach (var e in EnemySimple.AllEnemies)
        {
            if (e == null || !e.isActiveAndEnabled || e.IsDead) continue;
            if (string.IsNullOrEmpty(e.ConfigId)) continue;
            snap.enemies.Add(new EnemySave
            {
                enemyId = e.ConfigId,
                x = e.transform.position.x,
                y = e.transform.position.y,
                hp = e.CurrentHp,
                maxHp = e.MaxHpValue,
                speedMul = e.SpeedMultiplier
            });
        }

        // 地上掉落物（食材 / 经验宝石）
        foreach (var d in DropItem.AllDrops)
        {
            if (d == null || !d.isActiveAndEnabled) continue;
            snap.drops.Add(new DropSave
            {
                kind = d.Type == DropType.Exp ? 1 : 0,
                itemId = d.IngredientId,
                value = d.ExpValue,
                x = d.transform.position.x,
                y = d.transform.position.y
            });
        }

        // BOSS（存活则按实时状态；已死/无 BOSS 则清空）
        OverlayBossState(snap);

        // 战场炒菜触发进度 + 进行中的画中画炒菜
        if (FieldCookingDirector.HasInstance)
            snap.fieldCookingKills = FieldCookingDirector.Instance.KillsSinceTrigger;
        if (FieldCookingEngine.HasInstance && FieldCookingEngine.Instance.IsRunning && FieldCookingEngine.Instance.Recipe != null)
            snap.fieldCookingRecipeId = FieldCookingEngine.Instance.Recipe.recipeId;

        // 整局/阶段统计
        if (BattleStatsCollector.HasInstance)
            snap.stats = BattleStatsCollector.Instance.Export();

        return snap;
    }

    /// <summary>BOSS 字段覆盖：有存活 BOSS 用实时状态（含音游进行中），否则清空</summary>
    private static void OverlayBossState(RunSaveData snap)
    {
        var boss = BossHealth.Current;
        if (boss == null || boss.IsDead)
        {
            snap.bossId = null;
            snap.bossCooking = false;
            snap.cookingRecipeId = null;
            return;
        }

        snap.bossId = boss.BossId;
        snap.bossHp = boss.CurrentHp;
        snap.bossPhaseIndex = boss.CurrentPhaseIndex;
        snap.bossBreakBar = boss.BreakBarValue;
        snap.bossBreakBarFull = boss.IsBreakBarFull;
        snap.bossWeakened = boss.IsWeakened;
        snap.bossWeaknessRemain = boss.GetWeaknessRemain();
        snap.bossPosX = boss.transform.position.x;
        snap.bossPosY = boss.transform.position.y;

        // 血量已空 = 正在烹饪音游（BOSS 不会因血量归零死亡，胜负由音游裁定）
        snap.bossCooking = boss.IsCookingTriggered && boss.CurrentHp <= 0f;
        snap.cookingRecipeId = snap.bossCooking && BossBattleManager.HasInstance
            ? BossBattleManager.Instance.CurrentRecipeId
            : null;
    }

    // ===== 恢复 =====

    /// <summary>还原断点（Battle 场景就绪、各系统已创建后调用；snap 为空则无操作）</summary>
    public static void RestoreCheckpoint(RunSaveData snap)
    {
        // 静态字段会跨场景残留：先把上一次的"待重开面板"清掉，避免恢复失败时误弹旧面板
        ClearDeferred();

        if (snap == null) return;

        var player = GameManager.Instance?.Player;
        if (player == null) return;

        mRestoring = true;
        try
        {
            ApplySnapshot(snap);
        }
        finally
        {
            mRestoring = false;
        }

        Debug.Log($"[RunSave] 断点已恢复：第 {snap.phaseIndex + 1} 阶段 / 第 {snap.waveIndex} 波 " +
                  $"(状态 {(WaveState)snap.waveState})，HP {snap.playerHp:F0}，怪物 {snap.enemies?.Count ?? 0} 只，" +
                  $"掉落 {snap.drops?.Count ?? 0} 个，BOSS={(string.IsNullOrEmpty(snap.bossId) ? "无" : snap.bossId)}");
    }

    /// <summary>把快照数据应用到各系统（顺序有依赖，见各步注释）</summary>
    private static void ApplySnapshot(RunSaveData snap)
    {
        var player = GameManager.Instance?.Player;
        if (player == null) return;

        // 1. 玩家基础属性（战前一致）→ 动态加成；位置/法力/闪避冷却
        player.ApplyBaseAttribute(snap.baseAttr);

        // ⚠ 快照里的 bonusAttr 已剔除"效果引擎总贡献"（BuildSnapshot 减去 GetLastBonus，
        //    后者 = 升级卡 Modifier + PlayerEffectController 写入的全部属性）。
        //    「角色被动」「成品菜星级效果」是 ApplyLoadoutToPlayer 在进入战斗时写入的，
        //    断点恢复不会重放它们（RestoreModifiers 只重放升级卡），所以不能直接清空 BonusAttr，
        //    否则续玩后角色被动（如大厨 AttackOfMoveSpeed）与菜品加成会永久消失，
        //    且控制器 mAttrTotal 仍记着这份值 → 下次存档会多扣一次、逐次漂移。
        //    这里先备份装配阶段写好的加成，清空后与快照一起写回。
        PlayerAttribute loadoutBonus = player.BonusAttr;
        player.ClearBonusAttribute();
        player.AddBonusAttribute(snap.bonusAttr + loadoutBonus);
        player.transform.position = new Vector3(snap.posX, snap.posY, 0f);
        if (snap.mana >= 0f) player.SetMana(snap.mana);
        var dodge = player.GetComponent<DodgeSkill>();
        if (dodge != null) dodge.SetCooldownRemain(snap.dodgeCooldown);

        // 2. 等级/经验
        var stats = player.GetComponent<PlayerStats>();
        if (stats != null) stats.Restore(snap.playerLevel, snap.playerExp);

        // 3. 武器槽位（3 槽同时生效，槽位列表由战前配置派生）
        var autoAttack = player.GetComponent<AutoAttack>();
        if (autoAttack != null)
        {
            autoAttack.SetWeaponSlots(RosterManager.HasInstance ? RosterManager.Instance.GetCurrentWeaponIds() : null);
        }

        // 4. SkillSystem（烹饪Buff/特殊效果，会重算 DamageBonus/AttackSpeedBonus）
        if (SkillSystem.HasInstance)
        {
            var effect = new SkillEffectData
            {
                type = (SkillEffectType)snap.effectType,
                param = snap.effectParam,
                duration = snap.effectDuration
            };
            SkillSystem.Instance.RestoreState(snap.cookingDamageBonus, effect, snap.effectPowerBonus);
        }

        // 5. 主动技能冷却/临时Buff + 限时战斗修饰（按剩余时间续算）
        if (ActiveSkillSystem.HasInstance)
        {
            ActiveSkillSystem.Instance.RestoreCooldowns(snap.skillCooldowns);
            ActiveSkillSystem.Instance.RestoreTempBuffs(snap.tempBuffs);
            ActiveSkillSystem.Instance.RestoreCombatMods(snap.skillMods);
        }

        // 6. 掉落（击杀数 / 食材背包 / 拾取倍率 / 连杀）
        if (DropManager.HasInstance)
        {
            DropManager.Instance.RestoreRun(snap.killCount, snap.ingredientInventory,
                snap.dropMultiplierBonus, snap.streakCount, snap.streakRemain);
        }

        // 7. 升级Buff Modifier（重算写回玩家）+ 复杂增益重放 + 增益卡历史
        if (UpgradeBuffSystem.HasInstance)
        {
            UpgradeBuffSystem.Instance.RestoreModifiers(snap.buffModifiers, snap.upgradeEffectIds);
            UpgradeBuffSystem.Instance.RestoreSelectedBuffIds(snap.selectedBuffIds);
        }

        // 7.5 效果引擎瞬态防御状态（护盾/免疫/格挡）—— 必须在效果重放之后，否则会被覆盖
        var effectCtrl = player.GetComponent<PlayerEffectController>();
        if (effectCtrl != null)
            effectCtrl.RestoreTransientDefense(snap.shield, snap.immunityRemain, snap.blockCharges);

        // 8. 波次字段先写回（阶段/波次/击杀基准/刷怪游标），但**不启动状态机**：
        //    敌人生成时的属性倍率是按「当前阶段 + 当前波次」解析的，必须先落位
        if (WaveManager.HasInstance) WaveManager.Instance.PrepareRestore(snap);

        // 9. BOSS：重新生成并还原血量/破韧/虚弱/阶段（BossBattleManager 自动经 BossSpawned 事件接管）
        BossHealth restoredBoss = null;
        if (!string.IsNullOrEmpty(snap.bossId))
        {
            var bossController = BossSpawner.Spawn(snap.bossId, new Vector3(snap.bossPosX, snap.bossPosY, 0f), ResolveBossHpScale(snap));
            var boss = bossController != null ? bossController.GetComponent<BossHealth>() : null;
            if (boss != null)
            {
                boss.RestoreState(snap.bossHp, snap.bossPhaseIndex, snap.bossBreakBar,
                    snap.bossBreakBarFull, snap.bossWeakened, snap.bossWeaknessRemain);
                restoredBoss = boss;
            }
        }

        // 10. 场上怪物（位置 + 血量）—— 必须在波次状态机启动前重建，
        //     否则 Clearing 状态会因场上为空而立刻判定本波清空
        RestoreEnemies(snap.enemies);

        // 11. 地上掉落物
        RestoreDrops(snap.drops);

        // 12. 波次：按存档状态启动（Prep 续倒计时 / Spawning 续刷怪游标 / Clearing 等清场 / Boss 接管）
        if (WaveManager.HasInstance) WaveManager.Instance.RestoreRun(snap, restoredBoss);

        // 13. 整局/阶段统计
        if (BattleStatsCollector.HasInstance && snap.stats != null)
            BattleStatsCollector.Instance.Import(snap.stats);

        // 13.5 存档停在「BOSS 已清、阶段结算未确认」时，RestoreRun 已把阶段推进到下一阶段：
        //      上一阶段的统计计数还留在收集器里（Import 会写回），必须清零重开本阶段，
        //      否则新阶段的结算页会带着上一阶段的数据。
        if (WaveManager.HasInstance && WaveManager.Instance.RestoredPastClearedBoss && BattleStatsCollector.HasInstance)
        {
            BattleStatsCollector.Instance.BeginStage();
            LogKit.Log("[RunSave] 断点恢复：已跳过已清 BOSS 的阶段，重开本阶段统计");
        }

        // 14. 战场炒菜触发进度
        if (FieldCookingDirector.HasInstance)
            FieldCookingDirector.Instance.KillsSinceTrigger = snap.fieldCookingKills;

        // 15. 本局计时回退到"保存瞬间的已用时长"（HUD 计时基准）
        if (GameManager.Instance != null)
            GameManager.Instance.MarkRunStart(Time.time - snap.runElapsed);

        // 16. 血量最后写：
        //     上限优先用存档里的 Health.MaxHp 原样写回（这才是保存瞬间 HUD 上那条血条的真实上限）。
        //     ⚠ 不能用「player.MaxHp - 进入时的上限」做差值补：升级卡 MaxHp 走 BonusAttr，
        //     而效果引擎的 MaxHp 直接加在 Health 上、并且会顺手 RefreshToMaxHp（Health.Init(player.MaxHp)），
        //     两条通道互相覆盖，差值法在这种情况下会二次叠加或漏加。
        //     存档没有该字段（旧档）时回退到 player.MaxHp（= 基础 + 加成）。
        if (player.Health != null)
        {
            float targetMax = snap.healthMax > 1f ? snap.healthMax : player.MaxHp;
            player.Health.Init(targetMax);      // 上限对齐（当前血量同时被置满）
            player.Health.SetHp(snap.playerHp); // 再写回保存瞬间的血量
        }

        // 17. 升级抽卡计数 + 暂存未选完的 3 选 1（面板由 GameManager 在 HUD 打开后统一重开）
        if (LevelUpManager.HasInstance)
            LevelUpManager.Instance.RestoreSelectionState(snap.pickedBuffCounts, snap.pendingUpgradeIds);

        // 18. 需要 UI 的恢复项（音游面板 / 3 选 1 面板 / 画中画炒菜）延后到 HUD 打开后执行，见 ResumeDeferredPanels()
        mDeferredBoss = snap.bossCooking ? restoredBoss : null;
        mDeferredCookingRecipe = snap.bossCooking ? snap.cookingRecipeId : null;
        mDeferredFieldCookingRecipe = string.IsNullOrEmpty(snap.fieldCookingRecipeId) ? null : snap.fieldCookingRecipeId;
    }

    // ===== 延后到 HUD 就绪的 UI 恢复 =====

    private static BossHealth mDeferredBoss;
    private static string mDeferredCookingRecipe;
    private static string mDeferredFieldCookingRecipe;

    /// <summary>
    /// 断点续玩的 UI 侧恢复（由 GameManager 在 HUD 打开之后调用）：
    /// 1) 音游进行中被中断 → 重开音游面板；
    /// 2) 未选完的 3 选 1 → 重新弹选卡面板；
    /// 3) 画中画炒菜进行中 → 用同一道菜重新开始（小游戏内部进度无法序列化，重开一局计时）。
    /// 这三者互斥，按优先级依次尝试。
    /// </summary>
    public static void ResumeDeferredPanels()
    {
        if (mDeferredBoss != null && BossBattleManager.HasInstance)
        {
            BossBattleManager.Instance.RestoreCookingInProgress(mDeferredCookingRecipe, mDeferredBoss);
            ClearDeferred();
            return; // 音游与其它玩法互斥
        }

        if (LevelUpManager.HasInstance && LevelUpManager.Instance.ReopenPendingSelection())
        {
            ClearDeferred();
            return; // 选卡期间 timeScale=0，不启动炒菜
        }

        if (!string.IsNullOrEmpty(mDeferredFieldCookingRecipe) && FieldCookingEngine.HasInstance)
        {
            var recipes = GameDataMgr.Instance.CookingRecipes;
            CookingRecipeConfig recipe = null;
            if (recipes?.Recipes != null)
            {
                foreach (var r in recipes.Recipes)
                {
                    if (r != null && r.recipeId == mDeferredFieldCookingRecipe) { recipe = r; break; }
                }
            }
            if (recipe != null)
            {
                FieldCookingEngine.Instance.StartCooking(recipe);
                LogKit.Log($"[RunSave] 断点恢复：重开画中画炒菜 {recipe.recipeName}（小游戏进度从头开始）");
            }
        }

        ClearDeferred();
    }

    private static void ClearDeferred()
    {
        mDeferredBoss = null;
        mDeferredCookingRecipe = null;
        mDeferredFieldCookingRecipe = null;
    }

    /// <summary>重建场上怪物（按 enemyId 重新走 Init，再写回血量/位置/移速倍率）</summary>
    private static void RestoreEnemies(List<EnemySave> list)
    {
        if (list == null || list.Count == 0) return;

        int ok = 0, missing = 0;
        foreach (var e in list)
        {
            if (e == null || string.IsNullOrEmpty(e.enemyId)) continue;
            var cfg = GameDataMgr.Instance.GetEnemy(e.enemyId);
            if (cfg == null || string.IsNullOrEmpty(cfg.prefabPath))
            {
                missing++;
                continue;
            }

            var go = PoolFactory.Get(cfg.prefabPath, new Vector3(e.x, e.y, 0f), Quaternion.identity);
            if (go == null) { missing++; continue; }

            var enemy = go.GetComponent<EnemySimple>();
            if (enemy == null) { missing++; continue; }

            enemy.Init(e.enemyId);
            enemy.transform.position = new Vector3(e.x, e.y, 0f);
            if (e.speedMul > 0f && !Mathf.Approximately(e.speedMul, 1f)) enemy.SpeedMultiplier = e.speedMul;

            // 血量按当前配置复算的上限夹取（配置/难度变动时不至于出现超上限血量）
            if (enemy.MaxHpValue > 0f && e.maxHp > 0f && !Mathf.Approximately(enemy.MaxHpValue, e.maxHp))
            {
                LogKit.Log($"[RunSave] {e.enemyId} 血量上限变化 {e.maxHp:F0} → {enemy.MaxHpValue:F0}，按当前上限夹取");
            }
            var health = go.GetComponent<Health>();
            if (health != null) health.SetHp(Mathf.Max(1f, e.hp)); // 存档里不应有死怪，兜底 1 血
            ok++;
        }

        if (missing > 0)
            LogKit.Warning($"[RunSave] 恢复怪物时跳过 {missing} 只（配置/预制体缺失）");
        LogKit.Log($"[RunSave] 已恢复场上怪物 {ok} 只");
    }

    /// <summary>重建地上掉落物</summary>
    private static void RestoreDrops(List<DropSave> list)
    {
        if (list == null || list.Count == 0 || !DropManager.HasInstance) return;

        int count = 0;
        foreach (var d in list)
        {
            if (d == null) continue;
            var pos = new Vector3(d.x, d.y, 0f);
            if (d.kind == 1)
            {
                DropManager.Instance.SpawnExpGemAt(Mathf.Max(1, d.value), pos);
            }
            else
            {
                if (string.IsNullOrEmpty(d.itemId)) continue;
                DropManager.Instance.SpawnIngredientAt(d.itemId, pos);
            }
            count++;
        }
        LogKit.Log($"[RunSave] 已恢复地上掉落物 {count} 个");
    }

    /// <summary>按存档的副本+阶段取 BOSS 血量倍率（与正常生成一致，避免断点后血量上限错位）</summary>
    private static float ResolveBossHpScale(RunSaveData snap)
    {
        var dungeon = GameDataMgr.Instance.GetDungeon(snap.dungeonId);
        var phases = dungeon?.phases;
        if (phases == null || snap.phaseIndex < 0 || snap.phaseIndex >= phases.Count) return 1f;
        float scale = phases[snap.phaseIndex]?.bossHpScale ?? 1f;
        return scale > 0f ? scale : 1f;
    }
}
