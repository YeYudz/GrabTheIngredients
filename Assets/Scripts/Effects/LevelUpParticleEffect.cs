using UnityEngine;
using YokiFrame;

/// <summary>
/// 升级粒子特效：订阅升级事件，在相机顶部生成下落粒子，强化升级瞬间的反馈。
/// 粒子系统按序列化参数在运行时构建，与逻辑完全解耦，只承担表现。
/// 生命周期随 Battle 场景——进场景创建、离场景销毁。
/// </summary>
public class LevelUpParticleEffect : MonoBehaviour
{
    // ===== 发射 =====

    [Header("发射")]
    [Tooltip("一次升级特效的总粒子数（不含开场爆发）")]
    [SerializeField] private int m_particleCount = 300;

    [Tooltip("发射持续时长（秒）；总粒子数会在这段时间内均匀撒下")]
    [SerializeField] private float m_emissionDuration = 2f;

    [Tooltip("开场瞬间爆发的粒子数（0 = 不爆发；营造'哗'一下的冲击感）")]
    [SerializeField] private int m_initialBurst = 60;

    // ===== 生成区域（天空） =====

    [Header("生成区域（天空）")]
    [Tooltip("勾选后自动按主相机宽度铺设生成区域；取消则使用下方固定宽度")]
    [SerializeField] private bool m_useCameraBounds = true;

    [Tooltip("非相机模式下的生成区域宽度（世界单位）")]
    [SerializeField] private float m_spawnWidth = 18f;

    [Tooltip("生成区域位于相机顶部上方的高度（世界单位）")]
    [SerializeField] private float m_spawnHeightOffset = 1.5f;

    [Tooltip("生成区域的垂直厚度（世界单位，制造层次感）")]
    [SerializeField] private float m_spawnAreaThickness = 0.8f;

    // ===== 下落运动 =====

    [Header("下落运动")]
    [Tooltip("基础下落速度（世界单位/秒）")]
    [SerializeField] private float m_fallSpeed = 6f;

    [Tooltip("下落速度的随机浮动幅度（快慢交错）")]
    [SerializeField] private float m_fallSpeedVariance = 2f;

    [Tooltip("水平漂移幅度（世界单位/秒，左右随机，营造飘落感）")]
    [SerializeField] private float m_horizontalDrift = 1.2f;

    [Tooltip("重力加速系数（越大下落越越来越快）")]
    [SerializeField] private float m_gravityModifier = 0.35f;

    // ===== 外观 =====

    [Header("外观")]
    [Tooltip("粒子最小尺寸")]
    [SerializeField] private float m_sizeMin = 0.2f;

    [Tooltip("粒子最大尺寸")]
    [SerializeField] private float m_sizeMax = 0.5f;

    [Tooltip("粒子最短生命周期（秒）")]
    [SerializeField] private float m_lifetimeMin = 1.5f;

    [Tooltip("粒子最长生命周期（秒）")]
    [SerializeField] private float m_lifetimeMax = 2.6f;

    [Tooltip("彩色调色板：每颗粒子随机取其中一色")]
    [SerializeField] private Color[] m_colors = DefaultPalette();

    [Tooltip("粒子生命周期末段是否渐隐")]
    [SerializeField] private bool m_fadeOut = true;

    [Tooltip("自旋速度（度/秒，左右随机）")]
    [SerializeField] private float m_spinSpeed = 180f;

    [Tooltip("粒子材质；留空则使用内置柔光材质")]
    [SerializeField] private Material m_material;

    [Tooltip("粒子贴图；留空则使用内置圆形柔光贴图")]
    [SerializeField] private Texture2D m_texture;

    // ===== 渲染 =====

    [Header("渲染")]
    [Tooltip("排序层名称")]
    [SerializeField] private string m_sortingLayerName = "Default";

    [Tooltip("排序层内顺序（越大越靠前，避免被场景精灵遮挡）")]
    [SerializeField] private int m_sortingOrder = 100;

    [Tooltip("勾选后即使在 timeScale=0（升级暂停）时粒子仍按真实时间播放")]
    [SerializeField] private bool m_useUnscaledTime = true;

    /// <summary>场景内单例引用（随场景销毁自动失效，Unity 空检查可识别已销毁对象）</summary>
    private static LevelUpParticleEffect s_instance;

    /// <summary>升级特效预制体缓存（跨战斗复用，省掉每次 EnsureCreated 的 Resources.Load）</summary>
    private static GameObject s_prefabCache;

    /// <summary>默认柔光材质（跨实例共享，避免每次创建特效都 Shader.Find + new Material + 生成贴图）</summary>
    private static Material s_defaultMaterial;

