using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

/// <summary>
/// 画面设置（分辨率 / 窗口模式 / 渲染效果 / 垂直同步 / 亮度）的唯一读写方与唯一应用方。
/// UI 只与本类交互、不直接碰 Screen 与 QualitySettings，使设置入口收敛、存档与实况不会双写不一致。
/// 亮度用全屏黑色遮罩的不透明度实现（内置管线 + 2D 没有后处理 Volume，只能压暗）；遮罩压在 UIRoot 之上，
/// 连 UI 一起变暗，与"显示器亮度"体感一致，并关闭 raycastTarget 以免吃掉输入。
/// 亮度是连续值、拖动时每帧都变，故约定「只应用、不落盘」，由调用方在松手 / 关面板时调 <see cref="FlushPendingSave"/>。
/// </summary>
public static class VideoSettingsManager
{
    /// <summary>亮度遮罩画布 sortingOrder：高于 UIRoot 的所有面板层级（UIRoot 用默认 0）</summary>
    private const int kOverlaySortingOrder = 30000;

    /// <summary>亮度 = 0 时遮罩的不透明度上限（不压成纯黑，保留画面轮廓）</summary>
    private const float kMaxDim = 0.72f;

    /// <summary>遮罩完全不透明的阈值：低于它就直接隐藏遮罩对象，省一次全屏绘制</summary>
    private const float kDimEpsilon = 0.002f;

    /// <summary>亮度遮罩画布（DontDestroyOnLoad，跨场景常驻）</summary>
    private static Canvas s_overlayCanvas;

    /// <summary>亮度遮罩本体（全屏黑 Image）</summary>
    private static Image s_overlayImage;

    /// <summary>亮度有"只应用未落盘"的改动（拖动中），见 <see cref="FlushPendingSave"/></summary>
    private static bool s_brightnessDirty;

    /// <summary>是否已初始化（幂等标记）</summary>
    public static bool IsInitialized { get; private set; }

    // ===== 存档读写 =====

    /// <summary>存档设置对象（无存档 / 未加载时返回 null，不主动创建 MetaManager）</summary>
    private static SettingsSaveData Settings
    {
        get
        {
            if (!MetaManager.HasInstance) return null;
            var model = MetaManager.Instance.Model;
            if (model == null) return null;
            return model.settings ??= new SettingsSaveData();
        }
    }

    /// <summary>画面亮度（0~1；无存档时按"不压暗"返回 1）</summary>
    public static float Brightness
    {
        get
        {
            var s = Settings;
            return s != null ? Mathf.Clamp01(s.brightness) : 1f;
        }
    }

    /// <summary>窗口模式（存档越界时退回无边框）</summary>
    public static GameWindowMode WindowMode
    {
        get
        {
            var s = Settings;
            if (s == null) return GameWindowMode.Borderless;
            return (GameWindowMode)Mathf.Clamp(s.windowMode, (int)GameWindowMode.Borderless, (int)GameWindowMode.FullScreen);
        }
    }

    /// <summary>垂直同步是否开启（无存档时跟随工程当前档）</summary>
    public static bool VSync
    {
        get
        {
            var s = Settings;
            return s != null ? s.vSync : QualitySettings.vSyncCount > 0;
        }
    }

