using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

/// <summary>
/// 战斗表现工厂：订阅伤害 / 受击事件，产出飘字、受击闪白与屏幕震动。
/// 战斗逻辑只发事件、本类只做表现，二者完全解耦；
/// 生命周期跟随一局——进局 Init、离局 Dispose，由 GameManager 统一调度。
/// </summary>
public class CombatFeedbackFactory : MonoSingleton<CombatFeedbackFactory>
{
    /// <summary>飘字 Canvas（ScreenSpaceOverlay，独立于 UIRoot，避免与业务 UI 耦合）</summary>
    private Canvas mCanvas;
    private Transform mCanvasRoot;

    /// <summary>飘字对象池（根节点 + 数字 Text + 暴击小标签 Text，参考抖音大数字样式重做）</summary>
    private readonly Stack<DamageTextItem> mTextPool = new Stack<DamageTextItem>();

    private Camera mMainCam;
    private Coroutine mShakeRoutine;
    private Vector3 mShakeOrigin;   // 相机震动起点（停止时复位用）
    private bool mSubscribed;

    /// <summary>当前存活（正在飘字动画中）的伤害数字 → 对应协程，供升级面板打开时清理</summary>
    private readonly Dictionary<DamageTextItem, Coroutine> mActiveDamageTexts = new Dictionary<DamageTextItem, Coroutine>();

    /// <summary>同屏伤害飘字并发上限：超出直接跳过（成群怪围殴时每秒可产生几十次飘字，会引发 Canvas 重建风暴）。
    /// 参考「数字多」的取舍：不做数字合并，仍用跳过策略。</summary>
    private const int kMaxActiveDamageTexts = 20;

    /// <summary>暴击橙（参考样式的橙红大数字）</summary>
    private static readonly Color kCritColor = new Color(1f, 0.42f, 0.1f);

    /// <summary>弱点金（打中 BOSS 当前阶段弱点的专用色）</summary>
    private static readonly Color kWeakColor = new Color(1f, 0.8f, 0.16f);

    /// <summary>元素青蓝（元素伤害数字主色，与物理的暖白形成强对比）</summary>
    private static readonly Color kElemColor = new Color(0.36f, 0.86f, 1f);

    /// <summary>描边深棕黑（让白/橙数字在浅色场景上也清晰）</summary>
    private static readonly Color kOutlineColor = new Color(0.16f, 0.09f, 0.06f, 0.9f);

    /// <summary>描边深青黑（元素伤害专用描边，配合青蓝数字）</summary>
    private static readonly Color kElemOutlineColor = new Color(0.02f, 0.15f, 0.2f, 0.9f);

    /// <summary>敌人死亡喷血粒子表现（常驻单实例，随本工厂生命周期销毁）</summary>
    private BloodSprayEffect mBloodSpray;

    /// <summary>订阅事件 + 初始化 Canvas/相机（防重入）</summary>
    public void Init()
    {
        if (mSubscribed) return;
        mSubscribed = true;

        EnsureCanvas();
        if (mMainCam == null) mMainCam = Camera.main;
        mBloodSpray = BloodSprayEffect.Create(transform);

        EventKit.Type.Register<GameEvents.DamageDealt>(OnDamageDealt)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        // 玩家受击 → 闪白（玩家伤害走 PlayerDamaged，不属于 DamageDealt）
        EventKit.Type.Register<GameEvents.PlayerDamaged>(OnPlayerDamaged)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        // BOSS 实际伤害（含弱点/虚弱倍率，飘字显示真实数字）
        EventKit.Type.Register<GameEvents.BossDamageApplied>(OnBossDamageApplied)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        // 敌人死亡 → 沿受击方向喷射血花（增强打击感）
        EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        // 进入音游（timeScale=0）时立即停止震动，避免暴击抖动残留
        EventKit.Type.Register<GameEvents.BossHpDepleted>(_ => StopShake())
            .UnRegisterWhenGameObjectDestroyed(gameObject);
        LogKit.Log("[CombatFeedbackFactory] 已初始化（飘字/震动/闪白/喷血）");
    }

