using UnityEngine;
using YokiFrame;

/// <summary>
/// 受击白闪：复用 Sprites/HitFlash 着色器，经 MaterialPropertyBlock 逐帧写入强度，
/// 既不产生材质实例、也不改 SpriteRenderer.color，因此不会破坏精英与词缀的原色。
/// </summary>
[DisallowMultipleComponent]
public class SpriteHitFlash : MonoBehaviour
{
    // 默认白闪时长（秒）与峰值强度
    private const float k_defaultDuration = 0.12f;
    private const float k_peakAmount = 1f;

    // 着色器属性 ID（避免每帧字符串查找）
    private static readonly int s_flashAmountId = Shader.PropertyToID("_FlashAmount");

    // 全局共享的闪白材质（运行时按需创建一次，所有精灵共用，避免材质实例膨胀）
    private static Material s_sharedMaterial;

    private SpriteRenderer m_renderer;
    private MaterialPropertyBlock m_block;

    private float m_flashRemain;    // 剩余时长
    private float m_flashDuration;  // 本次时长（衰减分母）

    private void Awake()
    {
        // 渲染组件在本物体上取，取不到再退到子物体（兼容渲染在子物体的来源）
        m_renderer = GetComponent<SpriteRenderer>();
        if (m_renderer == null) m_renderer = GetComponentInChildren<SpriteRenderer>();
        if (m_renderer == null)
        {
            // 无精灵可渲染：关闭自身，避免无效 Update
            enabled = false;
            return;
        }

        if (EnsureSharedMaterial()) m_renderer.sharedMaterial = s_sharedMaterial;
        m_block = new MaterialPropertyBlock();
    }

    /// <summary>触发一次受击白闪；重复调用会刷新为满强度（连击时不会叠加变暗）</summary>
    public void Flash(float duration = k_defaultDuration)
    {
        if (m_renderer == null || m_block == null) return;

        m_flashDuration = Mathf.Max(0.01f, duration);
        m_flashRemain = m_flashDuration;
        ApplyAmount(k_peakAmount);
    }

    private void Update()
    {
        if (m_flashRemain <= 0f) return;

        // 线性衰减：命中瞬间满白，随后快速淡回原色
        m_flashRemain -= Time.deltaTime;
        float t = m_flashRemain <= 0f ? 0f : m_flashRemain / m_flashDuration;
        ApplyAmount(k_peakAmount * t);
    }

    private void OnDisable()
    {
        // 池化复用时复位：归还对象池后再次取出不应残留白闪
        m_flashRemain = 0f;
        ApplyAmount(0f);
    }

    /// <summary>把闪白强度写入渲染器（MPB 覆盖，不影响共享材质资产）</summary>
    private void ApplyAmount(float amount)
    {
        if (m_renderer == null || m_block == null) return;

        m_renderer.GetPropertyBlock(m_block);
        m_block.SetFloat(s_flashAmountId, amount);
        m_renderer.SetPropertyBlock(m_block);
    }

    /// <summary>首次访问时创建共享闪白材质（着色器随 Resources 打包）</summary>
    private static bool EnsureSharedMaterial()
    {
        if (s_sharedMaterial != null) return true;

        var shader = Resources.Load<Shader>("Shaders/HitFlash");
        if (shader == null)
        {
            LogKit.Warning("[SpriteHitFlash] 未找到 Shaders/HitFlash 着色器，受击闪白不可用");
            return false;
        }

        s_sharedMaterial = new Material(shader) { name = "HitFlash (Runtime)" };
        return true;
    }
}
