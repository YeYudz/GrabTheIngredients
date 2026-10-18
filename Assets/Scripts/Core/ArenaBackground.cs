using UnityEngine;
using YokiFrame;

/// <summary>
/// 竞技场背景：把整张背景图铺满 ArenaBounds（SortingOrder = -10，不平铺）。
/// 来源优先级为「当前副本的 DungeonConfig.BackGround」> Inspector 精灵 > Resources 路径 > 程序化木纹占位，
/// 保证不同副本有差异化背景，同时缺美术时仍有可读的场地边界。
/// </summary>
public class ArenaBackground : MonoBehaviour
{
    // 背景层 SortingOrder：铺在玩家/敌人/掉落物（默认 0）之下
    private const int k_backgroundSortingOrder = -10;

    /// <summary>场景内唯一的竞技场背景（OnEnable 注册；GameManager 应用副本后据此切背景）</summary>
    public static ArenaBackground Current { get; private set; }

    [Header("背景图（Inspector 拖入优先；留空则按下方路径加载）")]
    [SerializeField] private Sprite m_backgroundSprite;

    [Header("背景图 Resources 路径（不含扩展名，例如 Art/Scene/BG）")]
    [SerializeField] private string m_backgroundSpritePath = "Art/Scene/ArenaBackground";

    /// <summary>当前背景是否来自副本配置（DungeonConfig.BackGround）；为 true 时兜底逻辑不再覆盖</summary>
    public bool HasDungeonBackground { get; private set; }

    [Header("程序化占位纹理（背景图未就位时的兜底，像素越大越清晰）")]
    public int textureWidth = 512;

    [Header("配色")]
    public Color baseColor = new Color(0.60f, 0.44f, 0.27f);   // 案板木色
    public Color plankColor = new Color(0.50f, 0.36f, 0.21f); // 木板条纹
    public Color borderColor = new Color(0.24f, 0.17f, 0.11f);// 边框

    // 程序化木纹缓存（跨战斗复用）：兜底贴图是一张 textureWidth×N 的逐像素图，
    // 每像素要跑 2 次 Mathf.PerlinNoise，一次生成在几十毫秒量级。
    // 战场场景每次进入都会新建 Arena 物体，若不做静态缓存就要反复重算。
    private static Texture2D s_cachedWoodTexture;
    private static Sprite s_cachedWoodSprite;
    private static string s_cachedWoodKey;

    private void OnEnable() => Current = this;

    private void OnDisable()
    {
        if (Current == this) Current = null;
    }

    private void Start()
    {
        // GameManager 若已先行按副本铺好背景（Start 晚于状态机刷新时会出现），无需重复处理
        if (HasDungeonBackground) return;

        var bounds = GetComponent<ArenaBounds>() ?? ArenaBounds.Current;
        if (bounds == null)
        {
            LogKit.Warning("[ArenaBackground] 未找到 ArenaBounds，无法生成背景（请把本脚本和 ArenaBounds 挂到 Arena 物体上）");
            return;
        }

        float arenaWidth = bounds.halfWidth * 2f;
        float arenaHeight = bounds.halfHeight * 2f;

        // 1. 先按"本局进入的副本"取背景（DungeonConfig.BackGround）
        if (TryApplyDungeonBackground(ResolveEnteringDungeon(), arenaWidth, arenaHeight)) return;

        // 2. 副本没配背景 / 载图失败 → 兜底：Inspector 拖入的图 > Resources 路径 > 程序化木纹占位
        var sprite = ResolveBackgroundSprite();
        if (sprite != null) ApplyBackground(sprite, arenaWidth, arenaHeight);
        else ApplyProceduralWood(arenaWidth, arenaHeight);
    }

    /// <summary>
    /// 按副本配置切换背景（GameManager 在 Running 开局应用副本后调用）。
    /// 与 Start 内的自行解析互为兜底：无论脚本执行顺序如何，最终都以本局实际副本为准。
    /// </summary>
    public void ApplyDungeonBackground(DungeonConfig dungeon)
    {
        var bounds = GetComponent<ArenaBounds>() ?? ArenaBounds.Current;
        if (bounds == null)
        {
            LogKit.Warning("[ArenaBackground] 未找到 ArenaBounds，无法按副本切换背景");
            return;
        }

        TryApplyDungeonBackground(dungeon, bounds.halfWidth * 2f, bounds.halfHeight * 2f);
    }

    /// <summary>
    /// 读取副本的 BackGround 字段并铺底。返回 false 表示该副本未配置背景或图未找到（调用方继续走兜底）。
    /// </summary>
    private bool TryApplyDungeonBackground(DungeonConfig dungeon, float arenaWidth, float arenaHeight)
    {
        if (dungeon == null || string.IsNullOrEmpty(dungeon.BackGround)) return false;

        var sprite = Resources.Load<Sprite>(dungeon.BackGround);
        if (sprite == null)
        {
            LogKit.Warning($"[ArenaBackground] 副本「{dungeon.dungeonName}」的背景图未找到：Resources/{dungeon.BackGround}");
            return false;
        }

        m_backgroundSprite = sprite;
        HasDungeonBackground = true;
        ApplyBackground(sprite, arenaWidth, arenaHeight);
        return true;
    }

