using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 可改绑的局内动作（枚举值即数组下标，顺序不可随意调整）
/// </summary>
public enum GameInputAction
{
    /// <summary>角色主动技能 1（HUD 技能槽 0）</summary>
    Skill1 = 0,
    /// <summary>角色主动技能 2（HUD 技能槽 1）</summary>
    Skill2 = 1,
    /// <summary>武器主动技能 1（HUD 武器槽 0）</summary>
    WeaponSkill1 = 2,
    /// <summary>武器主动技能 2（HUD 武器槽 1）</summary>
    WeaponSkill2 = 3,
    /// <summary>武器主动技能 3（HUD 武器槽 2）</summary>
    WeaponSkill3 = 4,
    /// <summary>闪避（翻滚）</summary>
    Dodge = 5,
}

/// <summary>
/// 局内按键改绑注册表（静态服务）：6 个可改绑动作的唯一键位来源，消费方统一走 <see cref="IsDown"/>，不再各自硬编码 KeyCode。
/// 持久化只存 KeyCode 名称字符串，枚举重排 / 删值时旧档不会错位；存档未就绪（主菜单、MetaManager 未 Init）时一律用默认键位，不抛异常。
/// 「保留键」是其它系统已占用的固定键位，绑定到它会被拒绝，避免一个操作触发两个系统；一个键只允许绑一个动作。
/// 默认键位沿用改造前的硬编码输入（见 <see cref="kDefaults"/>），保证升级不改手感。
/// </summary>
public static class KeyBindingManager
{
    /// <summary>可改绑动作总数</summary>
    public const int ActionCount = 6;

    /// <summary>默认键位（下标 = GameInputAction）</summary>
    private static readonly KeyCode[] kDefaults =
    {
        KeyCode.K,          // Skill1
        KeyCode.J,          // Skill2
        KeyCode.Alpha1,     // WeaponSkill1
        KeyCode.Alpha2,     // WeaponSkill2
        KeyCode.Alpha3,     // WeaponSkill3
        KeyCode.LeftShift,  // Dodge
    };

    /// <summary>动作显示名（设置面板 / HUD 说明共用同一套文案）</summary>
    private static readonly string[] kActionNames =
    {
        "角色主动技能 1",
        "角色主动技能 2",
        "武器主动技能 1",
        "武器主动技能 2",
        "武器主动技能 3",
        "闪避",
    };

    /// <summary>保留键（其它系统占用的固定键位）：绑定被拒并给出原因</summary>
    private static readonly Dictionary<KeyCode, string> kReserved = new Dictionary<KeyCode, string>
    {
        { KeyCode.W, "移动" }, { KeyCode.A, "移动" }, { KeyCode.S, "移动" }, { KeyCode.D, "移动" },
        { KeyCode.UpArrow, "移动" }, { KeyCode.DownArrow, "移动" },
        { KeyCode.LeftArrow, "移动" }, { KeyCode.RightArrow, "移动" },
        { KeyCode.Escape, "暂停 / 返回" },
        { KeyCode.C, "状态统筹面板" },
        { KeyCode.Space, "烹饪翻面" },
        { KeyCode.Mouse0, "攻击 / 交互" },
        { KeyCode.Mouse1, "移动 / 交互" },
        { KeyCode.F1, "GM 控制台" },
    };

    /// <summary>运行期键位表（下标 = GameInputAction）</summary>
    private static readonly KeyCode[] sKeys = new KeyCode[ActionCount];

    /// <summary>已应用过键位的存档实例（存档换实例 —— 例如 MetaManager 晚于本类初始化 —— 时自动重新同步）</summary>
    private static SaveDataModel sBoundModel;

    /// <summary>KeyCode 全枚举快照（捕获按键用，避免每次 Enum.GetValues 分配）</summary>
    private static readonly KeyCode[] sAllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

    /// <summary>
    /// 键位变更计数（改绑 / 恢复默认 / 存档同步都会自增）。
    /// 供 UI 做"变化才刷新"：HUD 按键说明每帧只比一个 int，不必重建字符串。
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>
    /// 按键轮询屏蔽者（改绑监听期间由设置面板置为自身）：监听中按下的键只应被采集，不能再触发技能。
    /// 必须显式屏蔽：设置面板可从暂停界面打开，此时 timeScale=0 但 Update 照常跑，技能系统仍会响应按键。
    /// 用 UnityEngine.Object + 伪 null 判定：对象销毁后自动失效，不会"卡住屏蔽"。
    /// </summary>
    public static UnityEngine.Object InputSuppressor { get; set; }

    /// <summary>当前是否屏蔽局内按键轮询</summary>
    private static bool Suppressed => InputSuppressor != null;

    // ===== 查询 =====

