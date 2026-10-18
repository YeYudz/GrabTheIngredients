using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class SettingPanelData : IUIData
	{
	}

	/// <summary>
	/// 设置面板：音频 / 操作 / 画面 / 游戏 四页签的容器。
	/// 面板只负责「把存档值刷到控件」与「把玩家操作交回去」——每个设置项的唯一真相都在存档
	/// 与各自的 Manager（AudioManager / KeyBindingManager / VideoSettingsManager），面板不持有设置状态。
	/// 页签靠 ToggleGroup 子节点名与页根自动配对，省去逐项 Inspector 绑定；
	/// 改绑监听期间接管 Esc，避免误关面板打断按键采集。
	/// </summary>
	public partial class SettingPanel : UIPanel
	{
		[Header("标题")]
		[SerializeField] private Text mTitleText;   // 可选：标题

		[Header("音频")]
		[SerializeField] private Slider mBgmSlider;      // 音乐音量 0~1
		[SerializeField] private Toggle mBgmMuteToggle;  // 音乐静音
		[SerializeField] private Slider mSfxSlider;      // 音效音量 0~1
		[SerializeField] private Toggle mSfxMuteToggle;  // 音效静音

		[Header("按钮")]
		[SerializeField] private Button mBackButton;     // 返回

		[Header("游戏（Game 页签）· 武器主动自动释放")]
		[Tooltip("技能槽 1 的自动释放开关（Game/Viewport/Content/Skill0）；留空时运行时按名字 Skill0 兜底")]
		[SerializeField] private Toggle mAutoCastToggle0;
		[Tooltip("技能槽 2 的自动释放开关（Game/Viewport/Content/Skill1）；留空时运行时按名字 Skill1 兜底")]
		[SerializeField] private Toggle mAutoCastToggle1;
		[Tooltip("技能槽 3 的自动释放开关（Game/Viewport/Content/Skill2）；留空时运行时按名字 Skill2 兜底")]
		[SerializeField] private Toggle mAutoCastToggle2;

		[Header("画面（Vedio 页签）")]
		[Tooltip("分辨率下拉框（Vedio/ResolutionDropdown）；留空时运行时按名字兜底")]
		[SerializeField] private Dropdown mResolutionDropdown;
		[Tooltip("窗口模式 ToggleGroup（Vedio/ToggleGroup）；留空时运行时按名字兜底")]
		[SerializeField] private ToggleGroup mWindowModeGroup;
		[Tooltip("渲染效果下拉框（Vedio/RenderingDropdown*）；留空时运行时兜底")]
		[SerializeField] private Dropdown mRenderingDropdown;
		[Tooltip("垂直同步开关（Vedio 里唯一不在窗口模式组内的 Toggle）；留空时运行时兜底")]
		[SerializeField] private Toggle mVSyncToggle;
		[Tooltip("画面亮度滑块（Vedio/ScreenBrightnessSlider）；留空时运行时兜底")]
		[SerializeField] private Slider mBrightnessSlider;
		[Tooltip("画面亮度数值文本（Vedio/ScreenBrightnessLabel）；留空时取滑块下的 Text")]
		[SerializeField] private Text mBrightnessLabel;

		/// <summary>静音前音量备份（取消静音时恢复）</summary>
		private float mBgmBackup = 1f;
		private float mSfxBackup = 1f;

		// ===== 页签（ToggleGroup 自动配对，无需 Inspector 绑定） =====
		private ToggleGroup mTabGroup;
		private readonly List<Toggle> mTabs = new();
		/// <summary>页签节点名 → 页根（如 GameTogle → Game）</summary>
		private readonly Dictionary<string, GameObject> mTabPages = new();

		// ===== 游戏页签：自动释放开关（预制体固定 3 个，下标 = 武器技能槽序号） =====
		/// <summary>运行时收集的 3 个开关（Inspector 拖拽优先，缺了按名字 Skill0/1/2 兜底）</summary>
		private readonly Toggle[] mAutoCastToggles = new Toggle[MetaManager.WeaponSkillSlotCount];
		/// <summary>与开关同行的文案（开关行的 Label）；槽位没技能时压暗</summary>
		private readonly Text[] mAutoCastLabels = new Text[MetaManager.WeaponSkillSlotCount];
		/// <summary>游戏页开关是否已接过事件（OnInit 里只接一次）</summary>
		private bool mGameWired;

		// ===== 列表行样式（操作页的改绑行用；游戏页已改为预制体固定开关） =====
		private const float kHintHeight = 44f;
		private const float kListSpacing = 12f;
		private const float kListPadding = 16f;

		private static readonly Color kPrimaryText = new Color(0.97f, 0.97f, 0.97f);
		private static readonly Color kSecondaryText = new Color(0.74f, 0.77f, 0.80f);
		private static readonly Color kOnColor = new Color(0.36f, 0.90f, 0.46f);
		/// <summary>行底：页面底色是半透明白（叠在深色面板上偏灰），铺一层深色保证白字对比度</summary>
		private static readonly Color kRowBg = new Color(0f, 0f, 0f, 0.32f);

		// ===== 操作页签：按键改绑（运行时代码构建） =====

		/// <summary>操作页内容根（Control/Viewport/Content）；首次用到时解析并缓存</summary>
		private RectTransform mControlContent;

		/// <summary>正在等待按键的动作（null = 未监听）</summary>
		private GameInputAction? mListening;

		/// <summary>列表顶部的说明 / 报错行</summary>
		private Text mKeyNotice;

		/// <summary>顶部说明默认文案（非报错态恢复用）</summary>
		private const string kKeyNoticeDefault =
			"局内按键｜点击右侧「改绑」后按下新按键，Esc 或鼠标右键取消；已被占用的按键无法绑定";

		/// <summary>非空 = 顶部说明行改为显示该报错文案（红色）；改绑成功 / 取消时清空</summary>
		private string mKeyNoticeMessage;

		private const float kKeyRowHeight = 58f;
		private const float kKeyBoxWidth = 150f;
		private const float kKeyButtonWidth = 130f;
		/// <summary>行内控件高度（行高 58 - 上下内边距 8*2 = 42，正好铺满不撑高）</summary>
		private const float kKeyControlHeight = 42f;

		/// <summary>报错文案配色（冲突 / 保留键）</summary>
		private static readonly Color kDangerText = new Color(1f, 0.48f, 0.40f);

		protected override void OnInit(IUIData data = null)
		{
			mData = data as SettingPanelData ?? new SettingPanelData();

			if (mBgmSlider != null)
			{
				mBgmSlider.onValueChanged.RemoveAllListeners();
				mBgmSlider.onValueChanged.AddListener(OnBgmChanged);
			}
			if (mSfxSlider != null)
			{
				mSfxSlider.onValueChanged.RemoveAllListeners();
				mSfxSlider.onValueChanged.AddListener(OnSfxChanged);
			}
			if (mBgmMuteToggle != null)
			{
				mBgmMuteToggle.onValueChanged.RemoveAllListeners();
				mBgmMuteToggle.onValueChanged.AddListener(OnBgmMuteChanged);
			}
			if (mSfxMuteToggle != null)
			{
				mSfxMuteToggle.onValueChanged.RemoveAllListeners();
				mSfxMuteToggle.onValueChanged.AddListener(OnSfxMuteChanged);
			}
			if (mBackButton != null)
			{
				mBackButton.onClick.RemoveAllListeners();
				mBackButton.onClick.AddListener(OnBack);
			}

			// 画面页控件先接好：页签初始化会立刻切到默认页，可能马上要刷新画面页
			SetupVideoTab();
			// 游戏页的 3 个自动释放开关同样先接好（页签切过去就要刷状态）
			SetupGameTab();
			SetupTabs();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as SettingPanelData ?? mData;
			RefreshFromAudio();

			// 打开时刷新画面页：分辨率下拉框要按当前显示器重建选项，其余项回填存档值
			RefreshFromVideo();

			// 打开时刷新 3 个自动释放开关：战前换过装备（槽位空/有主动技能）都体现在这里
			RefreshAutoCastSlots();

			// 打开时刷新按键改绑列表：存档键位可能被上一局面板改过
			CancelListening();
			RefreshKeyBindingList();
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this);
		}

		protected override void OnHide()
		{
			// 面板被上层盖住时退出监听，避免按键被"偷"进改绑
			CancelListening();

			// 亮度是"拖动中只预览、松手才落盘"，这里兜一次：万一没收到松手事件也不会丢改动
			VideoSettingsManager.FlushPendingSave();
		}

		protected override void OnClose()
		{
			CancelListening();
			VideoSettingsManager.FlushPendingSave();
		}

		private void Update()
		{
			// 监听态：只等新按键（Esc / 右键 = 取消）。此时 Esc 不关闭面板。
			if (mListening != null)
			{
				if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
				{
					CancelListening();
					return;
				}
				if (KeyBindingManager.TryCaptureKeyDown(out var captured))
				{
					ApplyCapturedKey(captured);
				}
				return;
			}

			// ESC = 返回（关闭本面板，回到上层暂停面板）
			if (Input.GetKeyDown(KeyCode.Escape))
				OnBack();
		}

		// ===== 音量读取 =====

		/// <summary>从 AudioManager 读取当前音量回填 UI（进入面板时）</summary>
		private void RefreshFromAudio()
		{
			float bgm = AudioManager.HasInstance ? AudioManager.Instance.BgmVolume : 1f;
			float sfx = AudioManager.HasInstance ? AudioManager.Instance.SfxVolume : 1f;

			mBgmBackup = bgm > 0.001f ? bgm : mBgmBackup;
			mSfxBackup = sfx > 0.001f ? sfx : mSfxBackup;

			SetSliderValue(mBgmSlider, bgm);
			SetSliderValue(mSfxSlider, sfx);
			if (mBgmMuteToggle != null) mBgmMuteToggle.isOn = bgm <= 0.001f;
			if (mSfxMuteToggle != null) mSfxMuteToggle.isOn = sfx <= 0.001f;
		}

		/// <summary>给滑块赋值（不触发 onValueChanged，避免回填时误写存档）</summary>
		private static void SetSliderValue(Slider slider, float value)
		{
			if (slider == null) return;
			slider.SetValueWithoutNotify(value);
			slider.interactable = value > 0.001f;
		}

		// ===== 滑块回调 =====

		private void OnBgmChanged(float value)
		{
			if (AudioManager.HasInstance) AudioManager.Instance.BgmVolume = value;
			mBgmBackup = value > 0.001f ? value : mBgmBackup;
			if (mBgmMuteToggle != null && value > 0.001f) mBgmMuteToggle.isOn = false;
		}

		private void OnSfxChanged(float value)
		{
			if (AudioManager.HasInstance) AudioManager.Instance.SfxVolume = value;
			mSfxBackup = value > 0.001f ? value : mSfxBackup;
			if (mSfxMuteToggle != null && value > 0.001f) mSfxMuteToggle.isOn = false;
		}

		// ===== 静音开关回调 =====

		private void OnBgmMuteChanged(bool muted)
		{
			float target = muted ? 0f : mBgmBackup;
			if (AudioManager.HasInstance) AudioManager.Instance.BgmVolume = target;
			if (mBgmSlider != null)
			{
				mBgmSlider.SetValueWithoutNotify(target);
				mBgmSlider.interactable = !muted;
			}
		}

		private void OnSfxMuteChanged(bool muted)
		{
			float target = muted ? 0f : mSfxBackup;
			if (AudioManager.HasInstance) AudioManager.Instance.SfxVolume = target;
			if (mSfxSlider != null)
			{
				mSfxSlider.SetValueWithoutNotify(target);
				mSfxSlider.interactable = !muted;
			}
		}

		// ===== 返回 =====

		private void OnBack()
		{
			PlayClick();
			UIKit.ClosePanel<SettingPanel>();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// 页签切换：SettingWindow 下并列页签条与四个页根，按名字配对而非 Inspector 绑定

		private void SetupTabs()
		{
			if (mTabGroup == null) mTabGroup = ResolveTabGroup();
			if (mTabGroup == null)
			{
				LogKit.Warning("[SettingPanel] 未找到 ToggleGroup，页签切换不可用");
				return;
			}

			mTabs.Clear();
			mTabPages.Clear();

			var toggles = mTabGroup.GetComponentsInChildren<Toggle>(true);
			int startToggle = -1;

			for (int i = 0; i < toggles.Length; i++)
			{
				var toggle = toggles[i];
				string pageName = ResolvePageName(toggle.name);
				var page = string.IsNullOrEmpty(pageName) ? null : FindChild(transform, pageName);
				if (page == null)
				{
					// 只告警不抛：未搭建完成的页签不影响其它页签可用
					LogKit.Warning($"[SettingPanel] 页签 {toggle.name} 找不到同名页「{pageName}」，已跳过");
					continue;
				}

				mTabs.Add(toggle);
				mTabPages[toggle.name] = page.gameObject;

				toggle.onValueChanged.RemoveAllListeners();
				toggle.onValueChanged.AddListener(isOn =>
				{
					// 只处理"被选中"：ToggleGroup 会把其它页签置 false，避免重复切换
					if (isOn) ShowTab(toggle.name);
				});

				// 初始页签跟随预制体里已勾选的 Toggle（避免高亮页签与显示内容不一致）
				if (toggle.isOn) startToggle = mTabs.Count - 1;
			}

			if (mTabs.Count == 0) return;

			if (startToggle < 0) startToggle = 0;   // 预制体一个都没勾选 → 默认第一页
			ShowTab(mTabs[startToggle].name);
		}

	/// <summary>
	/// 定位页签条的 ToggleGroup：优先取 SettingWindow 直接子节点，避免被画面页的同名节点劫持；
	/// 找不到才退回深度搜索，兼容未搭画面页的旧预制体。
	/// </summary>
		private ToggleGroup ResolveTabGroup()
		{
			var window = FindChild(transform, "SettingWindow");
			if (window != null)
			{
				for (int i = 0; i < window.childCount; i++)
				{
					var group = window.GetChild(i).GetComponent<ToggleGroup>();
					if (group != null) return group;
				}
			}
			return FindComponent<ToggleGroup>("ToggleGroup");
		}

		/// <summary>切换页签：只显示目标页，其余页整根隐藏</summary>
		private void ShowTab(string tabKey)
		{
			foreach (var kv in mTabPages)
				kv.Value.SetActive(kv.Key == tabKey);

			// 操作页：切走时退出改绑监听；切进来时按当前键位重建列表
			CancelListening();
			if (ResolvePageName(tabKey) == "Control") RefreshKeyBindingList();
			// 画面页：分辨率下拉框要按当前显示器重建，切回来时重刷一遍
			if (ResolvePageName(tabKey) == "Vedio") RefreshFromVideo();
			// 游戏页：装备可能被上一局面板改过，切回来时重刷 3 个开关
			if (ResolvePageName(tabKey) == "Game") RefreshAutoCastSlots();
		}

		/// <summary>页签节点名 → 页名（AudioTogle→Audio；预制体把 Toggle 拼成了 Togle，这里都兼容）</summary>
		private static string ResolvePageName(string toggleName)
		{
			if (string.IsNullOrEmpty(toggleName)) return null;

			const string kToggle = "Toggle";
			const string kTogle = "Togle";
			if (toggleName.EndsWith(kToggle, System.StringComparison.OrdinalIgnoreCase))
				return toggleName.Substring(0, toggleName.Length - kToggle.Length);
			if (toggleName.EndsWith(kTogle, System.StringComparison.OrdinalIgnoreCase))
				return toggleName.Substring(0, toggleName.Length - kTogle.Length);
			return null;
		}

		// 游戏页：3 个固定的自动释放开关，下标即武器技能槽序号（对外显示为槽 1~3）。
		// 状态按槽位存 MetaManager 设置存档，改动即时广播，正局/反局都生效。
		// ⚠️ 这里只管「开 / 关」这个设置本身，不看槽位当前装没装武器、装的是不是带主动技能的武器：
		//    装备是战前/局外随时会变的东西，设置面板不该因为某个槽空着就把开关锁死（开了没东西放由 WeaponActiveSystem 自己忽略）。

		/// <summary>接游戏页的 3 个自动释放开关（OnInit 调用一次；只接线，状态由 RefreshAutoCastSlots 刷）</summary>
		private void SetupGameTab()
		{
			if (mGameWired) return;
			ResolveAutoCastToggles();
			mGameWired = true;

			for (int slot = 0; slot < mAutoCastToggles.Length; slot++)
			{
				var toggle = mAutoCastToggles[slot];
				if (toggle == null) continue;

				int index = slot;   // 闭包捕获：不能直接用循环变量
				toggle.onValueChanged.RemoveAllListeners();
				toggle.onValueChanged.AddListener(isOn =>
				{
					PlayClick();
					if (MetaManager.HasInstance) MetaManager.Instance.SetAutoCastEnabled(index, isOn);
					ApplyAutoCastVisual(index, isOn);
				});
			}
		}

		/// <summary>解析 3 个开关：Inspector 拖拽优先，为空时按名字（Game/Skill0..2）兜底</summary>
		private void ResolveAutoCastToggles()
		{
			mAutoCastToggles[0] = mAutoCastToggle0;
			mAutoCastToggles[1] = mAutoCastToggle1;
			mAutoCastToggles[2] = mAutoCastToggle2;

			var gamePage = FindChild(transform, "Game");
			if (gamePage == null)
			{
				LogKit.Warning("[SettingPanel] 找不到游戏页 Game，自动释放开关只能用 Inspector 绑定");
			}
			else
			{
				for (int slot = 0; slot < mAutoCastToggles.Length; slot++)
				{
					if (mAutoCastToggles[slot] != null) continue;
					mAutoCastToggles[slot] = ComponentAt<Toggle>(FindChild(gamePage, $"Skill{slot}"));
				}
			}

			for (int slot = 0; slot < mAutoCastToggles.Length; slot++)
			{
				var toggle = mAutoCastToggles[slot];
				// 行文案（预制体里的「技能槽N自动开关」）：文案与显隐都归预制体，这里只接管配色
				mAutoCastLabels[slot] = toggle != null ? toggle.GetComponentInChildren<Text>(true) : null;
				if (toggle == null)
					LogKit.Warning($"[SettingPanel] 技能槽 {slot + 1} 的自动释放开关未找到（Game/Skill{slot}）");
			}
		}

		/// <summary>把存档状态刷到 3 个开关上（打开面板 / 切到游戏页时）</summary>
		private void RefreshAutoCastSlots()
		{
			if (!mGameWired) SetupGameTab();

			for (int slot = 0; slot < mAutoCastToggles.Length; slot++)
			{
				var toggle = mAutoCastToggles[slot];
				if (toggle == null) continue;

				// 纯设置项：只看存档，与槽位当前装了什么武器无关（空槽也允许开着）
				bool on = !MetaManager.HasInstance || MetaManager.Instance.IsAutoCastEnabled(slot);

				// 先 SetIsOnWithoutNotify：回填不触发 onValueChanged，不会把"刷新"当成玩家操作写盘
				toggle.SetIsOnWithoutNotify(on);
				toggle.interactable = true;
				ApplyAutoCastVisual(slot, on);
			}
		}

		/// <summary>开关行配色：开 = 亮白，关 = 次级灰（勾的显隐由 Toggle.graphic 自己淡入淡出）</summary>
		private void ApplyAutoCastVisual(int slot, bool on)
		{
			var label = mAutoCastLabels[slot];
			if (label == null) return;

			label.color = on ? kPrimaryText : kSecondaryText;
		}

		/// <summary>列表行容器：深色底 + 固定行高 + 横向排列（行内元素左对齐）</summary>
		private static RectTransform AddRowContainer(RectTransform parent, string name, float height, bool expandChildWidth)
		{
			var go = new GameObject(name,
				typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
			var row = go.GetComponent<RectTransform>();
			row.SetParent(parent, false);

			go.GetComponent<LayoutElement>().preferredHeight = height;

			var bg = go.GetComponent<Image>();
			bg.color = kRowBg;
			bg.raycastTarget = false;

			var h = go.GetComponent<HorizontalLayoutGroup>();
			h.padding = new RectOffset(20, 20, 8, 8);
			h.spacing = 20f;
			h.childAlignment = TextAnchor.MiddleLeft;
			h.childControlWidth = true;
			h.childControlHeight = true;
			h.childForceExpandWidth = expandChildWidth;
			h.childForceExpandHeight = false;

			return row;
		}

		/// <summary>把内容根规整成"竖排列表"：顶部对齐 + 纵向排列 + 高度按行数撑开（≤3 行不会触发滚动）</summary>
		private static void PrepareListRoot(RectTransform content)
		{
			content.anchorMin = new Vector2(0f, 1f);
			content.anchorMax = new Vector2(1f, 1f);
			content.pivot = new Vector2(0f, 1f);
			content.anchoredPosition = Vector2.zero;

			var layout = content.GetComponent<VerticalLayoutGroup>();
			if (layout == null) layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
			layout.padding = new RectOffset((int)kListPadding, (int)kListPadding, (int)kListPadding, (int)kListPadding);
			layout.spacing = kListSpacing;
			layout.childAlignment = TextAnchor.UpperLeft;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = true;
			layout.childForceExpandHeight = false;
		}

		/// <summary>内容高度 = 内边距 + 各行高度 + 行间距（不依赖 ContentSizeFitter，避免与 ScrollRect 互相驱动）</summary>
		private static void LayoutListRoot(RectTransform content, List<float> rowHeights)
		{
			float total = kListPadding * 2f;
			for (int i = 0; i < rowHeights.Count; i++)
			{
				total += rowHeights[i];
				if (i > 0) total += kListSpacing;
			}
			content.sizeDelta = new Vector2(0f, total);
		}

		// 画面页：唯一真相是 SettingsSaveData 的画面字段，读、写、应用到 Unity 全在 VideoSettingsManager；
		// 本面板不直接碰 Screen / QualitySettings。
		// ⚠ 画面页内也有一个同名 ToggleGroup（窗口模式），故一律在 Vedio 子树内查找，绝不会拿到页签条。

		/// <summary>画面页里窗口模式的档数（无边框 / 窗口化 / 全屏），与 GameWindowMode 的数值一一对应</summary>
		private const int kWindowModeCount = 3;

		/// <summary>亮度滑块上的滚轮步进（归一化）</summary>
		private const float kBrightnessScrollStep = 0.04f;

		/// <summary>画面页根（Vedio）</summary>
		private GameObject mVideoPage;

		/// <summary>窗口模式三档的 Toggle，已按视觉顺序（左→右，其次上→下）排好，下标即 GameWindowMode</summary>
		private readonly List<Toggle> mWindowModeToggles = new();

		/// <summary>分辨率下拉框的选项（下标与下拉框 index 一一对应）</summary>
		private readonly List<Vector2Int> mResolutionOptions = new();

		/// <summary>亮度滑块上的拖拽组件（预制体没挂就运行时补挂）</summary>
		private DragSlider mBrightnessDrag;

		/// <summary>亮度文本里的"标题"部分（刷新时只替换数值，如「画面亮度 80%」）</summary>
		private string mBrightnessTitle = "画面亮度";

		/// <summary>画面页是否已接过事件（OnInit 里只接一次）</summary>
		private bool mVideoWired;

		/// <summary>接画面页控件与事件（OnInit 调用一次）</summary>
		private void SetupVideoTab()
		{
			if (mVideoWired) return;
			ResolveVideoControls();
			if (mVideoPage == null) return;
			mVideoWired = true;

			if (mResolutionDropdown != null)
			{
				mResolutionDropdown.onValueChanged.RemoveAllListeners();
				mResolutionDropdown.onValueChanged.AddListener(OnResolutionChanged);
			}

			// 窗口模式是"三选一分段控件"：只认被选中的那一次回调（ToggleGroup 会把其余档位置 false）
			for (int i = 0; i < mWindowModeToggles.Count; i++)
			{
				int index = i;
				var toggle = mWindowModeToggles[i];
				toggle.onValueChanged.RemoveAllListeners();
				toggle.onValueChanged.AddListener(isOn =>
				{
					if (isOn) OnWindowModeChanged(index);
				});
			}

			if (mRenderingDropdown != null)
			{
				mRenderingDropdown.onValueChanged.RemoveAllListeners();
				mRenderingDropdown.onValueChanged.AddListener(OnRenderingChanged);
			}

			if (mVSyncToggle != null)
			{
				mVSyncToggle.onValueChanged.RemoveAllListeners();
				mVSyncToggle.onValueChanged.AddListener(OnVSyncChanged);
			}

			if (mBrightnessSlider != null)
			{
				mBrightnessSlider.minValue = 0f;
				mBrightnessSlider.maxValue = 1f;
				mBrightnessSlider.wholeNumbers = false;
				mBrightnessSlider.onValueChanged.RemoveAllListeners();
				mBrightnessSlider.onValueChanged.AddListener(OnBrightnessChanged);

				// 拖拽组件：拖动 / 滚轮改值，松手时 onValueCommitted 把亮度落盘
				mBrightnessDrag = mBrightnessSlider.GetComponent<DragSlider>();
				if (mBrightnessDrag == null) mBrightnessDrag = mBrightnessSlider.gameObject.AddComponent<DragSlider>();
				mBrightnessDrag.Initialize(mBrightnessSlider, kBrightnessScrollStep);
				mBrightnessDrag.onValueCommitted -= OnBrightnessCommitted;
				mBrightnessDrag.onValueCommitted += OnBrightnessCommitted;
			}
		}

		/// <summary>解析画面页控件：Inspector 绑定优先，缺失时按名字/类型兜底（画面页子树内查找）</summary>
		private void ResolveVideoControls()
		{
			if (mVideoPage == null)
			{
				var found = FindChild(transform, "Vedio");
				mVideoPage = found != null ? found.gameObject : null;
			}
			if (mVideoPage == null)
			{
				LogKit.Warning("[SettingPanel] 找不到画面页 Vedio，画面设置不可用");
				return;
			}

			var page = mVideoPage.transform;

			if (mResolutionDropdown == null)
				mResolutionDropdown = ComponentAt<Dropdown>(FindChild(page, "ResolutionDropdown"));

			// 窗口模式组：画面页里唯一的 ToggleGroup
			if (mWindowModeGroup == null)
				mWindowModeGroup = page.GetComponentInChildren<ToggleGroup>(true);

			if (mWindowModeToggles.Count == 0 && mWindowModeGroup != null)
			{
				var found = mWindowModeGroup.GetComponentsInChildren<Toggle>(true);
				for (int i = 0; i < found.Length; i++)
					if (found[i] != null) mWindowModeToggles.Add(found[i]);

				// 按视觉位置排序：不依赖子节点命名（mode0/1/2）与层级顺序，界面改成竖排也不会翻车
				mWindowModeToggles.Sort(CompareToggleVisualOrder);

				if (mWindowModeToggles.Count != kWindowModeCount)
					LogKit.Warning($"[SettingPanel] 画面页窗口模式应有 {kWindowModeCount} 档，实际 {mWindowModeToggles.Count} 档");
			}

			if (mRenderingDropdown == null)
			{
				// 预制体里这个节点叫 "RenderingDropdown (1)"（Unity 复制出的后缀）→ 按前缀找；
				// 再不行就取「画面页里除分辨率外的另一个 Dropdown」
				foreach (var dropdown in page.GetComponentsInChildren<Dropdown>(true))
				{
					if (dropdown == null || dropdown == mResolutionDropdown) continue;
					if (dropdown.name.StartsWith("RenderingDropdown", System.StringComparison.Ordinal))
					{
						mRenderingDropdown = dropdown;
						break;
					}
					if (mRenderingDropdown == null) mRenderingDropdown = dropdown;
				}
			}

			// 垂直同步 = 画面页里「不在窗口模式组内」的那个 Toggle（窗口模式三档都挂在 ToggleGroup 下）
			if (mVSyncToggle == null)
			{
				foreach (var toggle in page.GetComponentsInChildren<Toggle>(true))
				{
					if (toggle == null || mWindowModeToggles.Contains(toggle)) continue;
					mVSyncToggle = toggle;
					break;
				}
			}

			if (mBrightnessSlider == null)
				mBrightnessSlider = page.GetComponentInChildren<Slider>(true);

			if (mBrightnessLabel == null && mBrightnessSlider != null)
				mBrightnessLabel = mBrightnessSlider.GetComponentInChildren<Text>(true);

			// 记下亮度文本的"标题"部分：之后刷新只在后面追加数值
			if (mBrightnessLabel != null && !string.IsNullOrEmpty(mBrightnessLabel.text))
				mBrightnessTitle = mBrightnessLabel.text.Trim();
		}

		/// <summary>Toggle 的视觉顺序：先按 x（左→右），x 相同再按 y（上→下）</summary>
		private static int CompareToggleVisualOrder(Toggle a, Toggle b)
		{
			var ra = a.transform as RectTransform;
			var rb = b.transform as RectTransform;
			if (ra == null || rb == null) return 0;

			float dx = ra.anchoredPosition.x - rb.anchoredPosition.x;
			if (Mathf.Abs(dx) > 0.01f) return dx < 0f ? -1 : 1;

			float dy = rb.anchoredPosition.y - ra.anchoredPosition.y;
			if (Mathf.Abs(dy) > 0.01f) return dy < 0f ? -1 : 1;
			return 0;
		}

		/// <summary>把存档里的画面设置刷到控件上（打开面板 / 切到画面页时调用；全部用 WithoutNotify，不触发回调）</summary>
		private void RefreshFromVideo()
		{
			if (!mVideoWired) SetupVideoTab();
			if (mVideoPage == null) return;

			RefreshResolutionDropdown();
			RefreshRenderingDropdown();

			int mode = (int)VideoSettingsManager.WindowMode;
			for (int i = 0; i < mWindowModeToggles.Count; i++)
				mWindowModeToggles[i].SetIsOnWithoutNotify(i == mode);

			if (mVSyncToggle != null)
				mVSyncToggle.SetIsOnWithoutNotify(VideoSettingsManager.VSync);

			if (mBrightnessSlider != null)
			{
				float brightness = VideoSettingsManager.Brightness;
				mBrightnessSlider.SetValueWithoutNotify(brightness);
				UpdateBrightnessLabel(brightness);
			}
		}

		/// <summary>重建分辨率选项（每次刷新都重建：换了显示器 / 拖动过窗口，候选列表都会变）</summary>
		private void RefreshResolutionDropdown()
		{
			if (mResolutionDropdown == null) return;

			mResolutionOptions.Clear();
			mResolutionOptions.AddRange(VideoSettingsManager.BuildResolutionOptions());

			var options = new List<Dropdown.OptionData>(mResolutionOptions.Count);
			for (int i = 0; i < mResolutionOptions.Count; i++)
				options.Add(new Dropdown.OptionData(VideoSettingsManager.FormatResolution(mResolutionOptions[i])));

			mResolutionDropdown.ClearOptions();
			mResolutionDropdown.AddOptions(options);

			// 存档里有值用存档，没设置过用"当前渲染分辨率"，保证下拉框显示的确实是现在生效的那个
			VideoSettingsManager.GetSavedResolution(out int width, out int height);
			if (width <= 0 || height <= 0)
			{
				width = Screen.width;
				height = Screen.height;
			}

			int index = mResolutionOptions.IndexOf(new Vector2Int(width, height));
			mResolutionDropdown.SetValueWithoutNotify(index >= 0 ? index : 0);
			mResolutionDropdown.RefreshShownValue();
		}

		/// <summary>重建渲染效果选项（档位名来自工程 QualitySettings，Unity 默认档名转中文）</summary>
		private void RefreshRenderingDropdown()
		{
			if (mRenderingDropdown == null) return;

			int count = VideoSettingsManager.QualityLevelCount;
			if (count <= 0) return;

			var options = new List<Dropdown.OptionData>(count);
			for (int i = 0; i < count; i++)
				options.Add(new Dropdown.OptionData(VideoSettingsManager.GetQualityLabel(i)));

			mRenderingDropdown.ClearOptions();
			mRenderingDropdown.AddOptions(options);
			mRenderingDropdown.SetValueWithoutNotify(Mathf.Clamp(VideoSettingsManager.QualityLevel, 0, count - 1));
			mRenderingDropdown.RefreshShownValue();
		}

		// ===== 画面页回调 =====

		private void OnResolutionChanged(int index)
		{
			if (index < 0 || index >= mResolutionOptions.Count) return;
			PlayClick();

			var resolution = mResolutionOptions[index];
			VideoSettingsManager.SetResolution(resolution.x, resolution.y);
		}

		private void OnWindowModeChanged(int index)
		{
			PlayClick();
			VideoSettingsManager.SetWindowMode((GameWindowMode)Mathf.Clamp(index, 0, kWindowModeCount - 1));
		}

		private void OnRenderingChanged(int level)
		{
			PlayClick();
			VideoSettingsManager.SetQualityLevel(level);
		}

		private void OnVSyncChanged(bool on)
		{
			PlayClick();
			VideoSettingsManager.SetVSync(on);
		}

		/// <summary>亮度：拖动 / 滚轮中只应用到屏幕并刷新数值（不写盘，避免一帧一次写存档）</summary>
		private void OnBrightnessChanged(float value)
		{
			UpdateBrightnessLabel(value);
			VideoSettingsManager.SetBrightness(value);
		}

		/// <summary>拖拽松手 / 滚轮一下 → 把亮度落盘</summary>
		private void OnBrightnessCommitted(float value)
		{
			VideoSettingsManager.FlushPendingSave();
		}

		private void UpdateBrightnessLabel(float value)
		{
			if (mBrightnessLabel == null) return;
			mBrightnessLabel.text = $"{mBrightnessTitle} {Mathf.RoundToInt(Mathf.Clamp01(value) * 100f)}%";
		}

		// 操作页：6 个动作的默认键位、冲突判定、存档读写全在 KeyBindingManager，
		// 本面板只渲染「动作名 + 当前键位 + 改绑按钮」，并把捕获到的按键交给它定夺。
		// 监听期间吞掉 Esc，避免误关面板打断采集。

		/// <summary>重建改绑列表（进入面板时 / 切到操作页时 / 改绑成功 / 恢复默认后）</summary>
		private void RefreshKeyBindingList()
		{
			var content = ResolveControlContent();
			if (content == null)
			{
				LogKit.Warning("[SettingPanel] 找不到操作页内容根 Control/Viewport/Content，按键改绑列表不显示");
				return;
			}

			PrepareListRoot(content);

			// 清掉历史内容（旧预制体把音频页的滑块复制到了这一页）
			// 先 SetActive(false)：Destroy 到帧末才生效，不先移出布局会有一帧新旧行重叠
			for (int i = content.childCount - 1; i >= 0; i--)
			{
				var child = content.GetChild(i).gameObject;
				child.SetActive(false);
				Destroy(child);
			}
			mKeyNotice = null;

			var heights = new List<float>();

			// 说明 / 报错行（同一行复用：有报错时变红顶替说明）
			bool hasError = !string.IsNullOrEmpty(mKeyNoticeMessage);
			var noticeRow = AddRowContainer(content, "KeyNotice", kHintHeight, expandChildWidth: true);
			mKeyNotice = AddListText(noticeRow,
				hasError ? mKeyNoticeMessage : kKeyNoticeDefault,
				20, hasError ? kDangerText : kSecondaryText, 0f);
			heights.Add(kHintHeight);

			// 6 行可改绑动作
			for (int i = 0; i < KeyBindingManager.ActionCount; i++)
			{
				AddKeyBindingRow(content, (GameInputAction)i);
				heights.Add(kKeyRowHeight);
			}

			// 全部恢复默认
			AddResetKeysRow(content);
			heights.Add(kKeyRowHeight);

			LayoutListRoot(content, heights);
		}

		/// <summary>一行：动作名（占满剩余宽度） + 当前键位方块 + 改绑按钮</summary>
		private void AddKeyBindingRow(RectTransform content, GameInputAction action)
		{
			bool listening = mListening.HasValue && mListening.Value == action;

			var row = AddRowContainer(content, $"Key_{action}", kKeyRowHeight, expandChildWidth: false);

			string title = KeyBindingManager.GetActionName(action);
			if (listening) title += "　← 等待新按键";
			AddFlexibleText(row, title, 28, listening ? kOnColor : kPrimaryText);

			// 当前键位（浅底方块）
			var boxGo = new GameObject("KeyBox", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
			var box = boxGo.GetComponent<RectTransform>();
			box.SetParent(row, false);

			var boxImg = boxGo.GetComponent<Image>();
			boxImg.sprite = RowBoxSprite();
			boxImg.type = Image.Type.Sliced;
			boxImg.color = new Color(1f, 1f, 1f, 0.22f);
			boxImg.raycastTarget = false;

			var boxLe = boxGo.GetComponent<LayoutElement>();
			boxLe.preferredWidth = kKeyBoxWidth;
			boxLe.minWidth = kKeyBoxWidth;
			boxLe.preferredHeight = kKeyControlHeight;
			boxLe.minHeight = kKeyControlHeight;
			boxLe.flexibleWidth = 0f;

			AddChildText(box,
				listening ? "…" : KeyBindingManager.GetKeyDisplay(KeyBindingManager.GetKey(action)),
				26, listening ? kOnColor : kPrimaryText, TextAnchor.MiddleCenter);

			// 改绑按钮
			AddListButton(row, listening ? "按下新键…" : "改绑", kKeyButtonWidth, kKeyControlHeight,
				listening ? kOnColor : kPrimaryText, () => BeginListening(action));
		}

		/// <summary>最后一行：全部恢复默认</summary>
		private void AddResetKeysRow(RectTransform content)
		{
			var row = AddRowContainer(content, "KeyReset", kKeyRowHeight, expandChildWidth: false);
			AddFlexibleText(row, "全部改回默认键位", 28, kPrimaryText);
			AddListButton(row, "恢复默认", kKeyButtonWidth, kKeyControlHeight, kPrimaryText, OnResetKeys);
		}

		/// <summary>点击「改绑」→ 进入监听态（重建列表，该行切换为「等待新按键」样式）</summary>
		private void BeginListening(GameInputAction action)
		{
			PlayClick();
			mListening = action;
			mKeyNoticeMessage = null;
			// 监听期间屏蔽局内按键轮询：正在改的这个键不能同时被当成释放指令
			KeyBindingManager.InputSuppressor = this;
			RefreshKeyBindingList();
		}

		/// <summary>退出监听态（只清状态，不重建；由调用方决定是否需要刷新列表）</summary>
		private void CancelListening()
		{
			mListening = null;
			mKeyNoticeMessage = null;
			KeyBindingManager.InputSuppressor = null;
		}

		/// <summary>把捕获到的按键应用到当前监听的动作（失败时保持监听，允许直接换一个键）</summary>
		private void ApplyCapturedKey(KeyCode key)
		{
			if (mListening == null) return;
			var action = mListening.Value;

			switch (KeyBindingManager.SetKey(action, key, out var conflict, out var reason))
			{
				case KeyBindingManager.BindResult.Success:
					PlayClick();
					CancelListening();   // 同时解除按键轮询屏蔽
					RefreshKeyBindingList();
					return;

				case KeyBindingManager.BindResult.Conflict:
					ShowKeyNotice($"{KeyBindingManager.GetKeyDisplay(key)} 已被「{KeyBindingManager.GetActionName(conflict.Value)}」占用，请换一个按键", true);
					return;

				case KeyBindingManager.BindResult.Reserved:
					ShowKeyNotice($"{KeyBindingManager.GetKeyDisplay(key)} 是「{reason}」的按键，不能用于改绑", true);
					return;

				default:
					ShowKeyNotice("该按键不可用，请换一个按键", true);
					return;
			}
		}

		/// <summary>把提示写到列表顶部那一行（isError 为 true 时变红）</summary>
		private void ShowKeyNotice(string message, bool isError)
		{
			mKeyNoticeMessage = message;
			if (mKeyNotice == null) return;
			mKeyNotice.text = message;
			mKeyNotice.color = isError ? kDangerText : kSecondaryText;
		}

		private void OnResetKeys()
		{
			PlayClick();
			KeyBindingManager.ResetAll();
			CancelListening();
			RefreshKeyBindingList();
		}

		/// <summary>操作页内容根：按名字逐级查找（Control > Viewport > Content）</summary>
		private RectTransform ResolveControlContent()
		{
			if (mControlContent != null) return mControlContent;

			var page = FindChild(transform, "Control");
			if (page == null) return null;

			var viewport = FindChild(page, "Viewport");
			if (viewport == null) return null;

			mControlContent = FindChild(viewport, "Content") as RectTransform;
			return mControlContent;
		}

		// ===== 运行时 UI 构建小工具 =====

		/// <summary>加一行文本；preferredHeight > 0 时用固定行高，否则交给父布局按字号算</summary>
		private static Text AddListText(Transform parent, string content, int fontSize, Color color, float preferredHeight)
		{
			var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
			go.transform.SetParent(parent, false);

			var text = go.GetComponent<Text>();
			text.font = BuiltinFont();
			text.fontSize = fontSize;
			text.color = color;
			text.text = content;
			text.alignment = TextAnchor.MiddleLeft;
			text.raycastTarget = false;
			text.horizontalOverflow = HorizontalWrapMode.Wrap;
			text.verticalOverflow = VerticalWrapMode.Truncate;

			if (preferredHeight > 0f)
				go.AddComponent<LayoutElement>().preferredHeight = preferredHeight;

			return text;
		}

		private static Font BuiltinFont() => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

		/// <summary>占满剩余宽度的文本（列表行左侧标题）</summary>
		private static Text AddFlexibleText(Transform parent, string content, int fontSize, Color color)
		{
			var text = AddListText(parent, content, fontSize, color, 0f);

			var le = text.gameObject.GetComponent<LayoutElement>();
			if (le == null) le = text.gameObject.AddComponent<LayoutElement>();
			le.flexibleWidth = 1f;
			le.minWidth = 200f;
			return text;
		}

		/// <summary>铺满父节点的文本（方块 / 按钮内的居中文字；不参与父布局 sizing）</summary>
		private static Text AddChildText(Transform parent, string content, int fontSize, Color color, TextAnchor align)
		{
			var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;

			var text = go.GetComponent<Text>();
			text.font = BuiltinFont();
			text.fontSize = fontSize;
			text.color = color;
			text.text = content;
			text.alignment = align;
			text.raycastTarget = false;
			text.horizontalOverflow = HorizontalWrapMode.Overflow;
			text.verticalOverflow = VerticalWrapMode.Overflow;
			return text;
		}

		/// <summary>列表行右侧的按钮（切片底板 + 居中文字），返回其文字节点便于后续改文案</summary>
		private static Text AddListButton(Transform parent, string label, float width, float height, Color textColor, System.Action onClick)
		{
			var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(Button));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);

			var img = go.GetComponent<Image>();
			img.sprite = RowBoxSprite();
			img.type = Image.Type.Sliced;
			img.color = new Color(1f, 1f, 1f, 0.22f);
			img.raycastTarget = true;

			var le = go.GetComponent<LayoutElement>();
			le.preferredWidth = width;
			le.minWidth = width;
			le.preferredHeight = height;
			le.minHeight = height;
			le.flexibleWidth = 0f;

			var button = go.GetComponent<Button>();
			button.targetGraphic = img;
			button.onClick.AddListener(() => onClick?.Invoke());

			return AddChildText(rt, label, 22, textColor, TextAnchor.MiddleCenter);
		}

		/// <summary>行内底板（键位方块 / 按钮）的圆角 9 切片贴图（跨实例共享，只在首次创建）</summary>
		private static Sprite s_rowBoxSprite;

		/// <summary>
		/// 行内底板的圆角 9 切片精灵（跨实例共享，首次创建）。
		/// ⚠ 不能用 Resources.GetBuiltinResource&lt;Sprite&gt;("UI/Skin/UISprite.psd")：UI/Skin/* 属内置额外资源，
		/// 只存在于编辑器，运行时按名取不到并刷错误日志（与 Default-Particle.psd 同类问题）。
		/// 故改为程序化生成，编辑器与打包后表现一致；预制体里的序列化底板引用不走这条路径。
		/// </summary>
		private static Sprite RowBoxSprite()
		{
			if (s_rowBoxSprite != null) return s_rowBoxSprite;

			const int size = 32;        // 贴图边长（像素）
			const float radius = 8f;    // 圆角半径（像素）
			const float border = 10f;   // 9 切片边距（像素）；画布参考 PPU = 100，贴图 PPU 也取 100 → 1:1 呈现

			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
			{
				name = "SettingRowBox",
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
			};

			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					// 到最近两条边的距离；两者都落在圆角半径内 → 用「到圆角圆心」的距离做 1px 柔边
					float edgeX = Mathf.Min(x + 0.5f, size - (x + 0.5f));
					float edgeY = Mathf.Min(y + 0.5f, size - (y + 0.5f));

					float alpha = 1f;
					if (edgeX < radius && edgeY < radius)
					{
						float dx = radius - edgeX;
						float dy = radius - edgeY;
						alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
					}

					texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
				}
			}
			texture.Apply();

			s_rowBoxSprite = Sprite.Create(
				texture,
				new Rect(0f, 0f, size, size),
				new Vector2(0.5f, 0.5f),
				100f,
				0,
				SpriteMeshType.FullRect,
				new Vector4(border, border, border, border));
			s_rowBoxSprite.name = "SettingRowBox";
			return s_rowBoxSprite;
		}

		/// <summary>深度优先按名字查找子节点（不包含自身）</summary>
		private static Transform FindChild(Transform parent, string nodeName)
		{
			for (int i = 0; i < parent.childCount; i++)
			{
				var child = parent.GetChild(i);
				if (child.name == nodeName) return child;
				var found = FindChild(child, nodeName);
				if (found != null) return found;
			}
			return null;
		}

		private T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
		}

		/// <summary>取节点上的组件（节点为空返回 null，省掉调用处的判空）</summary>
		private static T ComponentAt<T>(Transform node) where T : Component
			=> node != null ? node.GetComponent<T>() : null;
	}
}