    /// <summary>
    /// 解析"本局要进入的副本"：断点存档（续玩）> 选关选中 > 配置默认。
    /// 注意场景加载时 GameRunningState 还没进入（Arena 的 Start 先跑），取不到 WaveManager 的结果，
    /// 因此这里按与 GameManager 相同的优先级自行解析一次。
    /// </summary>
    private static DungeonConfig ResolveEnteringDungeon()
    {
        string dungeonId = null;
        if (RunSaveManager.HasSave())
        {
            var save = RunSaveManager.Load();
            if (save != null) dungeonId = save.dungeonId;
        }
        if (string.IsNullOrEmpty(dungeonId) && MetaManager.HasInstance)
            dungeonId = MetaManager.Instance.SelectedDungeonId;

        return GameDataMgr.Instance.GetDungeon(dungeonId) ?? GameDataMgr.Instance.CurrentDungeon;
    }

    /// <summary>解析背景图：优先 Inspector 拖入，其次按 Resources 路径加载</summary>
    private Sprite ResolveBackgroundSprite()
    {
        if (m_backgroundSprite != null) return m_backgroundSprite;
        if (string.IsNullOrEmpty(m_backgroundSpritePath)) return null;

        var sprite = Resources.Load<Sprite>(m_backgroundSpritePath);
        if (sprite == null)
        {
            LogKit.Warning($"[ArenaBackground] 背景图未找到：Resources/{m_backgroundSpritePath}，回退程序化占位");
        }
        return sprite;
    }

    /// <summary>把背景图作为单张整图铺满竞技场（不再平铺），铺在玩家/敌人之下</summary>
    private void ApplyBackground(Sprite sprite, float arenaWidth, float arenaHeight)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) sr = gameObject.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = k_backgroundSortingOrder;

        Vector2 spriteSize = sprite.bounds.size; // 已按 Pixels Per Unit 换算为世界单位
        if (spriteSize.x <= 0f || spriteSize.y <= 0f)
        {
            LogKit.Warning("[ArenaBackground] 背景图世界尺寸为 0，跳过铺底");
            return;
        }

        // 单张整图：缩放到正好铺满竞技场
        transform.localScale = new Vector3(arenaWidth / spriteSize.x, arenaHeight / spriteSize.y, 1f);
    }

    /// <summary>兜底：程序化生成木纹背景贴图铺满竞技场（无美术图时使用）</summary>
    private void ApplyProceduralWood(float arenaWidth, float arenaHeight)
    {
        transform.localScale = Vector3.one;

        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) sr = gameObject.AddComponent<SpriteRenderer>();
        sr.sortingOrder = k_backgroundSortingOrder;

        int texW = textureWidth;
        int texH = Mathf.Max(64, Mathf.RoundToInt(texW * (arenaHeight / arenaWidth)));

        sr.sprite = ResolveProceduralWood(texW, texH, arenaWidth);
    }

    /// <summary>
    /// 取程序化木纹精灵：尺寸/配色一致时复用静态缓存，避免每次进战斗都重跑逐像素 Perlin。
    /// </summary>
    private Sprite ResolveProceduralWood(int texW, int texH, float arenaWidth)
    {
        // 缓存键：尺寸 + 竞技场宽度（影响 Sprite 的 PPU）+ 三档配色
        string key = $"{texW}x{texH}@{arenaWidth:F3}|" +
                     $"{baseColor.r:F3},{baseColor.g:F3},{baseColor.b:F3},{baseColor.a:F3}|" +
                     $"{plankColor.r:F3},{plankColor.g:F3},{plankColor.b:F3},{plankColor.a:F3}|" +
                     $"{borderColor.r:F3},{borderColor.g:F3},{borderColor.b:F3},{borderColor.a:F3}";

        if (s_cachedWoodSprite != null && s_cachedWoodKey == key)
            return s_cachedWoodSprite;

        // 尺寸/配色变化：释放旧缓存后重建
        if (s_cachedWoodSprite != null) Destroy(s_cachedWoodSprite);
        if (s_cachedWoodTexture != null) Destroy(s_cachedWoodTexture);

        s_cachedWoodTexture = GenerateWoodTexture(texW, texH);
        s_cachedWoodSprite = Sprite.Create(
            s_cachedWoodTexture, new Rect(0f, 0f, texW, texH), new Vector2(0.5f, 0.5f), texW / arenaWidth);
        s_cachedWoodKey = key;
        return s_cachedWoodSprite;
    }

    /// <summary>生成案板木纹：横条木板 + Perlin 木纹 + 边缘深色边框</summary>
    private Texture2D GenerateWoodTexture(int texW, int texH)
    {
        var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        int borderPx = Mathf.Max(12, Mathf.RoundToInt(texW * 0.045f)); // 边框宽度（像素）
        float plankH = texH * 0.12f;                                  // 每块木板高度

        var px = new Color[texW * texH];
        for (int y = 0; y < texH; y++)
        {
            for (int x = 0; x < texW; x++)
            {
                // 边缘边框
                if (x < borderPx || x >= texW - borderPx || y < borderPx || y >= texH - borderPx)
                {
                    px[y * texW + x] = borderColor;
                    continue;
                }

                // 木板底色：按行分块，奇偶行微差
                int plank = Mathf.FloorToInt(y / plankH);
                Color c = (plank % 2 == 0) ? baseColor : plankColor;

                // Perlin 木纹：横向长纹理 + 竖向细纹
                float grain = Mathf.PerlinNoise(x * 0.02f, y * 0.06f);
                float streak = Mathf.PerlinNoise(x * 0.5f + y * 0.13f, y * 0.09f);
                c *= 0.82f + 0.18f * grain + 0.06f * streak;

                // 木板接缝加深
                float within = (y % plankH) / plankH;
                if (within < 0.03f || within > 0.97f) c *= 0.65f;

                px[y * texW + x] = c;
            }
        }

        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