    /// <summary>取消订阅 + 清理表现对象（基类静态 Dispose() 销毁实例时自动触发）</summary>
    private void DisposeInternal()
    {
        if (!mSubscribed) return;
        mSubscribed = false;

        EventKit.Type.UnRegister<GameEvents.DamageDealt>(OnDamageDealt);
        EventKit.Type.UnRegister<GameEvents.PlayerDamaged>(OnPlayerDamaged);
        EventKit.Type.UnRegister<GameEvents.BossDamageApplied>(OnBossDamageApplied);
        EventKit.Type.UnRegister<GameEvents.EnemyKilled>(OnEnemyKilled);

        if (mShakeRoutine != null)
        {
            StopCoroutine(mShakeRoutine);
            mShakeRoutine = null;
        }

        // 池内飘字/Canvas/喷血对象随本 GameObject 一起销毁，无需逐个 Destroy
        mTextPool.Clear();
        mActiveDamageTexts.Clear();
        mBloodSpray = null;
        mCanvasRoot = null;
        mCanvas = null;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        DisposeInternal();
    }

    // ===== 事件处理 =====

    private void OnDamageDealt(GameEvents.DamageDealt evt)
    {
        // BOSS 受击飘字由 BossDamageApplied 提供（含弱点/虚弱倍率后的真实伤害），这里跳过避免重复
        bool isBoss = evt.Target != null && evt.Target.GetComponent<BossHealth>() != null;
        if (!isBoss)
        {
            SpawnDamageText(evt.Position, evt.Damage, evt.IsCrit, evt.DamageType);
        }

        if (evt.Target != null)
        {
            FlashHit(evt.Target);
        }

        // 暴击才触发屏幕震动（避免普通命中每帧抖动过频）
        if (evt.IsCrit)
        {
            Shake(0.12f, 0.15f);
        }
    }

    /// <summary>玩家受击 → 自身精灵闪白（玩家掉血走 PlayerDamaged 事件）</summary>
    private void OnPlayerDamaged(GameEvents.PlayerDamaged evt)
    {
        if (evt.Damage <= 0f) return; // 0 伤害仅用于 HUD 刷新，不闪白

        var player = GameManager.Instance != null ? GameManager.Instance.Player : null;
        if (player != null) FlashHit(player.gameObject);
    }

    /// <summary>BOSS 实际受到伤害（飘字显示含弱点/虚弱倍率后的最终伤害）</summary>
    private void OnBossDamageApplied(GameEvents.BossDamageApplied evt)
    {
        // 暴击/弱点一律用结算时的真实标记，不再按"伤害数值够大"反推（那会让高伤害的每一刀都挂「暴击」）
        SpawnDamageText(evt.Position, evt.Damage, evt.IsCrit, evt.DamageType, evt.IsWeaknessHit);

        if (evt.IsCrit) Shake(0.12f, 0.18f);
        else if (evt.IsWeaknessHit) Shake(0.08f, 0.09f); // 弱点给一次更轻的震动
    }

    /// <summary>敌人死亡：沿致死一击的受击方向喷射血花（无方向时按玩家→敌人方向兜底）</summary>
    private void OnEnemyKilled(GameEvents.EnemyKilled evt)
    {
        if (mBloodSpray == null) return;

        Vector2 direction = evt.HitDirection;
        if (direction.sqrMagnitude < 0.0001f)
        {
            // 无定向命中（AOE/清场）：默认由玩家指向敌人，喷向远离玩家一侧
            var player = GameManager.Instance != null ? GameManager.Instance.Player : null;
            if (player != null)
                direction = (Vector2)(evt.Position - player.transform.position);
        }
        mBloodSpray.Play(evt.Position, direction);
    }

    // ===== 伤害飘字 =====

