using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 音频管理器（业务层单例）：音效与音乐的唯一出口，业务侧只发事件或调方法，不直接碰 AudioKit。
/// 音量以 AudioKit 逻辑总线（Music / Sfx）为唯一中枢——改音量立即作用于正在播放的 voice 并写存档，无需重播。
/// 事件订阅负责"某个时刻该响什么"，资源路径统一走 AssetKeys（类型安全，避免硬编码）。
/// BGM 切换的时机由状态机决定，不挂在流程事件上（见 <see cref="Init"/>）。
/// </summary>
public class AudioManager : MonoSingleton<AudioManager>
{
    // ===== 音量（0~1，总线级；并入存档） =====
    private float mBgmVolume = 1f;
    private float mSfxVolume = 1f;

    /// <summary>BGM 音量（0~1，实时作用于正在播放的音乐，并写入存档）</summary>
    public float BgmVolume
    {
        get => mBgmVolume;
        set
        {
            mBgmVolume = Mathf.Clamp01(value);
            ApplyBgmVolume();
            WriteVolumesToSave();
        }
    }

    /// <summary>SFX 音量（0~1，实时作用于正在播放的音效，并写入存档）</summary>
    public float SfxVolume
    {
        get => mSfxVolume;
        set
        {
            mSfxVolume = Mathf.Clamp01(value);
            ApplySfxVolume();
            WriteVolumesToSave();
        }
    }

    /// <summary>战斗 BGM 曲库（进入战斗场景时随机挑一首）</summary>
    private static readonly string[] sBattleBgmList =
    {
        AssetKeys.BGM_BATTLE_1,
        AssetKeys.BGM_BATTLE_2,
    };

    /// <summary>当前正在播放的音乐路径（同一首重复请求时不打断重播）</summary>
    private string mCurrentBgmPath;

    /// <summary>当前音乐 voice 句柄（切歌时用于淡出旧曲）</summary>
    private AudioVoiceHandle mCurrentBgmHandle;

    /// <summary>音乐是否正在播放（与 mCurrentBgmPath 配对，作为切歌守卫）</summary>
    private bool mBgmPlaying;

    /// <summary>资源存在性缓存 + 一次性告警（避免缺失资源每次播放都刷日志）</summary>
    private readonly Dictionary<string, bool> mExistsCache = new();

    /// <summary>同一音效的最小播放间隔（秒）：成群命中/击杀时抑制音效风暴</summary>
    private const float kSfxThrottleInterval = 0.05f;

    /// <summary>各音效上次实际播放时刻（Time.unscaledTime），用于节流</summary>
    private readonly Dictionary<string, float> mSfxLastPlayTime = new();

    // BGM 公开键（兼容旧引用；切歌请用 PlayMenuBgm / PlayBattleBgm）
    public const string BGM_MENU_KEY = AssetKeys.BGM_MENU;
    public const string BGM_GAME_KEY = AssetKeys.BGM_GAME;

    private IUnRegister[] mTokens;

