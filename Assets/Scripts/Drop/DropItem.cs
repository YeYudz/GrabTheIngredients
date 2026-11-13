using UnityEngine;
using UnityEngine.U2D;
using YokiFrame;

/// <summary>
/// 掉落物类型。
/// </summary>
public enum DropType
{
    Ingredient,  // 食材
    Exp          // 经验宝石
}

/// <summary>
/// 掉落物（业务层）：食材 / 经验宝石
/// 玩家靠近触发吸附 → 到达后拾取 → 归还对象池
/// 通过 Collider2D(Trigger) 检测玩家靠近
/// </summary>
public class DropItem : MonoBehaviour
{
    /// <summary>场上所有激活中的掉落物（断点保存遍历用）</summary>
    public static readonly System.Collections.Generic.List<DropItem> AllDrops = new();

    private DropType mType;
    private string mIngredientId;
    private int mExpValue;
    private bool mPicked;
    private Transform mPlayer;

    /// <summary>掉落物类型（断点保存用）</summary>
    public DropType Type => mType;

    /// <summary>食材ID（kind=食材时有效）</summary>
    public string IngredientId => mIngredientId;

    /// <summary>经验值（kind=经验宝石时有效）</summary>
    public int ExpValue => mExpValue;

    [Header("吸附参数")]
    [SerializeField] private float mPickupRange = 1.2f;   // 触发吸附的距离
    [SerializeField] private float mPickupSpeed = 8f;     // 吸附速度

    [Header("美术（Resources 路径）")]
    [Tooltip("食材图文件夹：图片名 = 食材ID（如 Art/Ingredient/tomato）")]
    [SerializeField] private string m_ingredientArtFolder = "Art/Ingredient/";
    [Tooltip("经验水晶图（不含扩展名）")]
    [SerializeField] private string m_expArtPath = "Art/Exp/Exp";
    [Tooltip("美术最长边的世界尺寸（统一不同分辨率图片的视觉大小）")]
    [SerializeField] private float m_artSize = 0.5f;

    // 渲染组件与预制体原始占位外观（美术缺失时回退）
    private SpriteRenderer mRenderer;
    private Poolable mPoolable;
    private Sprite m_defaultSprite;
    private Color m_defaultColor = Color.white;
    private Vector3 m_baseScale = Vector3.one;

    /// <summary>美术图缓存（跨实例共享）：掉落物高频生成（一局 5000+ 次），
    /// 避免每次从池取出都走 Resources.Load + 路径字符串分配</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Sprite> sArtCache = new();

    // ===== 掉落物图集支持（合批优化） =====
    // 场上同时存在多种食材/宝石掉落，每种是一张独立纹理 → 相机 Y 排序下与敌人交错渲染，
    // 每次纹理切换一次 SetPass（实测后期波次 SetPass 高达 297）。
    // 把 Ingredient/Exp 小图打包进 SpriteAtlas（Assets/Resources/SpriteAtlases/Drops）后，
    // 掉落物统一使用图集纹理 → 掉落物之间零纹理切换。
    // 图集缺失时回退 Resources.Load（未建图集/资源未迁移时功能不受影响）。
    // ⚠ UI 面板（iconPath 配置）仍走 Resources.Load 独立纹理：小图体积小，双份打包内存代价可忽略。

    private static SpriteAtlas sDropAtlas;
    private static bool sDropAtlasLoaded;

    /// <summary>图集内 sprite 名的忽略大小写索引（配置 iconPath 大小写不一致，GetSprite 是精确匹配）</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Sprite> sAtlasNameIndex = new();

    private static Sprite LoadDropSprite(string basePath)
    {
        EnsureDropAtlas();

        if (sDropAtlas != null)
        {
            string baseName = basePath.Substring(basePath.LastIndexOf('/') + 1);

            // 精确名优先（零开销），未命中再走忽略大小写索引兜底
            var hit = sDropAtlas.GetSprite(baseName);
            if (hit != null) return hit;
            if (sAtlasNameIndex.TryGetValue(baseName.ToLowerInvariant(), out var byIndex)) return byIndex;
        }

        return Resources.Load<Sprite>(basePath);
    }

    private static void EnsureDropAtlas()
    {
        if (sDropAtlasLoaded) return;
        sDropAtlasLoaded = true;

        sDropAtlas = Resources.Load<SpriteAtlas>("SpriteAtlases/Drops");
        if (sDropAtlas == null) return;

        // 建立忽略大小写索引：图集打包后 GetSprite 只认精确名
        var names = new System.Collections.Generic.List<string>();
        sDropAtlas.GetSprites(names);
        for (int i = 0; i < names.Count; i++)
        {
            var sp = sDropAtlas.GetSprite(names[i]);
            if (sp != null) sAtlasNameIndex[names[i].ToLowerInvariant()] = sp;
        }
    }