    /// <summary>取动作当前绑定的按键</summary>
    public static KeyCode GetKey(GameInputAction action)
    {
        SyncFromSave();
        int i = (int)action;
        return (i >= 0 && i < ActionCount) ? sKeys[i] : KeyCode.None;
    }

    /// <summary>取动作显示名</summary>
    public static string GetActionName(GameInputAction action)
    {
        int i = (int)action;
        return (i >= 0 && i < ActionCount) ? kActionNames[i] : action.ToString();
    }

    /// <summary>本帧该动作是否被按下（消费方统一入口；改绑监听期间恒为 false）</summary>
    public static bool IsDown(GameInputAction action)
    {
        if (Suppressed) return false;
        var key = GetKey(action);
        return key != KeyCode.None && Input.GetKeyDown(key);
    }

    /// <summary>该动作是否处于按住状态（改绑监听期间恒为 false）</summary>
    public static bool IsHeld(GameInputAction action)
    {
        if (Suppressed) return false;
        var key = GetKey(action);
        return key != KeyCode.None && Input.GetKey(key);
    }

    // ===== 修改 =====

    /// <summary>改绑结果</summary>
    public enum BindResult
    {
        Success,
        /// <summary>目标键被其它动作占用（ConflictAction 为占用者）</summary>
        Conflict,
        /// <summary>目标键是系统保留键（FailReason 为占用系统说明）</summary>
        Reserved,
        /// <summary>无效键（None）</summary>
        Invalid,
    }

    /// <summary>
    /// 改绑一个动作，成功后立即写盘；失败时不改动任何键位，由 conflictAction / failReason 给出原因。
    /// </summary>
    public static BindResult SetKey(GameInputAction action, KeyCode key,
        out GameInputAction? conflictAction, out string failReason)
    {
        conflictAction = null;
        failReason = null;

        int i = (int)action;
        if (i < 0 || i >= ActionCount) { failReason = "无效动作"; return BindResult.Invalid; }
        if (key == KeyCode.None || key == KeyCode.Escape) { failReason = "该按键不可用"; return BindResult.Invalid; }

        if (kReserved.TryGetValue(key, out string system))
        {
            failReason = system;
            return BindResult.Reserved;
        }

        var owner = FindActionByKey(key, action);
        if (owner != null)
        {
            conflictAction = owner.Value;
            failReason = GetActionName(owner.Value);
            return BindResult.Conflict;
        }

        sKeys[i] = key;
        Version++;
        WriteToSave();
        return BindResult.Success;
    }

    /// <summary>找出占用该按键的动作（<paramref name="except"/> 自身除外）</summary>
    public static GameInputAction? FindActionByKey(KeyCode key, GameInputAction except)
    {
        for (int i = 0; i < ActionCount; i++)
        {
            if (i == (int)except) continue;
            if (sKeys[i] == key) return (GameInputAction)i;
        }
        return null;
    }

    /// <summary>单个动作恢复默认键位</summary>
    public static bool ResetToDefault(GameInputAction action)
    {
        int i = (int)action;
        if (i < 0 || i >= ActionCount) return false;
        sKeys[i] = kDefaults[i];
        Version++;
        WriteToSave();
        return true;
    }

    /// <summary>全部恢复默认键位</summary>
    public static void ResetAll()
    {
        Array.Copy(kDefaults, sKeys, ActionCount);
        Version++;
        WriteToSave();
    }

    /// <summary>该动作当前是否已是默认键位</summary>
    public static bool IsDefault(GameInputAction action)
    {
        int i = (int)action;
        return i >= 0 && i < ActionCount && sKeys[i] == kDefaults[i];
    }

    // ===== 按键捕获（改绑时用） =====

    /// <summary>
    /// 取本帧按下的任意按键（不含 Esc —— Esc 留给"取消改绑"）。
    /// 不用 Input.anyKeyDown：它不覆盖 Esc，且给不出具体键。
    /// </summary>
    public static bool TryCaptureKeyDown(out KeyCode key)
    {
        for (int i = 0; i < sAllKeys.Length; i++)
        {
            var kc = sAllKeys[i];
            if (kc == KeyCode.None || kc == KeyCode.Escape) continue;
            if (Input.GetKeyDown(kc)) { key = kc; return true; }
        }
        key = KeyCode.None;
        return false;
    }

    // ===== 显示名 =====