    /// <summary>显式初始化：读取存档音量 + 应用总线音量 + 订阅所有音效事件（防重入）</summary>
    public void Init()
    {
        if (mTokens != null) return;

        // 旧档无音量字段时默认 1；读档后立即作用到总线
        var settings = GetSettings();
        mBgmVolume = Mathf.Clamp01(settings?.musicVolume ?? 1f);
        mSfxVolume = Mathf.Clamp01(settings?.sfxVolume ?? 1f);
        ApplyBgmVolume();
        ApplySfxVolume();

        mTokens = new IUnRegister[]
        {
            // 战斗
            EventKit.Type.Register<GameEvents.BulletHit>(_ => PlaySfx(AssetKeys.SFX_HIT)),
            EventKit.Type.Register<GameEvents.EnemyKilled>(_ => PlaySfx(AssetKeys.SFX_KILL)),
            EventKit.Type.Register<GameEvents.StatusEffectTriggered>(e =>
            {
                if (e.EffectType == SkillEffectType.Explosion) PlaySfx(AssetKeys.SFX_EXPLOSION);
            }),
            // 玩家
            EventKit.Type.Register<GameEvents.PlayerDamaged>(_ => PlaySfx(AssetKeys.SFX_PLAYER_HURT)),
            EventKit.Type.Register<GameEvents.PlayerDied>(_ => PlaySfx(AssetKeys.SFX_PLAYER_DEAD)),
            EventKit.Type.Register<GameEvents.PlayerLevelUp>(_ => PlaySfx(AssetKeys.SFX_LEVEL_UP)),
            // 波次 / 流程
            EventKit.Type.Register<GameEvents.WaveStarted>(_ => PlaySfx(AssetKeys.SFX_WAVE_START)),
            EventKit.Type.Register<GameEvents.GameStart>(_ => PlaySfx(AssetKeys.SFX_GAME_START)),
            // BOSS
            EventKit.Type.Register<GameEvents.BossSpawned>(OnBossSpawned),
            EventKit.Type.Register<GameEvents.BossPhaseChanged>(_ => PlaySfx(AssetKeys.SFX_BOSS_PHASE)),
            EventKit.Type.Register<GameEvents.BossBreakBarFull>(_ => PlaySfx(AssetKeys.SFX_BOSS_BREAK)),
            EventKit.Type.Register<GameEvents.BossDied>(_ =>
            {
                PlaySfx(AssetKeys.SFX_BOSS_DIE);
                PlayBattleBgm(); // BOSS 结束恢复战斗 BGM
            }),
            EventKit.Type.Register<GameEvents.BossRevived>(_ => PlaySfx(AssetKeys.SFX_BOSS_REVIVE)),
            // 烹饪音游
            EventKit.Type.Register<GameEvents.CookingStageStarted>(_ => PlaySfx(AssetKeys.SFX_COOKING_STAGE)),
            EventKit.Type.Register<GameEvents.CookingFinished>(_ => PlaySfx(AssetKeys.SFX_COOKING_FINISH)),
            EventKit.Type.Register<GameEvents.CookingNoteJudged>(OnNoteJudged),
            // 画中画炒菜（随机事件小窗颠勺）
            EventKit.Type.Register<GameEvents.FieldCookingTriggered>(_ => PlaySfx(AssetKeys.SFX_POT_OPEN)),
            EventKit.Type.Register<GameEvents.FieldCookingHintShown>(_ => PlaySfx(AssetKeys.SFX_HINT_SHOW)),
            EventKit.Type.Register<GameEvents.FieldCookingHintJudged>(OnFieldHintJudged),
            EventKit.Type.Register<GameEvents.FieldCookingFinished>(_ => PlaySfx(AssetKeys.SFX_COOKING_FINISH))
        };

        // BGM 由状态机在正确时机驱动（GameMenuState → PlayMenuBgm / GameRunningState → PlayBattleBgm），
        // 故此处不订阅 GameStart / BackToMenu，避免与状态切换重复播放。

        LogKit.Log("[AudioManager] 初始化完成，音效事件已订阅");
    }

    // ===== BGM 播放（按场景/状态调用） =====

    /// <summary>播放主菜单 BGM（进入 MainMenu 场景 / Menu 状态时调用）</summary>
    public void PlayMenuBgm(float volume = 0.6f) => PlayBgm(AssetKeys.BGM_MENU, volume);

    /// <summary>播放战斗 BGM（进入 Battle 场景 / Running 状态时调用；BattleBgm1、BattleBgm2 随机）</summary>
    public void PlayBattleBgm(float volume = 0.6f)
    {
        string path = PickBattleBgmPath();
        if (string.IsNullOrEmpty(path))
        {
            LogKit.Warning("[AudioManager] 未找到可播放的战斗 BGM（BattleBgm1/BattleBgm2）");
            return;
        }
        PlayBgm(path, volume);
    }