    /// <summary>
    /// 在世界坐标位置生成伤害飘字，样式分两个互不干扰的维度：
    /// ① 伤害类型 → 数字主色 + 描边：物理 = 暖白 / 元素 = 青蓝（元素额外在数字下方挂「元素」小标签）；
    /// ② 命中品质 → 数字上方的小标签：弱点 = 金色「弱点」/ 暴击 = 橙红「暴击」，两者可同时出现；
    /// 字号：暴击 &gt; 弱点 &gt; 普通。
    /// </summary>
    public void SpawnDamageText(Vector3 worldPos, float damage, bool isCrit,
        DamageType damageType = DamageType.Physical, bool isWeakness = false)
    {
        if (mMainCam == null) mMainCam = Camera.main;
        if (mMainCam == null) return;

        Vector3 screenPos = mMainCam.WorldToScreenPoint(worldPos);
        if (screenPos.z < 0f) return; // 相机背后的目标不显示

        // 并发上限：同屏飘字过多时直接跳过，避免 UI 重建风暴（超出部分只是不显示数字，不影响伤害结算）
        if (mActiveDamageTexts.Count >= kMaxActiveDamageTexts) return;

        var item = GetDamageText();

        // ① 错落堆叠：围绕命中点随机偏移 + 小角度随机旋转
        Vector3 pos = screenPos + new Vector3(Random.Range(-60f, 60f), Random.Range(-28f, 36f), 0f);
        item.Root.position = pos;
        item.Root.localEulerAngles = new Vector3(0f, 0f, Random.Range(-6f, 6f));

        bool isElem = damageType == DamageType.Element;

        // ② 字号：暴击显著更大，弱点居中，普通最小
        float size;
        if (isCrit) size = Random.Range(50f, 68f);
        else if (isWeakness) size = Random.Range(36f, 48f);
        else size = Random.Range(26f, 40f);

        // ③ 数字主色 = 伤害类型（品质靠标签表达，两套语义不抢同一个颜色通道）
        Color numColor = isElem
            ? Color.Lerp(kElemColor, Color.white, Random.Range(0f, 0.22f))
            : Color.Lerp(new Color(0.82f, 0.82f, 0.85f), Color.white, Random.value);

        bool withBang = isCrit || isWeakness; // 感叹号只给"特别的一击"，普通命中不再随机带（避免误读成暴击）
        item.Num.text = Mathf.RoundToInt(damage).ToString("N0", CultureInfo.InvariantCulture)
            + (withBang ? "!" : string.Empty);
        item.Num.fontSize = Mathf.RoundToInt(size);
        item.Num.color = numColor;
        item.NumOutline.effectColor = isElem ? kElemOutlineColor : kOutlineColor;
        item.NumBaseColor = numColor;

        // ④ 数字上方标签：弱点（金）/ 暴击（橙红），同时命中时左右并排
        float tagSize = Mathf.Max(13f, size * 0.4f);
        float tagY = size * 0.62f + tagSize * 0.55f;
        float tagOffset = tagSize * 1.28f; // 两个标签并排时的中心偏移（「X点」两字宽 ≈ 2×字号）
        bool both = isWeakness && isCrit;

        item.WeakTag.gameObject.SetActive(isWeakness);
        if (isWeakness)
        {
            item.WeakTag.fontSize = Mathf.RoundToInt(tagSize);
            item.WeakTag.color = kWeakColor;
            item.WeakTag.rectTransform.anchoredPosition = new Vector2(both ? -tagOffset : 0f, tagY);
            item.WeakBaseColor = kWeakColor;
        }

        item.CritTag.gameObject.SetActive(isCrit);
        if (isCrit)
        {
            item.CritTag.fontSize = Mathf.RoundToInt(tagSize);
            item.CritTag.color = kCritColor;
            item.CritTag.rectTransform.anchoredPosition = new Vector2(both ? tagOffset : 0f, tagY);
            item.CritBaseColor = kCritColor;
        }

        // ⑤ 元素伤害：数字下方挂「元素」小标签（物理不挂，靠暖白数字区分）
        item.TypeTag.gameObject.SetActive(isElem);
        if (isElem)
        {
            item.TypeTag.fontSize = Mathf.RoundToInt(Mathf.Max(11f, size * 0.3f));
            item.TypeTag.color = kElemColor;
            item.TypeTag.rectTransform.anchoredPosition = new Vector2(0f, -(size * 0.62f + tagSize * 0.45f));
            item.TypeBaseColor = kElemColor;
        }

        mActiveDamageTexts.Add(item, StartCoroutine(DamageTextRoutine(item)));
    }

    /// <summary>清空所有存活的伤害飘字（升级面板打开时调用，避免遮挡 3选1 内容）</summary>
    public void ClearAllDamageTexts()
    {
        if (mActiveDamageTexts.Count == 0) return;
        foreach (var kv in mActiveDamageTexts)
        {
            var item = kv.Key;
            if (item.Root == null) continue;
            StopCoroutine(kv.Value);
            item.Root.gameObject.SetActive(false);
            mTextPool.Push(item);
        }
        mActiveDamageTexts.Clear();
    }

    private DamageTextItem GetDamageText()
    {
        DamageTextItem item = mTextPool.Count > 0 ? mTextPool.Pop() : CreateDamageText();
        item.Root.gameObject.SetActive(true);
        item.Num.gameObject.SetActive(true);
        return item;
    }