    /// <summary>当前生效的渲染效果档位（没设置过 → 用工程当前档）</summary>
    public static int QualityLevel
    {
        get
        {
            int max = Mathf.Max(0, QualitySettings.names.Length - 1);
            var s = Settings;
            if (s == null || s.qualityLevel < 0) return Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, max);
            return Mathf.Clamp(s.qualityLevel, 0, max);
        }
    }

    /// <summary>工程里的渲染效果档位数（下拉框选项数）</summary>
    public static int QualityLevelCount => QualitySettings.names != null ? QualitySettings.names.Length : 0;

    /// <summary>存档里的分辨率；没设置过返回 0,0（调用方自行退回 Screen.width/height）</summary>
    public static void GetSavedResolution(out int width, out int height)
    {
        var s = Settings;
        width = s != null ? s.screenWidth : 0;
        height = s != null ? s.screenHeight : 0;
    }

    // ===== 初始化 / 应用 =====

    /// <summary>显式初始化：整体应用一次；依赖存档已加载，由启动流程在读档之后调用</summary>
    public static void Init()
    {
        if (IsInitialized) return;
        IsInitialized = true;
        ApplyAll();
    }

    /// <summary>把存档里的画面设置全部应用一遍（顺序有讲究，见下方注释）</summary>
    public static void ApplyAll()
    {
        var s = Settings;

        // 顺序：画质 → vSync → 分辨率/窗口模式 → 亮度。vSyncCount 是每档画质各自的设置，
        // 必须先切画质再写 vSync，否则会被该画质档的默认值盖掉。
        if (s != null && s.qualityLevel >= 0) ApplyQualityLevel(s.qualityLevel);
        if (s != null) ApplyVSync(s.vSync);
        if (s != null) ApplyResolution(s.screenWidth, s.screenHeight, s.windowMode);
        ApplyBrightness(s != null ? s.brightness : 1f);
    }

    // ===== 分辨率 / 窗口模式 =====

    /// <summary>设置分辨率（同时保留当前窗口模式）</summary>
    public static void SetResolution(int width, int height)
    {
        var s = Settings;
        if (width <= 0 || height <= 0) return;

        if (s != null)
        {
            if (s.screenWidth == width && s.screenHeight == height) return;
            s.screenWidth = width;
            s.screenHeight = height;
            ApplyResolution(width, height, s.windowMode);
        }
        else
        {
            ApplyResolution(width, height, (int)WindowMode);
        }

        Persist();
    }

    /// <summary>设置窗口模式（同时保留当前分辨率）</summary>
    public static void SetWindowMode(GameWindowMode mode)
    {
        var s = Settings;
        int raw = (int)mode;

        if (s != null)
        {
            if (s.windowMode == raw) return;
            s.windowMode = raw;
            ApplyResolution(s.screenWidth, s.screenHeight, raw);
        }
        else
        {
            ApplyResolution(0, 0, raw);
        }

        Persist();
    }

    /// <summary>把所有可选分辨率列出来（宽×高去重 + 升序；一定包含当前分辨率，避免存档值不在列表里导致选不中）</summary>
    public static List<Vector2Int> BuildResolutionOptions()
    {
        var result = new List<Vector2Int>();
        var seen = new HashSet<long>();

        var modes = Screen.resolutions;
        for (int i = 0; i < modes.Length; i++)
        {
            var r = modes[i];
            if (r.width <= 0 || r.height <= 0) continue;
            if (seen.Add(ResolutionKey(r.width, r.height)))
                result.Add(new Vector2Int(r.width, r.height));
        }

        // 兜底：Screen.resolutions 在部分平台 / 编辑器下为空，且窗口可能被拉成非标准尺寸
        // → 补上当前分辨率，否则下拉框选不中当前值
        if (seen.Add(ResolutionKey(Screen.width, Screen.height)))
            result.Add(new Vector2Int(Screen.width, Screen.height));

        result.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return result;
    }

    /// <summary>分辨率下拉框的显示文案</summary>
    public static string FormatResolution(Vector2Int r) => $"{r.x} × {r.y}";

    /// <summary>窗口模式的显示文案</summary>
    public static string GetWindowModeLabel(GameWindowMode mode)
    {
        switch (mode)
        {
            case GameWindowMode.Windowed: return "窗口化";
            case GameWindowMode.FullScreen: return "全屏";
            default: return "无边框";
        }
    }

    /// <summary>渲染效果下拉框的显示文案（工程用 Unity 默认档名 → 转中文；已自定义过就直接用原名）</summary>
    public static string GetQualityLabel(int level)
    {
        var names = QualitySettings.names;
        if (names == null || level < 0 || level >= names.Length) return $"档位 {level}";

        switch (names[level])
        {
            case "Very Low": return "极低";
            case "Low": return "低";
            case "Medium": return "中";
            case "High": return "高";
            case "Very High": return "很高";
            case "Ultra": return "极高";
            default: return names[level];
        }
    }

    private static long ResolutionKey(int width, int height) => ((long)width << 32) | (uint)height;

    /// <summary>应用到 Unity（w/h ≤ 0 表示"没设置过分辨率"，此刻只切窗口模式）</summary>
    private static void ApplyResolution(int width, int height, int windowMode)
    {
        var mode = ToFullScreenMode(windowMode);
        try
        {
            if (width > 0 && height > 0)
                Screen.SetResolution(width, height, mode);
            else
                Screen.fullScreenMode = mode;
        }
        catch (System.Exception e)
        {
            LogKit.Warning($"[VideoSettingsManager] 应用分辨率失败({width}x{height}, {mode}): {e.Message}");
        }
    }

    private static FullScreenMode ToFullScreenMode(int windowMode)
    {
        switch ((GameWindowMode)windowMode)
        {
            case GameWindowMode.Windowed: return FullScreenMode.Windowed;
            case GameWindowMode.FullScreen: return FullScreenMode.ExclusiveFullScreen;
            default: return FullScreenMode.FullScreenWindow;   // 无边框
        }
    }

    // ===== 渲染效果 / 垂直同步 =====

    /// <summary>设置渲染效果档位（QualitySettings 的 level；越界忽略）</summary>
    public static void SetQualityLevel(int level)
    {
        if (level < 0 || level >= QualitySettings.names.Length)
        {
            LogKit.Warning($"[VideoSettingsManager] 渲染效果档位越界，忽略: {level}");
            return;
        }

        var s = Settings;
        if (s != null)
        {
            if (s.qualityLevel == level) return;
            s.qualityLevel = level;
        }

        ApplyQualityLevel(level);

        // 切画质会带上该档自己的 vSyncCount → 把玩家的垂直同步偏好再写回去，保持设置面板显示与实况一致
        ApplyVSync(s != null ? s.vSync : QualitySettings.vSyncCount > 0);

        Persist();
    }

    private static void ApplyQualityLevel(int level)
    {
        if (level < 0 || level >= QualitySettings.names.Length) return;
        if (QualitySettings.GetQualityLevel() == level) return;

        // applyExpensiveChanges: true —— 让阴影/抗锯齿这类重设置立刻生效（默认要等下一次画质切换）
        QualitySettings.SetQualityLevel(level, true);
        LogKit.Log($"[VideoSettingsManager] 渲染效果 → {GetQualityLabel(level)}(level {level})");
    }

    /// <summary>设置垂直同步</summary>
    public static void SetVSync(bool on)
    {
        var s = Settings;
        if (s != null)
        {
            if (s.vSync == on) return;
            s.vSync = on;
        }

        ApplyVSync(on);
        Persist();
    }

    private static void ApplyVSync(bool on)
    {
        // 注意：vSyncCount 是按画质档存的，每次切画质都会被覆盖 → 必须先切画质再应用它（见 SetQualityLevel）
        QualitySettings.vSyncCount = on ? 1 : 0;
    }

    // ===== 画面亮度（全屏遮罩） =====

    /// <summary>
    /// 设置亮度：立即应用到屏幕，但不落盘（拖动中每帧都会调），
    /// 由调用方在松手 / 关面板时调 <see cref="FlushPendingSave"/> 统一写盘。
    /// </summary>
    public static void SetBrightness(float value)
    {
        value = Mathf.Clamp01(value);

        var s = Settings;
        if (s == null)
        {
            ApplyBrightness(value);
            return;
        }

        if (!Mathf.Approximately(s.brightness, value))
        {
            s.brightness = value;
            s_brightnessDirty = true;
        }
        ApplyBrightness(value);
    }

    /// <summary>把"只应用未落盘"的亮度值写进存档（松手 / 关面板 / 失焦时调用；没有待写内容时直接返回）</summary>
    public static void FlushPendingSave()
    {
        if (!s_brightnessDirty) return;
        s_brightnessDirty = false;
        Persist();
    }

    /// <summary>把亮度换算成遮罩不透明度并应用</summary>
    private static void ApplyBrightness(float value)
    {
        float dim = (1f - Mathf.Clamp01(value)) * kMaxDim;

        if (dim <= kDimEpsilon)
        {
            // 无需压暗时直接隐藏整个遮罩，省一次全屏绘制（对象保留，拉低亮度时复用）
            if (s_overlayCanvas != null) s_overlayCanvas.gameObject.SetActive(false);
            return;
        }

        EnsureOverlay();
        s_overlayCanvas.gameObject.SetActive(true);

        var color = s_overlayImage.color;
        color.a = dim;
        s_overlayImage.color = color;
    }

    /// <summary>创建（或复用）全屏遮罩：独立 Canvas + 全屏黑 Image，跨场景常驻</summary>
    private static void EnsureOverlay()
    {
        if (s_overlayCanvas == null)
        {
            var canvasGo = new GameObject("VideoBrightnessOverlay", typeof(Canvas), typeof(CanvasScaler));
            Object.DontDestroyOnLoad(canvasGo);

            s_overlayCanvas = canvasGo.GetComponent<Canvas>();
            s_overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            s_overlayCanvas.sortingOrder = kOverlaySortingOrder;   // 压在 UIRoot 之上

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
        }

        if (s_overlayImage == null)
        {
            var dimGo = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            dimGo.transform.SetParent(s_overlayCanvas.transform, false);

            var rt = dimGo.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            s_overlayImage = dimGo.GetComponent<Image>();
            s_overlayImage.color = new Color(0f, 0f, 0f, 0f);
            s_overlayImage.raycastTarget = false;   // 关键：遮罩不能吃输入，否则整个画面点不动
        }
    }

    // ===== 落盘 =====

    /// <summary>写存档；MetaManager 未就绪时跳过（启动早期没有存档对象可写）</summary>
    private static void Persist()
    {
        if (MetaManager.HasInstance) MetaManager.Instance.Save();
    }
}
