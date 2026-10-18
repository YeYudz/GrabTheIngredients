using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class CookingPanelData : IUIData
	{
		public string RecipeId;
	}

	/// <summary>
	/// 烹饪音游面板：轨道、音符与判定反馈的可视化。
	/// 节奏推进与判定全部委托 RhythmEngine，面板不持有音游状态，使表现层与判定层共享同一份数据。
	/// 滑轨按针孔模型做透视：Lane 背板与音符顶边统一向容器中心（消失点）收拢，
	/// 收缩比由 kPerspectiveFov 推导；判定时间轴保持线性，不受透视影响。
	/// </summary>
	public partial class CookingPanel : UIPanel
	{
		private Coroutine mRenderCoroutine;
		private Coroutine mPillBlinkCoroutine;
		private Dictionary<RhythmNote, Image> mNoteVisuals = new();
		private Dictionary<RhythmNote, Color> mNoteBaseColors = new();  // 音符底色（皮肤白染/轨道色），长按提亮用
		private float mTopY;
		private float mBottomY;
		private const int kMaxTracks = 4;        // 最大轨道数（与 D/F/J/K 按键一一对应）
		private int mTrackCount = 4;             // 当前阶段轨道数：Lane 与按键按此均分容器宽度

		// ===== 按键引导（2026-10-17 修"KeyBg 盖住字母"） =====
		// Key_i 的 Text 挂在自身节点上（不是子物体），背板做成它的子节点会被 UI 的子物体后绘规则压到字母上面；
		// 因此背板改为 KeyRow 直接子节点并前置（整排背板先绘制），布局由 RefreshKeys 与字母同步镜像。
		// 节点按名字缓存（不能用子节点下标：背板插入后下标会串号）
		private readonly RectTransform[] mKeyLabels = new RectTransform[kMaxTracks];
		private readonly RectTransform[] mKeyBgs = new RectTransform[kMaxTracks];
		// 滑轨透视视角（度）：针孔模型下顶部收缩比 k=(tanα-tan(α/2))/(tanα+tan(α/2))，α 越小透视越强；Lane 背板与音符共用
		private const float kPerspectiveFov = 60f;

		// 顶部收缩比（静态缓存，避免每帧算三角函数）
		private static readonly float kTopScale = CalcPerspectiveTopScale(kPerspectiveFov);

		// ===== 判定锚点视觉对齐（2026-10-17） =====
		/// <summary>音符视觉高度（判定线处 p=1 的像素高，缩放后为 kNoteHeight × s）</summary>
		private const float kNoteHeight = 50f;

		/// <summary>
		/// 判定锚点在音符视觉高度上的相对位置：0=底边压线（旧行为）、0.5=中心压线（推荐，行业通行）、1=顶边压线。
		/// 旧行为下"音符底边触线"= ArrivalTime，而玩家直觉是"音符压在判定线上"才按，
		/// 实测这一偏差 = 半格音符 ≈ 25px ≈ 31ms（fallDuration 2s / easy 档 40ms），
		/// 表现就是"判定线实际比视觉高半格" → 按视觉操作必然偏晚。
		/// </summary>
		private const float kJudgeAnchorRatio = 0.5f;

		/// <summary>
		/// 判定带调试（默认关闭）：打开后在轨道区画出「Perfect 判定带」与「迟到宽容线」，
		/// 用来现场核对"音符视觉重心压线"与"引擎判定时刻"是否重合。
		/// 调参验收流程：开 = true → 进 BOSS 音游 → 确认音符中心进入青色带中心时按键能拿 Perfect。
		/// </summary>
		private const bool kDebugJudgeBand = false;

		// ===== 战斗反馈特效参数 =====
		private const float kJudgeFlashDuration = 0.25f;  // 判定线命中闪亮时长（秒）
		private const float kMissFlashDuration = 0.25f;   // Miss 全屏红光时长（秒）
		private const float kMissFlashAlpha = 0.35f;      // Miss 红光初始透明度
		private const float kShakeDuration = 0.2f;        // Miss 震动时长（秒）
		private const float kShakeAmplitude = 6f;         // Miss 震动最大幅度（像素）
		private const float kParticleGravity = 600f;      // 命中粒子重力（像素/秒²）

		private RectTransform mRootRect;                  // 面板根节点（Miss 微震动载体）
		private Image mMissOverlay;
		private Image mJudgeLineImg;                      // 判定线 Image（呼吸/闪亮载体）
		private Color mJudgeLineBaseColor = new Color(1f, 200f / 255f, 64f / 255f); // 判定线常色（皮肤/预制体色）
		private Color mStagePillBaseColor = new Color(0f, 0f, 0f, 0.55f); // 阶段胶囊常色（皮肤白染/预制体黑），闪烁回退基准
		private float mJudgeLineFlash;                    // 判定线闪亮强度（1→0 线性衰减）
		private float mMissFlashTime;                     // Miss 红光剩余时长
		private float mShakeTimeLeft;                     // 震动剩余时长

		// 命中粒子：结构体状态 + 对象池复用，更新并入渲染协程（零实例化 GC）
		private struct Particle
		{
			public RectTransform Rt;
			public Image Img;
			public Vector2 Vel;
			public float Life;
			public float MaxLife;
		}
		private readonly List<Particle> mParticles = new();
		private readonly Stack<GameObject> mParticlePool = new();

		// 皮肤 Sprite 缓存（静态跨开局面复用，含 null 缓存避免重复 IO）
		private static readonly Dictionary<string, Sprite> s_SpriteCache = new();
		// 已提示过加载失败的皮肤路径（每个路径只报一次，避免每帧刷日志）
		private static readonly HashSet<string> s_SpriteWarned = new();

		/// <summary>事件订阅随面板显隐成对开关，防重复注册</summary>
		private bool mSubscribed;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as CookingPanelData ?? new CookingPanelData();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as CookingPanelData ?? mData;

			// UIKit 异步物化可能以未激活状态回调 OnOpen，而协程只能在激活对象上启动
			if (!gameObject.activeSelf) gameObject.SetActive(true);

			// 预制体未搭建时的兜底构建
			EnsureUIBuilt();

			// 特效载体与皮肤须先于音符渲染就绪
			mRootRect = GetComponent<RectTransform>();
			EnsureMissOverlay();
			ApplySkin();

			// 容器底锚：JudgeLine.posY 即 mBottomY，顶部 = 底 + 容器高
			if (mJudgeLine != null && mTrackContainer != null)
			{
				mBottomY = mJudgeLine.anchoredPosition.y;
				mTopY = mBottomY + mTrackContainer.rect.height;
			}

			var recipe = GameDataMgr.Instance != null ? GameDataMgr.Instance.GetRecipe(mData?.RecipeId) : null;
			if (mDishNameText != null && recipe != null)
			{
				mDishNameText.text = recipe.recipeName;
			}

			// 与 OnClose 成对反注册
			if (!mSubscribed)
			{
				mSubscribed = true;
				EventKit.Type.Register<GameEvents.CookingStageStarted>(OnStageStarted);
				EventKit.Type.Register<GameEvents.CookingNoteJudged>(OnNoteJudged);
			}

			if (!string.IsNullOrEmpty(mData?.RecipeId))
			{
				RhythmEngine.Instance.StartCooking(mData.RecipeId);

				// 上报越线时长：由轨道几何与透视映射反解，供引擎裁定 Miss 音符的滑出时机
				// 顶边触线时 sCross = H / (H - h·(1-r)·(1-k))，再由 s(p) 反解 pCross
				// h·(1-r) = 判定锚点下移后仍留在判定线上方的那部分音符高度
				float trackH = Mathf.Max(1f, mTopY - mBottomY);
				float crossH = kNoteHeight * (1f - kJudgeAnchorRatio);
				float sCross = trackH / Mathf.Max(1f, trackH - crossH * (1f - kTopScale));
				float pCross = 1f - (1f / sCross - 1f) / (1f / kTopScale - 1f);
				RhythmEngine.Instance.SetNoteFullCrossDuration((pCross - 1f) * RhythmEngine.Instance.FallDuration);

				if (kDebugJudgeBand) BuildDebugJudgeBands();
			}

			// 宿主为常驻的 RhythmEngine：面板自身在物化期间可能处于非激活状态
			StopRenderCoroutine();
			mRenderCoroutine = RhythmEngine.Instance.StartCoroutine(RenderNotes());

			LogKit.Log("[CookingPanel] 烹饪音游面板打开");
		}

		/// <summary>按预制体契约补齐未绑定的 UI 元素；幂等，已存在则保留</summary>
		private void EnsureUIBuilt()
		{
			var rootRt = GetComponent<RectTransform>();
			if (rootRt == null)
			{
				gameObject.AddComponent<RectTransform>();
				rootRt = GetComponent<RectTransform>();
			}
			rootRt.anchorMin = Vector2.zero;
			rootRt.anchorMax = Vector2.one;
			rootRt.offsetMin = Vector2.zero;
			rootRt.offsetMax = Vector2.zero;

			if (GetComponent<Image>() == null)
			{
				var bg = gameObject.AddComponent<Image>();
				bg.color = new Color(0f, 0f, 0f, 0.78f);
			}

			var topBar = EnsureChild("TopBar", transform);
			if (mDishNameText == null)
			{
				topBar.anchorMin = new Vector2(0f, 1f);
				topBar.anchorMax = new Vector2(1f, 1f);
				topBar.pivot = new Vector2(0.5f, 1f);
				topBar.anchoredPosition = new Vector2(0f, -24f);
				topBar.sizeDelta = new Vector2(0f, 72f);

				mDishNameText = CreateText("DishNameText", "", 32, TextAnchor.MiddleLeft, topBar);
				var dishRt = mDishNameText.rectTransform;
				dishRt.anchorMin = new Vector2(0f, 0.5f);
				dishRt.anchorMax = new Vector2(0f, 0.5f);
				dishRt.pivot = new Vector2(0f, 0.5f);
				dishRt.sizeDelta = new Vector2(420f, 40f);
				dishRt.anchoredPosition = new Vector2(40f, 0f);
			}

			if (mStagePillImage == null)
			{
				var pillGo = new GameObject("StagePill", typeof(RectTransform), typeof(Image));
				pillGo.transform.SetParent(topBar, false);
				mStagePillImage = pillGo.GetComponent<Image>();
				mStagePillImage.color = new Color(0f, 0f, 0f, 0.55f);
				var pillRt = mStagePillImage.rectTransform;
				pillRt.anchorMin = new Vector2(0.5f, 0.5f);
				pillRt.anchorMax = new Vector2(0.5f, 0.5f);
				pillRt.sizeDelta = new Vector2(340f, 48f);
				pillRt.anchoredPosition = Vector2.zero;
			}
			if (mStagePillText == null)
			{
				mStagePillText = CreateText("StageText", "", 28, TextAnchor.MiddleCenter, mStagePillImage.transform);
				var stageRt = mStagePillText.rectTransform;
				stageRt.anchorMin = Vector2.zero;
				stageRt.anchorMax = Vector2.one;
				stageRt.offsetMin = new Vector2(12f, 4f);
				stageRt.offsetMax = new Vector2(-12f, -4f);
				mStagePillText.color = new Color(1f, 214f / 255f, 102f / 255f); // 金色
			}

			if (mComboText == null)
			{
				mComboText = CreateText("ComboText", "", 40, TextAnchor.MiddleRight, topBar);
				var comboRt = mComboText.rectTransform;
				comboRt.anchorMin = new Vector2(1f, 0.5f);
				comboRt.anchorMax = new Vector2(1f, 0.5f);
				comboRt.pivot = new Vector2(1f, 0.5f);
				comboRt.sizeDelta = new Vector2(360f, 52f);
				comboRt.anchoredPosition = new Vector2(-40f, 0f);
				mComboText.color = new Color(1f, 214f / 255f, 102f / 255f); // 金色
			}

			if (mTrackContainer == null)
			{
				var go = new GameObject("TrackContainer", typeof(RectTransform));
				go.transform.SetParent(transform, false);
				mTrackContainer = go.GetComponent<RectTransform>();
				var trackRt = mTrackContainer;
				trackRt.anchorMin = new Vector2(0.5f, 0f);
				trackRt.anchorMax = new Vector2(0.5f, 0f);
				trackRt.pivot = new Vector2(0.5f, 0f);
				trackRt.sizeDelta = new Vector2(1440f, 810f);
				trackRt.anchoredPosition = new Vector2(0f, 120f);
			}

			// 先于判定线创建，保证渲染在其下层
			if (mLaneRoot == null)
			{
				var go = new GameObject("LaneRoot", typeof(RectTransform));
				go.transform.SetParent(mTrackContainer, false);
				mLaneRoot = go.GetComponent<RectTransform>();
				Stretch(mLaneRoot);
			}

			// 底边锚点：posY=0 即容器底边
			if (mJudgeLine == null)
			{
				var go = new GameObject("JudgeLine", typeof(RectTransform), typeof(Image));
				go.transform.SetParent(mTrackContainer, false);
				mJudgeLine = go.GetComponent<RectTransform>();
				go.GetComponent<Image>().color = new Color(1f, 200f / 255f, 64f / 255f); // 金色
				var lineRt = mJudgeLine;
				// 水平拉伸锚：容器改尺寸时自动跟随
				lineRt.anchorMin = new Vector2(0f, 0f);
				lineRt.anchorMax = new Vector2(1f, 0f);
				lineRt.pivot = new Vector2(0.5f, 0.5f);
				lineRt.sizeDelta = new Vector2(0f, 6f);
				lineRt.anchoredPosition = Vector2.zero;
			}

			if (mJudgmentText == null)
			{
				mJudgmentText = CreateText("JudgmentText", "", 44, TextAnchor.MiddleCenter, mTrackContainer);
				var judgeRt = mJudgmentText.rectTransform;
				judgeRt.anchorMin = new Vector2(0.5f, 0f);
				judgeRt.anchorMax = new Vector2(0.5f, 0f);
				judgeRt.pivot = new Vector2(0.5f, 0.5f);
				judgeRt.sizeDelta = new Vector2(480f, 80f);
				judgeRt.anchoredPosition = new Vector2(0f, 110f);
				mJudgmentText.gameObject.SetActive(false);
			}

			// Key_0..3 标签由预制体搭建，此处只按阶段刷新布局
			if (mKeyRow == null)
			{
				var go = new GameObject("KeyRow", typeof(RectTransform));
				go.transform.SetParent(mTrackContainer, false);
				mKeyRow = go.GetComponent<RectTransform>();
				var keyRt = mKeyRow;
				keyRt.anchorMin = new Vector2(0f, 0f);
				keyRt.anchorMax = new Vector2(1f, 0f);
				keyRt.pivot = new Vector2(0.5f, 1f);
				keyRt.sizeDelta = new Vector2(0f, 40f);
				keyRt.anchoredPosition = new Vector2(0f, -12f);
			}

			// 节点通常由预制体搭建，此处仅补齐缺失部分
			EnsureLaneChildren();
		}

		/// <summary>按名字取子节点，缺失时创建空 RectTransform</summary>
		private RectTransform EnsureChild(string name, Transform parent)
		{
			var found = parent.Find(name);
			if (found != null)
			{
				var foundRt = found.GetComponent<RectTransform>();
				if (foundRt != null) return foundRt;
			}
			var go = new GameObject(name, typeof(RectTransform));
			go.transform.SetParent(parent, false);
			return go.GetComponent<RectTransform>();
		}

		/// <summary>节点铺满父容器</summary>
		private static void Stretch(RectTransform rt)
		{
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}

		/// <summary>创建一个 Text 组件（默认挂根节点）</summary>
		private Text CreateText(string name, string content, int fontSize, TextAnchor anchor, Transform parent = null)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			go.transform.SetParent(parent != null ? parent : transform, false);
			var txt = go.GetComponent<Text>();
			txt.text = content;
			txt.fontSize = fontSize;
			txt.alignment = anchor;
			txt.color = Color.white;
			txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			return txt;
		}

		/// <summary>手动打开（供 BossBattleManager 在预制体缺失时调用）</summary>
		public void ManualOpen(IUIData data)
		{
			OnInit(data);
			OnOpen(data);
			OnShow();
		}

		/// <summary>手动关闭</summary>
		public void ManualClose()
		{
			OnClose();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		/// <summary>停止渲染协程（协程宿主是 RhythmEngine，必须用它来停）</summary>
		private void StopRenderCoroutine()
		{
			if (mRenderCoroutine == null) return;
			RhythmEngine.Instance.StopCoroutine(mRenderCoroutine);
			mRenderCoroutine = null;
		}

		/// <summary>停止阶段胶囊闪烁协程（协程宿主是 RhythmEngine，必须用它来停）</summary>
		private void StopPillBlinkCoroutine()
		{
			if (mPillBlinkCoroutine == null) return;
			RhythmEngine.Instance.StopCoroutine(mPillBlinkCoroutine);
			mPillBlinkCoroutine = null;
		}

		protected override void OnClose()
		{
			StopRenderCoroutine();
			StopPillBlinkCoroutine();

			// 取消订阅（与 OnOpen 成对）
			if (mSubscribed)
			{
				mSubscribed = false;
				EventKit.Type.UnRegister<GameEvents.CookingStageStarted>(OnStageStarted);
				EventKit.Type.UnRegister<GameEvents.CookingNoteJudged>(OnNoteJudged);
			}

			foreach (var img in mNoteVisuals.Values)
			{
				if (img != null) Destroy(img.gameObject);
			}
			mNoteVisuals.Clear();
			mNoteBaseColors.Clear();

			ResetCombatFx();

			RhythmEngine.Instance?.ForceStop();
		}

		/// <summary>阶段切换：按轨道数刷新轨道背板/按键引导布局 + 更新阶段胶囊</summary>
		private void OnStageStarted(GameEvents.CookingStageStarted evt)
		{
			var engine = RhythmEngine.Instance;
			var stage = engine != null ? engine.CurrentStage : null;
			if (stage == null) return;

			mTrackCount = stage.noteConfig != null && stage.noteConfig.trackCount > 0
				? stage.noteConfig.trackCount : 4;

			RefreshLanes(mTrackCount);
			RefreshKeys(mTrackCount);
			UpdateStagePill(evt, stage, engine);
		}

		/// <summary>
		/// 节点由预制体约定（Lane_i 的索引即轨道号），此处只按轨道数均分宽度并控制显隐，不创建也不销毁
		/// </summary>
		private void RefreshLanes(int trackCount)
		{
			if (mLaneRoot == null || mTrackContainer == null) return;

			var skin = Skin;

			if (mLaneRoot.childCount < trackCount)
			{
				LogKit.Warning($"[CookingPanel] LaneRoot 子节点数({mLaneRoot.childCount}) 少于阶段轨道数 {trackCount}，请在预制体补齐 Lane_i 节点");
			}

			float laneWidth = mTrackContainer.rect.width / trackCount;
			for (int i = 0; i < mLaneRoot.childCount; i++)
			{
				var lane = mLaneRoot.GetChild(i) as RectTransform;
				if (lane == null) continue;
				bool active = i < trackCount;
				lane.gameObject.SetActive(active);
				if (!active) continue;

				lane.anchorMin = new Vector2(0f, 0f);
				lane.anchorMax = new Vector2(0f, 1f);
				lane.pivot = new Vector2(0.5f, 0.5f);
				lane.sizeDelta = new Vector2(laneWidth, 0f);
				lane.anchoredPosition = new Vector2(laneWidth * (i + 0.5f), 0f);

				// 顶边向容器中心（消失点）收拢，各 Lane 顶边延长线交于同一点
				var trap = lane.GetComponent<TrapezoidImage>();
				if (trap == null) trap = lane.gameObject.AddComponent<TrapezoidImage>();
				trap.TopScale = kTopScale;
				// 消失点（容器中心 x=0）在 lane 局部坐标 = 容器半宽 - lane 中心相对左边缘的 x
				trap.TopCenterX = mTrackContainer.rect.width / 2f - lane.anchoredPosition.x;

				// 有皮肤资源时白染，保留美术原始配色
				var laneImg = lane.GetComponent<Image>();
				if (laneImg != null && skin != null)
				{
					var laneSprite = PickSprite(skin.laneSprites, i, skin.laneSprite);
					if (laneSprite != null)
					{
						laneImg.sprite = laneSprite;
						laneImg.color = Color.white;
					}
				}
			}
		}

		/// <summary>
		/// 节点由预制体约定（Key_i 对应 D/F/J/K），此处只按轨道数对齐位置并控制显隐；高度/字体/样式归预制体。
		/// 背板 KeyBg_i 与字母同一布局，随轨道一起显隐。
		/// </summary>
		private void RefreshKeys(int trackCount)
		{
			if (mKeyRow == null || mTrackContainer == null) return;
			EnsureKeyNodes();

			int labelCount = 0;
			for (int i = 0; i < kMaxTracks; i++)
			{
				if (mKeyLabels[i] != null) labelCount++;
			}
			if (labelCount < trackCount)
			{
				LogKit.Warning($"[CookingPanel] KeyRow 按键节点数({labelCount}) 少于阶段轨道数 {trackCount}，请在预制体补齐 Key_i 节点");
			}

			float laneWidth = mTrackContainer.rect.width / trackCount;
			for (int i = 0; i < kMaxTracks; i++)
			{
				var key = mKeyLabels[i];
				if (key == null) continue;
				bool active = i < trackCount;
				key.gameObject.SetActive(active);
				if (mKeyBgs[i] != null) mKeyBgs[i].gameObject.SetActive(active);
				if (!active) continue;

				key.anchorMin = new Vector2(0f, 0.5f);
				key.anchorMax = new Vector2(0f, 0.5f);
				key.pivot = new Vector2(0.5f, 0.5f);
				key.sizeDelta = new Vector2(laneWidth, key.sizeDelta.y);
				key.anchoredPosition = new Vector2(laneWidth * (i + 0.5f), 0f);

				// 背板必须与字母完全同框，否则错位一眼可见
				MirrorRect(key, mKeyBgs[i]);
			}
		}

		/// <summary>
		/// 收集 KeyRow 下的按键标签（Key_i）。按名字取（KeyBg_i 也是 KeyRow 的子节点，按下标会串号）
		/// </summary>
		private void EnsureKeyNodes()
		{
			if (mKeyRow == null) return;
			for (int i = 0; i < kMaxTracks; i++)
			{
				if (mKeyLabels[i] != null) continue;
				mKeyLabels[i] = mKeyRow.Find($"Key_{i}") as RectTransform;
			}
		}

		/// <summary>
		/// 取/建按键背板 KeyBg_i：KeyRow 的直接子节点、置于最前（先于所有字母绘制）。
		/// ⚠️ 不能挂成 Key_i 的子节点——UI 子物体一定后绘，背板会盖住字母（即使半透明也会让字看不清）。
		/// </summary>
		private RectTransform EnsureKeyBg(int index, RectTransform key)
		{
			if (index < 0 || index >= kMaxTracks || key == null || mKeyRow == null) return null;
			if (mKeyBgs[index] != null) return mKeyBgs[index];

			// 清掉旧实现遗留在 Key_i 内部的 KeyBg（"子物体盖住字母"的根源），避免同层双份
			var legacy = key.Find("KeyBg");
			if (legacy != null) Destroy(legacy.gameObject);

			var rt = mKeyRow.Find($"KeyBg_{index}") as RectTransform;
			if (rt == null)
			{
				var go = new GameObject($"KeyBg_{index}", typeof(RectTransform), typeof(Image));
				go.transform.SetParent(mKeyRow, false);
				rt = go.GetComponent<RectTransform>();
				var img = go.GetComponent<Image>();
				img.raycastTarget = false;
			}
			rt.SetAsFirstSibling();
			MirrorRect(key, rt);
			mKeyBgs[index] = rt;
			return rt;
		}

		/// <summary>把 src 的矩形布局全量镜像到 dst（锚点/pivot/尺寸/偏移/缩放）</summary>
		private static void MirrorRect(RectTransform src, RectTransform dst)
		{
			if (src == null || dst == null) return;
			dst.anchorMin = src.anchorMin;
			dst.anchorMax = src.anchorMax;
			dst.pivot = src.pivot;
			dst.sizeDelta = src.sizeDelta;
			dst.anchoredPosition = src.anchoredPosition;
			dst.localScale = Vector3.one;
			dst.localRotation = Quaternion.identity;
		}

		/// <summary>补齐轨道背板/按键标签子节点；节点通常已由预制体提供</summary>
		private void EnsureLaneChildren()
		{
			string[] keys = { "D", "F", "J", "K" };
			if (mLaneRoot != null)
			{
				for (int i = mLaneRoot.childCount; i < kMaxTracks; i++)
				{
					var go = new GameObject($"Lane_{i}", typeof(RectTransform), typeof(Image));
					go.transform.SetParent(mLaneRoot, false);
					var baseColor = GetTrackColor(i);
					go.GetComponent<Image>().color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.12f);
				}
			}
			if (mKeyRow != null)
			{
				EnsureKeyNodes();
				for (int i = 0; i < kMaxTracks; i++)
				{
					if (mKeyLabels[i] != null) continue;
					var txt = CreateText($"Key_{i}", i < keys.Length ? keys[i] : "", 24, TextAnchor.MiddleCenter, mKeyRow);
					txt.color = new Color(1f, 1f, 1f, 0.75f);
					mKeyLabels[i] = txt.rectTransform;
				}
			}
		}

		/// <summary>阶段胶囊文案：阶段名 · N/M · 点击/长按，并高亮闪烁提示</summary>
		private void UpdateStagePill(GameEvents.CookingStageStarted evt, RecipeStage stage, RhythmEngine engine)
		{
			if (mStagePillText == null) return;

			string stageName = evt.Stage switch
			{
				CookingStage.Cut => "切菜",
				CookingStage.Heat => "下锅",
				CookingStage.Stir => "翻炒",
				CookingStage.Seasoning => "调味",
				_ => evt.Stage.ToString()
			};
			bool isHold = stage.noteConfig != null && stage.noteConfig.holdBeats > 0f;
			mStagePillText.text = $"{stageName} · 阶段 {evt.StageIndex + 1}/{engine.TotalStageCount} · {(isHold ? "长按" : "点击")}";

			// 宿主为 RhythmEngine，重启闪烁前先停掉上一个
			StopPillBlinkCoroutine();
			mPillBlinkCoroutine = RhythmEngine.Instance.StartCoroutine(BlinkStagePill());
		}

		/// <summary>阶段胶囊高亮闪烁 3 次后回到常色</summary>
		private IEnumerator BlinkStagePill()
		{
			if (mStagePillImage == null) yield break;
			var baseColor = mStagePillBaseColor;
			var flashColor = new Color(1f, 0.84f, 0.4f, 0.85f);
			for (int i = 0; i < 3; i++)
			{
				if (mStagePillImage == null) yield break;
				mStagePillImage.color = flashColor;
				yield return new WaitForSecondsRealtime(0.18f);
				if (mStagePillImage == null) yield break;
				mStagePillImage.color = baseColor;
				yield return new WaitForSecondsRealtime(0.12f);
			}
		}

		/// <summary>音符判定：显示判定文字 + 更新 Combo + 触发反馈特效</summary>
		private void OnNoteJudged(GameEvents.CookingNoteJudged evt)
		{
			HandleJudgmentFx(evt);

			if (mJudgmentText != null)
			{
				mJudgmentText.text = evt.Judgment;
				mJudgmentText.color = evt.Judgment switch
				{
					"Perfect" => new Color(1f, 0.85f, 0f),  // 金色
					"Good" => new Color(0.3f, 1f, 0.3f),      // 绿色
					"Miss" => new Color(1f, 0.3f, 0.3f),       // 红色
					_ => Color.white
				};
				RhythmEngine.Instance.StartCoroutine(FlashJudgment());
			}

			if (mComboText != null)
			{
				mComboText.text = evt.Combo > 0 ? $"Combo {evt.Combo}" : "";
			}
		}

		/// <summary>判定文字闪烁</summary>
		private IEnumerator FlashJudgment()
		{
			if (mJudgmentText != null) mJudgmentText.gameObject.SetActive(true);
			yield return new WaitForSecondsRealtime(0.3f);
			if (mJudgmentText != null) mJudgmentText.gameObject.SetActive(false);
		}

		/// <summary>渲染协程：每帧更新音符位置 + 战斗反馈特效</summary>
		private IEnumerator RenderNotes()
		{
			while (true)
			{
				yield return null;
				UpdateNoteVisuals();
				// 特效更新独立于音游运行状态（面板开着就刷新，收尾帧也不冻结粒子/红光/震动）
				UpdateCombatEffects();
			}
		}

		/// <summary>
		/// 透视滑轨下更新音符位置：下落时按针孔模型向消失点收拢并缩放，判定时间轴保持线性
		/// </summary>
		private void UpdateNoteVisuals()
		{
			var engine = RhythmEngine.Instance;
			if (engine == null || !engine.IsRunning || mTrackContainer == null) return;

			// 每帧按容器实际 rect 刷新：OnOpen 时布局可能尚未就绪
			mBottomY = mJudgeLine != null ? mJudgeLine.anchoredPosition.y : 0f;
			mTopY = mBottomY + mTrackContainer.rect.height;

			float now = Time.unscaledTime;
			float fallDuration = engine.FallDuration;
			var activeNotes = engine.ActiveNotes;
			float containerWidth = mTrackContainer.rect.width;
			float laneWidth = containerWidth / Mathf.Max(1, mTrackCount);
			float halfW = containerWidth / 2f;

			foreach (var note in activeNotes)
			{
				// Miss 音符继续渲染到滑出时刻，其余已判定音符立即停渲染
				if (note.IsJudged && !(note.Judgment == "Miss" && now < note.DisappearTime)) continue;
				if (!mNoteVisuals.ContainsKey(note))
				{
					mNoteVisuals[note] = CreateNoteVisual(note, laneWidth);
				}

				var visual = mNoteVisuals[note];
				if (visual == null) continue;

				// 判定线处（p=1）轨道中心 x（相对容器中心）
				float laneXBottom = -halfW + laneWidth * (note.TrackIndex + 0.5f);
				var rt = visual.rectTransform;

				if (note.IsHold)
				{
					// 长按音符：纵向透视长条，底边=head（pivot 0.5,0），尾端向消失点收窄
					float holdSpan = note.HoldDuration / fallDuration;   // 进度空间长按时长
					float pHead, pTail;
					if (note.IsHolding)
					{
						// 按住中：head 钉判定线，尾端按剩余时长向上收
						pHead = 1f;
						pTail = 1f - Mathf.Max(0f, note.HoldEndTime - now) / fallDuration;
					}
					else if (note.IsJudged && note.HeadJudgment != null)
					{
						// 按住中松手漏接：head 从判定线钉住位置起整体下滑（避免从线上跳变到线下）
						pHead = 1f + Mathf.Max(0f, now - note.MissTime) / fallDuration;
						pTail = Mathf.Max(0f, pHead - holdSpan);
					}
					else
					{
						// 未按：head 沿轨道持续滑行越过判定线（p 外推不再钳制），尾端在 head 上方 holdSpan 处
						float elapsed = now - (note.ArrivalTime - fallDuration);
						pHead = elapsed / fallDuration;
						pTail = Mathf.Max(0f, pHead - holdSpan);
					}

					// 判定锚点下移：把整条长按音符按"锚点相对视觉高度的比例"下移，
					// 使 ArrivalTime 时 head 的视觉重心压在判定线上（与点击音符同一口径，见 kJudgeAnchorRatio）
					float anchorOffset = kNoteHeight * PerspectiveScale(pHead) * kJudgeAnchorRatio;
					float yHead = PerspectiveY(pHead) - anchorOffset;
					float yTail = PerspectiveY(pTail) - anchorOffset;
					float sHead = ScaleAtY(yHead);
					float sTail = ScaleAtY(yTail);
					// 宽度取所处深度的轨道宽（laneWidth × 透视缩放）
					float wHead = laneWidth * sHead;

					// pivot 已在创建时固定为 (0.5,0)，故高度直接取两端 y 之差
					rt.anchoredPosition = new Vector2(laneXBottom * sHead, yHead);
					rt.sizeDelta = new Vector2(wHead, Mathf.Max(4f, yTail - yHead));
					// 按住中向白色提亮（底色：皮肤白染 / 无皮肤轨道色）
					Color holdBase = mNoteBaseColors.TryGetValue(note, out var bc) ? bc : GetTrackColor(note.TrackIndex);
					visual.color = note.IsHolding ? Color.Lerp(holdBase, Color.white, 0.55f) : holdBase;

					// 顶边（尾端）须落在消失点上，故收缩中心取消失点的局部坐标 -anchoredPosition.x，使斜边与轨道平行
					var trap = visual.GetComponent<TrapezoidImage>();
					if (trap != null)
					{
						trap.TopScale = Mathf.Clamp01(sTail / Mathf.Max(0.01f, sHead));
						trap.TopCenterX = -rt.anchoredPosition.x;
					}
					visual.gameObject.SetActive(true);
					continue;
				}

				// 点击音符：progress 允许外推（越线后沿轨道继续滑行），透视收拢 + 等比缩放
				float clickElapsed = now - (note.ArrivalTime - fallDuration);
				float clickP = clickElapsed / fallDuration;
				float s = PerspectiveScale(clickP);
				// 判定锚点下移：底边锚点 (0.5,0) 下 anchorOffset 后，ArrivalTime 时音符视觉重心压在判定线上
				float yCenter = PerspectiveY(clickP) - kNoteHeight * s * kJudgeAnchorRatio;
				rt.anchoredPosition = new Vector2(laneXBottom * s, yCenter);
				rt.sizeDelta = new Vector2(laneWidth * s, kNoteHeight * s);

				// 顶边按顶边深度收窄（y 与 scale 线性，底边深度 scale 恒为 s），与轨道斜边平行
				float sTop = ScaleAtY(yCenter + kNoteHeight * s);
				var trapClick = visual.GetComponent<TrapezoidImage>();
				if (trapClick != null)
				{
					trapClick.TopScale = Mathf.Clamp01(sTop / Mathf.Max(0.01f, s));
					trapClick.TopCenterX = -rt.anchoredPosition.x;
				}
				visual.gameObject.SetActive(true);
			}

			// 隐藏已判定音符（Miss 音符滑行至完全越过判定线 DisappearTime 前仍显示）
			foreach (var kvp in mNoteVisuals)
			{
				bool sliding = kvp.Key.Judgment == "Miss" && Time.unscaledTime < kvp.Key.DisappearTime;
				if (kvp.Key.IsJudged && !sliding && kvp.Value != null)
				{
					kvp.Value.gameObject.SetActive(false);
				}
			}
		}

		/// <summary>由透视视角（度）计算顶部收缩比：k=(tanα-tan(α/2))/(tanα+tan(α/2))，α=60° → 0.5</summary>
		private static float CalcPerspectiveTopScale(float fovDeg)
		{
			float rad = fovDeg * Mathf.Deg2Rad;
			float tanA = Mathf.Tan(rad);
			float tanHalf = Mathf.Tan(rad * 0.5f);
			return Mathf.Clamp01((tanA - tanHalf) / (tanA + tanHalf));
		}

		/// <summary>
		/// 透视缩放（针孔模型）：p=0 顶部（远）→ kTopScale，p=1 判定线（近）→ 1；
		/// 仅由顶部收缩比确定，p 允许小范围外推（长按尾端），x 收拢与宽度缩放共用
		/// </summary>
		private static float PerspectiveScale(float p)
		{
			return 1f / (1f + (1f / kTopScale - 1f) * (1f - p));
		}

		/// <summary>透视 y 映射：屏幕 y 与 scale 线性（针孔模型 1/z ∝ scale），p=1 → 判定线、p=0 → 容器顶</summary>
		private float PerspectiveY(float p)
		{
			float s = PerspectiveScale(p);
			return mBottomY + (mTopY - mBottomY) * (1f - s) / (1f - kTopScale);
		}

		/// <summary>
		/// PerspectiveY 的逆映射：由屏幕 y 反解该深度的透视缩放（y=mBottomY → 1，y=mTopY → kTopScale）。
		/// 音符整体做判定锚点偏移后，深度已不再是原 p 对应的深度，必须用实际 y 反解，
		/// 否则宽度/顶边收缩会与轨道斜边脱节
		/// </summary>
		private float ScaleAtY(float y)
		{
			float fullH = Mathf.Max(1f, mTopY - mBottomY);
			return Mathf.Clamp(1f - (y - mBottomY) * (1f - kTopScale) / fullH, 0.05f, 2f);
		}

		// ===== 判定带调试（kDebugJudgeBand） =====

		/// <summary>
		/// 判定带可视化：按音符在判定线处的实际像素速度，把「Perfect 判定窗口」与「迟到宽容窗口」
		/// 换算成屏幕高度画出来（静态标尺，开面板时建一次，不随帧更新）。
		/// 用途是核对"音符视觉重心压线"与"引擎判定时刻"是否重合：青带中心 = 完美线，红线 = 过了就 Miss
		/// </summary>
		private void BuildDebugJudgeBands()
		{
			var engine = RhythmEngine.Instance;
			if (engine == null || mTrackContainer == null) return;

			float trackH = Mathf.Max(1f, mTopY - mBottomY);
			float fall = Mathf.Max(0.1f, engine.FallDuration);
			// 判定线处纵向速度（px/s）：y(p) 在 p=1 的斜率为 H/kTopScale，再除以下落时长
			float pxPerSec = trackH / Mathf.Max(0.01f, kTopScale) / fall;
			float perfectPx = pxPerSec * engine.BeatWindow;
			float gracePx = pxPerSec * engine.LateGrace;

			CreateDebugBand("DbgPerfectBand", -perfectPx, perfectPx, new Color(0.2f, 0.9f, 1f, 0.16f));
			CreateDebugBand("DbgGraceLine", -gracePx, -gracePx + 3f, new Color(1f, 0.35f, 0.35f, 0.6f));

			LogKit.Log($"[CookingPanel] 判定带调试：线速度={pxPerSec:F0}px/s，" +
					   $"Perfect=±{perfectPx:F0}px（±{engine.BeatWindow * 1000f:F0}ms），迟到宽容={gracePx:F0}px（+{engine.LateGrace * 1000f:F0}ms）");
		}

		/// <summary>在轨道区按"相对判定线的 y 区间"画一条调试带（与音符同一坐标基准）</summary>
		private void CreateDebugBand(string name, float yMin, float yMax, Color color)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(Image));
			go.transform.SetParent(mTrackContainer, false);
			var img = go.GetComponent<Image>();
			img.color = color;
			img.raycastTarget = false;
			var rt = img.rectTransform;
			rt.anchorMin = new Vector2(0f, 0f);
			rt.anchorMax = new Vector2(1f, 0f);
			rt.pivot = new Vector2(0.5f, 0f);
			rt.sizeDelta = new Vector2(0f, Mathf.Max(3f, yMax - yMin));
			rt.anchoredPosition = new Vector2(0f, mBottomY + yMin);
		}

		/// <summary>创建一个音符视觉对象（长按音符为纵向长条，底边=head）</summary>
		private Image CreateNoteVisual(RhythmNote note, float laneWidth)
		{
			var go = new GameObject($"Note_{note.TrackIndex}");
			go.transform.SetParent(mTrackContainer, false);

			var img = go.AddComponent<Image>();

			// 皮肤：长按音符优先 holdNote（按轨道覆盖，缺省回退 note），点击音符用 note
			var skin = Skin;
			Sprite noteSprite = null;
			if (skin != null)
			{
				noteSprite = note.IsHold
					? PickSprite(skin.holdNoteSprites, note.TrackIndex, skin.holdNoteSprite)
					  ?? PickSprite(skin.noteSprites, note.TrackIndex, skin.noteSprite)
					: PickSprite(skin.noteSprites, note.TrackIndex, skin.noteSprite);
			}

			// 有皮肤资源 = 白染展示原始配色；无资源 = 沿用轨道色块
			Color baseColor = noteSprite != null ? Color.white : GetTrackColor(note.TrackIndex);
			img.sprite = noteSprite;
			img.color = baseColor;
			mNoteBaseColors[note] = baseColor;

			// 统一挂梯形组件：长按尾条/点击音符均由渲染循环按透视收窄顶边（与轨道斜边平行）
			go.AddComponent<TrapezoidImage>();

			var rt = img.rectTransform;
			// 锚点固定为底边中心 (0.5,0)，与 mTopY/mBottomY 同一参考系；
			// 不能沿用判定线的水平拉伸锚——拉伸锚下 sizeDelta 是偏移量，会把音符宽撑到近满屏
			rt.anchorMin = new Vector2(0.5f, 0f);
			rt.anchorMax = new Vector2(0.5f, 0f);
			// 底边锚点 (0.5,0)：渲染循环会再按 kJudgeAnchorRatio 整体下移，使视觉重心压线=ArrivalTime（长按底边为 head）
			rt.pivot = new Vector2(0.5f, 0f);
			rt.sizeDelta = new Vector2(laneWidth, kNoteHeight);

			float x = -mTrackContainer.rect.width / 2f + laneWidth * (note.TrackIndex + 0.5f);
			// 出生点同样带上锚点偏移（此时 s=kTopScale），避免入场第一帧跳一下
			rt.anchoredPosition = new Vector2(x, mTopY - kNoteHeight * kTopScale * kJudgeAnchorRatio);

			return img;
		}

		/// <summary>轨道颜色（0青/1黄/2紫/3橙）</summary>
		private static Color GetTrackColor(int trackIndex)
		{
			return trackIndex switch
			{
				0 => new Color(0.3f, 0.8f, 1f),   // 青
				1 => new Color(1f, 0.8f, 0.3f),   // 黄
				2 => new Color(0.8f, 0.3f, 1f),   // 紫
				3 => new Color(1f, 0.5f, 0.3f),   // 橙
				_ => Color.white
			};
		}

		// ===== 音游皮肤（艺术资源更替） =====

		/// <summary>皮肤配置入口（RecipeConfigWrapper.rhythmSkin，可缺省）</summary>
		private static RhythmSkinConfig Skin =>
			GameDataMgr.Instance != null ? GameDataMgr.Instance.Recipes?.rhythmSkin : null;

		/// <summary>按 Resources 相对路径加载皮肤 Sprite（静态缓存，含 null 避免重复 IO；缺失返回 null 走默认渲染）</summary>
		private static Sprite LoadSkinSprite(string path)
		{
			if (string.IsNullOrEmpty(path)) return null;
			if (s_SpriteCache.TryGetValue(path, out var cached)) return cached;
			var sprite = Resources.Load<Sprite>(path);
			s_SpriteCache[path] = sprite;
			// 配了路径却加载不到 = 该图未导入为 Sprite（textureType≠Sprite）或路径写错；
			// 表现是该项目静默回退纯色块，故这里显式报一次，别让它无声失败
			if (sprite == null && s_SpriteWarned.Add(path))
			{
				LogKit.Warning($"[CookingPanel] 音游皮肤资源加载失败，已回退纯色块：Resources/{path}（检查该图 Import Settings 是否为 Sprite (2D and UI)）");
			}
			return sprite;
		}

		/// <summary>按轨道取皮肤 Sprite：perTrack[trackIndex] 优先，缺省回退统一路径</summary>
		private static Sprite PickSprite(List<string> perTrack, int trackIndex, string fallbackPath)
		{
			if (perTrack != null && trackIndex >= 0 && trackIndex < perTrack.Count)
			{
				var sprite = LoadSkinSprite(perTrack[trackIndex]);
				if (sprite != null) return sprite;
			}
			return LoadSkinSprite(fallbackPath);
		}

		/// <summary>
		/// 应用音游皮肤：轨道背景/判定线/按键背板换用艺术资源，资源缺失时回退代码默认渲染。
		/// 滑轨与音符皮肤随阶段/音符变化，故在 RefreshLanes/CreateNoteVisual 内动态应用
		/// </summary>
		private void ApplySkin()
		{
			var skin = Skin;

			// 置底作背景；无皮肤时退化为半透明黑底，增强轨道层次
			var trackBg = EnsureChild("TrackBg", mTrackContainer);
			trackBg.SetAsFirstSibling();
			Stretch(trackBg);
			var trackBgImg = trackBg.GetComponent<Image>();
			if (trackBgImg == null) trackBgImg = trackBg.gameObject.AddComponent<Image>();
			trackBgImg.raycastTarget = false;
			var trackBgSprite = skin != null ? LoadSkinSprite(skin.trackBgSprite) : null;
			trackBgImg.sprite = trackBgSprite;
			trackBgImg.color = trackBgSprite != null ? Color.white : new Color(0f, 0f, 0f, 0.35f);

			// 常色缓存作为呼吸/闪亮特效的基准色
			if (mJudgeLine != null)
			{
				mJudgeLineImg = mJudgeLine.GetComponent<Image>();
				if (mJudgeLineImg == null) mJudgeLineImg = mJudgeLine.gameObject.AddComponent<Image>();
				mJudgeLineBaseColor = mJudgeLineImg.color;
				var lineSprite = skin != null ? LoadSkinSprite(skin.judgeLineSprite) : null;
				if (lineSprite != null)
				{
					mJudgeLineImg.sprite = lineSprite;
					mJudgeLineImg.color = Color.white;
					mJudgeLineBaseColor = Color.white;
				}
			}

			// 阶段胶囊：有皮肤 = 白染展示原始配色；无皮肤 = 保持预制体默认（半透明黑）
			if (mStagePillImage != null)
			{
				var pillSprite = skin != null ? LoadSkinSprite(skin.stagePillSprite) : null;
				if (pillSprite != null)
				{
					mStagePillImage.sprite = pillSprite;
					mStagePillImage.color = Color.white;
					mStagePillBaseColor = Color.white;
				}
				else
				{
					mStagePillBaseColor = mStagePillImage.color;
				}
			}

			// 按键引导：Key_i 的 Text 直接挂在自身节点上，背板只能作为同层兄弟节点并前置（先绘制）。
			// 旧实现把 KeyBg 做成 Key_i 的子节点（还试图把 Text 搬进去，但 Text 就是 Key_i 本体，reparent 自身子级无效），
			// 结果背板后绘盖在字母上——即使调低 alpha 也会让字母发灰看不清。
			if (mKeyRow != null && skin != null)
			{
				EnsureKeyNodes();
				for (int i = 0; i < kMaxTracks; i++)
				{
					var key = mKeyLabels[i];
					if (key == null) continue;
					var keySprite = PickSprite(skin.keySprites, i, skin.keySprite);
					if (keySprite == null) continue;

					var keyBg = EnsureKeyBg(i, key);
					if (keyBg == null) continue;
					var keyBgImg = keyBg.GetComponent<Image>();
					if (keyBgImg == null) keyBgImg = keyBg.gameObject.AddComponent<Image>();
					keyBgImg.raycastTarget = false;
					keyBgImg.sprite = keySprite;
					keyBgImg.color = Color.white;
				}
			}
		}

		// ===== 战斗反馈特效 =====

		/// <summary>Miss 全屏红光遮罩（根节点直接子节点最顶层，不挡射线）</summary>
		private void EnsureMissOverlay()
		{
			if (mMissOverlay != null) return;
			var go = new GameObject("MissOverlay", typeof(RectTransform), typeof(Image));
			go.transform.SetParent(transform, false);
			Stretch(go.GetComponent<RectTransform>());
			mMissOverlay = go.GetComponent<Image>();
			mMissOverlay.raycastTarget = false;
			mMissOverlay.color = new Color(1f, 0.15f, 0.15f, 0f);
		}

		/// <summary>判定反馈特效分发：Perfect/Good=判定线闪亮+轨道粒子喷发，Miss=全屏红光+根节点微震</summary>
		private void HandleJudgmentFx(GameEvents.CookingNoteJudged evt)
		{
			switch (evt.Judgment)
			{
				case "Perfect":
					mJudgeLineFlash = 1f;
					SpawnHitParticles(evt.TrackIndex, new Color(1f, 0.85f, 0.2f), 16);
					break;
				case "Good":
					mJudgeLineFlash = 0.6f;
					SpawnHitParticles(evt.TrackIndex, Color.white, 10);
					break;
				case "Miss":
					mMissFlashTime = kMissFlashDuration;
					mShakeTimeLeft = kShakeDuration;
					break;
			}
		}

		/// <summary>
		/// 特效每帧推进（渲染协程调用，独立于音游运行状态）：
		/// 判定线呼吸脉冲+命中闪亮 / Miss 红光衰减 / 根节点震动衰减 / 粒子运动
		/// </summary>
		private void UpdateCombatEffects()
		{
			float dt = Time.unscaledDeltaTime;
			float now = Time.unscaledTime;

			// 呼吸脉冲叠加命中闪亮（后者加厚并向白提亮）
			if (mJudgeLineImg != null)
			{
				mJudgeLineFlash = Mathf.Max(0f, mJudgeLineFlash - dt / kJudgeFlashDuration);
				float breath = 1f + 0.04f * Mathf.Sin(now * 4f) + 0.35f * mJudgeLineFlash;
				mJudgeLine.localScale = new Vector3(1f, breath, 1f);
				mJudgeLineImg.color = Color.Lerp(mJudgeLineBaseColor, Color.white, mJudgeLineFlash);
			}

			if (mMissOverlay != null && mMissOverlay.color.a > 0f)
			{
				mMissFlashTime = Mathf.Max(0f, mMissFlashTime - dt);
				var c = mMissOverlay.color;
				c.a = kMissFlashAlpha * (mMissFlashTime / kMissFlashDuration);
				mMissOverlay.color = c;
			}

			// 根为全屏拉伸锚，anchoredPosition 可直接当偏移量用；结束须还原，否则面板永久偏移
			if (mRootRect != null)
			{
				if (mShakeTimeLeft > 0f)
				{
					mShakeTimeLeft = Mathf.Max(0f, mShakeTimeLeft - dt);
					float amp = kShakeAmplitude * (mShakeTimeLeft / kShakeDuration);
					mRootRect.anchoredPosition = Random.insideUnitCircle * amp;
				}
				else if (mRootRect.anchoredPosition != Vector2.zero)
				{
					mRootRect.anchoredPosition = Vector2.zero;
				}
			}

			UpdateParticles(dt);
		}

		/// <summary>粒子推进：重力下坠 + 渐隐，寿终回池；倒序遍历以安全移除</summary>
		private void UpdateParticles(float dt)
		{
			if (mParticles.Count == 0) return;
			for (int i = mParticles.Count - 1; i >= 0; i--)
			{
				var p = mParticles[i];
				p.Life -= dt;
				if (p.Life <= 0f || p.Rt == null)
				{
					if (p.Rt != null)
					{
						p.Rt.gameObject.SetActive(false);
						mParticlePool.Push(p.Rt.gameObject);
					}
					mParticles.RemoveAt(i);
					continue;
				}
				p.Vel.y -= kParticleGravity * dt;
				p.Rt.anchoredPosition += p.Vel * dt;
				var c = p.Img.color;
				c.a = p.Life / p.MaxLife;
				p.Img.color = c;
				mParticles[i] = p;
			}
		}

		/// <summary>判定线命中粒子：该轨道判定线处上半扇形喷发（30°~150°），重力下坠渐隐</summary>
		private void SpawnHitParticles(int trackIndex, Color color, int count)
		{
			if (mTrackContainer == null) return;

			float containerWidth = mTrackContainer.rect.width;
			float laneWidth = containerWidth / Mathf.Max(1, mTrackCount);
			// 判定线处该轨道中心 x（相对容器中心）+ 判定线 y
			float originX = -containerWidth / 2f + laneWidth * (trackIndex + 0.5f);
			float originY = mJudgeLine != null ? mJudgeLine.anchoredPosition.y : 0f;

			for (int i = 0; i < count; i++)
			{
				var rt = GetPooledParticle();
				rt.SetParent(mTrackContainer, false);
				rt.anchorMin = new Vector2(0.5f, 0f);
				rt.anchorMax = new Vector2(0.5f, 0f);
				rt.pivot = new Vector2(0.5f, 0.5f);
				rt.sizeDelta = new Vector2(9f, 9f);
				rt.anchoredPosition = new Vector2(originX, originY);
				rt.localScale = Vector3.one * Random.Range(0.7f, 1.6f);

				var img = rt.GetComponent<Image>();
				img.color = color;

				// 上半扇形随机初速：命中反馈更自然
				float angle = Random.Range(30f, 150f) * Mathf.Deg2Rad;
				float speed = Random.Range(220f, 460f);
				float life = Random.Range(0.45f, 0.65f);
				mParticles.Add(new Particle
				{
					Rt = rt,
					Img = img,
					Vel = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed,
					Life = life,
					MaxLife = life
				});
			}
		}

		/// <summary>从对象池取粒子（池空才实例化；池内引用可能随面板销毁失效，取到已销毁对象则继续弹栈）</summary>
		private RectTransform GetPooledParticle()
		{
			while (mParticlePool.Count > 0)
			{
				var go = mParticlePool.Pop();
				if (go != null) return go.GetComponent<RectTransform>();
			}
			var newGo = new GameObject("HitParticle", typeof(RectTransform), typeof(Image));
			newGo.GetComponent<Image>().raycastTarget = false;
			return newGo.GetComponent<RectTransform>();
		}

		/// <summary>粒子全部回池并停用</summary>
		private void ClearParticles()
		{
			foreach (var p in mParticles)
			{
				if (p.Rt != null)
				{
					p.Rt.gameObject.SetActive(false);
					mParticlePool.Push(p.Rt.gameObject);
				}
			}
			mParticles.Clear();
		}

		/// <summary>面板关闭时还原特效状态：粒子回池、红光/震动/判定线复位</summary>
		private void ResetCombatFx()
		{
			ClearParticles();
			if (mMissOverlay != null)
			{
				var c = mMissOverlay.color;
				c.a = 0f;
				mMissOverlay.color = c;
			}
			mMissFlashTime = 0f;
			mShakeTimeLeft = 0f;
			mJudgeLineFlash = 0f;
			if (mRootRect != null) mRootRect.anchoredPosition = Vector2.zero;
			if (mJudgeLine != null) mJudgeLine.localScale = Vector3.one;
		}
	}
}
