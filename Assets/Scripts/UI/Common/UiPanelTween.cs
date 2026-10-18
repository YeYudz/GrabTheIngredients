using System.Collections;
using UnityEngine;

namespace GameUI
{
	/// <summary>
	/// 面板级打开动画的运行时宿主（挂在面板根节点上，**不需要改预制体**，首次调用时自动添加）。
	/// 约定：根节点整体淡入 + 主窗缩放弹入，避免"整块全屏遮罩跟着缩放"露出背后的画面。
	/// 主窗识别：直接子节点里第一个以 `Window` 结尾的节点（ShopWindow / PauseWindow / CookingWindow…），
	/// 退而求其次找 `Card`（ItemDetailPanel）、`Board`（MetaUpgradePanel）；
	/// 都没有时（MainMenuPanel 这类）退化为整块面板轻微缩放。
	///
	/// 为什么不用框架的 UIPanel.SetShowAnimation：UIKit 的 Close 是同步的、不播隐藏动画，
	/// 而 Show 动画会把面板状态压在 Opening 直到播完；自己播协程则与框架状态机完全解耦。
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class UiPanelTween : MonoBehaviour
	{
		/// <summary>找到主窗时的起始缩放</summary>
		private const float k_windowFromScale = 0.94f;

		/// <summary>没有主窗时整块面板的起始缩放（更收敛，避免全屏元素缩边）</summary>
		private const float k_rootFromScale = 0.98f;

		/// <summary>默认时长（秒）</summary>
		private const float k_defaultDuration = 0.2f;

		private Coroutine mShowTween;
		private CanvasGroup mRootGroup;
		private RectTransform mContent;
		private Vector3 mContentBaseScale = Vector3.one;
		private bool mContentResolved;

		/// <summary>
		/// 播一次"面板打开"动画。面板没露脸（未激活）时直接落终态，避免留下半透明/缩放中间状态。
		/// </summary>
		/// <param name="panel">面板实例（传 this 即可）</param>
		/// <param name="duration">时长，默认 0.2s</param>
		/// <param name="scaleContent">是否缩放主窗；HUD 这类铺满屏幕的常驻面板传 false，只淡入</param>
		public static void PlayIn(MonoBehaviour panel, float duration = k_defaultDuration, bool scaleContent = true)
		{
			if (panel == null) return;

			// 面板每次显示时补齐按钮悬停反馈（已挂过的会跳过，动态新增的按钮也能覆盖）
			UiHoverInstaller.Install(panel.gameObject);

			var tween = panel.GetComponent<UiPanelTween>();
			if (tween == null) tween = panel.gameObject.AddComponent<UiPanelTween>();
			tween.PlayInInternal(duration, scaleContent);
		}

		private void PlayInInternal(float duration, bool scaleContent)
		{
			if (!isActiveAndEnabled)
			{
				ApplyFinalState();
				return;
			}

			if (mRootGroup == null) mRootGroup = UiTween.EnsureCanvasGroup(gameObject);
			var content = ResolveContent();

			if (duration <= 0f)
			{
				ApplyFinalState();
				return;
			}

			UiTween.Play(this, ref mShowTween, PlayInRoutine(content, duration, scaleContent));
		}

		private IEnumerator PlayInRoutine(RectTransform content, float duration, bool scaleContent)
		{
			// 初始状态同步写入（StartCoroutine 会先跑到第一个 yield），因此不会有一帧闪出终态
			if (mRootGroup != null) mRootGroup.alpha = 0f;

			Vector3 from = mContentBaseScale;
			if (scaleContent && content != null)
			{
				from = mContentBaseScale * (content == transform as RectTransform ? k_rootFromScale : k_windowFromScale);
				content.localScale = from;
			}

			yield return UiTween.Run(duration, t =>
			{
				// 透明度比缩放略快收尾，弹入结束时不会"先看清再缩小"
				if (mRootGroup != null) mRootGroup.alpha = Mathf.Clamp01(t * 1.3f);
				if (scaleContent && content != null) content.localScale = Vector3.LerpUnclamped(from, mContentBaseScale, t);
			}, UiEase.OutCubic);

			ApplyFinalState();
			mShowTween = null;
		}

		private void ApplyFinalState()
		{
			if (mRootGroup != null) mRootGroup.alpha = 1f;
			if (mContent != null) mContent.localScale = mContentBaseScale;
		}

		/// <summary>解析主窗节点（只找一次并缓存）</summary>
		private RectTransform ResolveContent()
		{
			if (mContentResolved)
			{
				if (mContent != null) return mContent;
				// 主窗被销毁（面板重建）时才重新解析
				mContentResolved = false;
			}

			mContentResolved = true;
			mContent = FindWindow(transform);

			if (mContent == null)
			{
				// 没有独立主窗：整块面板轻微缩放入场
				mContent = transform as RectTransform;
			}

			if (mContent != null) mContentBaseScale = mContent.localScale;
			return mContent;
		}

		/// <summary>按命名约定找主窗：*Window → Card → Board</summary>
		private static RectTransform FindWindow(Transform root)
		{
			if (root == null) return null;

			Transform card = null;
			Transform board = null;

			for (int i = 0; i < root.childCount; i++)
			{
				var child = root.GetChild(i);
				if (!child.gameObject.activeSelf) continue;

				string name = child.name;
				if (name.EndsWith("Window")) return child as RectTransform;
				if (card == null && name == "Card") card = child;
				else if (board == null && name == "Board") board = child;
			}

			return (card ?? board) as RectTransform;
		}
	}
}