    /// <summary>BOSS 出场：优先按 BOSS 配置 bgmPaths 随机选曲，无可用曲目则沿用战斗曲库</summary>
    private void OnBossSpawned(GameEvents.BossSpawned evt)
    {
        PlaySfx(AssetKeys.SFX_BOSS_SPAWN);

        var boss = GameDataMgr.Instance != null ? GameDataMgr.Instance.GetBoss(evt.BossId) : null;
        string path = PickPlayable(boss?.bgmPaths);
        if (string.IsNullOrEmpty(path)) path = PickBattleBgmPath(); // 无 BOSS 专属曲 → 沿用战斗 BGM

        if (string.IsNullOrEmpty(path))
        {
            LogKit.Warning("[AudioManager] BOSS BGM 无可播放资源，保持当前音乐");
            return;
        }
        PlayBgm(path, 0.6f);
    }

    /// <summary>音符判定音效：按 Perfect/Good/Miss 区分</summary>
    private void OnNoteJudged(GameEvents.CookingNoteJudged evt)
    {
        switch (evt.Judgment)
        {
            case "Perfect": PlaySfx(AssetKeys.SFX_RHYTHM_PERFECT); break;
            case "Good": PlaySfx(AssetKeys.SFX_RHYTHM_GOOD); break;
            case "Miss": PlaySfx(AssetKeys.SFX_RHYTHM_MISS); break;
        }
    }

    /// <summary>画中画颠勺判定音效：Hit=点击成功（滋啦~ 炒菜声）/ Miss=漏点（节奏 Miss 音）</summary>
    private void OnFieldHintJudged(GameEvents.FieldCookingHintJudged evt)
    {
        switch (evt.Judgment)
        {
            case "Hit": PlaySfx(AssetKeys.SFX_STIR); break;
            default: PlaySfx(AssetKeys.SFX_RHYTHM_MISS); break;
        }
    }

    /// <summary>从战斗中随机取一首（尽量避开当前正在播放的那首，避免连播同一首）</summary>
    private string PickBattleBgmPath()
    {
        var valid = new List<string>();
        foreach (var path in sBattleBgmList)
        {
            if (Exists(path)) valid.Add(path);
        }
        if (valid.Count == 0) return null;
        if (valid.Count == 1) return valid[0];

        var pool = new List<string>();
        foreach (var path in valid)
        {
            if (path != mCurrentBgmPath) pool.Add(path);
        }
        if (pool.Count == 0) pool = valid;
        return pool[Random.Range(0, pool.Count)];
    }

    /// <summary>从候选列表中随机取一首真实存在的曲目（过滤掉配置里尚未提供的路径）</summary>
    private string PickPlayable(IList<string> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;

        var valid = new List<string>();
        foreach (var path in candidates)
        {
            if (Exists(path)) valid.Add(path);
        }
        return valid.Count == 0 ? null : valid[Random.Range(0, valid.Count)];
    }

    // ===== 音量应用（总线级音量对正在播放的 voice 立即生效） =====

    /// <summary>把 BGM 音量应用到 Music 总线（后端未创建时先暂存，创建时自动同步）</summary>
    private void ApplyBgmVolume()
    {
        try
        {
            AudioKit.SetBusVolume(AudioBus.Music, mBgmVolume);
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[AudioManager] 应用音乐音量失败: {e.Message}");
        }
    }

    /// <summary>把 SFX 音量应用到 Sfx 总线</summary>
    private void ApplySfxVolume()
    {
        try
        {
            AudioKit.SetBusVolume(AudioBus.Sfx, mSfxVolume);
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[AudioManager] 应用音效音量失败: {e.Message}");
        }
    }

    /// <summary>获取存档设置对象（无存档/未加载时返回 null，不主动创建 MetaManager）</summary>
    private SettingsSaveData GetSettings()
    {
        if (!MetaManager.HasInstance || MetaManager.Instance.Model == null) return null;
        var model = MetaManager.Instance.Model;
        if (model.settings == null)
            model.settings = new SettingsSaveData { musicVolume = 1f, sfxVolume = 1f };
        return model.settings;
    }

