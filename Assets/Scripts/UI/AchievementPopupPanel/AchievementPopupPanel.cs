using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class AchievementPopupPanelData : IUIData
	{
		public AchievementConfig Config;
	}

	/// <summary>
	/// 成就解锁提示：纯展示、无按钮。
	/// 时序固定为「渐入 1s → 完整显示 2s → 渐出 1s → 自动关闭并弹出下一条」。
	/// 面板自身不持有队列状态，收尾时回调 <see cref="AchievementManager.OnPopupClosed"/> 驱动队列。
	/// </summary>
	public partial class AchievementPopupPanel : UIPanel
	{
		/// <summary>渐入时长（秒）</summary>
		private const float k_fadeIn = 1f;

		/// <summary>完全显示后的停留时长（秒）</summary>
		private const float k_hold = 2f;

		/// <summary>渐出时长（秒）</summary>
		private const float k_fadeOut = 1f;

		[Header("内容")]
		[SerializeField] private Text mTitleText;   // 成就标题
		[SerializeField] private Text mDescText;    // 成就描述
		[SerializeField] private Text mPointsText;  // 成就点数

		/// <summary>本轮展示的成就</summary>
		private AchievementConfig mConfig;

		/// <summary>整块面板的透明度宿主（预制体没有就运行时补一个）</summary>
		private CanvasGroup mGroup;

		private Coroutine mRoutine;

		protected override void OnOpen(IUIData data = null)
		{
			mConfig = (data as AchievementPopupPanelData)?.Config;
			RefreshText();
		}

		protected override void OnShow()
		{
			if (mGroup == null) mGroup = UiTween.EnsureCanvasGroup(gameObject);
			UiTween.Stop(this, ref mRoutine);
			mRoutine = StartCoroutine(PlaySequence());
		}

		protected override void OnHide()
		{
			// 中途被外部关闭时收干净，避免下次打开从半透明状态开始
			UiTween.Stop(this, ref mRoutine);
			if (mGroup != null) mGroup.alpha = 0f;
		}

		/// <summary>只负责三个文本的内容，字号/颜色/排版全部走预制体</summary>
		private void RefreshText()
		{
			var cfg = mConfig;
			if (cfg == null) return;

			if (mTitleText != null) mTitleText.text = $"成就解锁！{cfg.title}";
			if (mDescText != null) mDescText.text = cfg.description;
			if (mPointsText != null) mPointsText.text = $"+{cfg.pointValue} 成就点数";
		}

		private IEnumerator PlaySequence()
		{
			if (mGroup != null) mGroup.alpha = 0f;

			// 渐入 1s
			if (mGroup != null) yield return UiTween.Fade(mGroup, 0f, 1f, k_fadeIn, UiEase.Linear);
			else yield return new WaitForSecondsRealtime(k_fadeIn);

			// 完整显示 2s：用真实时间，暂停 / 结算 / timeScale = 0 时也照常计时
			yield return new WaitForSecondsRealtime(k_hold);

			// 渐出 1s
			if (mGroup != null) yield return UiTween.Fade(mGroup, 1f, 0f, k_fadeOut, UiEase.Linear);
			else yield return new WaitForSecondsRealtime(k_fadeOut);

			mRoutine = null;

			// 先缓存队列引用：ClosePanel 会让本物体失活，之后再取实例更稳妥
			var manager = AchievementManager.HasInstance ? AchievementManager.Instance : null;

			UIKit.ClosePanel<AchievementPopupPanel>();
			if (manager != null) manager.OnPopupClosed();
		}
	}
}
