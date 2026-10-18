using UnityEngine;
using YokiFrame;

/// <summary>
/// 死亡喷血表现：全局仅一个常驻 ParticleSystem，每次死亡重新定位并定向 Emit，
/// 省去 Instantiate/Destroy 开销。World 模拟空间保证多只敌人同时死亡时血花互不干扰。
/// </summary>
public class BloodSprayEffect : MonoBehaviour
{
    /// <summary>血花颜色（暗红，冲击感更强）</summary>
    private static readonly Color k_bloodColor = new Color(0.72f, 0.06f, 0.06f);

    /// <summary>单次死亡喷射的粒子数量</summary>
    private const int k_burstCount = 18;

    /// <summary>受击方向判定阈值（低于此值视为无方向，回退为向上喷射）</summary>
    private const float k_directionDeadZone = 0.0001f;

    private ParticleSystem m_particleSystem;
    private Material m_material;
    private Texture2D m_texture;

    /// <summary>由工厂创建：挂到指定父节点并构建粒子系统</summary>
    public static BloodSprayEffect Create(Transform parent)
    {
        var go = new GameObject("BloodSprayEffect");
        go.transform.SetParent(parent, false);
        var effect = go.AddComponent<BloodSprayEffect>();
        effect.BuildParticleSystem();
        return effect;
    }

    /// <summary>在指定位置朝受击方向喷射一次血花（direction 为零向量时默认向上）</summary>
    public void Play(Vector3 position, Vector2 direction)
    {
        if (m_particleSystem == null) return;

        Vector2 dir = direction.sqrMagnitude > k_directionDeadZone ? direction.normalized : Vector2.up;

        // 让粒子系统的发射轴（局部 +Z）对齐 2D 受击方向，使血花沿受击方向喷出
        transform.position = position;
        transform.rotation = Quaternion.FromToRotation(Vector3.forward, new Vector3(dir.x, dir.y, 0f));

        // 每次喷射前刷新颜色（复用同一系统时新粒子才取到当前色）
        var main = m_particleSystem.main;
        main.startColor = k_bloodColor;

        m_particleSystem.Emit(k_burstCount);
    }

    private void OnDestroy()
    {
        // 运行时创建的材料/贴图需手动释放，避免显存泄漏
        if (m_material != null) Destroy(m_material);
        if (m_texture != null) Destroy(m_texture);
    }

    /// <summary>构建常驻粒子系统（仅手动 Emit 喷射，不持续发射）</summary>
    private void BuildParticleSystem()
    {
        m_particleSystem = gameObject.AddComponent<ParticleSystem>();

        // AddComponent 创建的粒子系统 playOnAwake 默认为 true，会立即进入播放态；
        // 此时设置 duration 会报 "Setting the duration while system is still playing"。
        // 先彻底停止（StopEmittingAndClear 清空并停播）再配置，之后全部由 Play()/Emit 手动驱动。
        m_particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = m_particleSystem.main;
        main.playOnAwake = false;
        main.duration = 1f;
        main.loop = true;                       // 常驻模拟：靠 Emit 手动喷射
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.45f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 6.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.24f);
        main.startColor = k_bloodColor;
        main.simulationSpace = ParticleSystemSimulationSpace.World; // 血花留在世界坐标，不随发射点漂移
        main.gravityModifier = 0f;
        main.maxParticles = 400;

        var emission = m_particleSystem.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;             // 无持续发射，全部由 Emit 触发

        // 锥形发射：围绕局部 +Z（即受击方向）成锥角扩散
        var shape = m_particleSystem.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 24f;
        shape.radius = 0.05f;
        shape.length = 0.05f;

        // 生命期内透明度淡出（前半段保持，后半段快速消失）
        var colorOverLifetime = m_particleSystem.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.55f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

        // 生命期内尺寸收缩（模拟血滴飞散变淡）
        var sizeOverLifetime = m_particleSystem.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.25f));

        var particleRenderer = m_particleSystem.GetComponent<ParticleSystemRenderer>();
        particleRenderer.renderMode = ParticleSystemRenderMode.Stretch; // 沿速度拉伸 → 血线
        particleRenderer.velocityScale = 0.06f;
        particleRenderer.lengthScale = 1.4f;
        particleRenderer.sortingOrder = 5;      // 盖在敌人精灵之上
        particleRenderer.sharedMaterial = CreateMaterial();

        m_particleSystem.Play();
    }

    /// <summary>创建粒子材质（Sprites/Default + 程序化径向渐变贴图）</summary>
    private Material CreateMaterial()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
        {
            LogKit.Error("[BloodSprayEffect] 找不到可用粒子 Shader，喷血效果将不可见");
            return null;
        }

        m_texture = CreateBloodTexture();
        m_material = new Material(shader);
        m_material.mainTexture = m_texture;
        return m_material;
    }

    /// <summary>程序化生成软边圆点贴图（中心不透明、边缘透明），无需美术资源</summary>
    private static Texture2D CreateBloodTexture()
    {
        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float radius = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - radius + 0.5f;
                float dy = y - radius + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / radius;
                float alpha = Mathf.Clamp01(1f - dist);
                alpha *= alpha; // 二次衰减，边缘更柔和
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        return tex;
    }
}