    /// <summary>运行时构建的粒子系统</summary>
    private ParticleSystem m_system;

    private void Awake()
    {
        BuildSystem();

        // 升级即播放（LevelUpManager 可能同时把 timeScale 置 0，粒子用 unscaledTime 保证照常播放）
        EventKit.Type.Register<GameEvents.PlayerLevelUp>(OnPlayerLevelUp)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
    }

    private void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
    }

    // ===== 对外接口 =====

    /// <summary>播放一次升级特效（按相机顶部区域从天而降）</summary>
    public void Play()
    {
        if (m_system == null) return;

        ResolveSpawnArea(out Vector3 center, out float width);
        transform.position = center;

        // 生成区域按本次解析出的宽度重设（相机宽度随分辨率/窗口变化）
        var shape = m_system.shape;
        shape.scale = new Vector3(width, Mathf.Max(0.01f, m_spawnAreaThickness), 0.1f);

        m_system.Clear();
        m_system.Play();
    }

    /// <summary>确保场景内存在一个升级特效实例（由 GameRunningState 调用；已存在则复用）</summary>
    public static LevelUpParticleEffect EnsureCreated()
    {
        if (s_instance != null) return s_instance;

        // 预制体跨战斗缓存：Resources.Load 本身有查找开销，没必要每局都走一次
        if (s_prefabCache == null)
            s_prefabCache = Resources.Load<GameObject>(AssetKeys.LEVELUP_EFFECT);

        if (s_prefabCache == null)
        {
            LogKit.Warning($"[LevelUpParticleEffect] 升级特效预制体缺失: {AssetKeys.LEVELUP_EFFECT}");
            return null;
        }

        var go = Instantiate(s_prefabCache);
        go.name = "LevelUpEffect";
        s_instance = go.GetComponent<LevelUpParticleEffect>();
        return s_instance;
    }

    /// <summary>销毁场景内实例（离开战斗时调用）</summary>
    public static void Dispose()
    {
        if (s_instance == null) return;
        Destroy(s_instance.gameObject);
        s_instance = null;
    }

    // ===== 粒子系统构建 =====

    /// <summary>按序列化参数在运行时构建粒子系统（子对象 Particles）</summary>
    private void BuildSystem()
    {
        var go = new GameObject("Particles");
        go.transform.SetParent(transform, false);

        m_system = go.AddComponent<ParticleSystem>();

        // AddComponent 默认 playOnAwake=true 会立即开始播放，此时设置 duration 会报
        // "Setting the duration while system is still playing"。先彻底停止再配置参数。
        m_system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = m_system.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = Mathf.Max(0.1f, m_emissionDuration);
        main.startLifetime = new ParticleSystem.MinMaxCurve(m_lifetimeMin, m_lifetimeMax);
        main.startSize = new ParticleSystem.MinMaxCurve(m_sizeMin, m_sizeMax);
        main.startSpeed = 0f;                        // 初速为 0，运动完全交给 velocityOverLifetime
        main.startColor = BuildStartColor();         // 彩色调色板随机取色
        main.gravityModifier = m_gravityModifier;    // 下落加速
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.useUnscaledTime = m_useUnscaledTime;    // 升级暂停时仍播放
        main.maxParticles = Mathf.Max(1000, (m_particleCount + m_initialBurst) * 2);

        var emission = m_system.emission;
        emission.enabled = true;
        emission.rateOverTime = m_emissionDuration > 0f ? m_particleCount / m_emissionDuration : 0f;
        if (m_initialBurst > 0)
        {
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)m_initialBurst) });
        }

        // 生成区域：相机顶部一条水平带（点状线形发射，宽度在 Play 时按相机重设）
        var shape = m_system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(m_spawnWidth, Mathf.Max(0.01f, m_spawnAreaThickness), 0.1f);
        shape.position = Vector3.zero;

        // 运动：持续向下 + 左右随机漂移
        var velocity = m_system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = new ParticleSystem.MinMaxCurve(-m_horizontalDrift, m_horizontalDrift);
        velocity.y = new ParticleSystem.MinMaxCurve(-m_fallSpeed - m_fallSpeedVariance, -m_fallSpeed + m_fallSpeedVariance);
        // 三个轴必须处于同一模式：x/y 设为 TwoConstants 后，z 若保持默认 Constant 会报
        // "Particle Velocity curves must all be in the same mode"，这里显式设为同模式（恒定 0）。
        velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

        // 末段渐隐
        var colorOverLife = m_system.colorOverLifetime;
        colorOverLife.enabled = m_fadeOut;
        colorOverLife.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient());

        // 自旋
        var rotation = m_system.rotationOverLifetime;
        rotation.enabled = Mathf.Abs(m_spinSpeed) > 0.01f;
        rotation.z = new ParticleSystem.MinMaxCurve(-m_spinSpeed, m_spinSpeed);

        var psRenderer = m_system.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        psRenderer.sortingLayerName = m_sortingLayerName;
        psRenderer.sortingOrder = m_sortingOrder;
        psRenderer.sharedMaterial = ResolveMaterial();
    }

    /// <summary>彩色起始色：把调色板转为 RandomColor 渐变（每颗粒子随机取一色）</summary>
    private ParticleSystem.MinMaxGradient BuildStartColor()
    {
        var palette = (m_colors != null && m_colors.Length > 0) ? m_colors : DefaultPalette();

        // Gradient 最多支持 8 个颜色键（超出会报 "Max number of color keys is 8"）。
        // 调色板超过 8 色时，沿整个色带均匀重采样到 8 个键，保留颜色分布。
        const int k_maxColorKeys = 8;
        int keyCount = Mathf.Min(palette.Length, k_maxColorKeys);
        var colorKeys = new GradientColorKey[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            float t = keyCount > 1 ? (float)i / (keyCount - 1) : 0f;
            int srcIndex = palette.Length > 1 ? Mathf.RoundToInt(t * (palette.Length - 1)) : 0;
            colorKeys[i] = new GradientColorKey(palette[srcIndex], t);
        }

        var gradient = new Gradient();
        gradient.SetKeys(colorKeys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });

        var startColor = new ParticleSystem.MinMaxGradient(gradient);
        startColor.mode = ParticleSystemGradientMode.RandomColor;
        return startColor;
    }

    /// <summary>生命末段渐隐（白色乘子，只改 alpha，与起始彩色相乘）</summary>
    private static Gradient BuildFadeGradient()
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
        return gradient;
    }

    /// <summary>
    /// 解析粒子材质：Inspector 指定优先，否则用跨实例共享的内置柔光材质（只在首次创建）。
    /// 原来每次创建特效都跑一遍 Shader.Find + new Material + 生成 64×64 贴图，属于加载期无谓开销。
    /// </summary>
    private Material ResolveMaterial()
    {
        if (m_material != null) return m_material;
        if (s_defaultMaterial != null) return s_defaultMaterial;

        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");

        s_defaultMaterial = new Material(shader) { name = "LevelUpParticleMaterial" };
        // Unity 2022.3 内置资源中已不存在 "Default-Particle.psd"，调用 GetBuiltinResource 会报
        // "Failed to find Default-Particle.psd"。改用程序化圆形柔光贴图，不依赖任何内置/美术资源。
        s_defaultMaterial.mainTexture = m_texture != null ? m_texture : CreateSoftCircleTexture(64);
        return s_defaultMaterial;
    }

    /// <summary>程序化圆形柔光贴图（贴图缺失时兜底，避免出现方块粒子）</summary>
    private static Texture2D CreateSoftCircleTexture(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;

        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(1f - dist);
                alpha *= alpha; // 边缘加倍柔和
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply();
        return texture;
    }

    /// <summary>解析生成区域：优先主相机顶部，无相机则退回玩家上方</summary>
    private void ResolveSpawnArea(out Vector3 center, out float width)
    {
        var cam = Camera.main;
        if (m_useCameraBounds && cam != null && cam.orthographic)
        {
            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;
            Vector3 camPos = cam.transform.position;
            center = new Vector3(camPos.x, camPos.y + halfHeight + m_spawnHeightOffset, 0f);
            width = halfWidth * 2f;
            return;
        }

        var player = GameManager.Instance != null ? GameManager.Instance.Player : null;
        Vector3 basePos = player != null ? player.transform.position : transform.position;
        center = new Vector3(basePos.x, basePos.y + m_spawnHeightOffset, 0f);
        width = m_spawnWidth;
    }

    private void OnPlayerLevelUp(GameEvents.PlayerLevelUp evt) => Play();

    /// <summary>默认彩色调色板（红橙黄绿青蓝紫白，共 8 色，与 Gradient 键上限一致）</summary>
    private static Color[] DefaultPalette()
    {
        return new[]
        {
            new Color(1f, 0.29f, 0.29f),
            new Color(1f, 0.60f, 0.20f),
            new Color(1f, 0.92f, 0.30f),
            new Color(0.45f, 0.95f, 0.40f),
            new Color(0.35f, 0.85f, 1f),
            new Color(0.40f, 0.55f, 1f),
            new Color(0.75f, 0.45f, 1f),
            new Color(1f, 1f, 1f)
        };
    }
}