    private DamageTextItem CreateDamageText()
    {
        EnsureCanvas();

        // 根节点：只负责整体缩放 / 旋转 / 位移
        var root = new GameObject("DamageText", typeof(RectTransform)).transform;
        root.SetParent(mCanvasRoot, false);

        var item = new DamageTextItem { Root = (RectTransform)root };

        // 数字 Text：大号粗体 + 深色描边 + 投影（样式里的厚重感主要来自描边）
        var numGo = new GameObject("Num", typeof(RectTransform));
        numGo.transform.SetParent(root, false);
        var numRect = (RectTransform)numGo.transform;
        numRect.sizeDelta = new Vector2(600f, 140f);
        item.Num = numGo.AddComponent<Text>();
        item.Num.font = GetBuiltinFont();
        item.Num.fontStyle = FontStyle.Bold;
        item.Num.horizontalOverflow = HorizontalWrapMode.Overflow;
        item.Num.verticalOverflow = VerticalWrapMode.Overflow;
        item.Num.raycastTarget = false;
        item.Num.alignment = TextAnchor.MiddleCenter;
        item.NumOutline = numGo.AddComponent<Outline>();
        item.NumOutline.effectColor = kOutlineColor;
        item.NumOutline.effectDistance = new Vector2(3f, -3f);
        var numShadow = numGo.AddComponent<Shadow>();
        numShadow.effectColor = new Color(0f, 0f, 0f, 0.35f);
        numShadow.effectDistance = new Vector2(0f, -5f);

        // 上方品质标签：弱点（金）/ 暴击（橙红）—— 各自独立 Text，可同时显示
        item.WeakTag = CreateTag(root, "WeakLabel", "弱点");
        item.CritTag = CreateTag(root, "CritLabel", "暴击");
        // 下方类型标签：元素（青蓝）
        item.TypeTag = CreateTag(root, "TypeLabel", "元素");

        item.WeakTag.gameObject.SetActive(false);
        item.CritTag.gameObject.SetActive(false);
        item.TypeTag.gameObject.SetActive(false);

        return item;
    }

    /// <summary>创建数字旁的小标签（默认隐藏，由 SpawnDamageText 决定内容/颜色/位置）</summary>
    private static Text CreateTag(Transform root, string name, string text)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(root, false);
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = new Vector2(240f, 60f);

        var label = go.AddComponent<Text>();
        label.font = GetBuiltinFont();
        label.fontStyle = FontStyle.Bold;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;
        label.alignment = TextAnchor.MiddleCenter;
        label.text = text;

