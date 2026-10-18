using System;
using System.Collections.Generic;

/// <summary>
/// 单条按键改绑记录：存枚举与 KeyCode 的「名称字符串」而非数值，
/// 使枚举增删重排后旧档仍可解析，解析失败则退回默认键位。
/// </summary>
[Serializable]
public class KeyBindingSaveEntry
{
    /// <summary>GameInputAction 名称（如 Skill1 / WeaponSkill3 / Dodge）</summary>
    public string action;

    /// <summary>KeyCode 名称（如 K / Alpha1 / LeftShift）</summary>
    public string key;
}

/// <summary>
/// 窗口模式（设置面板-画面页签）。数值即 <see cref="SettingsSaveData.windowMode"/> 的取值，
/// **顺序不可改**（存档按数值解析，改顺序会让旧档的窗口模式串档）。
/// </summary>
public enum GameWindowMode
{
    /// <summary>无边框全屏（FullScreenWindow）：没有标题栏与边框，Alt+Tab 秒切，PC 端默认</summary>
    Borderless = 0,

    /// <summary>窗口化</summary>
    Windowed = 1,

    /// <summary>独占全屏（ExclusiveFullScreen）：可独占刷新率，切出会黑屏一下</summary>
    FullScreen = 2,
}

/// <summary>游戏设置存档（音量 / 武器主动"自动释放"偏好 / 局内按键改绑 / 画面设置，随 SaveFactory 持久化到存档）</summary>
[Serializable]
public class SettingsSaveData
{
    public float musicVolume = 1f;   // BGM 音量（0~1）
    public float sfxVolume = 1f;     // SFX 音量（0~1）

    /// <summary>
    /// 关闭「自动释放」的**武器技能槽序号**列表（设置面板-游戏页签的 3 个固定开关，下标 0~2 = 界面上的技能槽 1~3）。
    /// 记录"关闭项"而非"开启项"：缺省（旧档无此字段 / 空列表）= 全部开启，与旧档行为一致。
    /// 口径：按槽位而非武器 id —— 面板开关就是"槽位开关"，换武器后沿用同一槽位的开关状态。
    /// </summary>
    public List<int> autoCastOffSlots = new List<int>();

    /// <summary>
    /// 局内按键改绑（设置面板-Control 页签控制，见 KeyBindingManager）。
    /// 旧档没有该字段 → 为 null，KeyBindingManager 自动使用默认键位（K/J/1/2/3/左Shift），因此无需版本迁移。
    /// </summary>
    public List<KeyBindingSaveEntry> keyBindings = new List<KeyBindingSaveEntry>();

    // ===== 画面设置（设置面板-Vedio 页签）=====
    // 唯一读写方 = VideoSettingsManager（读存档 → 应用到 Unity；玩家操作 → 写这里 + 落盘）。
    // 旧档没有这些字段 → LitJson 反序列化时缺键保留下面的初始值，因此**不需要新增版本迁移**。
    // 注意区分「哨兵值」与「真实值」：分辨率 0 / 画质 -1 都表示"没设置过"，运行时不写回 Unity。

    /// <summary>分辨率宽（0 = 没设置过，保持 Unity 启动时的分辨率）</summary>
    public int screenWidth = 0;

    /// <summary>分辨率高（0 = 没设置过）</summary>
    public int screenHeight = 0;

    /// <summary>窗口模式（取值见 <see cref="GameWindowMode"/>）</summary>
    public int windowMode = (int)GameWindowMode.Borderless;

    /// <summary>渲染效果档位 = QualitySettings 的 level（-1 = 没设置过，跟随工程当前档）</summary>
    public int qualityLevel = -1;

    /// <summary>垂直同步（true = 开）</summary>
    public bool vSync = true;

    /// <summary>画面亮度（0~1，1 = 原始画面；小于 1 时由 VideoSettingsManager 叠一层黑色遮罩压暗）</summary>
    public float brightness = 1f;
}
