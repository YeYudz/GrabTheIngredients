using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class HudPanelData : IUIData
	{
	}

	/// <summary>
	/// 战斗内 HUD：玩家状态、波次进度、武器与技能冷却、BOSS 战况的只读展示。
	/// 纯显示层——不持有业务状态，数值单向流入，交互只以事件形式上抛，使 HUD 与战斗逻辑可独立演化。
	/// 事件订阅随面板显隐，隐藏期间不接收回调。
	/// </summary>
	public partial class HudPanel : UIPanel
	{
		private Coroutine mHudUpdateCoroutine;
		private Coroutine mInfoClearCoroutine;

		// 武器列表运行时从 WeaponRoot 解析；3 槽同时生效，无切换/高亮概念
		// 自动释放开关已移至设置面板-游戏页签（MetaManager.SetAutoCastEnabled）
		private List<Image> mWeaponIcons = new();
		private List<Image> mWeaponMasks = new();
		private List<Text> mWeaponTexts = new();       // 冷却倒计时；与武器槽同序

		/// <summary>iconPath → Sprite 缓存；未命中也记录 null，避免对缺失资源反复 Resources.Load</summary>
		private static readonly Dictionary<string, Sprite> s_weaponIconCache = new();

		/// <summary>技能图标 iconPath → Sprite 缓存，与武器图标同策略</summary>
		private static readonly Dictionary<string, Sprite> s_skillIconCache = new();

		// 主动技能列表：按战前技能组在 SkillRoot 下动态构建
		private List<string> mSkillIds = new();
		private List<Image> mSkillMasks = new();
		private List<Text> mSkillTexts = new();
		private List<GameObject> mDynamicSkillNodes = new();

		// 闪避为固有技能，独立于主动技能列表，位于 SkillRoot 最右侧
		private Image mDodgeMask;
		private Text mDodgeText;
		private GameObject mDodgeNode;

		// 本局计时基准由 GameManager 持有：断点恢复时回退到"波次开局的已用时长"

		// 平滑进度：target 为业务值、display 为显示值，每帧收敛（见 UpdateHudUI）
		private float mHpTarget, mHpDisplay;
		private float mShieldTarget, mShieldDisplay;   // 护盾占比（护盾/最大生命），随血条一起平滑
		private float mManaTarget, mManaDisplay;
		private float mExpTarget, mExpDisplay;
		private float mBossHpTarget, mBossHpDisplay;
		private float mProgressTarget, mProgressDisplay;

		/// <summary>订阅随面板显隐，用于保证显隐切换幂等</summary>
		private bool mSubscribed;

		/// <summary>按键说明已应用的 KeyBindingManager 版本（变化才重建文案）</summary>
		private int mKeyHintVersion = -1;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as HudPanelData ?? new HudPanelData();

			// 继续/设置/退出本局的处理都在 PausePanel 内，HUD 只负责打开
			if (mPauseButton != null)
			{
				mPauseButton.onClick.RemoveAllListeners();
				mPauseButton.onClick.AddListener(OnPausePressed);
			}

			// 键位可在设置面板 Control 页签改绑，故每次显示都要重刷
			RefreshKeyDescription();
		}

		/// <summary>按当前键位重建按键说明文本</summary>
		private void RefreshKeyDescription()
		{
			if (mKeyDescription == null) return;
			mKeyDescription.text = KeyBindingManager.BuildKeyHintText();
		}

		// 槽位键位提示：各槽位图标上的 ChangeInput

		/// <summary>
		/// 把各槽位图标上的键位提示刷成当前键位。
		/// 现场按名扫描而不缓存引用：槽节点会被重建（换装备、重开一局、兜底动态创建），
		/// 缓存的 Text 会指向已销毁对象。
		/// </summary>
		private void RefreshSlotKeyHints()
		{
			RefreshSlotKeyHintsIn(mWeaponRoot, isWeaponRoot: true);
			RefreshSlotKeyHintsIn(mSkillRoot, isWeaponRoot: false);
		}

		/// <summary>刷新某根节点下各槽位的键位提示；槽位命名口径须与 BuildWeaponList / FindOrCreateSkillNode 一致</summary>
		private static void RefreshSlotKeyHintsIn(RectTransform root, bool isWeaponRoot)
		{
			if (root == null) return;

			for (int i = 0; i < root.childCount; i++)
			{
				var slot = root.GetChild(i);
				var action = ResolveSlotAction(slot.name, isWeaponRoot);
				if (action == null) continue;

				// 预制体已为每个槽位预置 ChangeInput；兜底动态创建的槽位才需要补建
				var text = FindChildText(slot, "ChangeInput");
				if (text == null) text = CreateChangeInput(slot);
				if (text == null) continue;

				text.text = KeyBindingManager.GetKeyDisplay(KeyBindingManager.GetKey(action.Value));
			}
		}

		/// <summary>槽位节点名 → 对应的可改绑动作（null = 该槽位没有可改绑按键）</summary>
		private static GameInputAction? ResolveSlotAction(string nodeName, bool isWeaponRoot)
		{
			if (nodeName == "Dodge") return GameInputAction.Dodge;

			string prefix = isWeaponRoot ? "Weapon_" : "Skill_";
			if (!nodeName.StartsWith(prefix)) return null;
			if (!int.TryParse(nodeName.Substring(prefix.Length), out int index)) return null;

			if (isWeaponRoot)
			{
				switch (index)
				{
					case 0: return GameInputAction.WeaponSkill1;
					case 1: return GameInputAction.WeaponSkill2;
					case 2: return GameInputAction.WeaponSkill3;
				}
			}
			else
			{
				switch (index)
				{
					case 0: return GameInputAction.Skill1;
					case 1: return GameInputAction.Skill2;
				}
			}
			return null;
		}

		/// <summary>兜底补建键位提示文字，样式对齐预制体的 Skill 槽</summary>
		private static Text CreateChangeInput(Transform slot)
		{
			var go = new GameObject("ChangeInput", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(slot, false);
			rt.anchorMin = new Vector2(0.5f, 0.5f);
			rt.anchorMax = new Vector2(0.5f, 0.5f);
			rt.pivot = new Vector2(0.5f, 0.5f);
			rt.anchoredPosition = new Vector2(0f, -60f);
			rt.sizeDelta = new Vector2(80f, 40f);

			var text = go.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 25;
			text.alignment = TextAnchor.MiddleCenter;
			text.color = Color.white;
			text.raycastTarget = false;   // 不挡槽位点击
			return text;
		}

		/// <summary>
		/// 订阅战斗事件；幂等。
		/// 订阅随面板显隐：关闭后不再接收事件，避免对 inactive 面板回调。
		/// </summary>
		private void SubscribeEvents()
		{
			if (mSubscribed) return;
			mSubscribed = true;

			EventKit.Type.Register<GameEvents.PlayerDamaged>(OnPlayerDamaged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.PlayerExpChanged>(OnPlayerExpChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.WaveStarted>(OnWaveStarted)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.EnemyKilled>(OnEnemyKilled)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.PlayerLevelUp>(OnPlayerLevelUp)
				.UnRegisterWhenGameObjectDestroyed(gameObject);

			EventKit.Type.Register<GameEvents.StreakChanged>(OnStreakChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.WeaponLoadoutChanged>(OnWeaponLoadoutChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.UpgradeSelected>(OnUpgradeSelected)
				.UnRegisterWhenGameObjectDestroyed(gameObject);

			EventKit.Type.Register<GameEvents.BossSpawned>(OnBossSpawned)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.BossPhaseChanged>(OnBossPhaseChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.BossDied>(OnBossDied)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.BossRevived>(OnBossRevived)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
			EventKit.Type.Register<GameEvents.BossHealthChanged>(OnBossHealthChanged)
				.UnRegisterWhenGameObjectDestroyed(gameObject);
		}

		/// <summary>取消订阅；与 SubscribeEvents 成对</summary>
		private void UnsubscribeEvents()
		{
			if (!mSubscribed) return;
			mSubscribed = false;

			EventKit.Type.UnRegister<GameEvents.PlayerDamaged>(OnPlayerDamaged);
			EventKit.Type.UnRegister<GameEvents.PlayerExpChanged>(OnPlayerExpChanged);
			EventKit.Type.UnRegister<GameEvents.WaveStarted>(OnWaveStarted);
			EventKit.Type.UnRegister<GameEvents.EnemyKilled>(OnEnemyKilled);
			EventKit.Type.UnRegister<GameEvents.PlayerLevelUp>(OnPlayerLevelUp);

			EventKit.Type.UnRegister<GameEvents.StreakChanged>(OnStreakChanged);
			EventKit.Type.UnRegister<GameEvents.WeaponLoadoutChanged>(OnWeaponLoadoutChanged);
			EventKit.Type.UnRegister<GameEvents.UpgradeSelected>(OnUpgradeSelected);

			EventKit.Type.UnRegister<GameEvents.BossSpawned>(OnBossSpawned);
			EventKit.Type.UnRegister<GameEvents.BossPhaseChanged>(OnBossPhaseChanged);
			EventKit.Type.UnRegister<GameEvents.BossDied>(OnBossDied);
			EventKit.Type.UnRegister<GameEvents.BossRevived>(OnBossRevived);
			EventKit.Type.UnRegister<GameEvents.BossHealthChanged>(OnBossHealthChanged);
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as HudPanelData ?? mData;

			// 与 OnClose 的取消订阅成对；mSubscribed 防重复
			SubscribeEvents();
		}

		protected override void OnShow()
		{
			// inactive 的 GameObject 无法启动协程
			if (!gameObject.activeInHierarchy) return;

			// HUD 铺满屏幕：只淡入不缩放（缩放会让布局与安全区一起变形）
			UiPanelTween.PlayIn(this, 0.25f, false);

			SubscribeEvents();

			// 面板显示时从业务层拉取一次当前值，避免订阅前的状态丢失
			RefreshAll();

			// 断点恢复：BossSpawned 可能早于本面板订阅，按 BossHealth.Current 兜底
			RefreshBossUiFromCurrent();

			// 首帧同步平滑显示值 = 目标值（避免打开时从 0 填充的过渡动画）
			RefreshShieldBar();
			mHpDisplay = mHpTarget;
			mShieldDisplay = mShieldTarget;
			mManaDisplay = mManaTarget;
			mExpDisplay = mExpTarget;
			mBossHpDisplay = mBossHpTarget;
			mProgressDisplay = WaveManager.HasInstance ? WaveManager.Instance.WaveKillProgress : 0f;

			BuildWeaponList();
			AssignWeaponIcons();

			BuildSkillList();

			BuildDodgeButton();

			// 顺序依赖：槽节点可能在上面被重建，键位提示须在构建之后刷
			RefreshSlotKeyHints();

			if (mHudUpdateCoroutine != null) StopCoroutine(mHudUpdateCoroutine);
			mHudUpdateCoroutine = StartCoroutine(UpdateHudUI());
		}

		protected override void OnHide()
		{
			UnsubscribeEvents();
		}

		protected override void OnClose()
		{
			UnsubscribeEvents();

			if (mHudUpdateCoroutine != null)
			{
				StopCoroutine(mHudUpdateCoroutine);
				mHudUpdateCoroutine = null;
			}
			if (mInfoClearCoroutine != null)
			{
				StopCoroutine(mInfoClearCoroutine);
				mInfoClearCoroutine = null;
			}
			ClearSkillList();
		}

		/// <summary>
		/// 打开暂停面板；继续/设置/退出本局、timeScale 冻结与断点保存均由 PausePanel 负责。
		/// </summary>
		private void OnPausePressed()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			UIKit.OpenPanel<PausePanel>(UILevel.Pop);
		}

		/// <summary>
		/// ESC 与 C 两个全局快捷键的响应。
		/// 以 timeScale > 0 把响应限制在正常游玩中，避免打断升级选择 / 烹饪音游等暂停态流程。
		/// </summary>
		private void Update()
		{
			// 仅在键位版本变化时重建，避免逐帧开销
			if (mKeyHintVersion != KeyBindingManager.Version)
			{
				mKeyHintVersion = KeyBindingManager.Version;
				RefreshKeyDescription();
				RefreshSlotKeyHints();
			}

			if (Time.timeScale > 0f && Input.GetKeyDown(KeyCode.Escape))
			{
				OnPausePressed();
			}
			if (Time.timeScale > 0f && Input.GetKeyDown(KeyCode.C))
			{
				if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
				// Persistent：关闭仅隐藏并保留实例，不进有界 LRU，避免反复实例化的开销
				UIKit.OpenPanel<StatusPanel>(UILevel.Pop, cachePolicy: PanelCachePolicy.Persistent);
			}
		}

		private void OnPlayerDamaged(GameEvents.PlayerDamaged evt)
		{
			SetHp(evt.CurrentHp, evt.MaxHp);
		}

		private void OnPlayerExpChanged(GameEvents.PlayerExpChanged evt)
		{
			SetExp(evt.Exp, evt.ExpToNext);
			// 经验变化时同步刷新等级（PlayerStats 升级后会先扣经验再发事件）
			RefreshLevelText();
		}

		private void OnPlayerLevelUp(GameEvents.PlayerLevelUp evt)
		{
			if (mLevelText != null)
				mLevelText.text = $"Lv.{evt.NewLevel}";
			ShowInfo($"升级！Lv.{evt.NewLevel}", new Color(1f, 0.85f, 0f));
		}

		private void OnWaveStarted(GameEvents.WaveStarted evt)
		{
			if (mWaveText != null)
				mWaveText.text = BuildWaveText();
		}

		/// <summary>波次显示文本：阶段模式显示"阶段 N/M · 第 X 波"（X 为本关累计波次序号）</summary>
		private string BuildWaveText()
		{
			if (!WaveManager.HasInstance) return "";
			var wm = WaveManager.Instance;
			return wm.IsPhaseMode
				? $"阶段 {wm.CurrentPhaseIndex + 1}/{wm.PhaseCount} · 第 {wm.DungeonWaveOrdinal} 波"
				: $"第 {wm.CurrentWave} 波";
		}

		private void OnEnemyKilled(GameEvents.EnemyKilled evt)
		{
			// 击杀数由业务层 DropManager 统计
			if (mKillText != null && DropManager.Instance != null)
				mKillText.text = $"击杀: {DropManager.Instance.KillCount}";
		}

		private void OnStreakChanged(GameEvents.StreakChanged evt)
		{
			if (evt.Count <= 0)
			{
				SetStreakUI(false);
				return;
			}

			SetStreakUI(true);
			if (mKillStreakText != null)
				mKillStreakText.text = $"连杀 x{evt.Count}";

			// 计时条 FillAmount 由 1 递减到 0，对应连杀窗口的剩余时间
			if (mStreakTimeBar != null)
			{
				float ratio = evt.Duration <= 0f ? 0f : Mathf.Clamp01(evt.RemainTime / evt.Duration);
				mStreakTimeBar.fillAmount = ratio;
			}
		}

		/// <summary>连杀 UI 显隐；StreakTimeBack 默认隐藏，仅在连杀窗口内出现</summary>
		private void SetStreakUI(bool visible)
		{
			if (mStreakTimeBack != null) mStreakTimeBack.SetActive(visible);
		}

		private void OnWeaponLoadoutChanged(GameEvents.WeaponLoadoutChanged evt)
		{
			BuildWeaponList();
			AssignWeaponIcons();
		}

		private void OnUpgradeSelected(GameEvents.UpgradeSelected evt)
		{
			var buff = GameDataMgr.Instance.GetBuffUpgrade(evt.BuffId);
			string name = buff != null ? buff.buffName : evt.BuffId;
			ShowInfo($"获得强化：{name}", new Color(0.3f, 1f, 0.3f));
		}

		private void RefreshAll()
		{
			var player = GameManager.Instance?.Player;
			if (player != null && player.Health != null)
			{
				// 上限取聚合属性（角色+菜品+局内 Buff），不再用 PlayerConfig
				SetHp(player.Health.CurrentHp, player.MaxHp);
			}

			var stats = player != null ? player.GetComponent<PlayerStats>() : null;
			if (stats != null)
			{
				SetExp(stats.Exp, stats.ExpToNext);
				if (mLevelText != null) mLevelText.text = $"Lv.{stats.Level}";
			}

			if (mWaveText != null)
				mWaveText.text = BuildWaveText();
			if (mKillText != null && DropManager.Instance != null)
				mKillText.text = $"击杀: {DropManager.Instance.KillCount}";

			RefreshRoleIcon();

			bool hasStreak = DropManager.HasInstance && DropManager.Instance.StreakCount > 0;
			SetStreakUI(hasStreak);
			if (hasStreak)
			{
				var dm = DropManager.Instance;
				if (mKillStreakText != null) mKillStreakText.text = $"连杀 x{dm.StreakCount}";
				if (mStreakTimeBar != null)
				{
					float ratio = dm.StreakDuration <= 0f ? 0f : Mathf.Clamp01(dm.StreakRemainTime / dm.StreakDuration);
					mStreakTimeBar.fillAmount = ratio;
				}
			}
			else
			{
				if (mKillStreakText != null) mKillStreakText.text = "";
				if (mStreakTimeBar != null) mStreakTimeBar.fillAmount = 0f;
			}
		}

		/// <summary>从 PlayerStats 读取当前等级并刷新文本</summary>
		private void RefreshLevelText()
		{
			var player = GameManager.Instance?.Player;
			var stats = player != null ? player.GetComponent<PlayerStats>() : null;
			if (stats != null && mLevelText != null)
				mLevelText.text = $"Lv.{stats.Level}";
		}

		/// <summary>刷新角色头像：按 RosterManager 当前出战角色加载 iconPath（无角色/图标时隐藏）</summary>
		private void RefreshRoleIcon()
		{
			if (mRoleIcon == null) return;

			var character = RosterManager.HasInstance
				? RosterManager.Instance.GetCurrentCharacter() : null;
			var sprite = character != null && !string.IsNullOrEmpty(character.iconPath)
				? Resources.Load<Sprite>(character.iconPath) : null;

			mRoleIcon.gameObject.SetActive(sprite != null);
			if (sprite != null) mRoleIcon.sprite = sprite;
		}

		private void SetHp(float current, float max)
		{
			float ratio = max <= 0f ? 0f : Mathf.Clamp01(current / max);
			mHpTarget = ratio;
			// 文本立即刷新，fillAmount 由每帧 lerp 平滑（见 UpdateHudUI）
			if (mHpText != null) mHpText.text = $"{Mathf.CeilToInt(current)}/{Mathf.CeilToInt(max)}";
		}

		private void SetExp(int exp, int expToNext)
		{
			float ratio = expToNext <= 0 ? 0f : Mathf.Clamp01((float)exp / expToNext);
			mExpTarget = ratio;
		}

		/// <summary>预留：法力消耗待技能系统接入后由事件驱动</summary>
		private void SetMana(float current, float max)
		{
			float ratio = max <= 0f ? 0f : Mathf.Clamp01(current / max);
			mManaTarget = ratio;
			if (mManaText != null) mManaText.text = $"{Mathf.CeilToInt(current)}/{Mathf.CeilToInt(max)}";
		}

		/// <summary>播报一条战斗信息，3 秒后淡出</summary>
		private void ShowInfo(string msg, Color color)
		{
			// 面板已关闭（inactive）时不响应：协程无法在 inactive 的 GameObject 上启动
			if (mInfoText == null || !gameObject.activeInHierarchy) return;

			mInfoText.text = msg;
			mInfoText.color = color;
			mInfoText.gameObject.SetActive(true);

			if (mInfoClearCoroutine != null) StopCoroutine(mInfoClearCoroutine);
			mInfoClearCoroutine = StartCoroutine(ClearInfoAfter(3f));
		}

		private IEnumerator ClearInfoAfter(float delay)
		{
			yield return new WaitForSeconds(delay);
			if (mInfoText != null) mInfoText.gameObject.SetActive(false);
		}

		/// <summary>按 Weapon_0/1/2 解析武器槽：槽自身 Image 为图标，子节点 Filled Image 为冷却遮罩</summary>
		private void BuildWeaponList()
		{
			mWeaponIcons.Clear();
			mWeaponMasks.Clear();
			mWeaponTexts.Clear();
			if (mWeaponRoot == null) return;

			for (int i = 0; i < mWeaponRoot.childCount; i++)
			{
				var child = mWeaponRoot.GetChild(i);
				if (child.name != $"Weapon_{i}") continue;

				var icon = child.GetComponent<Image>();
				mWeaponIcons.Add(icon);

				Image mask = null;
				for (int j = 0; j < child.childCount; j++)
				{
					var img = child.GetChild(j).GetComponent<Image>();
					if (img != null && img.type == Image.Type.Filled)
					{
						mask = img;
						break;
					}
				}
				mWeaponMasks.Add(mask);

				mWeaponTexts.Add(BindCooldownText(child));

				// 点击只上报释放请求；自动释放开关在设置面板-游戏页签
				WireWeaponButton(child, i);
			}
		}

		/// <summary>按战前装备给武器槽赋图标；空槽整只隐藏</summary>
		private void AssignWeaponIcons()
		{
			var slots = RosterManager.HasInstance
				? RosterManager.Instance.GetCurrentWeaponIds()
				: new List<string>();

			for (int i = 0; i < mWeaponIcons.Count; i++)
			{
				if (mWeaponIcons[i] == null) continue;

				string id = i < slots.Count ? slots[i] : null;
				var cfg = string.IsNullOrEmpty(id) ? null : GameDataMgr.Instance.GetWeapon(id);

				if (cfg == null)
				{
					mWeaponIcons[i].sprite = null;
					// 空槽整只隐藏（子物体一并消失）；图标恢复不透明，避免装回后残留半透明
					var emptyColor = mWeaponIcons[i].color;
					emptyColor.a = 1f;
					mWeaponIcons[i].color = emptyColor;
					SetSlotVisible(mWeaponIcons[i].gameObject, false);
					continue;
				}

				if (!string.IsNullOrEmpty(cfg.iconPath))
				{
					// 未命中才 Load；找不到也缓存 null，避免反复尝试
					if (!s_weaponIconCache.TryGetValue(cfg.iconPath, out var spr))
					{
						spr = Resources.Load<Sprite>(cfg.iconPath);
						s_weaponIconCache[cfg.iconPath] = spr;
					}
					if (spr != null) mWeaponIcons[i].sprite = spr;
				}

				// 缺蓝半透明由 RefreshWeaponActives 每帧覆盖，此处仅复位
				SetSlotVisible(mWeaponIcons[i].gameObject, true);
				var activeColor = mWeaponIcons[i].color;
				activeColor.a = 1f;
				mWeaponIcons[i].color = activeColor;
			}
		}

		/// <summary>
		/// 武器槽点击只上报释放请求，冷却/法力/禁用校验均在 WeaponActiveSystem；
		/// 自动释放开关已移至设置面板-游戏页签，HUD 不再提供。
		/// </summary>
		private void WireWeaponButton(Transform node, int index)
		{
			var button = node.GetComponent<Button>();
			if (button == null) button = node.gameObject.AddComponent<Button>();
			// 槽位按钮是运行时补的（可能晚于面板打开），这里单独补悬停反馈
			UiHoverInstaller.Ensure(button);
			button.onClick.RemoveAllListeners();
			button.onClick.AddListener(() =>
			{
				if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
				EventKit.Type.Send(new GameEvents.WeaponActiveRequested { WeaponIndex = index });
			});
		}

		/// <summary>每帧刷新武器主动：冷却遮罩 / 缺蓝半透明</summary>
		private void RefreshWeaponActives()
		{
			if (!WeaponActiveSystem.HasInstance) return;
			var system = WeaponActiveSystem.Instance;

			for (int i = 0; i < mWeaponIcons.Count; i++)
			{
				var skill = system.GetSkill(i);
				if (skill == null)   // 空槽（无武器主动）不处理
				{
					// 但要清掉倒计时文字，避免残留上一局的数字
					if (i < mWeaponTexts.Count && mWeaponTexts[i] != null) mWeaponTexts[i].text = "";
					continue;
				}

				float total = system.GetCooldownTotal(i);
				float remain = system.GetCooldownRemain(i);

				if (i < mWeaponMasks.Count && mWeaponMasks[i] != null)
					mWeaponMasks[i].fillAmount = total > 0f ? Mathf.Clamp01(remain / total) : 0f;

				if (i < mWeaponTexts.Count && mWeaponTexts[i] != null)
					mWeaponTexts[i].text = remain > 0f ? Mathf.CeilToInt(remain).ToString() : "";

				// 缺蓝表现：图标半透明；空槽的 CanvasGroup 由 AssignWeaponIcons 维护，此处不处理
				// 约束：GetSkill(i) 非空即代表该槽有主动技能，不可用 system.Count 当槽位数判断
				if (mWeaponIcons[i] != null)
				{
					var c = mWeaponIcons[i].color;
					c.a = system.IsManaEnough(i) ? 1f : 0.35f;
					mWeaponIcons[i].color = c;
				}
			}
		}

		/// <summary>
		/// 按战前技能组在 SkillRoot 下构建技能按钮，只负责显示与点击上报。
		/// 槽位顺序须与 RosterManager.GetCurrentSkillIds() 一致。
		/// </summary>
		private void BuildSkillList()
		{
			ClearSkillList();
			if (mSkillRoot == null) return;

			int count = ActiveSkillSystem.HasInstance ? ActiveSkillSystem.Instance.SkillCount : 0;

			// 未装配的技能槽整只隐藏并清空图标；闪避为固有技能，始终可见
			for (int i = 0; i < mSkillRoot.childCount; i++)
			{
				var child = mSkillRoot.GetChild(i);
				if (!child.name.StartsWith("Skill_")) continue;
				SetSlotVisible(child.gameObject, false);
				SetSkillIcon(child, null);   // 清空：未装配的槽位不残留上一局/上一个技能的图标
				var emptyCd = FindChildText(child, "CooldownText");
				if (emptyCd != null) emptyCd.text = "";   // 同时清掉倒计时数字
			}

			for (int i = 0; i < count; i++)
			{
				var cfg = ActiveSkillSystem.Instance.GetSkill(i);
				if (cfg == null) continue;

				var node = FindOrCreateSkillNode(i, cfg.skillName);
				if (node == null) continue;

				SetSlotVisible(node.gameObject, true);
				SetSkillIcon(node, cfg.iconPath);

				mSkillIds.Add(cfg.id);

				Image mask = null;
				for (int j = 0; j < node.childCount; j++)
				{
					var img = node.GetChild(j).GetComponent<Image>();
					if (img != null && img.type == Image.Type.Filled)
					{
						mask = img;
						break;
					}
				}
				mSkillMasks.Add(mask);

				// 冷却文字：节点下 CooldownText，预制体预置与运行时创建同名
				mSkillTexts.Add(BindCooldownText(node));

				// 点击只上报事件，执行在 ActiveSkillSystem
				string skillId = cfg.id;
				var button = node.GetComponent<Button>();
				if (button != null)
				{
					button.onClick.RemoveAllListeners();
					button.onClick.AddListener(() =>
					{
						if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
						EventKit.Type.Send(new GameEvents.ActiveSkillRequested { SkillId = skillId });
					});
				}
			}
		}

		/// <summary>查找或动态创建技能节点（缺失时创建 Button+Image+Filled遮罩+冷却Text）</summary>
		private RectTransform FindOrCreateSkillNode(int index, string skillName)
		{
			string nodeName = $"Skill_{index}";
			var exist = mSkillRoot.Find(nodeName);
			if (exist != null) return exist as RectTransform;

			var nodeGo = new GameObject(nodeName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var nodeRt = nodeGo.GetComponent<RectTransform>();
			nodeRt.SetParent(mSkillRoot, false);
			nodeRt.sizeDelta = new Vector2(70f, 70f);
			nodeRt.anchoredPosition = new Vector2(index * 90f, 0f);
			nodeGo.GetComponent<Image>().color = new Color(0.25f, 0.3f, 0.35f, 0.9f);

			// 冷却遮罩（Filled Image，全屏填充时遮住图标）
			var maskGo = new GameObject("Mask", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var maskRt = maskGo.GetComponent<RectTransform>();
			maskRt.SetParent(nodeRt, false);
			maskRt.anchorMin = Vector2.zero;
			maskRt.anchorMax = Vector2.one;
			maskRt.offsetMin = Vector2.zero;
			maskRt.offsetMax = Vector2.zero;
			var maskImg = maskGo.GetComponent<Image>();
			maskImg.type = Image.Type.Filled;
			maskImg.fillMethod = Image.FillMethod.Vertical;
			maskImg.fillOrigin = 1; // Top
			maskImg.fillAmount = 0f;
			maskImg.color = new Color(0f, 0f, 0f, 0.7f);

			var nameGo = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var nameRt = nameGo.GetComponent<RectTransform>();
			nameRt.SetParent(nodeRt, false);
			nameRt.anchorMin = Vector2.zero;
			nameRt.anchorMax = Vector2.one;
			nameRt.offsetMin = Vector2.zero;
			nameRt.offsetMax = Vector2.zero;
			var nameText = nameGo.GetComponent<Text>();
			nameText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			nameText.fontSize = 14;
			nameText.alignment = TextAnchor.MiddleCenter;
			nameText.color = Color.white;
			nameText.text = skillName;

			var cdGo = new GameObject("CooldownText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var cdRt = cdGo.GetComponent<RectTransform>();
			cdRt.SetParent(nodeRt, false);
			cdRt.anchorMin = Vector2.zero;
			cdRt.anchorMax = Vector2.one;
			cdRt.offsetMin = Vector2.zero;
			cdRt.offsetMax = Vector2.zero;
			var cdText = cdGo.GetComponent<Text>();
			cdText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			cdText.fontSize = 28;
			cdText.alignment = TextAnchor.MiddleCenter;
			cdText.color = Color.white;
			cdText.text = "";
			cdText.transform.SetAsLastSibling(); // 置为末位兄弟，确保画在图标与遮罩之上

			mDynamicSkillNodes.Add(nodeGo);
			return nodeRt;
		}

		/// <summary>每帧刷新技能冷却遮罩与倒计时文字（从 ActiveSkillSystem 轮询）</summary>
		private void RefreshSkillCooldown()
		{
			if (!ActiveSkillSystem.HasInstance) return;

			for (int i = 0; i < mSkillIds.Count; i++)
			{
				string id = mSkillIds[i];
				float remain = ActiveSkillSystem.Instance.GetCooldownRemain(id);
				float total = ActiveSkillSystem.Instance.GetCooldownTotal(id);

				if (i < mSkillMasks.Count && mSkillMasks[i] != null)
					mSkillMasks[i].fillAmount = total > 0f ? Mathf.Clamp01(remain / total) : 0f;
				if (i < mSkillTexts.Count && mSkillTexts[i] != null)
					mSkillTexts[i].text = remain > 0f ? Mathf.CeilToInt(remain).ToString() : "";
			}
		}

		/// <summary>清空技能列表（重建前/面板关闭时调用）</summary>
		private void ClearSkillList()
		{
			mSkillIds.Clear();
			mSkillMasks.Clear();
			mSkillTexts.Clear();

			// 销毁动态创建的节点（预制体预置的保留）
			foreach (var node in mDynamicSkillNodes)
			{
				if (node != null) Destroy(node);
			}
			mDynamicSkillNodes.Clear();
			mDodgeNode = null;
			mDodgeMask = null;
			mDodgeText = null;
		}

		/// <summary>
		/// 给技能槽节点赋图标：图标画在节点自身的 Image 上（与武器槽一致；Mask 冷却遮罩与键位提示是其子节点，天然盖在图标之上）。
		/// 空槽（iconPath 为空或资源缺失）置为全透明，避免残留上一个技能图标或渲染出白色方块。
		/// </summary>
		private static void SetSkillIcon(Transform node, string iconPath)
		{
			if (node == null) return;
			var img = node.GetComponent<Image>();
			if (img == null) return;

			Sprite spr = null;
			if (!string.IsNullOrEmpty(iconPath))
			{
					// 未命中才 Load；找不到也缓存 null，避免反复尝试
					if (!s_skillIconCache.TryGetValue(iconPath, out spr))
				{
					spr = Resources.Load<Sprite>(iconPath);
					s_skillIconCache[iconPath] = spr;
				}
			}

			img.sprite = spr;
			// 有图标 → 透明白（动态创建节点的深灰底板不应给图标染色）；空槽 → alpha 0
			img.color = spr != null ? Color.white : new Color(1f, 1f, 1f, 0f);
		}

		/// <summary>在 SkillRoot 内构建闪避按钮，位于主动技能右侧；点击只调用业务层施放</summary>
		private void BuildDodgeButton()
		{
			if (mSkillRoot == null) return;

			string nodeName = "Dodge";
			var exist = mSkillRoot.Find(nodeName);
			if (exist != null)
			{
				mDodgeNode = exist.gameObject;
			}
			else
			{
				int index = ActiveSkillSystem.HasInstance ? ActiveSkillSystem.Instance.SkillCount : 0;
				var nodeGo = new GameObject(nodeName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
				var nodeRt = nodeGo.GetComponent<RectTransform>();
				nodeRt.SetParent(mSkillRoot, false);
				nodeRt.sizeDelta = new Vector2(70f, 70f);
				nodeRt.anchoredPosition = new Vector2(index * 90f, 0f);
				nodeGo.GetComponent<Image>().color = new Color(0.2f, 0.25f, 0.35f, 0.9f);
				mDodgeNode = nodeGo;
				mDynamicSkillNodes.Add(nodeGo);
			}
			if (mDodgeNode == null) return;
			// 闪避为固有技能（非选择装配），始终显示
			SetSlotVisible(mDodgeNode, true);

			// 图标必刷：闪避不在战前装配列表里（ActiveSkillSystem 只装"携带"的主动技能），
			// 所以不能走 BuildSkillList 的赋图标流程，这里无条件赋一次固定图标
			SetSkillIcon(mDodgeNode.transform, AssetKeys.ICON_DODGE);
			var dodgeIcon = mDodgeNode.GetComponent<Image>();
			bool hasIcon = dodgeIcon != null && dodgeIcon.sprite != null;
			if (!hasIcon)
				LogKit.Warning($"[HudPanel] 闪避图标缺失：Resources/{AssetKeys.ICON_DODGE}");

			// 冷却遮罩（Filled Image，全屏填充时遮住图标）
			var maskImg = FindFilledChild(mDodgeNode.transform);
			if (maskImg == null)
			{
				var maskGo = new GameObject("Mask", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
				var maskRt = maskGo.GetComponent<RectTransform>();
				maskRt.SetParent(mDodgeNode.transform, false);
				maskRt.anchorMin = Vector2.zero;
				maskRt.anchorMax = Vector2.one;
				maskRt.offsetMin = Vector2.zero;
				maskRt.offsetMax = Vector2.zero;
				maskImg = maskGo.GetComponent<Image>();
				maskImg.type = Image.Type.Filled;
				maskImg.fillMethod = Image.FillMethod.Vertical;
				maskImg.fillOrigin = 1; // Top
				maskImg.fillAmount = 0f;
				maskImg.color = new Color(0f, 0f, 0f, 0.7f);
			}
			mDodgeMask = maskImg;

			// 名称文字只作图标缺失时的兜底：有图标就藏起来（与 Skill_0/1 一致，槽位只显示图标）
			var nameText = FindChildText(mDodgeNode.transform, "Name");
			if (nameText == null && !hasIcon)
			{
				var nameGo = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
				var nameRt = nameGo.GetComponent<RectTransform>();
				nameRt.SetParent(mDodgeNode.transform, false);
				nameRt.anchorMin = Vector2.zero;
				nameRt.anchorMax = Vector2.one;
				nameRt.offsetMin = Vector2.zero;
				nameRt.offsetMax = Vector2.zero;
				nameText = nameGo.GetComponent<Text>();
				nameText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
				nameText.fontSize = 14;
				nameText.alignment = TextAnchor.MiddleCenter;
				nameText.color = Color.white;
				nameText.text = "闪避";
			}
			if (nameText != null) nameText.gameObject.SetActive(!hasIcon);

			// 冷却倒计时文字（预制体已预置 CooldownText；缺失时才动态创建）
			var cdText = BindCooldownText(mDodgeNode.transform);
			if (cdText == null)
			{
				var cdGo = new GameObject("CooldownText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
				var cdRt = cdGo.GetComponent<RectTransform>();
				cdRt.SetParent(mDodgeNode.transform, false);
				cdRt.anchorMin = Vector2.zero;
				cdRt.anchorMax = Vector2.one;
				cdRt.offsetMin = Vector2.zero;
				cdRt.offsetMax = Vector2.zero;
				cdText = cdGo.GetComponent<Text>();
				cdText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
				cdText.fontSize = 28;
				cdText.alignment = TextAnchor.MiddleCenter;
				cdText.color = Color.white;
				cdText.raycastTarget = false; // 不挡槽位点击
				cdText.text = "";
				cdText.transform.SetAsLastSibling(); // 遮罩之上
			}
			mDodgeText = cdText;

			// 点击只调用 DodgeSkill.TryDodge；位移与冷却由业务层处理
			var button = mDodgeNode.GetComponent<Button>();
			if (button != null)
			{
				button.onClick.RemoveAllListeners();
				button.onClick.AddListener(() =>
				{
					if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
					var player = GameManager.Instance?.Player;
					var dodge = player != null ? player.GetComponent<DodgeSkill>() : null;
					if (dodge != null) dodge.TryDodge();
				});
			}
		}

		/// <summary>每帧刷新闪避冷却遮罩与倒计时（从 DodgeSkill 轮询）</summary>
		private void RefreshDodgeCooldown()
		{
			var player = GameManager.Instance?.Player;
			var dodge = player != null ? player.GetComponent<DodgeSkill>() : null;
			if (dodge == null) return;

			if (mDodgeMask != null)
				mDodgeMask.fillAmount = dodge.CooldownTotal > 0f ? Mathf.Clamp01(dodge.CooldownRemain / dodge.CooldownTotal) : 0f;
			if (mDodgeText != null)
				mDodgeText.text = dodge.CooldownRemain > 0f ? Mathf.CeilToInt(dodge.CooldownRemain).ToString() : "";
		}

		/// <summary>每帧刷新法力条（业务层 PlayerController 持有法力）</summary>
		private void RefreshMana()
		{
			var player = GameManager.Instance?.Player;
			if (player != null) SetMana(player.CurrentMana, player.MaxMana);
		}

		/// <summary>
		/// 槽位整体显隐：空槽直接隐藏 —— 冷却遮罩 / 倒计时文字 / 键位提示（ChangeInput）都是槽位的子物体，
		/// 随父节点一起消失，不需要逐个处理。
		/// </summary>
		private static void SetSlotVisible(GameObject slot, bool visible)
		{
			if (slot == null) return;
			slot.SetActive(visible);
			// 显示时复位历史半透明，避免槽位复用/重建后残留压暗
			if (visible) SetNodeAlpha(slot, 1f);
		}

		/// <summary>设置节点整体透明度（CanvasGroup 缺失时自动补齐；影响节点及其全部子物体）</summary>
		private static void SetNodeAlpha(GameObject node, float alpha)
		{
			if (node == null) return;
			var group = node.GetComponent<CanvasGroup>();
			if (group == null) group = node.AddComponent<CanvasGroup>();
			group.alpha = alpha;
		}

		/// <summary>查找子物体中的 Filled Image（冷却遮罩）</summary>
		private static Image FindFilledChild(Transform root)
		{
			for (int j = 0; j < root.childCount; j++)
			{
				var img = root.GetChild(j).GetComponent<Image>();
				if (img != null && img.type == Image.Type.Filled) return img;
			}
			return null;
		}

		/// <summary>查找指定名字的子 Text</summary>
		private static Text FindChildText(Transform root, string name)
		{
			for (int j = 0; j < root.childCount; j++)
			{
				var child = root.GetChild(j);
				if (child.name == name)
				{
					var comp = child.GetComponent<Text>();
					if (comp != null) return comp;
				}
			}
			return null;
		}

		/// <summary>
		/// 查找槽位下的冷却倒计时文字：优先精确名 CooldownText，再退化为任意含 "Cooldown" 的直接子 Text。
		/// 各槽位（Weapon_0/1/2、Skill_0/1、Dodge）均由预制体预置，命名统一。
		/// </summary>
		private static Text FindCooldownText(Transform slot)
		{
			if (slot == null) return null;

			var exact = FindChildText(slot, "CooldownText");
			if (exact != null) return exact;

			for (int j = 0; j < slot.childCount; j++)
			{
				var txt = slot.GetChild(j).GetComponent<Text>();
				if (txt != null && txt.name.Contains("Cooldown")) return txt;
			}
			return null;
		}

		/// <summary>
		/// 绑定槽位的冷却倒计时文字：清空预制体里的占位文本（空槽不残留数字）、关闭射线（不挡槽位点击）、
		/// 并置为最后一个子物体（保证数字画在图标与冷却遮罩之上）
		/// </summary>
		private static Text BindCooldownText(Transform slot)
		{
			var text = FindCooldownText(slot);
			if (text == null) return null;

			text.text = "";
			text.raycastTarget = false;
			text.transform.SetAsLastSibling();
			return text;
		}

		private void RefreshProgressBar()
		{
			if (mProgressBar == null) return;
			mProgressTarget = WaveManager.HasInstance ? WaveManager.Instance.WaveKillProgress : 0f;
		}

		// ===== 本局计时（TopMid） =====

		private void RefreshTimerText()
		{
			if (mTimerText == null) return;
			float start = GameManager.Instance != null ? GameManager.Instance.RunStartTime : 0f;
			int totalSec = Mathf.Max(0, Mathf.FloorToInt(Time.time - start));
			mTimerText.text = $"{totalSec / 60:00}:{totalSec % 60:00}";
		}

		// ===== 综合刷新协程（每帧） =====

		private IEnumerator UpdateHudUI()
		{
			while (true)
			{
				yield return null;
			SmoothBars();
			RefreshWeaponActives();
			RefreshSkillCooldown();
			RefreshDodgeCooldown();
			RefreshMana();
			RefreshTimerText();
			RefreshStreakTimer();
			}
		}

		/// <summary>每帧把各条 display 值向 target 收敛（lerp），再写回 fillAmount，实现平滑过渡</summary>
		private void SmoothBars()
		{
			const float speed = 8f; // 收敛速率（越大反应越快）
			float dt = Time.deltaTime;

			RefreshShieldBar();   // 护盾目标 = 每帧实时拉取（与 ProgressBar 同策略）

			mHpDisplay = Mathf.Lerp(mHpDisplay, mHpTarget, 1f - Mathf.Exp(-speed * dt));
			if (mHpFill != null) mHpFill.fillAmount = mHpDisplay;

			mShieldDisplay = Mathf.Lerp(mShieldDisplay, mShieldTarget, 1f - Mathf.Exp(-speed * dt));
			if (mShieldFill != null) mShieldFill.fillAmount = mShieldDisplay;

			mManaDisplay = Mathf.Lerp(mManaDisplay, mManaTarget, 1f - Mathf.Exp(-speed * dt));
			if (mManaFill != null) mManaFill.fillAmount = mManaDisplay;

			mExpDisplay = Mathf.Lerp(mExpDisplay, mExpTarget, 1f - Mathf.Exp(-speed * dt));
			if (mExpFill != null) mExpFill.fillAmount = mExpDisplay;

			mBossHpDisplay = Mathf.Lerp(mBossHpDisplay, mBossHpTarget, 1f - Mathf.Exp(-speed * dt));
			if (mBossHpFill != null) mBossHpFill.fillAmount = mBossHpDisplay;

			// ProgressBar 目标 = 每帧实时拉取（见 RefreshProgressBar），这里只做平滑收敛
			RefreshProgressBar();
			mProgressDisplay = Mathf.Lerp(mProgressDisplay, mProgressTarget, 1f - Mathf.Exp(-speed * dt));
			if (mProgressBar != null) mProgressBar.fillAmount = mProgressDisplay;
		}

		/// <summary>
		/// 护盾条：每帧从 PlayerEffectController 拉取护盾值，
		/// 占比 = 护盾 / 最大生命 → ShieldBar（Filled/Right）右端固定、从右向左延伸。
		/// 与 ProgressBar 同策略每帧实时拉取，覆盖获得/吸收/衰减所有变化路径。
		/// </summary>
		private void RefreshShieldBar()
		{
			var player = GameManager.Instance != null ? GameManager.Instance.Player : null;
			var effect = player != null ? player.GetComponent<PlayerEffectController>() : null;
			float shield = effect != null ? effect.Shield : 0f;
			float maxHp = player != null ? player.MaxHp : 0f;
			mShieldTarget = maxHp <= 0f ? 0f : Mathf.Clamp01(shield / maxHp);
		}

		// ===== 连杀计时条（每帧递减） =====

		/// <summary>
		/// 每帧从业务层拉取连杀剩余时间，驱动 StreakTimeBar 从 1 减到 0。
		/// 击杀事件只负责：显示/重置为 1/更新文本；递减必须靠每帧刷新。
		/// </summary>
		private void RefreshStreakTimer()
		{
			if (!DropManager.HasInstance) return;
			var dm = DropManager.Instance;

			// 无连杀 → 兜底隐藏（正常路径由 OnStreakChanged 触发隐藏）
			if (dm.StreakCount <= 0)
			{
				if (mStreakTimeBack != null && mStreakTimeBack.activeSelf)
					SetStreakUI(false);
				return;
			}

			if (mStreakTimeBar != null)
			{
				float ratio = dm.StreakDuration <= 0f ? 0f : Mathf.Clamp01(dm.StreakRemainTime / dm.StreakDuration);
				mStreakTimeBar.fillAmount = ratio;
			}
		}

		// ===== BOSS 战 UI =====

		/// <summary>从 BossHealth.Current 兜底恢复 BOSS UI（断点恢复时 BossSpawned 事件早于订阅触发）</summary>
		private void RefreshBossUiFromCurrent()
		{
			var boss = BossHealth.Current;
			if (boss == null || boss.IsDead)
			{
				if (mBossRoot != null) mBossRoot.SetActive(false);
				return;
			}

			if (mBossRoot != null) mBossRoot.SetActive(true);
			mBossHpTarget = boss.HpRatio;
			if (mBossHpText != null)
				mBossHpText.text = $"BOSS {Mathf.CeilToInt(boss.CurrentHp)}/{Mathf.CeilToInt(boss.MaxHp)}";
			if (mBossBreakBarFill != null) mBossBreakBarFill.fillAmount = boss.BreakBarRatio;
			var phase = boss.CurrentPhase;
			if (mBossPhaseText != null && phase != null)
				mBossPhaseText.text = $"阶段 {phase.phaseIndex + 1}";
		}

		private void OnBossSpawned(GameEvents.BossSpawned evt)
		{
			if (mBossRoot != null) mBossRoot.SetActive(true);
			mBossHpTarget = 1f;
			if (mBossHpText != null) mBossHpText.text = $"BOSS {Mathf.CeilToInt(evt.MaxHp)}/{Mathf.CeilToInt(evt.MaxHp)}";
			LogKit.Log("[HudPanel] BOSS 生成，显示 BOSS UI");
		}

		private void OnBossPhaseChanged(GameEvents.BossPhaseChanged evt)
		{
			if (mBossPhaseText != null)
				mBossPhaseText.text = $"阶段 {evt.PhaseIndex + 1}";
			if (mBossWeaknessText != null)
			{
				string weaknessName = evt.WeaknessType switch
				{
					AttackType.Vegetable => "蔬菜",
					AttackType.Meat => "肉类",
					AttackType.Spice => "香料",
					_ => "通用"
				};
				Color weaknessColor = evt.WeaknessType switch
				{
					AttackType.Vegetable => new Color(0.3f, 1f, 0.3f),   // 绿
					AttackType.Meat => new Color(1f, 0.3f, 0.3f),          // 红
					AttackType.Spice => new Color(1f, 1f, 0.3f),           // 黄
					_ => Color.white
				};
				mBossWeaknessText.text = $"弱点: {weaknessName}";
				mBossWeaknessText.color = weaknessColor;
			}
		}

		private void OnBossDied(GameEvents.BossDied evt)
		{
			if (mBossRoot != null) mBossRoot.SetActive(false);
		}

		private void OnBossRevived(GameEvents.BossRevived evt)
		{
			if (mBossRoot != null) mBossRoot.SetActive(true);
		}

		/// <summary>事件驱动：BOSS 掉血/破韧时更新血条（替代每帧 FindObjectOfType 轮询）</summary>
		private void OnBossHealthChanged(GameEvents.BossHealthChanged evt)
		{
			mBossHpTarget = evt.HpRatio;
			if (mBossHpText != null)
				mBossHpText.text = $"BOSS {Mathf.CeilToInt(evt.CurrentHp)}/{Mathf.CeilToInt(evt.MaxHp)}";
			if (mBossBreakBarFill != null) mBossBreakBarFill.fillAmount = evt.BreakBarRatio;
		}
	}
}
