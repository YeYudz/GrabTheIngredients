/// <summary>
/// Resources 资源路径常量：把散落的硬编码路径收敛到一处，
/// 改名或移动目录只需改这里，调用方零改动。
/// </summary>
public static class AssetKeys
{
    // ===== 音频（Resources/Audio/ 下） =====

    // 玩家 / 战斗 SFX
    public const string SFX_SHOOT = "Audio/Sfx/Shoot";
    public const string SFX_HIT = "Audio/Sfx/Hit";
    public const string SFX_EXPLOSION = "Audio/Sfx/Explosion";
    public const string SFX_KILL = "Audio/Sfx/Kill";
    public const string SFX_PLAYER_HURT = "Audio/Sfx/PlayerHurt";
    public const string SFX_PLAYER_DEAD = "Audio/Sfx/PlayerDead";
    public const string SFX_LEVEL_UP = "Audio/Sfx/LevelUp";

    // BOSS 战 SFX
    public const string SFX_BOSS_SPAWN = "Audio/Sfx/BossSpawn";
    public const string SFX_BOSS_PHASE = "Audio/Sfx/BossPhase";
    public const string SFX_BOSS_BREAK = "Audio/Sfx/BossBreak";
    public const string SFX_BOSS_DIE = "Audio/Sfx/BossDie";
    public const string SFX_BOSS_REVIVE = "Audio/Sfx/BossRevive";

    // 烹饪音游 SFX
    public const string SFX_RHYTHM_PERFECT = "Audio/Sfx/RhythmPerfect";
    public const string SFX_RHYTHM_GOOD = "Audio/Sfx/RhythmGood";
    public const string SFX_RHYTHM_MISS = "Audio/Sfx/RhythmMiss";
    public const string SFX_COOKING_STAGE = "Audio/Sfx/CookingStage";
    public const string SFX_COOKING_FINISH = "Audio/Sfx/CookingFinish";

    // 画中画炒菜反差音效（治愈厨房感：开锅"叮~" / 提示"叮" / 点击成功"滋啦~"）
    public const string SFX_POT_OPEN = "Audio/Sfx/PotOpen";    // 小锅弹出：清脆叮~
    public const string SFX_HINT_SHOW = "Audio/Sfx/HintShow";  // 高亮提示出现：短促叮/嗖~
    public const string SFX_STIR = "Audio/Sfx/Stir";           // 点击成功：滋啦~ 炒菜声

    // UI / 流程 SFX
    public const string SFX_UI_CLICK = "Audio/Sfx/UIClick";
    public const string SFX_WAVE_START = "Audio/Sfx/WaveStart";
    public const string SFX_GAME_START = "Audio/Sfx/GameStart";

    // BGM（Resources/Audio/Bgm/ 下）
    public const string BGM_MENU = "Audio/Bgm/MainMenuBgm1";      // 主菜单 BGM
    public const string BGM_BATTLE_1 = "Audio/Bgm/BattleBgm1";    // 战斗 BGM 候选 1
    public const string BGM_BATTLE_2 = "Audio/Bgm/BattleBgm2";    // 战斗 BGM 候选 2
    // 兼容旧键：战斗 BGM 现由 AudioManager 在 BGM_BATTLE_1/2 中随机挑选
    public const string BGM_GAME = BGM_BATTLE_1;

    // ===== UI 面板（Resources/Prefabs/UI/ 下） =====
    public const string PANEL_PREFIX = "Prefabs/UI/";

    // ===== 预制体（Resources/Prefabs/ 下） =====
    public const string BULLET_SEASONING = "Prefabs/Bullets/Bullet_Seasoning";
    public const string BULLET_CHILI="Prefabs/Bullets/Bullet_Chili";
    public const string BULLET_PAN="Prefabs/Bullets/Bullet_Pan";
    public const string DROP_EXP = "Prefabs/Drops/Drop_Exp";
    public const string DROP_INGREDIENT = "Prefabs/Drops/Drop_Ingredient";
    public const string LEVELUP_EFFECT = "Prefabs/Effects/LevelUpEffect";
    public const string BOSS_PUMPKIN_KING = "Prefabs/Boss/Boss_PumpkinKing";
    public const string ENEMY_GOBLIN = "Prefabs/Enemies/Enemy_Goblin";
    public const string ENEMY_MUSHROOM = "Prefabs/Enemies/Enemy_Mushroom";
    public const string ENEMY_SLIME = "Prefabs/Enemies/Enemy_Slime";
    
    

    // ===== 图标（Resources/Art/Icons/ 下） =====
    public const string ICON_PREFIX = "Art/Icons/";

    // ===== UI 图标（Resources/Art/UI/Icon/ 下） =====
    public const string ICON_UI_PREFIX = "Art/UI/Icon/";
    /// <summary>HUD 闪避按钮图标：闪避是固有技能，图标固定、不来自战前装配。
    /// 用 chroma 版（已抠底，真 alpha），避免 HUD 上出现方块底</summary>
    public const string ICON_DODGE = ICON_UI_PREFIX + "icon_dodge_chroma";
}
