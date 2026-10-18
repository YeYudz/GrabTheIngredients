using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class MainMenuPanelData : IUIData
	{
	}

	/// <summary>
	/// 主菜单面板：入口聚合页，只负责打开面板与广播事件，游戏流程由 GameManager 处理。
	/// 「继续」依赖对局断点是否存在，无断点时禁用，避免把玩家送进不可恢复的状态。
	/// </summary>
	public partial class MainMenuPanel : UIPanel
	{
		[Header("当前对局（NowGame）")]
		[SerializeField] private Text mDungeonNameText;     // NowGame 文本：当前关卡 + 波次（仅存在进行中的断点时显示）

		[Header("底部提示")]
		[SerializeField] private Text mTipsText;            // 随机提示（文案来自 Resources/Data/TipsConfig.json，每次回到本页换一条）

		private Coroutine mTipFade;                          // 提示文案淡入动画槽位
		private bool mTipCovered;                            // 上一帧是否被上层面板盖住（盖住过 → 重新露出时再换一条）

		[Header("元游戏入口")]
		[SerializeField] private Button mAchievementButton;  // 成就
		[SerializeField] private Button mAtlasButton;        // 烹饪图鉴
		[SerializeField] private Button mUpgradeButton;      // 永久升级
		[SerializeField] private Button mExitButton;         // 退出游戏

		[Header("设置")]
		[SerializeField] private Button mSettingsButton;     // 设置

		protected override void OnInit(IUIData data = null)
		{
			mData = data as MainMenuPanelData ?? new MainMenuPanelData();

			if (mStartButton != null)
			{
				mStartButton.onClick.RemoveAllListeners();
				mStartButton.onClick.AddListener(OnStartGame);
			}
			if (mContinueButton != null)
			{
				mContinueButton.onClick.RemoveAllListeners();
				mContinueButton.onClick.AddListener(OnContinueGame);
			}
			if (mInventoryButton != null)
			{
				mInventoryButton.onClick.RemoveAllListeners();
				mInventoryButton.onClick.AddListener(OnOpenInventory);
			}
			if (mShopButton != null)
			{
				mShopButton.onClick.RemoveAllListeners();
				mShopButton.onClick.AddListener(OnOpenShop);
			}
			if (mAchievementButton != null)
			{
				mAchievementButton.onClick.RemoveAllListeners();
				mAchievementButton.onClick.AddListener(OnOpenAchievement);
			}
			if (mAtlasButton != null)
			{
				mAtlasButton.onClick.RemoveAllListeners();
				mAtlasButton.onClick.AddListener(OnOpenAtlas);
			}
			if (mUpgradeButton != null)
			{
				mUpgradeButton.onClick.RemoveAllListeners();
				mUpgradeButton.onClick.AddListener(OnOpenUpgrade);
			}
			if (mSettingsButton != null)
			{
				mSettingsButton.onClick.RemoveAllListeners();
				mSettingsButton.onClick.AddListener(OnOpenSettings);
			}
			if (mExitButton != null)
			{
				mExitButton.onClick.RemoveAllListeners();
				mExitButton.onClick.AddListener(OnExitGame);
			}
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as MainMenuPanelData ?? mData;
			RefreshContinueButton();
			RefreshRunInfo();
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this);
			// 从选关/背包/商店等上层面板返回后，刷新易变显示
			RefreshRunInfo();
			RefreshContinueButton();
			// 每次进入/重新显示主菜单：换一条底部提示
			mTipCovered = false;
			RefreshTip();
		}

		/// <summary>上层面板离栈后恢复（UIKit 命名栈路径）</summary>
		protected override void OnResume()
		{
			RefreshRunInfo();
			RefreshContinueButton();
		}

		protected override void OnHide()
		{
		}

		/// <summary>
		/// 主菜单被商店/背包这类 Pop 面板压住时其实一直可见（UIKit 不会隐藏下层），
		/// 所以这里补一条：被盖住过后重新露出（= 又回到了主菜单页面），再换一条提示。
		/// </summary>
		private void Update()
		{
			if (mTipsText == null) return;

			var top = UIKit.GetGlobalTopPanel();
			bool covered = top != null && !ReferenceEquals(top, (IPanel)this);

			if (covered)
			{
				mTipCovered = true;
				return;
			}
			if (!mTipCovered) return;

			mTipCovered = false;
			RefreshTip();
		}

		protected override void OnClose()
		{
		}

		/// <summary>点击开始：先选副本（主菜单 → 副本选择 → 战前准备 → 开始游戏）</summary>
		private void OnStartGame()
		{
			PlayClick();
			GameManager.TryOpenPanel<DungeonPanel>(UILevel.Pop);
		}

		/// <summary>点击继续：有对局断点则发 ContinueRun 事件（GameManager 接管续玩）</summary>
		private void OnContinueGame()
		{
			if (!RunSaveManager.HasSave()) return; // 无断点不可点（按钮已禁用，防御）
			PlayClick();
			EventKit.Type.Send(new GameEvents.ContinueRun());
		}

		/// <summary>刷新"继续"按钮可用状态（无对局断点 → 禁用）</summary>
		private void RefreshContinueButton()
		{
			if (mContinueButton != null)
				mContinueButton.interactable = RunSaveManager.HasSave();
		}

		/// <summary>
		/// 刷新底部提示：从 TipsConfig.json 随机抽一条（自动拼上配置里的前缀）。
		/// 抽样会避开上一条，因此连续两次进入不会看到同一句。
		/// </summary>
		private void RefreshTip()
		{
			if (mTipsText == null) return;

			string tip = GameDataMgr.Instance.GetRandomTip();
			if (string.IsNullOrEmpty(tip))
			{
				mTipsText.text = string.Empty;
				return;
			}

			mTipsText.text = tip;
			// 只做淡入，不加位移：主菜单整体已有 UiPanelTween 的开场动画
			var group = UiTween.EnsureCanvasGroup(mTipsText.gameObject);
			UiTween.Play(this, ref mTipFade, UiTween.Fade(group, 0f, 1f, 0.25f));
		}

		/// <summary>打开成品菜背包</summary>
		private void OnOpenInventory()
		{
			PlayClick();
			UIKit.OpenPanel<InventoryPanel>(UILevel.Pop);
		}

		/// <summary>打开商店</summary>
		private void OnOpenShop()
		{
			PlayClick();
			UIKit.OpenPanel<ShopPanel>(UILevel.Pop);
		}

		/// <summary>打开成就面板</summary>
		private void OnOpenAchievement()
		{
			PlayClick();
			GameManager.TryOpenPanel<AchievementPanel>(UILevel.Pop);
		}

		/// <summary>打开图鉴（全物品图鉴：角色/武器/菜品/技能/敌人）</summary>
		private void OnOpenAtlas()
		{
			PlayClick();
			GameManager.TryOpenPanel<IllustrationPanel>(UILevel.Pop);
		}

		/// <summary>打开永久升级面板（局外养成：钱币购买永久属性加成）</summary>
		private void OnOpenUpgrade()
		{
			PlayClick();
			GameManager.TryOpenPanel<MetaUpgradePanel>(UILevel.Pop);
		}

		/// <summary>打开设置面板</summary>
		private void OnOpenSettings()
		{
			PlayClick();
			UIKit.OpenPanel<SettingPanel>(UILevel.Pop);
		}

		/// <summary>退出游戏（编辑器内停止播放，便于快速验证；构建版真正退出）</summary>
		private void OnExitGame()
		{
			PlayClick();
#if UNITY_EDITOR
			UnityEditor.EditorApplication.isPlaying = false;
#else
			Application.Quit();
#endif
		}

		/// <summary>
		/// 刷新 NowGame（当前关卡 + 波次）。
		/// 只有「对局中断」才有断点（暂停→退出本局 / 切后台 / 每波自动落盘）；
		/// 通关或阵亡时 GameManager.RecordRunAndSave 已清掉断点，因此这里直接以断点为准：
		/// 无断点（或旧版坏档 / 本局已结束）→ 整个 NowGame 隐藏。
		/// </summary>
		private void RefreshRunInfo()
		{
			if (mDungeonNameText == null) return;

			var snap = RunSaveManager.Load();
			if (snap == null || snap.wavesStopped || !RunSaveManager.IsCompatible(snap))
			{
				SetRunInfoVisible(false);
				return;
			}

			// 续玩时会按断点记录的副本进战场；断点没记（旧档）则用当前选中的副本（与 GameRunningState 同口径）
			var dungeon = GameDataMgr.Instance.GetDungeon(snap.dungeonId)
				?? (MetaManager.HasInstance ? GameDataMgr.Instance.GetDungeon(MetaManager.Instance.SelectedDungeonId) : null);
			string dungeonName = dungeon != null ? dungeon.dungeonName : "未配置";

			int wave = GetWaveOrdinal(snap, dungeon);
			int total = GetWaveTotal(dungeon);
			string waveText = total > 0 ? $"{wave} / {total}" : $"第 {wave} 波";

			mDungeonNameText.text = $"当前关卡：{dungeonName}\n波次：{waveText}";
			SetRunInfoVisible(true);
		}

		/// <summary>显示/隐藏 NowGame 节点（Text 与节点同物体）</summary>
		private void SetRunInfoVisible(bool visible)
		{
			if (mDungeonNameText.gameObject.activeSelf != visible)
				mDungeonNameText.gameObject.SetActive(visible);
		}

		/// <summary>断点所在的「本关累计波次序号」（跨阶段连续，与 HUD 的 第 N 波 同口径）</summary>
		private static int GetWaveOrdinal(RunSaveData snap, DungeonConfig dungeon)
		{
			int waveInPhase = Mathf.Max(1, snap.waveIndex);
			var phases = dungeon?.phases;
			if (phases == null || phases.Count == 0) return waveInPhase;

			int sum = 0;
			for (int i = 0; i < snap.phaseIndex && i < phases.Count; i++)
				sum += phases[i]?.waveIds?.Count ?? 0;
			return sum + waveInPhase;
		}

		/// <summary>副本总波次数（含各阶段末尾的 BOSS 波；取不到返回 0 = 不显示总数）</summary>
		private static int GetWaveTotal(DungeonConfig dungeon)
		{
			if (dungeon == null) return 0;

			var phases = dungeon.phases;
			if (phases != null && phases.Count > 0)
			{
				int total = 0;
				foreach (var p in phases) total += p?.waveIds?.Count ?? 0;
				return total;
			}
			return dungeon.waveIds?.Count ?? 0;
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}
	}
}