        var outline = go.AddComponent<Outline>();
        outline.effectColor = kOutlineColor;
        outline.effectDistance = new Vector2(2f, -2f);
        return label;
    }

    /// <summary>飘字动画：弹出（带一次过冲）→ 缓慢上漂 → 后 40% 时间淡出，结束后回收</summary>
    private IEnumerator DamageTextRoutine(DamageTextItem item)
    {
        const float popDuration = 0.16f;
        const float duration = 0.9f;
        float elapsed = 0f;
        Vector3 startPos = item.Root.position;

        SetAlpha(item, 0f);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Min(elapsed / duration, 1f);

            // ① 弹性放大：从 20% 弹出并带一次过冲（EaseOutBack）
            float scale = elapsed < popDuration
                ? Mathf.Lerp(0.2f, 1f, EaseOutBack(elapsed / popDuration))
                : 1f;
            item.Root.localScale = new Vector3(scale, scale, 1f);

            // ② 缓慢上漂（错落感主要来自出生偏移，这里只做轻微漂移）
            item.Root.position = startPos + Vector3.up * (55f * t);

            // ③ 后 40% 时间淡出
            float alpha = t > 0.6f ? Mathf.Lerp(1f, 0f, (t - 0.6f) / 0.4f) : 1f;
            SetAlpha(item, alpha);
            yield return null;
        }

        item.Root.localScale = Vector3.one;
        item.Root.localEulerAngles = Vector3.zero;
        item.Num.gameObject.SetActive(false);
        item.WeakTag.gameObject.SetActive(false);
        item.CritTag.gameObject.SetActive(false);
        item.TypeTag.gameObject.SetActive(false);
        item.Root.gameObject.SetActive(false);
        mTextPool.Push(item);
        mActiveDamageTexts.Remove(item);
    }

    /// <summary>统一按各自基色设置透明度（数字 / 弱点 / 暴击 / 元素 四个 Text）</summary>
    private static void SetAlpha(DamageTextItem item, float alpha)
    {
        item.Num.color = WithAlpha(item.NumBaseColor, alpha);
        if (item.WeakTag.gameObject.activeSelf) item.WeakTag.color = WithAlpha(item.WeakBaseColor, alpha);
        if (item.CritTag.gameObject.activeSelf) item.CritTag.color = WithAlpha(item.CritBaseColor, alpha);
        if (item.TypeTag.gameObject.activeSelf) item.TypeTag.color = WithAlpha(item.TypeBaseColor, alpha * 0.95f);
    }

    private static Color WithAlpha(Color c, float alpha)
    {
        c.a = alpha;
        return c;
    }

    /// <summary>回弹缓动（带一次过冲），用于飘字的弹出节奏</summary>
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float xm1 = x - 1f;
        return 1f + c3 * xm1 * xm1 * xm1 + c1 * xm1 * xm1;
    }

    // ===== 受击闪白 =====

    /// <summary>
    /// 受击目标精灵短暂闪白。
    /// 敌人预制体上已预挂 SpriteHitFlash（首批受击时才 AddComponent 会把
    /// Shader 加载 + new Material + 组件 Awake 的开销正好叠在开局掉帧时刻）；
    /// 这里的 AddComponent 仅作为其它目标（玩家/BOSS/掉落物）的兜底。
    /// </summary>
    public void FlashHit(GameObject target, float duration = 0.12f)
    {
        if (target == null) return;

        var flash = target.GetComponent<SpriteHitFlash>();
        if (flash == null) flash = target.AddComponent<SpriteHitFlash>();
        flash.Flash(duration);
    }

    // ===== 屏幕震动 =====

    /// <summary>相机震动（衰减式抖动）</summary>
    public void Shake(float duration, float magnitude)
    {
        if (mMainCam == null) mMainCam = Camera.main;
        if (mMainCam == null) return;

        mShakeOrigin = mMainCam.transform.localPosition;
        if (mShakeRoutine != null) StopCoroutine(mShakeRoutine);
        mShakeRoutine = StartCoroutine(ShakeRoutine(duration, magnitude));
    }

    /// <summary>立即停止震动并复位相机（进入音游/暂停时调用，防止 timeScale=0 时抖动残留）</summary>
    public void StopShake()
    {
        if (mShakeRoutine != null)
        {
            StopCoroutine(mShakeRoutine);
            mShakeRoutine = null;
        }
        if (mMainCam != null)
        {
            mMainCam.transform.localPosition = mShakeOrigin;
        }
    }

    private IEnumerator ShakeRoutine(float duration, float magnitude)
    {
        Vector3 origin = mMainCam.transform.localPosition;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            // 用 unscaledDeltaTime：音游暂停(timeScale=0)时震动也能按真实时间结束
            elapsed += Time.unscaledDeltaTime;
            float decay = 1f - (elapsed / duration);
            mMainCam.transform.localPosition = origin + (Vector3)(Random.insideUnitCircle * magnitude * decay);
            yield return null;
        }

        mMainCam.transform.localPosition = origin;
        mShakeRoutine = null;
    }

    // ===== 工具 =====

    private void EnsureCanvas()
    {
        if (mCanvasRoot != null) return;

        var go = new GameObject("DamageCanvas");
        go.transform.SetParent(transform, false);

        mCanvas = go.AddComponent<Canvas>();
        mCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // 位于战斗表现层（低于 UIRoot Canvas 的 0）：飘字绘制在 3D 场景之上，
        // 但会被任意业务 UI 面板遮挡（面板排序值均 >= Hud），不再置顶。
        mCanvas.sortingOrder = UILevel.Hud; // -50

        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>();
        mCanvasRoot = go.transform;
    }

    private static Font GetBuiltinFont()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    /// <summary>单条飘字：根节点（负责缩放/旋转/位移）+ 数字 + 弱点/暴击/元素 三个小标签</summary>
    private sealed class DamageTextItem
    {
        public RectTransform Root;
        public Text Num;
        public Outline NumOutline;
        public Text WeakTag;   // 弱点（金）
        public Text CritTag;   // 暴击（橙红）
        public Text TypeTag;   // 元素（青蓝）

        // 各 Text 的基色（淡出动画按基色改 alpha，避免二次 Lerp 丢色相）
        public Color NumBaseColor;
        public Color WeakBaseColor;
        public Color CritBaseColor;
        public Color TypeBaseColor;
    }
}