    private void Awake()
    {
        // 缓存渲染组件与预制体占位外观，供美术加载失败时回退
        mRenderer = GetComponent<SpriteRenderer>();
        mPoolable = GetComponent<Poolable>();
        if (mRenderer != null)
        {
            m_defaultSprite = mRenderer.sprite;
            m_defaultColor = mRenderer.color;
        }
        m_baseScale = transform.localScale;
    }

    private void OnEnable()
    {
        // 注册到场上掉落物列表（断点保存按它遍历；池化复用同样走这里）
        if (!AllDrops.Contains(this)) AllDrops.Add(this);
    }

    private void OnDisable()
    {
        AllDrops.Remove(this);
    }

    /// <summary>配置为食材掉落物</summary>
    public void SetupIngredient(string ingredientId)
    {
        mType = DropType.Ingredient;
        mIngredientId = ingredientId;
        mExpValue = 0;
        mPicked = false;
        mPlayer = GameManager.Instance.Player ? GameManager.Instance.Player.transform : null;

        // 按食材ID加载对应美术图（图片名 = 食材ID）
        ApplyArt(string.IsNullOrEmpty(ingredientId) ? null : m_ingredientArtFolder + ingredientId);
    }

    /// <summary>配置为经验宝石</summary>
    public void SetupExp(int expValue)
    {
        mType = DropType.Exp;
        mExpValue = expValue;
        mIngredientId = null;
        mPicked = false;
        mPlayer = GameManager.Instance.Player ? GameManager.Instance.Player.transform : null;

        // 经验宝石统一使用经验水晶美术图
        ApplyArt(m_expArtPath);
    }

    /// <summary>应用掉落物美术（按 Resources 路径加载精灵；加载失败回退预制体占位外观）</summary>
    private void ApplyArt(string resourcesPath)
    {
        if (mRenderer == null) return;

        // 先查静态缓存（命中即零分配），未命中才真正加载（图集优先，Resources 兜底）
        Sprite sprite = null;
        if (!string.IsNullOrEmpty(resourcesPath))
        {
            if (!sArtCache.TryGetValue(resourcesPath, out sprite) || sprite == null)
            {
                sprite = LoadDropSprite(resourcesPath);
                sArtCache[resourcesPath] = sprite;
            }
        }
        if (sprite == null)
        {
            mRenderer.sprite = m_defaultSprite;
            mRenderer.color = m_defaultColor;
            transform.localScale = m_baseScale;
            return;
        }

        // 使用美术原色（不叠加预制体占位色），并按最长边统一视觉大小
        mRenderer.sprite = sprite;
        mRenderer.color = Color.white;
        transform.localScale = m_baseScale * ComputeArtScale(sprite);
    }

    /// <summary>按最长边目标尺寸计算缩放系数（统一不同分辨率图片的视觉大小）</summary>
    private float ComputeArtScale(Sprite sprite)
    {
        Vector2 size = sprite.bounds.size;
        float maxSide = Mathf.Max(size.x, size.y);
        if (maxSide <= 0.0001f || m_artSize <= 0f) return 1f;
        return m_artSize / maxSide;
    }

    private void Update()
    {
        if (mPicked || mPlayer == null) return;

        // 距离检测：进入拾取范围开始吸附
        float dist = Vector2.Distance(transform.position, mPlayer.position);
        if (dist <= mPickupRange)
        {
            // 吸附：向玩家移动
            Vector2 dir = ((Vector2)(mPlayer.position - transform.position)).normalized;
            transform.position += (Vector3)(dir * mPickupSpeed) * Time.deltaTime;

            // 到达玩家 → 拾取
            if (dist <= 0.1f)
            {
                Pickup();
            }
        }
    }

    /// <summary>拾取处理</summary>
    private void Pickup()
    {
        if (mPicked) return;
        mPicked = true;

        switch (mType)
        {
            case DropType.Ingredient:
                // 食材 → 更新背包 + 广播事件
                DropManager.Instance.OnIngredientPicked(mIngredientId, transform.position);
                break;
            case DropType.Exp:
                // 经验 → 玩家成长
                var stats = GameManager.Instance.Player?.GetComponent<PlayerStats>();
                if (stats != null)
                {
                    stats.AddExp(mExpValue);
                }
                break;
        }

        // 归还对象池
        if (mPoolable != null && !string.IsNullOrEmpty(mPoolable.PoolKey))
        {
            PoolFactory.Release(mPoolable.PoolKey, gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
