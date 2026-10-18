using UnityEngine;
using YokiFrame;

/// <summary>
/// BOSS 生成器：优先加载预制体，缺失时动态构建带组件的对象，
/// 使 BOSS 战在占位美术阶段也能跑通核心逻辑。
/// </summary>
public static class BossSpawner
{
    /// <summary>生成 BOSS，返回 BossController（hpScale = 阶段/关卡难度血量倍率）</summary>
    public static BossController Spawn(string bossId, Vector3 position, float hpScale = 1f)
    {
        var config = GameDataMgr.Instance.GetBoss(bossId);
        if (config == null)
        {
            LogKit.Error($"[BossSpawner] 找不到 BOSS 配置: {bossId}");
            return null;
        }

        GameObject bossGo = null;

        // 1. 优先加载预制体
        var prefab = Resources.Load<GameObject>(config.prefabPath);
        if (prefab != null)
        {
            bossGo = Object.Instantiate(prefab, position, Quaternion.identity);
            LogKit.Log($"[BossSpawner] 从预制体加载 BOSS: {config.prefabPath}");
        }
        else
        {
            // 2. 预制体不存在 → 动态构建占位 BOSS
            bossGo = new GameObject($"Boss_{config.bossName}");
            bossGo.transform.position = position;

            // 占位 Sprite（橙色方块）
            var sr = bossGo.AddComponent<SpriteRenderer>();
            sr.color = new Color(1f, 0.55f, 0f);
            sr.sprite = CreatePlaceholderSprite();
            sr.sortingOrder = 0;

            // 碰撞体
            var col = bossGo.AddComponent<CircleCollider2D>();
            col.radius = 1.2f;
            col.isTrigger = true;

            // 缩放
            bossGo.transform.localScale = Vector3.one * 2.5f;

            LogKit.Log($"[BossSpawner] 动态构建占位 BOSS: {config.bossName}");
        }

        // 确保组件存在
        var health = bossGo.GetComponent<BossHealth>();
        if (health == null) health = bossGo.AddComponent<BossHealth>();

        // 阶段难度血量倍率（须在 Init 之前写入）
        // 开启 scaleHpWithPlayerDps 时，这里会按玩家**当前单目标 DPS** 反推成"等效倍率"
        health.SetHpScale(ResolveHpScale(config, hpScale));

        var controller = bossGo.GetComponent<BossController>();
        if (controller == null) controller = bossGo.AddComponent<BossController>();

        controller.Init(bossId);
        return controller;
    }

    /// <summary>创建一个纯白色方块 Sprite（运行时生成，无需美术资源）</summary>
    private static Sprite CreatePlaceholderSprite()
    {
        var tex = new Texture2D(64, 64);
        var pixels = new Color[64 * 64];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f), 64f);
    }

    /// <summary>
    /// 把「阶段/副本血量倍率」换算成实际写入 BossHealth 的倍率。
    /// <para>
    /// 未开启 <see cref="BossConfig.scaleHpWithPlayerDps"/> 时原样返回；
    /// 开启时按玩家当前**单目标 DPS** 反推血量：finalHp = clamp(targetFightSeconds × DPS, 基准×(min~max)Factor)，
    /// 再除以 config.maxHp 换算回倍率（这样 BossHealth 侧无需改动）。
    /// </para>
    /// </summary>
    private static float ResolveHpScale(BossConfig config, float hpScale)
    {
        if (config == null || !config.scaleHpWithPlayerDps) return hpScale;

        float dps = EstimatePlayerSingleTargetDps();
        if (dps <= 0f)
        {
            LogKit.Warning("[BossSpawner] 无法估算玩家 DPS，BOSS 血量回退为固定倍率");
            return hpScale;
        }

        float baseHp = Mathf.Max(1f, config.maxHp * hpScale);
        float target = Mathf.Max(1f, config.targetFightSeconds) * dps;
        float lo = baseHp * Mathf.Max(0.01f, config.minHpFactor);
        float hi = baseHp * Mathf.Max(1f, config.maxHpFactor);
        float finalHp = Mathf.Clamp(target, lo, hi);

        LogKit.Log($"[BossSpawner] BOSS 血量自适应：玩家单目标 DPS≈{dps:F0}，"
                 + $"目标 {target:F0}，基准 {baseHp:F0}，带宽 [{lo:F0}, {hi:F0}] → 实际 {finalHp:F0}");
        return finalHp / config.maxHp;
    }

    /// <summary>
    /// 估算玩家**单目标**持续 DPS（与 AutoAttack / DamageCalculator 同口径，不含仅命中瞬间生效的附加伤害）。
    /// <para>
    /// 近战命中频率 = 攻速 × 武器攻速倍率 ÷ meleeTicksPerLap（每圈每目标至多一次）；
    /// 远程命中频率 = 攻速 × 武器攻速倍率 × projectileCount。
    /// 面板攻击 × (1 + 伤害加成) × Σ(武器倍率 × 命中频率) × 暴击期望。
    /// </para>
    /// </summary>
    public static float EstimatePlayerSingleTargetDps()
    {
        var gm = GameManager.Instance;
        var player = gm != null ? gm.Player : null;
        if (player == null) return 0f;

        var auto = player.GetComponent<AutoAttack>();
        if (auto == null || auto.WeaponCount == 0) return 0f;

        var pc = GameDataMgr.Instance?.Player?.Player;
        int ticksPerLap = Mathf.Max(1, pc != null ? pc.meleeTicksPerLap : 4);

        float speed = Mathf.Max(0.01f, player.AttackSpeed * (1f + player.AttackSpeedBonus));
        float critMult = 1f + Mathf.Max(0f, player.CritChance) * Mathf.Max(0f, player.CritDamage - 1f);

        float hitRateSum = 0f;
        for (int i = 0; i < auto.WeaponCount; i++)
        {
            var cfg = auto.GetWeaponAt(i);
            if (cfg == null) continue;

            float weaponSpeed = speed * Mathf.Max(0.01f, cfg.attackSpeedScale);
            float rate = cfg.attackMode == WeaponAttackMode.Melee
                ? weaponSpeed / ticksPerLap                          // 单目标：每圈至多 1 击
                : weaponSpeed * Mathf.Max(1, cfg.projectileCount);   // 远程：每发 projectileCount 弹
            hitRateSum += cfg.damageMultiplier * rate;
        }

        return Mathf.Max(0f, player.Attack * (1f + player.DamageBonus) * hitRateSum * critMult);
    }
}