    /// <summary>按键友好显示名（按钮与 HUD 共用同一套文案）</summary>
    public static string GetKeyDisplay(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.None: return "未绑定";
            case KeyCode.LeftShift: return "左Shift";
            case KeyCode.RightShift: return "右Shift";
            case KeyCode.LeftControl: return "左Ctrl";
            case KeyCode.RightControl: return "右Ctrl";
            case KeyCode.LeftAlt: return "左Alt";
            case KeyCode.RightAlt: return "右Alt";
            case KeyCode.LeftCommand: return "左Cmd";
            case KeyCode.RightCommand: return "右Cmd";
            case KeyCode.Space: return "空格";
            case KeyCode.Return: return "回车";
            case KeyCode.KeypadEnter: return "小键盘回车";
            case KeyCode.Tab: return "Tab";
            case KeyCode.Backspace: return "退格";
            case KeyCode.Delete: return "Del";
            case KeyCode.Insert: return "Ins";
            case KeyCode.Home: return "Home";
            case KeyCode.End: return "End";
            case KeyCode.PageUp: return "PgUp";
            case KeyCode.PageDown: return "PgDn";
            case KeyCode.Mouse0: return "鼠标左键";
            case KeyCode.Mouse1: return "鼠标右键";
            case KeyCode.Mouse2: return "鼠标中键";
        }

        string n = key.ToString();
        if (n.StartsWith("Alpha", StringComparison.Ordinal)) return n.Substring(5);
        if (n.StartsWith("Keypad", StringComparison.Ordinal)) return "小键盘" + n.Substring(6);
        if (n.StartsWith("UpArrow", StringComparison.Ordinal)) return "↑";
        if (n.StartsWith("DownArrow", StringComparison.Ordinal)) return "↓";
        if (n.StartsWith("LeftArrow", StringComparison.Ordinal)) return "←";
        if (n.StartsWith("RightArrow", StringComparison.Ordinal)) return "→";
        return n;
    }

    /// <summary>所有动作当前键位的简称串（HUD 按键说明用）</summary>
    public static string BuildKeyHintText()
    {
        var sb = new StringBuilder(96);
        sb.Append("移动: WASD / 右键   武器主动: ");
        sb.Append(GetKeyDisplay(GetKey(GameInputAction.WeaponSkill1))).Append('/')
          .Append(GetKeyDisplay(GetKey(GameInputAction.WeaponSkill2))).Append('/')
          .Append(GetKeyDisplay(GetKey(GameInputAction.WeaponSkill3)));
        sb.Append("   技能: ");
        sb.Append(GetKeyDisplay(GetKey(GameInputAction.Skill1))).Append('/')
          .Append(GetKeyDisplay(GetKey(GameInputAction.Skill2)));
        sb.Append("   闪避: ").Append(GetKeyDisplay(GetKey(GameInputAction.Dodge)));
        sb.Append("   或点击按钮");
        return sb.ToString();
    }

    // ===== 存档同步 =====

    /// <summary>
    /// 与当前存档对齐键位：只在存档实例变化时重新应用（首次调用 / MetaManager 晚初始化 / 重开档都会命中），
    /// 因此可以在 GetKey 里每帧安全调用。
    /// </summary>
    private static void SyncFromSave()
    {
        var model = MetaManager.HasInstance ? MetaManager.Instance.Model : null;
        if (ReferenceEquals(model, sBoundModel)) return;

        sBoundModel = model;
        Array.Copy(kDefaults, sKeys, ActionCount);
        Version++; // 换了存档实例（首次同步 / 重开档）→ 通知 UI 重新读一次键位

        var entries = model?.settings?.keyBindings;
        if (entries == null) return;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null || string.IsNullOrEmpty(e.action) || string.IsNullOrEmpty(e.key)) continue;
            if (!Enum.TryParse(e.action, out GameInputAction action)) continue;
            if (!Enum.TryParse(e.key, out KeyCode key)) continue;

            int idx = (int)action;
            if (idx < 0 || idx >= ActionCount) continue;
            // 旧档损坏时可能重复/保留键 → 跳过，保证内存表始终是"一项一键"
            if (key == KeyCode.None || kReserved.ContainsKey(key)) continue;
            bool dup = false;
            for (int j = 0; j < ActionCount; j++)
            {
                if (j != idx && sKeys[j] == key) { dup = true; break; }
            }
            if (dup) continue;
            sKeys[idx] = key;
        }
    }

    /// <summary>把内存键位全量写回存档并落盘</summary>
    private static void WriteToSave()
    {
        if (!MetaManager.HasInstance || MetaManager.Instance.Model == null) return;

        var model = MetaManager.Instance.Model;
        if (model.settings == null) model.settings = new SettingsSaveData();
        if (model.settings.keyBindings == null) model.settings.keyBindings = new List<KeyBindingSaveEntry>();

        model.settings.keyBindings.Clear();
        for (int i = 0; i < ActionCount; i++)
        {
            model.settings.keyBindings.Add(new KeyBindingSaveEntry
            {
                action = ((GameInputAction)i).ToString(),
                key = sKeys[i].ToString(),
            });
        }

        sBoundModel = model; // 与内存表保持一致，避免下次 SyncFromSave 覆盖掉刚改的键位
        MetaManager.Instance.Save();
    }
}
