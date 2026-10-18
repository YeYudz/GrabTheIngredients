using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GameUI
{
	/// <summary>
	/// 增益卡「鎏金」选中特效：由 LevelUpPanel 在运行时 <see cref="Ensure"/> 到 SkillCard_x 根节点上。
	/// 组成（全部 raycastTarget=false，不拦点击/长按）：
	/// - 卡面金化：把卡底 Image 材质换成 <c>UI/GildedGold</c>（竖向金渐变 + 斜向流光扫过 + 轮廓内侧流金呼吸），
	///   不选中时淡出参数并还原默认材质，输出与原图完全一致；
	/// - 背光光晕：卡牌后方的径向金晕（程序生成贴图，全局缓存），透明度与缩放呼吸；
	/// - 金屑上浮：若干小光点在卡面范围内循环上浮闪烁；
	/// - 选中弹跳：卡牌轻微放大强调（复用 UiTween.Pulse，非缩放时间）。
	/// 所有动画一律用 unscaled 时间——升级选卡时 Time.timeScale = 0，动画不能停。
	/// </summary>
	public sealed class BuffCardGildEffect : MonoBehaviour
	{
		private const string ShaderName = "UI/GildedGold";

		/// <summary>光晕相对卡面的放大倍数</summary>
		private const float AuraScale = 1.45f;
		/// <summary>金屑数量</summary>
		private const int SparkCount = 7;
		/// <summary>金化淡入时长（秒）</summary>
		private const float FadeIn = 0.22f;
		/// <summary>金化淡出时长（秒）</summary>
		private const float FadeOut = 0.18f;
		/// <summary>流光扫过一整趟的周期（秒）</summary>
		private const float ShineCycle = 1.6f;

		// 程序化贴图跨实例共享（对齐 SettingPanel.RowBoxSprite 的先例：不用内置 UI 皮肤资源）
		private static Shader s_shader;
		private static Sprite s_ringSprite;
		private static Sprite s_sparkSprite;

		private Image mCardImage;
		private Material mDefaultMaterial;
		private Material mGildMaterial;
		private Image mAura;
		private readonly Image[] mSparks = new Image[SparkCount];
		/// <summary>金屑各自的随机参数（x 位置 0~1 / 周期偏移 0~1 / 上浮时长 / 大小）</summary>
		private readonly Vector4[] mSparkParams = new Vector4[SparkCount];

		private Coroutine mAnimSlot;
		private Coroutine mPulseSlot;
		private bool mGilded;
		private float mGoldAmount;

		/// <summary>当前是否处于鎏金状态</summary>
		public bool Gilded => mGilded;

		// ===== 生命周期 =====

		/// <summary>确保卡牌根节点挂有本组件并完成一次性搭建（幂等）</summary>
		public static BuffCardGildEffect Ensure(GameObject cardRoot)
		{
			if (cardRoot == null) return null;
			var fx = cardRoot.GetComponent<BuffCardGildEffect>();
			if (fx == null) fx = cardRoot.AddComponent<BuffCardGildEffect>();
			fx.Init();
			return fx;
		}

		private void Init()
		{
			if (mCardImage != null) return; // 已初始化

			mCardImage = GetComponent<Image>();
			if (mCardImage != null) mDefaultMaterial = mCardImage.material;

			BuildAura();
			BuildSparks();
			SetExtrasVisible(false);
		}

		private void OnDestroy()
		{
			RestoreCardMaterial();
			if (mGildMaterial != null) Destroy(mGildMaterial);
			mGildMaterial = null;
		}

		private void OnDisable()
		{
			// 面板关闭/卡牌隐藏：立刻还原视觉，避免下次打开残留金化
			mGilded = false;
			StopSlots();
			mGoldAmount = 0f;
			RestoreCardMaterial();
			SetExtrasVisible(false);
			var rt = transform as RectTransform;
			if (rt != null) rt.localScale = Vector3.one;
		}

		private void OnEnable()
		{
			// ⚠ 关键时序：UIKit 把面板预制体挂在 UIRoot 的存储节点下（inactive），
			// 直到 CommitShow 才 SetActive(true)，而 OnOpen（ShowBuffs → 置默认中间卡鎏金）
			// 发生在激活之前 —— 那时 StartCoroutine 会被 isActiveAndEnabled 挡掉，
			// 且 mGilded 已经为 true → 后续 SetGilded(true) 会被重复保护直接 return，
			// 表现为"刚打开面板没有鎏金，按一下 A/D 才出现"。
			// 故激活时必须补一次启动。
			if (!mGilded || mAnimSlot != null || !isActiveAndEnabled) return;

			Init();
			mAnimSlot = StartCoroutine(GildAnim(true));
			UiTween.Play(this, ref mPulseSlot, UiTween.Pulse(transform as RectTransform, 1.05f, 0.3f));
		}

		// ===== 对外接口 =====

		/// <summary>切换鎏金状态（重复调用同值无副作用）</summary>
		public void SetGilded(bool on)
		{
			if (mGilded == on) return;
			mGilded = on;

			StopSlots();
			if (isActiveAndEnabled) mAnimSlot = StartCoroutine(GildAnim(on));

			if (on)
			{
				// 选中弹跳：放大一点再回弹，强调"待选择"
				UiTween.Play(this, ref mPulseSlot, UiTween.Pulse(transform as RectTransform, 1.05f, 0.3f));
			}
		}

		private void StopSlots()
		{
			UiTween.Stop(this, ref mAnimSlot);
			UiTween.Stop(this, ref mPulseSlot);
		}

		// ===== 动画主循环 =====

		private IEnumerator GildAnim(bool on)
		{
			if (on)
			{
				EnsureMaterial();
				SetExtrasVisible(true);

				// 金化淡入
				float t = 0f;
				while (t < FadeIn && mGilded)
				{
					t += Time.unscaledDeltaTime;
					mGoldAmount = Mathf.Clamp01(t / FadeIn);
					ApplyGoldAmount();
					yield return null;
				}
				mGoldAmount = mGilded ? 1f : mGoldAmount;
				ApplyGoldAmount();

				// 常驻循环：流光 + 边缘呼吸 + 光晕呼吸 + 金屑上浮（直到取消选中）
				float time = Random.Range(0f, ShineCycle);
				while (mGilded)
				{
					time += Time.unscaledDeltaTime;

					// 斜向流光：-0.35 → 1.35 一趟一趟扫
					if (mGildMaterial != null)
					{
						float shinePos = Mathf.Lerp(-0.35f, 1.35f, Mathf.Repeat(time, ShineCycle) / ShineCycle);
						mGildMaterial.SetFloat(PropShinePos, shinePos);

						// 轮廓流金呼吸
						float edge = 0.55f + 0.30f * Mathf.Sin(time * 3.1f);
						mGildMaterial.SetFloat(PropEdgeGlow, edge);
					}

					// 背光光晕呼吸
					if (mAura != null)
					{
						float pulse = 0.5f + 0.5f * Mathf.Sin(time * 2.4f);
						var c = mAura.color;
						c.a = Mathf.Lerp(0.45f, 0.78f, pulse);
						mAura.color = c;
						mAura.transform.localScale = Vector3.one * Mathf.Lerp(0.97f, 1.04f, pulse);
					}

					AnimateSparks(time);
					yield return null;
				}
			}

			// 淡出（无论从哪条路径进来都收尾干净）
			float from = mGoldAmount;
			float u = 0f;
			while (u < FadeOut)
			{
				u += Time.unscaledDeltaTime;
				mGoldAmount = Mathf.Lerp(from, 0f, Mathf.Clamp01(u / FadeOut));
				ApplyGoldAmount();
				yield return null;
			}

			mGoldAmount = 0f;
			ApplyGoldAmount();
			RestoreCardMaterial();
			SetExtrasVisible(false);
			mAnimSlot = null;
		}

		/// <summary>金屑：按各自参数循环上浮 + 闪烁（参数 time 为全局动画时钟）</summary>
		private void AnimateSparks(float time)
		{
			var rt = transform as RectTransform;
			if (rt == null) return;
			float w = rt.rect.width;
			float h = rt.rect.height;

			for (int i = 0; i < mSparks.Length; i++)
			{
				var spark = mSparks[i];
				if (spark == null) continue;
				var p = mSparkParams[i];

				// 单程上浮：phase 决定起点，riseT 归一化 0→1
				float local = Mathf.Repeat(time / p.z + p.y, 1f);
				float x = (p.x - 0.5f) * w + Mathf.Sin(local * 6.28f + p.x * 9f) * w * 0.03f;
				float y = Mathf.Lerp(-h * 0.48f, h * 0.48f, local);

				var tr = spark.transform as RectTransform;
				if (tr != null) tr.anchoredPosition = new Vector2(x, y);

				// 透明度：起止渐隐 + 中途闪烁
				float fade = Mathf.Sin(local * Mathf.PI);
				float twinkle = 0.6f + 0.4f * Mathf.Sin(time * 7f + p.x * 31f);
				var c = spark.color;
				c.a = fade * twinkle;
				spark.color = c;
			}
		}

		// ===== 材质 =====

		private static readonly int PropGoldAmount = Shader.PropertyToID("_GoldAmount");
		private static readonly int PropShinePos = Shader.PropertyToID("_ShinePos");
		private static readonly int PropEdgeGlow = Shader.PropertyToID("_EdgeGlow");

		private void EnsureMaterial()
		{
			if (mGildMaterial != null) return;
			if (s_shader == null) s_shader = Shader.Find(ShaderName);
			if (s_shader == null)
			{
				// 找不到 shader（理论上不会：在 Resources 下）→ 静默退化为无特效
				Debug.LogWarning($"[BuffCardGildEffect] 找不到 {ShaderName}，鎏金特效不可用");
				return;
			}

			mGildMaterial = new Material(s_shader) { name = "GildedGold_Inst" };
			mGildMaterial.SetColor(PropGoldLight, new Color(0.98f, 0.84f, 0.45f)); // 项目金 #F3C862 提亮
			mGildMaterial.SetColor(PropGoldDark, new Color(0.82f, 0.60f, 0.20f));  // 项目金 #DEA32A
			mGildMaterial.SetColor(PropEdgeColor, new Color(1f, 0.88f, 0.50f));
		}

		private void ApplyGoldAmount()
		{
			if (mGildMaterial != null) mGildMaterial.SetFloat(PropGoldAmount, mGoldAmount);
			if (mCardImage != null && mGoldAmount > 0.001f && mGildMaterial != null)
				mCardImage.material = mGildMaterial;
		}

		private void RestoreCardMaterial()
		{
			if (mCardImage != null) mCardImage.material = mDefaultMaterial;
		}

		private static readonly int PropGoldLight = Shader.PropertyToID("_GoldLight");
		private static readonly int PropGoldDark = Shader.PropertyToID("_GoldDark");
		private static readonly int PropEdgeColor = Shader.PropertyToID("_EdgeColor");

		// ===== 程序化节点 =====

		private void BuildAura()
		{
			var go = new GameObject("GildAura", typeof(RectTransform), typeof(Image));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(transform, false);
			// ⚠ uGUI 里父节点自身的 Image 永远先绘制、子节点一律盖在上面 —— 光晕不可能"压在卡底图后面"。
			// 因此贴图做成「中空光环」：卡面覆盖区域内 alpha 恒为 0，只在轮廓外侧一圈发光，
			// 叠在卡上也只会表现为从卡后透出的金色光晕。
			rt.SetAsFirstSibling(); // 仍放最前，排在卡内文字/按钮之下

			rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
			rt.anchoredPosition = Vector2.zero;
			var self = transform as RectTransform;
			float cw = self != null ? self.rect.width : 350f;
			float ch = self != null ? self.rect.height : 600f;
			rt.sizeDelta = new Vector2(cw * AuraScale, ch * AuraScale);

			mAura = go.GetComponent<Image>();
			mAura.sprite = RingSprite();
			mAura.color = new Color(1f, 0.82f, 0.45f, 0.6f);
			mAura.raycastTarget = false;
		}

		private void BuildSparks()
		{
			for (int i = 0; i < SparkCount; i++)
			{
				var go = new GameObject($"GildSpark_{i}", typeof(RectTransform), typeof(Image));
				var rt = go.GetComponent<RectTransform>();
				rt.SetParent(transform, false);
				rt.SetAsLastSibling(); // 盖在卡面内容之上
				rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
				rt.sizeDelta = Vector2.one * 22f;

				var img = go.GetComponent<Image>();
				img.sprite = SparkSprite();
				img.color = new Color(1f, 0.95f, 0.75f, 0f);
				img.raycastTarget = false;
				mSparks[i] = img;

				// 随机参数：x 位置 / 周期偏移 / 上浮时长(1.8~3.2s) / 保留位
				mSparkParams[i] = new Vector4(Random.value, Random.value, Random.Range(1.8f, 3.2f), 0f);
			}
		}

		private void SetExtrasVisible(bool on)
		{
			if (mAura != null) mAura.gameObject.SetActive(on);
			for (int i = 0; i < mSparks.Length; i++)
				if (mSparks[i] != null) mSparks[i].gameObject.SetActive(on);
		}

		// ===== 程序化贴图（跨实例共享）=====

		/// <summary>
		/// 中空光环贴图：半径 0.60 以内全透明（被卡面完全覆盖，绝不能染色），
		/// 峰值在 0.78（刚好落在卡面轮廓外侧 —— 光晕尺寸 1.45× 卡面，卡面半径约 0.69），
		/// 到 1.0 平滑衰减为 0。白色，使用处染色。
		/// </summary>
		private static Sprite RingSprite()
		{
			if (s_ringSprite != null) return s_ringSprite;

			const int size = 256;
			const float inner = 0.60f; // 中心留空半径
			const float peak = 0.78f;  // 最亮半径
			const float outer = 1.00f; // 完全衰减

			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
			{
				name = "GildAuraRing",
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
			};

			var half = size * 0.5f;
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float dx = (x + 0.5f - half) / half;
					float dy = (y + 0.5f - half) / half;
					float d = Mathf.Sqrt(dx * dx + dy * dy);

					float a = 0f;
					if (d > inner && d < outer)
					{
						float up = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(inner, peak, d));
						float down = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(peak, outer, d));
						a = up * down;
					}
					texture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
				}
			}
			texture.Apply();

			s_ringSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
				new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
			s_ringSprite.name = "GildAuraRing";
			return s_ringSprite;
		}

		/// <summary>小光点：热核 + 柔边（白色，使用处染色）</summary>
		private static Sprite SparkSprite()
		{
			if (s_sparkSprite != null) return s_sparkSprite;

			const int size = 64;
			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
			{
				name = "GildSparkDot",
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
			};

			var half = size * 0.5f;
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float dx = (x + 0.5f - half) / half;
					float dy = (y + 0.5f - half) / half;
					float d = Mathf.Sqrt(dx * dx + dy * dy);
					// 中心亮核 + 柔边
					float core = Mathf.Pow(Mathf.Clamp01(1f - d), 1.4f);
					float halo = Mathf.Pow(Mathf.Clamp01(1f - d), 3.5f) * 0.6f;
					texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(core + halo)));
				}
			}
			texture.Apply();

			s_sparkSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
				new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
			s_sparkSprite.name = "GildSparkDot";
			return s_sparkSprite;
		}
	}
}