    /// <summary>音量变更 → 写入存档并保存</summary>
    private void WriteVolumesToSave()
    {
        var settings = GetSettings();
        if (settings == null) return;
        settings.musicVolume = mBgmVolume;
        settings.sfxVolume = mSfxVolume;
        if (MetaManager.HasInstance) MetaManager.Instance.Save();
    }

    // ===== 公开播放接口（音量由总线统一缩放，此处 volume 为单次播放的基础音量） =====

    /// <summary>播放音效（路径缺失/后端未就绪时静默失败，不打断游戏）</summary>
    public void PlaySfx(string path, float volume = 1f, float pitch = 1f)
    {
        if (!Exists(path)) return;

        // 节流：同一音效在极短间隔内被反复请求时只播一次。近战扫掠每帧命中会连续触发 BulletHit / EnemyKilled，
        // 每秒产生几十个 voice（后端 MAX_ACTIVE_VOICES=128），逼近上限时还会刷 Warning 日志。
        float now = Time.unscaledTime;
        if (mSfxLastPlayTime.TryGetValue(path, out float last) && now - last < kSfxThrottleInterval) return;
        mSfxLastPlayTime[path] = now;

        try
        {
            AudioKit.PlaySfx(path, Mathf.Clamp01(volume), pitch);
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[AudioManager] 播放音效失败 {path}: {e.Message}");
        }
    }

    /// <summary>
    /// 播放音乐（自动循环）。同一首已在播放时不重启；切歌先淡出旧曲，避免 Music 总线多首叠加。
    /// </summary>
    public void PlayBgm(string path, float volume = 1f)
    {
        if (!Exists(path)) return;

        // 同一首在播则不打断：跨场景 / 重复事件安全
        if (mBgmPlaying && mCurrentBgmPath == path) return;

        StopBgm();
        try
        {
            mCurrentBgmHandle = AudioKit.PlayMusic(path, true, Mathf.Clamp01(volume));
            mCurrentBgmPath = path;
            mBgmPlaying = true;
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[AudioManager] 播放音乐失败 {path}: {e.Message}");
        }
    }

    /// <summary>停止当前音乐（淡出旧曲，随后回收其 voice）</summary>
    public void StopBgm(float fadeDuration = 0.35f)
    {
        try
        {
            if (fadeDuration > 0f && mCurrentBgmHandle.IsValid && AudioKit.StopWithFade(mCurrentBgmHandle, fadeDuration))
            {
                // 已请求淡出，voice 到点自动回收
            }
            else
            {
                AudioKit.StopBus(AudioBus.Music);
            }
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[AudioManager] 停止音乐失败: {e.Message}");
        }
        mCurrentBgmHandle = default;
        mCurrentBgmPath = null;
        mBgmPlaying = false;
    }

    // ===== 业务点直接调用（无事件的情况） =====

    /// <summary>发射子弹音效；音高随机 ±5%，避免连射听感重复</summary>
    public void PlayShoot() => PlaySfx(AssetKeys.SFX_SHOOT, 0.8f, Random.Range(0.95f, 1.05f));

    /// <summary>UI 点击音效</summary>
    public void PlayUiClick() => PlaySfx(AssetKeys.SFX_UI_CLICK, 1f, 1f);

    /// <summary>重置：停止音乐，单例与已订阅事件保持不动</summary>
    public void Reset()
    {
        StopBgm(0f);
    }

    // ===== 资源存在性 =====

    /// <summary>判断 Resources 下是否存在该音频资源（结果缓存；缺失时仅告警一次）</summary>
    private bool Exists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (mExistsCache.TryGetValue(path, out bool cached)) return cached;

        bool exists = Resources.Load<AudioClip>(path) != null;
        mExistsCache[path] = exists;
        if (!exists) LogKit.Warning($"[AudioManager] 音频资源不存在，已跳过: {path}");
        return exists;
    }
}
