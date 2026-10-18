using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class PausePanelData : IUIData
	{
	}

	/// <summary>
	/// 暂停面板：以 Time.timeScale = 0 冻结战斗，提供继续 / 设置 / 退出本局。
	/// 打开上层设置面板时保持暂停态，并用 mSubMenuOpen 把 Esc 交还上层，避免叠层面板抢同一按键。
	/// 退出本局先落对局断点再回主菜单，使主菜单的「继续」能够接上。
	/// </summary>
	public partial class PausePanel : UIPanel
	{
		[Header("标题/信息")]
		[SerializeField] private Text mTitleText;      // 可选：标题
		[SerializeField] private Text mInfoText;       // 可选：当前关卡名

		[Header("按钮")]
		[SerializeField] private Button mResumeButton;    // 继续游戏
		[SerializeField] private Button mSettingsButton;  // 设置
		[SerializeField] private Button mQuitButton;      // 退出本局

		/// <summary>是否打开了上层面板（设置面板），期间 ESC 交给上层处理</summary>
		private bool mSubMenuOpen;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as PausePanelData ?? new PausePanelData();

			if (mResumeButton != null)
			{
				mResumeButton.onClick.RemoveAllListeners();
				mResumeButton.onClick.AddListener(OnResumeGame);
			}
			if (mSettingsButton != null)
			{
				mSettingsButton.onClick.RemoveAllListeners();
				mSettingsButton.onClick.AddListener(OnOpenSettings);
			}
			if (mQuitButton != null)
			{
				mQuitButton.onClick.RemoveAllListeners();
				mQuitButton.onClick.AddListener(OnQuitRun);
			}
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as PausePanelData ?? mData;
			mSubMenuOpen = false;
			Time.timeScale = 0f; // 冻结战斗
			RefreshInfo();
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this);
		}

		protected override void OnHide()
		{
			// 上层面板（设置）打开：暂停态由本面板维持，ESC 交由上层处理
			mSubMenuOpen = true;
		}

		/// <summary>上层面板关闭后恢复（UIKit 命名栈路径）</summary>
		protected override void OnResume()
		{
			mSubMenuOpen = false;
		}

		protected override void OnClose()
		{
			Time.timeScale = 1f; // 防御：关闭即恢复时间流速
		}

		private void Update()
		{
			// 设置面板打开时 ESC 由设置面板处理；否则 ESC = 继续
			if (mSubMenuOpen) return;
			if (Input.GetKeyDown(KeyCode.Escape))
				OnResumeGame();
		}

		/// <summary>继续游戏：恢复时间流速并关闭面板</summary>
		private void OnResumeGame()
		{
			PlayClick();
			Time.timeScale = 1f;
			CloseSelf();
		}

		/// <summary>打开设置面板（暂停状态保持：timeScale 不变）</summary>
		private void OnOpenSettings()
		{
			PlayClick();
			UIKit.OpenPanel<SettingPanel>(UILevel.Pop);
		}

		/// <summary>退出本局：保存对局断点（回主菜单后可用"继续"）+ 局外存档 → 返回主菜单</summary>
		private void OnQuitRun()
		{
			PlayClick();

			// 保存对局断点（中断续玩）+ 局外存档
			RunSaveManager.CaptureCheckpoint();
			if (MetaManager.HasInstance) MetaManager.Instance.Save();

			Time.timeScale = 1f;
			CloseSelf();
			// 返回主菜单（GameManager 接管：清理局内系统 → 加载主菜单场景）
			EventKit.Type.Send(new GameEvents.BackToMenu());
		}

		/// <summary>刷新信息文本（当前关卡名；节点未绑定时跳过）</summary>
		private void RefreshInfo()
		{
			if (mInfoText == null) return;

			var dungeon = GameDataMgr.Instance.GetDungeon(MetaManager.Instance.SelectedDungeonId)
				?? GameDataMgr.Instance.CurrentDungeon;
			mInfoText.text = $"当前关卡：{(dungeon != null ? dungeon.dungeonName : "未配置")}";
		}

		private void CloseSelf()
		{
			UIKit.ClosePanel<PausePanel>();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}
	}
}
