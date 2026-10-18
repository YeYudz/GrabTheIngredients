using UnityEngine;
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
    private Sprite m_defaultSprite;
    private Color m_defaultColor = Color.white;
    private Vector3 m_baseScale = Vector3.one;

    private void Awake()
    {
        // 缓存渲染组件与预制体占位外观，供美术加载失败时回退
        mRenderer = GetComponent<SpriteRenderer>();
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

        Sprite sprite = string.IsNullOrEmpty(resourcesPath) ? null : Resources.Load<Sprite>(resourcesPath);
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
        var poolable = GetComponent<Poolable>();
        if (poolable != null && !string.IsNullOrEmpty(poolable.PoolKey))
        {
            PoolFactory.Release(poolable.PoolKey, gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
