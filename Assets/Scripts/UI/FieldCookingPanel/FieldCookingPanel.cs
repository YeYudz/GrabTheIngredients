using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class FieldCookingPanelData : IUIData
	{
	}

	/// <summary>
	/// 画中画炒菜面板：右上角小窗（菜名 / 颠勺提示 / combo / 倒计时）与中心结算横幅的展示。
	/// 纯显示层——只订阅引擎事件驱动 UI，不持有玩法状态；世界不暂停，玩家边打边炒。
	/// 反馈演出全部用代码动画（缩放 / 摇晃 / 闪光）生成，避免为一次性动效引入美术资源依赖。
	/// </summary>
	public partial class FieldCookingPanel : UIPanel
	{
		private Coroutine mBannerCoroutine;
		private Coroutine mHideWindowCoroutine;
		private Coroutine mShakeCoroutine;
		private Coroutine mBoardFlashCoroutine;
		private Coroutine mHintPopCoroutine;
		private Coroutine mComboPopCoroutine;
		private Coroutine mStepFxCoroutine;     // 步骤演出协程（FlyIn/Pour/Drop/Toss/Sprinkle）
		private Coroutine mWindowPunchCoroutine; // 小窗 punch 放大（颠勺提示出现信号）
		private readonly Dictionary<string, Sprite> mStepSpriteCache = new(); // 按路径缓存，避免重复 Resources 加载
		private float mHintWindowSeconds = 0.8f; // 当前提示判定窗口时长（OnHintShown 时记录，驱动收缩动画）
		private Color mStatusBaseColor = Color.white; // 状态文字基色（提示期高亮后恢复）
		private int mStatusBaseFontSize = 20;
		private bool mSubscribed;
		private bool mArtApplied;                    // 美术资源覆盖是否已执行（提示图/背板切片）
		private Sprite mCurrentFxSprite;             // 当前步骤的特效图（fxIcon，null=纯色/主图兜底）
		private Color mWindowBoardBaseColor = new Color(0.08f, 0.06f, 0.04f, 0.72f); // 背板基色（动画后恢复）

		// ===== 结算菜品卡（菜品图标 + 名称，替代整段纯文字） =====
		private const string kBagItemTemplatePath = "Prefabs/UI/StatusBagItem";
		private const int BannerFontReady = 42;   // 「灶台就绪…」提示（文字最长，字号压小避免糊满小窗）
		private const int BannerFontJudge = 48;   // 颠勺判定（Hit/Miss，短句可放大）
		private const int BannerFontFail = 38;    // 炒糊 / 不炒
		private RectTransform mResultRoot;
		private GameObject mResultBagItem;
		private Text mResultQualityText;
		private Text mResultHintText;
		private Coroutine mResultCoroutine;   // 结果卡自动收起
		private Coroutine mResultPopCoroutine; // 结果卡弹入
		private int mBannerBaseFontSize = 40;

		private static readonly Color HitColor = new Color(1f, 0.84f, 0.1f);    // 颠勺成功（金）
		private static readonly Color MissColor = new Color(1f, 0.3f, 0.3f);    // 漏点（红）
		private static readonly Color ReadyColor = new Color(0.5f, 1f, 0.6f);   // 灶台就绪（绿）
		private static readonly Color FailColor = new Color(1f, 0.45f, 0.2f);   // 炒糊/不炒（橙红）
		private static readonly Color HintFlashColor = new Color(1f, 0.9f, 0.2f); // 提示闪烁（亮黄）
		private static readonly Color BoardFlashColor = new Color(1f, 0.95f, 0.75f); // 颠勺成功背板闪白金
		private static readonly Color BoardMissColor = new Color(0.75f, 0.12f, 0.1f); // 漏点背板闪红

		protected override void OnInit(IUIData data = null)
		{
			mData = data as FieldCookingPanelData ?? new FieldCookingPanelData();

			// 根节点 Image 只作背景，启用会整屏遮挡
			var rootImage = GetComponent<Image>();
			if (rootImage != null) rootImage.enabled = false;

			ValidateBindings(); // 缺失字段仅告警，不阻断打开
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as FieldCookingPanelData ?? mData;
		}

		protected override void OnShow()
		{
			Subscribe();
			ApplyArtOverrides(); // 美术覆盖：配置就绪后由 mArtApplied 保证只执行一次

			// 默认隐藏：小窗由炒菜触发事件唤起
			SetWindowVisible(false);
			if (mBannerText != null) mBannerText.text = "";
			HideResultCard();
		}

		protected override void OnHide()
		{
			Unsubscribe();
		}

		protected override void OnClose()
		{
			Unsubscribe();
		}

		/// <summary>每帧刷新：倒计时条 + 提示闪烁摆动（仅小窗可见时）</summary>
		private void Update()
		{
			if (mWindowRoot == null || !mWindowRoot.activeSelf) return;

			var engine = FieldCookingEngine.HasInstance ? FieldCookingEngine.Instance : null;
			if (engine == null) return;

			if (mTimerBar != null) mTimerBar.fillAmount = engine.RunRemainRatio;

			// 闪烁与摆动：窗口期的高频呼吸感
			if (mHintImage != null)
			{
				if (engine.HintActive)
				{
					// 越接近窗口结束图形越小，强化紧迫感
					float pulse = 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 14f);
					mHintImage.color = new Color(HintFlashColor.r, HintFlashColor.g, HintFlashColor.b, pulse);

					float remain = Mathf.Clamp01((engine.HintEndTime - Time.time) / Mathf.Max(0.05f, mHintWindowSeconds));
					float scale = Mathf.Lerp(0.8f, 1.25f, remain);
					mHintImage.rectTransform.localScale = Vector3.one * scale;
					mHintImage.rectTransform.localRotation =
						Quaternion.Euler(0f, 0f, Mathf.Sin(Time.unscaledTime * 9f) * 18f);
				}
				else
				{
					mHintImage.rectTransform.localScale = Vector3.one;
					mHintImage.rectTransform.localRotation = Quaternion.identity;
				}
			}
		}

		// 订阅随面板显隐，避免隐藏期间空转

		private void Subscribe()
		{
			if (mSubscribed) return;
			mSubscribed = true;

			EventKit.Type.Register<GameEvents.FieldCookingTriggered>(OnTriggered)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.FieldCookingHintShown>(OnHintShown)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.FieldCookingHintJudged>(OnHintJudged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.FieldCookingStepChanged>(OnStepChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.FieldCookingFinished>(OnFinished)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
		}

		private void Unsubscribe()
		{
			if (!mSubscribed) return;
			mSubscribed = false;

			EventKit.Type.UnRegister<GameEvents.FieldCookingTriggered>(OnTriggered);
			EventKit.Type.UnRegister<GameEvents.FieldCookingHintShown>(OnHintShown);
			EventKit.Type.UnRegister<GameEvents.FieldCookingHintJudged>(OnHintJudged);
			EventKit.Type.UnRegister<GameEvents.FieldCookingStepChanged>(OnStepChanged);
			EventKit.Type.UnRegister<GameEvents.FieldCookingFinished>(OnFinished);
		}

		private void OnTriggered(GameEvents.FieldCookingTriggered evt)
		{
			if (mHideWindowCoroutine != null)
			{
				StopCoroutine(mHideWindowCoroutine);
				mHideWindowCoroutine = null;
			}

			SetWindowVisible(true);
			SetWindowArtVisible(true);        // 恢复上一轮结算时收起的画中画美术（锅/食材/提示/小图）
			ApplyArtOverrides();              // 配置还没就绪时（首次 OnShow 太早）在这里补一次
			HideResultCard();                 // 上一轮的结算菜品卡不要残留
			ApplyDishIcon(evt.RecipeId);      // 菜品图标（CookingRecipeConfig.iconPath）
			if (mTitleText != null) mTitleText.text = evt.RecipeName;
			if (mComboText != null) mComboText.text = "combo 0";
			RestoreStatusText("等待颠勺时机…");
			if (mTimerBar != null) mTimerBar.fillAmount = 1f;
			if (mHintRoot != null) mHintRoot.SetActive(false);
			if (mStepImage != null) mStepImage.gameObject.SetActive(false); // 步骤图由 StepChanged 事件驱动

			// 复位上轮残留：特效层与动画状态
			ClearFxLayer();
			if (mStepFxCoroutine != null) { StopCoroutine(mStepFxCoroutine); mStepFxCoroutine = null; }
			if (mBoardFlashCoroutine != null) { StopCoroutine(mBoardFlashCoroutine); mBoardFlashCoroutine = null; }
			if (mWindowPunchCoroutine != null) { StopCoroutine(mWindowPunchCoroutine); mWindowPunchCoroutine = null; }
			mWindowRoot.transform.localScale = Vector3.one;
			if (mWindowBoard != null) mWindowBoard.color = mWindowBoardBaseColor;

			ShowBanner($"灶台就绪：{evt.RecipeName}！按【空格】或点击小窗颠勺", ReadyColor, BannerFontReady);
		}

		/// <summary>换步骤主图（Resources 加载，缺失纯色兜底）→ 状态文字 → 入场演出</summary>
		private void OnStepChanged(GameEvents.FieldCookingStepChanged evt)
		{
			if (mStepImage != null)
			{
				mStepImage.gameObject.SetActive(true);
				ApplyStepVisual(evt);
				ResetStepTransform(); // 防上一动画残留
			}
			if (mStatusText != null)
			{
				RestoreStatusText();
				mStatusText.text = $"{evt.StepName}（{evt.StepIndex + 1}/{evt.StepCount}）";
			}

			// 入场演出前先停掉上一步特效，避免叠加
			if (mStepFxCoroutine != null) { StopCoroutine(mStepFxCoroutine); mStepFxCoroutine = null; }
			ClearFxLayer();
			mStepFxCoroutine = StartCoroutine(PlayStepAnimCo(evt.Anim));
		}

		private void OnHintShown(GameEvents.FieldCookingHintShown evt)
		{
			mHintWindowSeconds = evt.WindowSeconds > 0f ? evt.WindowSeconds : 0.8f;

			if (mHintRoot != null)
			{
				mHintRoot.SetActive(true);
				PopScale(mHintRoot.transform, ref mHintPopCoroutine);
			}

			// 三路强信号叠加：确保窗口期一眼看到判定时机
			FlashBoard(HintFlashColor);
			PunchWindow();
			if (mStatusText != null)
			{
				mStatusText.text = "▲ 点击颠勺！";
				mStatusText.color = HintFlashColor;
				mStatusText.fontSize = mStatusBaseFontSize + 6;
			}
		}

		private void OnHintJudged(GameEvents.FieldCookingHintJudged evt)
		{
			if (mHintRoot != null) mHintRoot.SetActive(false);
			RestoreStatusText();

			if (evt.Judgment == "Hit")
			{
				ShakeWindow();
				FlashBoard(BoardFlashColor);
				if (mComboText != null) PopScale(mComboText.transform, ref mComboPopCoroutine);
				ShowBanner($"颠勺！combo {evt.Combo}", HitColor, BannerFontJudge);
			}
			else
			{
				FlashBoard(BoardMissColor);
				ShowBanner("漏了！combo 断", MissColor, BannerFontJudge);
			}

			if (mComboText != null) mComboText.text = $"combo {evt.Combo}";
		}

		private void OnFinished(GameEvents.FieldCookingFinished evt)
		{
			// 结算：收起画中画美术（Art/FieldCooking 的锅/食材/提示图/小图标），只留背板 + 菜品卡
			SetWindowArtVisible(false);

			// 成功（S/A/B/C）：用「菜品图标 + 名称」的结果卡替代整段文字；失败仍走文字横幅
			bool cooked = evt.Quality == "S" || evt.Quality == "A"
				|| evt.Quality == "B" || evt.Quality == "C";

			if (cooked)
			{
				ShowDishResultCard(evt);
			}
			else
			{
				HideResultCard();
				ShowBanner(evt.Quality == "不炒" ? "错失灶台…手忙脚乱 debuff" : "炒糊了…手忙脚乱 debuff",
					FailColor, BannerFontFail);
			}

			// 横幅展示后渐隐小窗，下次触发再显示
			if (mHideWindowCoroutine != null) StopCoroutine(mHideWindowCoroutine);
			mHideWindowCoroutine = StartCoroutine(HideWindowAfter(2.2f));
		}

		// ===== 结算菜品卡（背包条目母版：图标 + 菜名 + 星级，右侧品质文字） =====

		/// <summary>
		/// 展示结算菜品卡：图标与菜名取「成品菜谱」（画中画菜 dishRecipeId → RecipeConfig），
		/// 无映射时回退到画中画菜品自身的图标/名称。同时清空文字横幅，避免整屏都是字。
		/// </summary>
		private void ShowDishResultCard(GameEvents.FieldCookingFinished evt)
		{
			EnsureResultCard();
			if (mResultRoot == null) return; // 模板缺失时 EnsureResultCard 已告警

			var cooking = GameDataMgr.Instance?.GetCookingRecipe(evt.RecipeId);
			RecipeConfig dish = cooking != null && !string.IsNullOrEmpty(cooking.dishRecipeId)
				? GameDataMgr.Instance.GetRecipe(cooking.dishRecipeId) : null;

			string dishName = dish != null ? dish.recipeName
				: cooking != null ? cooking.recipeName : "菜品";
			string iconPath = dish != null ? dish.iconPath : cooking?.iconPath;
			if (evt.DishStar > 0) dishName = $"{dishName} ★{evt.DishStar}";

			Color rarity = DishRarity.GetColor(evt.DishStar > 0 ? evt.DishStar : 1);

			if (mResultBagItem != null)
			{
				var nameText = FindDeep(mResultBagItem.transform, "ItemName")?.GetComponent<Text>();
				if (nameText != null)
				{
					nameText.text = dishName;
					nameText.color = rarity; // 与状态栏背包口径一致：成品菜名按稀有度着色
				}

				var countText = FindDeep(mResultBagItem.transform, "ItemCount")?.GetComponent<Text>();
				if (countText != null) countText.text = "×1";

				var iconImage = FindDeep(mResultBagItem.transform, "ItemIcon")?.GetComponent<Image>();
				var sprite = LoadSprite(iconPath);
				if (iconImage != null)
				{
					iconImage.sprite = sprite;
					iconImage.gameObject.SetActive(sprite != null); // 缺图时不留白块
				}
				if (sprite == null) LogKit.Warning($"[FieldCookingPanel] 结算菜品图标缺失：Resources/{iconPath}");
			}

			if (mResultQualityText != null)
			{
				mResultQualityText.text = $"{evt.Quality} 品质";
				mResultQualityText.color = rarity;
			}
			if (mResultHintText != null)
			{
				mResultHintText.text = evt.DishStar > 0
					? $"×{evt.Multiplier:F1} 加成\n已入背包"
					: $"×{evt.Multiplier:F1} 加成";
			}

			// 文字横幅让位给图标卡
			if (mBannerCoroutine != null) { StopCoroutine(mBannerCoroutine); mBannerCoroutine = null; }
			if (mBannerText != null) mBannerText.text = "";

			mResultRoot.gameObject.SetActive(true);
			mResultRoot.SetAsLastSibling(); // 压在小窗之上
			PopScale(mResultRoot, ref mResultPopCoroutine);

			if (mResultCoroutine != null) StopCoroutine(mResultCoroutine);
			mResultCoroutine = StartCoroutine(HideResultCardAfter(2.6f));
		}

		private IEnumerator HideResultCardAfter(float seconds)
		{
			yield return new WaitForSeconds(seconds);
			HideResultCard();
		}

		/// <summary>隐藏结算菜品卡（不销毁，便于下次复用）</summary>
		private void HideResultCard()
		{
			if (mResultCoroutine != null) { StopCoroutine(mResultCoroutine); mResultCoroutine = null; }
			if (mResultPopCoroutine != null) { StopCoroutine(mResultPopCoroutine); mResultPopCoroutine = null; }
			if (mResultRoot == null) return;
			mResultRoot.localScale = Vector3.one;
			mResultRoot.gameObject.SetActive(false);
		}

		/// <summary>
		/// 首次用到时生成结果卡：挂在小窗内并铺满小窗 → 与小窗同步渐隐/缩放，位置天然对齐。
		/// 小窗缺失时退化为复刻横幅的锚点尺寸（仍是同一块区域）。
		/// </summary>
		private void EnsureResultCard()
		{
			if (mResultRoot != null) return;

			var windowRect = mWindowRoot != null ? mWindowRoot.transform as RectTransform : null;
			var bannerRect = mBannerText != null ? mBannerText.rectTransform : null;

			var go = new GameObject("DishResultCard", typeof(RectTransform));
			var rt = (RectTransform)go.transform;
			if (windowRect != null)
			{
				rt.SetParent(windowRect, false);
				rt.anchorMin = Vector2.zero;
				rt.anchorMax = Vector2.one;
				rt.pivot = new Vector2(0.5f, 0.5f);
				rt.offsetMin = Vector2.zero;
				rt.offsetMax = Vector2.zero;
			}
			else
			{
				var parent = bannerRect != null ? bannerRect.parent : transform;
				rt.SetParent(parent, false);
				if (bannerRect != null)
				{
					rt.anchorMin = bannerRect.anchorMin;
					rt.anchorMax = bannerRect.anchorMax;
					rt.pivot = bannerRect.pivot;
					rt.anchoredPosition = bannerRect.anchoredPosition;
					rt.sizeDelta = bannerRect.sizeDelta;
				}
			}
			mResultRoot = rt;

			// 菜品格：复用状态栏背包条目母版（图标 + 菜名 + 星级）
			var template = mBagItemTemplate != null ? mBagItemTemplate : Resources.Load<GameObject>(kBagItemTemplatePath);
			if (template != null)
			{
				mResultBagItem = Instantiate(template, rt, false);
				mResultBagItem.name = "DishItem";
				var itemRt = (RectTransform)mResultBagItem.transform;
				itemRt.anchorMin = itemRt.anchorMax = new Vector2(0.5f, 0.5f);
				itemRt.pivot = new Vector2(0.5f, 0.5f);
				itemRt.anchoredPosition = new Vector2(-130f, 0f);
				mResultBagItem.SetActive(true);

				// 不参与点击：小窗整体是颠勺按钮，卡片不能拦截
				foreach (var graphic in mResultBagItem.GetComponentsInChildren<Graphic>(true))
					graphic.raycastTarget = false;
			}
			else
			{
				LogKit.Warning($"[FieldCookingPanel] 结算菜品卡模板缺失：Resources/{kBagItemTemplatePath}（结算仅显示品质文字）");
			}

			mResultQualityText = CreateResultText(rt, "QualityText", new Vector2(85f, 34f), new Vector2(240f, 52f), 32);
			mResultHintText = CreateResultText(rt, "BagHintText", new Vector2(85f, -28f), new Vector2(240f, 64f), 20);

			go.SetActive(false);
		}

		/// <summary>结果卡内的文字（字体沿用横幅字体，避免额外字体资源依赖）</summary>
		private Text CreateResultText(RectTransform parent, string nodeName, Vector2 pos, Vector2 size, int fontSize)
		{
			var go = new GameObject(nodeName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
			rt.pivot = new Vector2(0.5f, 0.5f);
			rt.anchoredPosition = pos;
			rt.sizeDelta = size;

			var text = go.GetComponent<Text>();
			text.font = mBannerText != null && mBannerText.font != null
				? mBannerText.font
				: Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = fontSize;
			text.alignment = TextAnchor.MiddleCenter;
			text.horizontalOverflow = HorizontalWrapMode.Wrap;
			text.verticalOverflow = VerticalWrapMode.Overflow;
			text.raycastTarget = false;
			return text;
		}

		/// <summary>按名字递归查找子节点（模板结构可能被美术调整，不写死层级）</summary>
		private static Transform FindDeep(Transform root, string nodeName)
		{
			if (root == null) return null;
			if (root.name == nodeName) return root;
			for (int i = 0; i < root.childCount; i++)
			{
				var found = FindDeep(root.GetChild(i), nodeName);
				if (found != null) return found;
			}
			return null;
		}

		private void SetWindowVisible(bool visible)
		{
			if (mWindowRoot != null) mWindowRoot.SetActive(visible);
		}

		/// <summary>
		/// 画中画美术显隐（结算时收起）：步骤舞台（Art/FieldCooking 的锅/食材/油水颗粒特效）、
		/// 颠勺提示图、菜名旁的小菜图，以及结算后不再适用的标题/状态/连击文字一一并隐藏，
		/// 只保留小窗背板与「菜品卡（StatusBagItem + 品质文字）」。下一次触发由 OnTriggered 恢复。
		/// </summary>
		private void SetWindowArtVisible(bool visible)
		{
			if (mWokStage != null) mWokStage.SetActive(visible);
			if (mHintRoot != null && !visible) mHintRoot.SetActive(false); // 提示图只隐不现（窗口期由 HintShown 事件唤起）
			if (mDishIcon != null) mDishIcon.enabled = visible;
			if (mTitleText != null) mTitleText.gameObject.SetActive(visible);
			if (mStatusText != null) mStatusText.gameObject.SetActive(visible);
			if (mComboText != null) mComboText.gameObject.SetActive(visible);
		}

		/// <summary>结算后延迟渐隐关闭小窗（CanvasGroup 透明度 1→0 后隐藏并复位）</summary>
		private IEnumerator HideWindowAfter(float seconds)
		{
			yield return new WaitForSeconds(seconds);

			var group = mWindowRoot != null ? mWindowRoot.GetComponent<CanvasGroup>() : null;
			if (group != null)
			{
				float t = 0f;
				while (t < 0.35f)
				{
					t += Time.deltaTime;
					group.alpha = 1f - Mathf.Clamp01(t / 0.35f);
					yield return null;
				}
				group.alpha = 0f;
			}

			SetWindowVisible(false);
			if (group != null) group.alpha = 1f; // 复位，下次弹出正常显示
			mHideWindowCoroutine = null;
		}

		// 动画均由代码生成，不依赖美术素材

		/// <summary>Pop 弹跳缩放（0.3→1.18→1.0），用于提示出现与 combo 数字</summary>
		private void PopScale(Transform target, ref Coroutine running)
		{
			if (target == null) return;
			if (running != null) StopCoroutine(running);
			running = StartCoroutine(PopScaleCo(target));
		}

		private IEnumerator PopScaleCo(Transform target, float duration = 0.25f)
		{
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				float s = p < 0.3f
					? Mathf.Lerp(0.3f, 1.18f, p / 0.3f)
					: Mathf.Lerp(1.18f, 1f, (p - 0.3f) / 0.7f);
				target.localScale = Vector3.one * s;
				yield return null;
			}
			target.localScale = Vector3.one;
		}

		/// <summary>小窗水平摇晃（颠勺成功的手感反馈）</summary>
		private void ShakeWindow()
		{
			if (mWindowRoot == null) return;
			var rt = mWindowRoot.transform as RectTransform;
			if (rt == null) return;
			if (mShakeCoroutine != null) StopCoroutine(mShakeCoroutine);
			mShakeCoroutine = StartCoroutine(ShakeCo(rt));
		}

		private IEnumerator ShakeCo(RectTransform rt, float strength = 12f, float duration = 0.3f)
		{
			Vector2 origin = rt.anchoredPosition;
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float decay = 1f - Mathf.Clamp01(t / duration);
				rt.anchoredPosition = origin + new Vector2(Mathf.Sin(t * 70f) * strength * decay, 0f);
				yield return null;
			}
			rt.anchoredPosition = origin;
		}

		/// <summary>小窗背板闪光（颠勺成功=白金 / 漏点=红），结束后恢复基色</summary>
		private void FlashBoard(Color flashColor)
		{
			if (mWindowBoard == null) return;
			if (mBoardFlashCoroutine != null) StopCoroutine(mBoardFlashCoroutine);
			mBoardFlashCoroutine = StartCoroutine(FlashBoardCo(flashColor));
		}

		private IEnumerator FlashBoardCo(Color flashColor, float duration = 0.3f)
		{
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = 1f - Mathf.Clamp01(t / duration);
				mWindowBoard.color = Color.Lerp(mWindowBoardBaseColor, flashColor, p);
				yield return null;
			}
			mWindowBoard.color = mWindowBoardBaseColor;
		}

		// 步骤演出按配置的 anim 字段分发：FlyIn/Pour/Drop/Toss/Sprinkle

		private Vector2 mStepImageHomePos;      // 步骤主图原位（首次演出时记录）
		private bool mStepImageHomeCached;
		private Color mStepFallbackColor = Color.white; // 当前步骤兜底色（图片缺失时）

		/// <summary>带缓存的 Resources 加载。失败不缓存：补上资源后重开即可生效，不被上次的 null 卡住</summary>
		private Sprite LoadSprite(string path)
		{
			if (string.IsNullOrEmpty(path)) return null;
			if (mStepSpriteCache.TryGetValue(path, out var cached)) return cached;
			var sprite = Resources.Load<Sprite>(path);
			if (sprite != null) mStepSpriteCache[path] = sprite;
			return sprite;
		}

		/// <summary>换装步骤主图；Resources 缺失时纯色兜底并告警</summary>
		private void ApplyStepVisual(GameEvents.FieldCookingStepChanged evt)
		{
			mStepFallbackColor = ColorUtility.TryParseHtmlString(evt.FallbackColor, out var c)
				? c : Color.white;

			// 特效图（fxIcon）：Pour 流柱 / Drop 落料 / Sprinkle 颗粒共用，空=兜底
			mCurrentFxSprite = LoadSprite(evt.FxIcon);

			Sprite sprite = LoadSprite(evt.Icon);

			if (sprite != null)
			{
				mStepImage.sprite = sprite;
				mStepImage.color = Color.white;
			}
			else
			{
				mStepImage.sprite = null;
				mStepImage.color = mStepFallbackColor;
				LogKit.Warning($"[FieldCookingPanel] 步骤图缺失：Resources/{evt.Icon}（已用纯色兜底）");
			}
		}

		/// <summary>复位步骤主图变换，防上一动画残留</summary>
		private void ResetStepTransform()
		{
			var rt = mStepImage.rectTransform;
			rt.localScale = Vector3.one;
			rt.localRotation = Quaternion.identity;
			if (mStepImageHomeCached) rt.anchoredPosition = mStepImageHomePos;
		}

		private void CacheStepHome()
		{
			if (mStepImageHomeCached) return;
			mStepImageHomePos = mStepImage.rectTransform.anchoredPosition;
			mStepImageHomeCached = true;
		}

		/// <summary>步骤入场演出调度（按 anim 类型分发）</summary>
		private IEnumerator PlayStepAnimCo(string anim)
		{
			if (mStepImage == null) yield break;
			CacheStepHome();

			switch (anim)
			{
				case "FlyIn": yield return FlyInCo(); break;
				case "Pour": yield return PourCo(); break;
				case "Drop": yield return DropCo(); break;
				case "Toss": yield return TossCo(); break;
				case "Sprinkle": yield return SprinkleCo(); break;
			}
			mStepFxCoroutine = null;
		}

		/// <summary>起锅：锅从右侧飞入舞台中心</summary>
		private IEnumerator FlyInCo(float duration = 0.45f)
		{
			var rt = mStepImage.rectTransform;
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / duration), 3f); // EaseOutCubic
				rt.anchoredPosition = mStepImageHomePos + new Vector2((1f - p) * 420f, 0f);
				yield return null;
			}
			rt.anchoredPosition = mStepImageHomePos;
		}

		/// <summary>烧油/倒水：流柱从锅上方倾倒而下，播 2 次</summary>
		private IEnumerator PourCo()
		{
			for (int i = 0; i < 2; i++)
			{
				yield return PourOnceCo();
				if (i == 0) yield return new WaitForSeconds(0.35f);
			}
		}

		private IEnumerator PourOnceCo(float duration = 0.7f)
		{
			var stream = CreateFxImage("PourFx", new Vector2(14f, 110f), mStepFallbackColor, mCurrentFxSprite);
			var rt = (RectTransform)stream.transform;
			var img = stream.GetComponent<Image>();
			Vector2 start = mStepImageHomePos + new Vector2(0f, 120f);
			Vector2 end = mStepImageHomePos + new Vector2(0f, 15f);

			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				rt.anchoredPosition = Vector2.Lerp(start, end, p);
				rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(0f, 35f, p)); // 倾倒感
				var c = img.color; // 只动 alpha，保留亮化后的 RGB
				img.color = new Color(c.r, c.g, c.b, 1f - p * 0.6f);
				yield return null;
			}
			Destroy(stream);
		}

		/// <summary>下菜：食材从顶落到锅口上方（略偏上避免融入锅色）+ 落地压扁弹跳</summary>
		private IEnumerator DropCo(float duration = 0.55f)
		{
			// 落料图优先级：fxIcon > 步骤主图 > 纯色兜底
			var food = CreateFxImage("DropFx", new Vector2(70f, 70f), mStepFallbackColor,
				mCurrentFxSprite != null ? mCurrentFxSprite : mStepImage.sprite);
			var rt = (RectTransform)food.transform;
			Vector2 start = mStepImageHomePos + new Vector2(0f, 150f);
			Vector2 end = mStepImageHomePos + new Vector2(0f, 40f); // 落在锅口上方，不沉进锅底

			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				rt.anchoredPosition = Vector2.Lerp(start, end, p * p); // 加速下落
				yield return null;
			}
			rt.anchoredPosition = end;

			float bt = 0f;
			while (bt < 0.25f)
			{
				bt += Time.deltaTime;
				float p = bt / 0.25f;
				float squash = 1f - Mathf.Sin(p * Mathf.PI) * 0.25f;
				rt.localScale = new Vector3(2f - squash, squash, 1f);
				yield return null;
			}
			Destroy(food);
		}

		/// <summary>翻炒：锅快速上抬+旋转，颠两下</summary>
		private IEnumerator TossCo()
		{
			var rt = mStepImage.rectTransform;
			for (int i = 0; i < 2; i++)
			{
				yield return TossOnceCo(rt);
				yield return new WaitForSeconds(0.08f);
			}
			ResetStepTransform();
		}

		private IEnumerator TossOnceCo(RectTransform rt, float duration = 0.28f)
		{
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				float lift = Mathf.Sin(p * Mathf.PI); // 上抬半周期
				rt.anchoredPosition = mStepImageHomePos + new Vector2(0f, lift * 30f);
				rt.localRotation = Quaternion.Euler(0f, 0f, -15f * lift);
				yield return null;
			}
			rt.anchoredPosition = mStepImageHomePos;
			rt.localRotation = Quaternion.identity;
		}

		/// <summary>调味：小颗粒在锅上方抖动撒落，7 颗错开</summary>
		private IEnumerator SprinkleCo()
		{
			for (int i = 0; i < 7; i++)
			{
				StartCoroutine(SprinkleOnceCo(Random.Range(-75f, 75f), Random.Range(0.5f, 0.9f)));
				yield return new WaitForSeconds(0.09f);
			}
			yield return new WaitForSeconds(1f); // 等最后一颗落地
		}

		private IEnumerator SprinkleOnceCo(float x, float duration)
		{
			var grain = CreateFxImage("GrainFx", new Vector2(10f, 10f), mStepFallbackColor, mCurrentFxSprite);
			var rt = (RectTransform)grain.transform;
			var img = grain.GetComponent<Image>();
			Vector2 start = mStepImageHomePos + new Vector2(x, 140f);
			Vector2 end = mStepImageHomePos + new Vector2(x * 0.6f, 10f);

			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				// 抖动下落（横向 sin 摆动随下落衰减）
				rt.anchoredPosition = Vector2.Lerp(start, end, p)
					+ new Vector2(Mathf.Sin(t * 40f) * 6f * (1f - p), 0f);
				var c = img.color; // 只动 alpha，保留亮化后的 RGB
				img.color = new Color(c.r, c.g, c.b, 1f - p * 0.5f);
				yield return null;
			}
			Destroy(grain);
		}

		/// <summary>特效层临时图（动画粒子性质，用完即销毁；非面板结构兜底）</summary>
		private GameObject CreateFxImage(string fxName, Vector2 size, Color color, Sprite sprite = null)
		{
			var go = new GameObject(fxName, typeof(RectTransform), typeof(Image));
			go.transform.SetParent(mFxLayer != null ? mFxLayer : mStepImage.rectTransform.parent, false);
			var rt = (RectTransform)go.transform;
			// 有贴图时按宽高比反推宽度，避免流柱 / 颗粒被拉伸
			if (sprite != null && sprite.rect.height > 0f)
				size = new Vector2(size.y * (sprite.rect.width / sprite.rect.height), size.y);
			rt.sizeDelta = size;
			rt.anchoredPosition = mStepImageHomePos;
			var img = go.GetComponent<Image>();
			img.sprite = sprite;
			// 纯色兜底时亮化并加黑描边，与主图（褐色系）拉开区分
			if (sprite == null)
			{
				img.color = Color.Lerp(color, Color.white, 0.45f);
				var outline = go.AddComponent<Outline>();
				outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
				outline.effectDistance = new Vector2(2f, -2f);
			}
			else
			{
				img.color = Color.white;
			}
			img.raycastTarget = false;
			return go;
		}

		/// <summary>清空特效层临时图</summary>
		private void ClearFxLayer()
		{
			if (mFxLayer == null) return;
			for (int i = mFxLayer.childCount - 1; i >= 0; i--)
				Destroy(mFxLayer.GetChild(i).gameObject);
		}

		/// <summary>小窗 punch 放大（1→1.06→1，颠勺提示出现的抓眼信号）</summary>
		private void PunchWindow()
		{
			if (mWindowRoot == null) return;
			if (mWindowPunchCoroutine != null) StopCoroutine(mWindowPunchCoroutine);
			mWindowPunchCoroutine = StartCoroutine(WindowPunchCo());
		}

		private IEnumerator WindowPunchCo(float duration = 0.25f)
		{
			var rt = mWindowRoot.transform;
			float t = 0f;
			while (t < duration)
			{
				t += Time.deltaTime;
				float p = Mathf.Clamp01(t / duration);
				rt.localScale = Vector3.one * (1f + Mathf.Sin(p * Mathf.PI) * 0.06f);
				yield return null;
			}
			rt.localScale = Vector3.one;
			mWindowPunchCoroutine = null;
		}

		/// <summary>状态文字恢复基色/基础字号（提示期高亮后调用，可顺便改文字）</summary>
		private void RestoreStatusText(string text = null)
		{
			if (mStatusText == null) return;
			if (text != null) mStatusText.text = text;
			mStatusText.color = mStatusBaseColor;
			mStatusText.fontSize = mStatusBaseFontSize;
		}

		/// <summary>横幅显示；fontSize=0 时用预制体基准字号（长句提示请显式传小字号）</summary>
		private void ShowBanner(string text, Color color, int fontSize = 0)
		{
			if (mBannerText == null) return;
			if (mBannerCoroutine != null) StopCoroutine(mBannerCoroutine);

			mBannerText.text = text;
			mBannerText.color = color;
			mBannerText.fontSize = fontSize > 0 ? fontSize : mBannerBaseFontSize;
			mBannerCoroutine = StartCoroutine(HideBannerAfter(1.6f));
		}

		private IEnumerator HideBannerAfter(float seconds)
		{
			yield return new WaitForSeconds(seconds);
			if (mBannerText != null) mBannerText.text = "";
			mBannerCoroutine = null;
		}

		// 绑定校验：字段缺失仅告警定位，不阻断打开

		/// <summary>校验预制体绑定并注册小窗点击；缺失字段仅告警，靠防御性判空保证面板仍可打开</summary>
		private void ValidateBindings()
		{
			if (mWindowRoot == null) LogKit.Warning("[FieldCookingPanel] mWindowRoot 未绑定：预制体缺少右上角画中画小窗容器");
			if (mTitleText == null) LogKit.Warning("[FieldCookingPanel] mTitleText 未绑定：小窗菜名标题");
			if (mHintRoot == null) LogKit.Warning("[FieldCookingPanel] mHintRoot 未绑定：颠勺提示区域");
			if (mHintImage == null) LogKit.Warning("[FieldCookingPanel] mHintImage 未绑定：颠勺提示闪烁图形");
			if (mStatusText == null) LogKit.Warning("[FieldCookingPanel] mStatusText 未绑定：状态提示文字");
			if (mComboText == null) LogKit.Warning("[FieldCookingPanel] mComboText 未绑定：combo 计数");
			if (mTimerBar == null) LogKit.Warning("[FieldCookingPanel] mTimerBar 未绑定：总时长倒计时条");
			if (mBannerText == null) LogKit.Warning("[FieldCookingPanel] mBannerText 未绑定：中心横幅");
			if (mWokStage == null) LogKit.Warning("[FieldCookingPanel] mWokStage 未绑定：炒菜步骤舞台（步骤演出不可用）");
			if (mStepImage == null) LogKit.Warning("[FieldCookingPanel] mStepImage 未绑定：步骤主图（锅/食材，步骤演出不可用）");
			if (mFxLayer == null) LogKit.Warning("[FieldCookingPanel] mFxLayer 未绑定：特效层（油/水/颗粒特效不可用）");

			// 缓存基色 / 字号：提示期高亮后按此恢复
			if (mStatusText != null)
			{
				mStatusBaseColor = mStatusText.color;
				mStatusBaseFontSize = mStatusText.fontSize;
			}
			if (mBannerText != null) mBannerBaseFontSize = mBannerText.fontSize;

			// HintImage 描边：纯色兜底/贴图模式下都在锅图上保持可见
			if (mHintImage != null && mHintImage.GetComponent<Outline>() == null)
			{
				var outline = mHintImage.gameObject.AddComponent<Outline>();
				outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
				outline.effectDistance = new Vector2(2.5f, -2.5f);
			}

			// 兄弟顺序兜底：HintRoot 须排在 WokStage 之后，否则提示图被锅遮挡
			if (mHintRoot != null && mWokStage != null
				&& mHintRoot.transform.GetSiblingIndex() < mWokStage.transform.GetSiblingIndex())
			{
				mHintRoot.transform.SetSiblingIndex(mWokStage.transform.GetSiblingIndex());
				LogKit.Warning("[FieldCookingPanel] HintRoot 排在 WokStage 之前（提示图会被锅遮挡），已自动调整渲染顺序；建议在预制体中把 WokStage 拖到 HintRoot 上面");
			}

			if (mWindowRoot != null)
			{
				// 背板是闪光动画载体：未拖绑时回退到小窗自身 Image
				if (mWindowBoard == null) mWindowBoard = mWindowRoot.GetComponent<Image>();
				if (mWindowBoard != null) mWindowBoardBaseColor = mWindowBoard.color;

				// 结算渐隐依赖 CanvasGroup，缺失则补挂
				if (mWindowRoot.GetComponent<CanvasGroup>() == null)
					mWindowRoot.AddComponent<CanvasGroup>();

				// 整个小窗即点击区：窗口内点击 = 颠勺判定
				var flipButton = mWindowRoot.GetComponent<Button>();
				if (flipButton != null)
				{
					flipButton.onClick.AddListener(OnWindowClicked);
				}
				else
				{
					LogKit.Warning("[FieldCookingPanel] mWindowRoot 缺少 Button 组件：点击颠勺不可用（空格键仍有效）");
				}
			}
		}

		// ===== 美术资源覆盖（把「代码生成的纯图形」换成导入的艺术资源） =====

		/// <summary>菜品图标：按 CookingRecipeConfig.iconPath 加载（mDishIcon 未绑定时整段跳过）</summary>
		private void ApplyDishIcon(string recipeId)
		{
			if (mDishIcon == null) return;

			var recipe = string.IsNullOrEmpty(recipeId)
				? null : GameDataMgr.Instance?.CookingRecipes?.GetById(recipeId);
			var sprite = LoadSprite(recipe?.iconPath);

			mDishIcon.sprite = sprite;
			mDishIcon.enabled = true; // 结算时曾被收起，触发时恢复
			// 无图时置全透明，避免渲染出白色方块（与 HudPanel 空槽同口径）
			mDishIcon.color = sprite != null ? Color.white : new Color(1f, 1f, 1f, 0f);
		}

		/// <summary>
		/// 美术资源覆盖：pipHintIcon（颠勺提示图形）+ pipBoardSprite（小窗背板切片）。
		/// 配置为空时什么都不做，保持预制体原有的纯图形表现；资源缺失时告警并继续用纯色。
		/// 配置未加载完时返回 false，下次 OnShow / 炒菜触发时重试。
		/// </summary>
		private void ApplyArtOverrides()
		{
			if (mArtApplied) return;

			var cfg = GameDataMgr.Instance?.CookingRecipes;
			if (cfg == null) return; // 配置还没就绪，下一帧/下次触发再试
			mArtApplied = true;

			if (mHintImage != null && !string.IsNullOrEmpty(cfg.pipHintIcon))
			{
				var hint = Resources.Load<Sprite>(cfg.pipHintIcon);
				if (hint != null)
				{
					mHintImage.sprite = hint;
					mHintImage.color = Color.white; // 换图后不再依赖纯色闪烁（闪烁改走 alpha）
				}
				else
				{
					LogKit.Warning($"[FieldCookingPanel] 颠勺提示图缺失：Resources/{cfg.pipHintIcon}（已用预制体纯色兜底）");
				}
			}

			if (mWindowBoard != null && !string.IsNullOrEmpty(cfg.pipBoardSprite))
			{
				var board = Resources.Load<Sprite>(cfg.pipBoardSprite);
				if (board != null)
				{
					mWindowBoard.sprite = board;
					// 基色以预制体当前色为准：换切片后请把 PipWindow 的 Image.color 设为纯白
					mWindowBoardBaseColor = mWindowBoard.color;
				}
				else
				{
					LogKit.Warning($"[FieldCookingPanel] 小窗背板图缺失：Resources/{cfg.pipBoardSprite}（已用预制体纯色兜底）");
				}
			}
		}

		/// <summary>小窗点击 → 引擎颠勺判定（窗口期内才生效）</summary>
		private void OnWindowClicked()
		{
			if (FieldCookingEngine.HasInstance) FieldCookingEngine.Instance.OnFlipInput();
		}
	}
}
