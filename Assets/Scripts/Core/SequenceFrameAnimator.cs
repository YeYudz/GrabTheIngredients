using System.Collections.Generic;
using UnityEngine;
using YokiFrame;

/// <summary>
/// 序列帧动画播放器：运行时把按网格排列的序列图切分为单帧，以固定帧率在 SpriteRenderer 上循环播放，
/// 绕开 Unity 的 Animator / AnimationClip。切分顺序 row-major，与美术出图顺序一致。
/// 一个角色的移动图与待机图同放于 Art/Role/{角色ID}/ 下，由 SetMoving 在两套之间切换。
/// 切出的 Sprite 按「路径 + 网格 + PPU」为键跨实例共享，本类只持有引用，不拥有资源。
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class SequenceFrameAnimator : MonoBehaviour
{
    /// <summary>单套序列图的切分与播放参数</summary>
    [System.Serializable]
    public class SequenceClip
    {
        [Tooltip("是否启用该套序列图；关闭则不加载（例如敌人只有一套图时关闭待机）")]
        public bool isEnabled = true;

        [Tooltip("文件名后缀（拼在「文件夹 + 角色ID」之后）：空 = {ID}.png；_idle = {ID}_idle.png")]
        public string fileSuffix = "";

        [Tooltip("横向列数")]
        public int columns = 4;

        [Tooltip("纵向行数")]
        public int rows = 3;

        [Tooltip("实际播放帧数（0 = 取满 列×行）")]
        public int frameCount = 0;

        [Tooltip("播放帧率（帧/秒）")]
        public float framesPerSecond = 12f;

        [Tooltip("播放速度倍率（1 = 原始帧率；1.5 = 加快到 1.5 倍）")]
        public float playbackSpeed = 1f;

        [Tooltip("是否循环播放")]
        public bool loop = true;
    }

    [Header("资源文件夹（Resources 相对路径，不含扩展名）")]
    [Tooltip("角色序列图所在文件夹，例如 Art/Role/chef/；运行时会按角色ID从 CharacterConfig.spriteSheetPath 覆盖")]
    [SerializeField] private string m_roleFolder = "Art/Role/chef/";

    [Header("移动动画（{角色ID}.png）")]
    [SerializeField] private SequenceClip m_moveClip = new SequenceClip();

    [Header("待机动画（{角色ID}_idle.png）")]
    [SerializeField] private SequenceClip m_idleClip = new SequenceClip
    {
        fileSuffix = "_idle",
        columns = 7,
        rows = 7,
        frameCount = 0,
        framesPerSecond = 12f,
        playbackSpeed = 1f,
        loop = true
    };

    [Header("播放")]
    [Tooltip("是否在启用时自动开始播放")]
    [SerializeField] private bool m_playOnEnable = true;

    [Header("初始状态")]
    [Tooltip("true = 初始播放待机动画；false = 初始播放移动动画")]
    [SerializeField] private bool m_startIdle = true;

    [Header("调试")]
    [Tooltip("启动时打一条「加载了哪套图、共几帧」的日志。BOSS 这类美术接线验证可以打开；" +
             "敌人是池化对象，开了会反复刷屏")]
    [SerializeField] private bool m_logOnStart = false;

    [Header("显示")]
    [Tooltip("单帧的像素/世界单位；越小角色越大（256 表示每帧占 1 个世界单位）")]
    [SerializeField] private float m_pixelsPerUnit = 256f;

    [Tooltip("精灵轴心（0=帧底, 0.5=帧中心, 1=帧顶）。各角色的脚底在帧内的留白不一致，" +
             "用它能对齐「脚踩地面」的位置，避免换角色后忽高忽低")]
    [SerializeField, Range(0f, 1f)] private float m_pivotY = 0.5f;

    // 换角色前必须先还原预制体默认值，否则上一个角色的覆写会残留到下一个角色身上
    private SequenceClip m_moveClipDefault;
    private SequenceClip m_idleClipDefault;
    private float m_pixelsPerUnitDefault;
    private float m_pivotYDefault;
    private string m_roleFolderDefault;

    private SpriteRenderer m_renderer;

    // 同一套序列图（路径 + 网格 + PPU 相同）跨实例共享同一批 Sprite：
    // 否则每个实例各切一份，且池化对象不走 OnDestroy 使引用计数无法收敛，Sprite 会一路泄漏到切场景

    /// <summary>共享帧缓存条目：切好的 Sprite 数组 + 当前引用它的实例数</summary>
    private sealed class CachedFrames
    {
        public Sprite[] Frames;
        public int Refs;
    }

    /// <summary>共享帧缓存（键：Resources 路径 | 列x行 | 帧数 | PPU）</summary>
    private static readonly Dictionary<string, CachedFrames> s_sharedFrames =
        new Dictionary<string, CachedFrames>();

    // 当前实例持有的两套共享帧的缓存键（用于释放引用计数）
    private string m_moveKey;
    private string m_idleKey;

    // 运行期切分出的单帧（移动 / 待机 两套）—— 现在指向共享缓存，不再由本实例拥有
    private Sprite[] m_moveFrames;
    private Sprite[] m_idleFrames;

    // 当前生效的单帧集合（指向 m_moveFrames 或 m_idleFrames）
    private Sprite[] m_frames;

    // 当前生效的切分/播放参数
    private SequenceClip m_activeClip;

    // 角色ID（SetCharacter 传入；未指定时退化为文件夹最后一段）
    private string m_characterId;

    // 决定当前播放移动图还是待机图
    private bool m_isMoving;

    private int m_currentIndex;
    private float m_timer;
    private bool m_isPlaying;

    /// <summary>当前帧序号</summary>
    public int CurrentIndex => m_currentIndex;

    /// <summary>当前生效序列图的单帧总数</summary>
    public int FrameCount => m_frames != null ? m_frames.Length : 0;

    /// <summary>是否处于移动动画状态</summary>
    public bool IsMoving => m_isMoving;

    private void Awake()
    {
        // 预切分：保证首帧渲染即为序列图而非空白
        m_renderer = GetComponent<SpriteRenderer>();
        CaptureClipDefaults();
        BuildAllFrames();

        m_isMoving = !m_startIdle;
        SelectClip(true);
    }

    private void OnEnable()
    {
        if (m_playOnEnable) Play();
    }

    private void OnDestroy()
    {
        // 只归还引用计数（归零才真正销毁 Sprite）；纹理本身由 Resources 生命周期管理
        ReleaseFrames(m_moveKey);
        ReleaseFrames(m_idleKey);
        m_moveKey = null;
        m_idleKey = null;
        m_moveFrames = null;
        m_idleFrames = null;
        m_frames = null;
    }

    private void Update()
    {
        if (!m_isPlaying || m_frames == null || m_frames.Length <= 1) return;

        // 固定帧率累加推进，播放速度倍率叠加其上；timeScale=0 时 deltaTime 为 0，自动暂停
        float fps = m_activeClip != null ? Mathf.Max(0.01f, m_activeClip.framesPerSecond) : 12f;
        float speed = m_activeClip != null ? Mathf.Max(0f, m_activeClip.playbackSpeed) : 1f;

        m_timer += Time.deltaTime * speed;
        float interval = 1f / fps;
        while (m_timer >= interval)
        {
            m_timer -= interval;
            AdvanceFrame();
        }
    }

    /// <summary>开始播放（重置计时并立即刷新当前帧）</summary>
    public void Play()
    {
        bool wasPlaying = m_isPlaying;
        m_isPlaying = true;
        m_timer = 0f;
        ApplyFrame(m_currentIndex);

        // 只在「从停到播」时打一条，BOSS 初始化里的保险 Play() 不会重复刷日志
        if (m_logOnStart && !wasPlaying)
        {
            LogKit.Log($"[SequenceFrameAnimator] {name} 序列图 {m_roleFolder}"
                     + $"｜共 {FrameCount} 帧｜{(m_isMoving ? "移动" : "待机")}"
                     + $"｜{m_renderer?.sprite?.name ?? "空"}");
        }
    }

    /// <summary>停止播放（保留当前帧）</summary>
    public void Stop()
    {
        m_isPlaying = false;
    }

    /// <summary>
    /// 按角色ID切换序列图：取 CharacterConfig.spriteSheetPath 与 sequence 重新切图并应用。
    /// 角色无专属序列图（路径为空）时只记录ID，保留当前文件夹不切图。
    /// </summary>
    public void SetCharacter(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)) return;

        var character = GameDataMgr.Instance?.GetCharacter(characterId);
        string folder = character?.spriteSheetPath;
        if (string.IsNullOrEmpty(folder))
        {
            // 无专属序列图：仅记录ID（保留当前文件夹），不重新切图
            m_characterId = characterId;
            return;
        }

        if (folder == m_roleFolder && m_characterId == characterId && m_moveFrames != null) return;

        m_characterId = characterId;
        m_roleFolder = folder;
        ApplySequenceConfig(character.sequence);
        BuildAllFrames();
        SelectClip(true);
    }

    /// <summary>
    /// 直接指定序列图文件夹并重新切图，供没有 CharacterConfig、美术路径由数据指定的对象（敌人）使用。
    /// 约定文件夹最后一段即图片基名（Art/Enemy/Slime_Elite/ → Slime_Elite.png）；folder 为空 = 还原预制体默认文件夹，
    /// 池化对象换回普通条目时靠它复位外观。网格沿用预制体默认（精英图与原图同规格）。
    /// 与 SetCharacter 互斥：调用后清空角色ID，基名改由文件夹推导。
    /// </summary>
    public void SetSheetFolder(string folder)
    {
        string target = string.IsNullOrEmpty(folder) ? m_roleFolderDefault : folder;
        if (string.IsNullOrEmpty(target)) return;
        if (!target.EndsWith("/")) target += "/";

        // 已是目标文件夹（且非按角色ID推导）则无需重切
        if (string.IsNullOrEmpty(m_characterId) && target == m_roleFolder && m_moveFrames != null) return;

        m_characterId = null;
        m_roleFolder = target;
        BuildAllFrames();
        SelectClip(true);
    }

    /// <summary>
    /// 把角色配置的序列图参数覆写到本实例 clip。
    /// 先还原预制体默认值再覆写，避免上一个角色的参数残留；配置为空则等价于沿用预制体默认。
    /// </summary>
    private void ApplySequenceConfig(CharacterSequenceConfig sequence)
    {
        RestoreClipDefaults();
        if (sequence == null) return;

        if (sequence.pixelsPerUnit > 0f) m_pixelsPerUnit = sequence.pixelsPerUnit;
        if (sequence.pivotY > 0f) m_pivotY = Mathf.Clamp01(sequence.pivotY);
        ApplyClipConfig(m_moveClip, sequence.move);
        ApplyClipConfig(m_idleClip, sequence.idle);
    }

    /// <summary>把单套覆写写进 SequenceClip（数值 ≤0 的字段视为「沿用默认」不覆盖；isEnabled 以配置为准）</summary>
    private static void ApplyClipConfig(SequenceClip clip, SequenceClipConfig config)
    {
        if (clip == null || config == null) return;

        clip.isEnabled = config.isEnabled;
        if (config.columns > 0) clip.columns = config.columns;
        if (config.rows > 0) clip.rows = config.rows;
        if (config.frameCount > 0) clip.frameCount = config.frameCount;
        if (config.framesPerSecond > 0f) clip.framesPerSecond = config.framesPerSecond;
        if (config.playbackSpeed > 0f) clip.playbackSpeed = config.playbackSpeed;
    }

    /// <summary>快照预制体上配置的默认 clip 参数（Awake 时执行一次，此时还没有任何覆写）</summary>
    private void CaptureClipDefaults()
    {
        m_moveClipDefault = CloneClip(m_moveClip);
        m_idleClipDefault = CloneClip(m_idleClip);
        m_pixelsPerUnitDefault = m_pixelsPerUnit;
        m_pivotYDefault = m_pivotY;
        m_roleFolderDefault = m_roleFolder;
    }

    /// <summary>把 clip 参数还原成预制体默认值</summary>
    private void RestoreClipDefaults()
    {
        if (m_moveClipDefault != null) CopyClip(m_moveClipDefault, m_moveClip);
        if (m_idleClipDefault != null) CopyClip(m_idleClipDefault, m_idleClip);
        m_pixelsPerUnit = m_pixelsPerUnitDefault;
        m_pivotY = m_pivotYDefault;
    }

    private static SequenceClip CloneClip(SequenceClip src)
    {
        if (src == null) return null;
        var copy = new SequenceClip();
        CopyClip(src, copy);
        return copy;
    }

    private static void CopyClip(SequenceClip src, SequenceClip dst)
    {
        if (src == null || dst == null) return;
        dst.isEnabled = src.isEnabled;
        dst.fileSuffix = src.fileSuffix;
        dst.columns = src.columns;
        dst.rows = src.rows;
        dst.frameCount = src.frameCount;
        dst.framesPerSecond = src.framesPerSecond;
        dst.playbackSpeed = src.playbackSpeed;
        dst.loop = src.loop;
    }

    /// <summary>切换移动 / 待机动画；由输入状态每帧驱动，故状态未变时直接返回</summary>
    public void SetMoving(bool isMoving)
    {
        if (m_isMoving == isMoving) return;

        m_isMoving = isMoving;
        SelectClip(true);
    }

    /// <summary>设置精灵水平翻转开关（翻转状态独立于切帧，切帧不会重置它）</summary>
    public void SetFlipX(bool flipX)
    {
        if (m_renderer != null) m_renderer.flipX = flipX;
    }

    /// <summary>
    /// 按 m_isMoving 选择生效的序列图集合。
    /// 两套互为兜底：任一套缺失 / 加载失败时退到另一套，保证角色不会冻在上一帧或渲染成空白。
    /// </summary>
    private void SelectClip(bool resetToFirstFrame)
    {
        SequenceClip clip = m_moveClip;
        Sprite[] frames = m_moveFrames;

        if (!m_isMoving && m_idleFrames != null && m_idleFrames.Length > 0)
        {
            clip = m_idleClip;
            frames = m_idleFrames;
        }

        if (frames == null && m_idleFrames != null && m_idleFrames.Length > 0)
        {
            clip = m_idleClip;
            frames = m_idleFrames;
        }

        m_activeClip = clip;
        m_frames = frames;
        m_currentIndex = 0;
        m_timer = 0f;

        if (resetToFirstFrame) ApplyFrame(0);
    }

    /// <summary>推进到下一帧（到达末帧时按循环开关决定回到首帧或停止）</summary>
    private void AdvanceFrame()
    {
        int next = m_currentIndex + 1;
        if (next >= m_frames.Length)
        {
            bool loop = m_activeClip == null || m_activeClip.loop;
            if (!loop)
            {
                m_isPlaying = false;
                return;
            }
            next = 0;
        }
        ApplyFrame(next);
    }

    /// <summary>把指定序号的帧应用到渲染组件</summary>
    private void ApplyFrame(int index)
    {
        m_currentIndex = index;
        if (m_renderer != null && m_frames != null && index >= 0 && index < m_frames.Length)
        {
            m_renderer.sprite = m_frames[index];
        }
    }

    /// <summary>切分两套序列图（移动 + 待机），优先复用跨实例共享的帧缓存</summary>
    private void BuildAllFrames()
    {
        // 先释放上一次持有的共享帧引用（重建/换角色时），避免引用计数泄漏
        ReleaseFrames(m_moveKey);
        ReleaseFrames(m_idleKey);

        m_moveFrames = ResolveFrames(m_moveClip, out m_moveKey);
        m_idleFrames = ResolveFrames(m_idleClip, out m_idleKey);
    }

    /// <summary>
    /// 取一套序列帧：命中共享缓存则直接复用（引用计数 +1），否则切图后写入缓存。
    /// key 输出为空表示该套序列图不可用（未配置/未找到纹理/网格非法），调用方无需释放。
    /// </summary>
    private Sprite[] ResolveFrames(SequenceClip clip, out string key)
    {
        key = null;
        if (clip == null || !clip.isEnabled) return null;

        string path = BuildClipPath(clip.fileSuffix);
        if (string.IsNullOrEmpty(path)) return null;

        key = $"{path}|{clip.columns}x{clip.rows}|{clip.frameCount}|{m_pixelsPerUnit}|{m_pivotY:0.###}";
        if (s_sharedFrames.TryGetValue(key, out var cached) && cached.Frames != null)
        {
            cached.Refs++;
            return cached.Frames;
        }

        var frames = BuildFrames(clip, path);
        if (frames == null)
        {
            // 切图失败：不占用缓存键，避免之后误减引用
            key = null;
            return null;
        }

        s_sharedFrames[key] = new CachedFrames { Frames = frames, Refs = 1 };
        return frames;
    }

    /// <summary>释放对一套共享帧的引用；引用计数归零时销毁 Sprite 并移除缓存条目</summary>
    private static void ReleaseFrames(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!s_sharedFrames.TryGetValue(key, out var cached)) return;

        cached.Refs--;
        if (cached.Refs > 0) return;

        DestroyFrameArray(cached.Frames);
        s_sharedFrames.Remove(key);
    }

    /// <summary>
    /// 清空共享帧缓存（离开战斗 / 清空对象池时调用）。
    /// 池化对象不会走 OnDestroy，引用计数无法自然收敛，必须显式清理。
    /// </summary>
    public static void ClearSharedFrames()
    {
        foreach (var kv in s_sharedFrames)
            DestroyFrameArray(kv.Value.Frames);
        s_sharedFrames.Clear();
    }

    /// <summary>按单套参数读取纹理并切分为单帧（row-major 从左到右、从上到下）</summary>
    private Sprite[] BuildFrames(SequenceClip clip, string path)
    {
        var texture = Resources.Load<Texture2D>(path);
        if (texture == null)
        {
            // 加载失败时保留原有 Sprite，仅告警
            LogKit.Warning($"[SequenceFrameAnimator] 序列图未找到：Resources/{path}");
            return null;
        }

        int columns = Mathf.Max(1, clip.columns);
        int rows = Mathf.Max(1, clip.rows);

        int frameWidth = texture.width / columns;
        int frameHeight = texture.height / rows;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            LogKit.Warning($"[SequenceFrameAnimator] 网格尺寸非法：{texture.width}x{texture.height} / {columns}x{rows}（{path}）");
            return null;
        }

        int available = columns * rows;
        int count = clip.frameCount > 0 ? Mathf.Min(clip.frameCount, available) : available;

        float pixelsPerUnit = m_pixelsPerUnit > 0f ? m_pixelsPerUnit : frameHeight;

        var frames = new Sprite[count];
        for (int i = 0; i < count; i++)
        {
            int column = i % columns;
            int row = i / columns; // 0 = 顶行
            // Unity 纹理坐标原点在左下角，故纵向需按行数翻转
            float x = column * frameWidth;
            float y = (rows - 1 - row) * frameHeight;

            var sprite = Sprite.Create(texture, new Rect(x, y, frameWidth, frameHeight), new Vector2(0.5f, m_pivotY), pixelsPerUnit);
            sprite.name = $"{path}_{i}";
            frames[i] = sprite;
        }
        return frames;
    }

    /// <summary>拼出单套序列图的 Resources 路径（文件夹 + 角色ID + 后缀），不含扩展名</summary>
    private string BuildClipPath(string fileSuffix)
    {
        string folder = m_roleFolder;
        if (string.IsNullOrEmpty(folder)) return null;
        if (!folder.EndsWith("/")) folder += "/";

        return folder + ResolveBaseName() + fileSuffix;
    }

    /// <summary>序列图文件名基名：优先取 SetCharacter 传入的角色ID，未指定时取文件夹最后一段（约定文件夹名 = 角色ID）</summary>
    private string ResolveBaseName()
    {
        if (!string.IsNullOrEmpty(m_characterId)) return m_characterId;

        string folder = (m_roleFolder ?? "").TrimEnd('/');
        int slash = folder.LastIndexOf('/');
        return slash >= 0 ? folder.Substring(slash + 1) : folder;
    }

    /// <summary>销毁一套运行期创建的 Sprite（静态：共享缓存归零/整体清空时调用）</summary>
    private static void DestroyFrameArray(Sprite[] frames)
    {
        if (frames == null) return;
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i] != null) Destroy(frames[i]);
        }
    }
}
