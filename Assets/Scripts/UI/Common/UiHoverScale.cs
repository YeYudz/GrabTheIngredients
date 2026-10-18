using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameUI
{
	/// <summary>
	/// 通用按钮反馈：鼠标移入放大、按下回缩、移出复位（非缩放时间驱动，暂停中依然跟手）。
	/// 由 <see cref="UiHoverInstaller"/> 在面板打开 / 列表重建时自动挂载，**不需要改预制体**。
	/// 需要单独关掉某个按钮的效果时，把 <see cref="HoverEnabled"/> 置 false 即可。
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class UiHoverScale : MonoBehaviour,
		IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
	{
		/// <summary>悬停放大比例</summary>
		public const float HoverScale = 1.06f;

		/// <summary>按下回缩比例</summary>
		public const float PressScale = 0.97f;

		/// <summary>单段过渡时长</summary>
		public const float Duration = 0.09f;

		[SerializeField] private bool m_hoverEnabled = true;
		[SerializeField] private bool m_pressEnabled = true;

		private Selectable mSelectable;
		private Vector3 mBaseScale = Vector3.one;
		private bool mBaseCaptured;
		private bool mHovering;
		private bool mPressing;
		private Coroutine mTween;

		/// <summary>是否启用悬停放大</summary>
		public bool HoverEnabled
		{
			get => m_hoverEnabled;
			set
			{
				m_hoverEnabled = value;
				if (!value) ResetScale();
			}
		}

		/// <summary>是否启用按下回缩</summary>
		public bool PressEnabled
		{
			get => m_pressEnabled;
			set
			{
				m_pressEnabled = value;
				if (!value) ResetScale();
			}
		}

		private void Awake() => CaptureBase();

		private void OnEnable()
		{
			if (mSelectable == null) mSelectable = GetComponent<Selectable>();
			if (!mBaseCaptured) CaptureBase();
		}

		private void OnDisable()
		{
			mHovering = false;
			mPressing = false;
			UiTween.Stop(this, ref mTween);
			// 面板隐藏时可能正卡在放大中间值，复位后再显示才不会"一大一小"
			if (mBaseCaptured) transform.localScale = mBaseScale;
		}

		private void Update()
		{
			// 只在悬停期间监视可交互状态：钱不够 / 售罄时按钮变灰，鼠标没移开也要缩回去
			if (!mHovering) return;
			if (mSelectable != null && !mSelectable.IsInteractable())
			{
				mHovering = false;
				mPressing = false;
				PlayTo(1f);
			}
		}

		public void OnPointerEnter(PointerEventData eventData)
		{
			mHovering = true;
			if (!CanScale(needHover: true)) return;
			PlayTo(mPressing ? PressScale : HoverScale);
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			mHovering = false;
			mPressing = false;
			PlayTo(1f);
		}

		public void OnPointerDown(PointerEventData eventData)
		{
			mPressing = true;
			if (!CanScale(needPress: true)) return;
			PlayTo(PressScale);
		}

		public void OnPointerUp(PointerEventData eventData)
		{
			mPressing = false;
			PlayTo(mHovering && CanScale(needHover: true) ? HoverScale : 1f);
		}

		/// <summary>立即复位（不做过渡）。列表项被复用时调用。</summary>
		public void ResetScale()
		{
			UiTween.Stop(this, ref mTween);
			mHovering = false;
			mPressing = false;
			if (mBaseCaptured) transform.localScale = mBaseScale;
		}

		/// <summary>重新记录基准缩放（外部动画改过尺寸后调用）</summary>
		public void RecaptureBase()
		{
			mBaseCaptured = false;
			CaptureBase();
		}

		private bool CanScale(bool needHover = false, bool needPress = false)
		{
			if (needHover && !m_hoverEnabled) return false;
			if (needPress && !m_pressEnabled) return false;
			if (!isActiveAndEnabled) return false;
			return mSelectable == null || mSelectable.IsInteractable();
		}

		private void CaptureBase()
		{
			Vector3 scale = transform.localScale;
			// 列表弹入动画（StaggerPop）在 0.2s 内会把 scale 从 0.86 抬到 1；
			// 挂载时机早于动画，读到的是正常值。这里只兜底防御 0 缩放。
			if (Mathf.Abs(scale.x) <= 0.01f || Mathf.Abs(scale.y) <= 0.01f) scale = Vector3.one;
			mBaseScale = scale;
			mBaseCaptured = true;
		}

		private void PlayTo(float factor)
		{
			if (!isActiveAndEnabled) return;
			if (!mBaseCaptured) CaptureBase();

			Vector3 from = transform.localScale;
			Vector3 to = mBaseScale * factor;
			if ((from - to).sqrMagnitude < 0.000001f) return;

			UiTween.Play(this, ref mTween, UiTween.Run(Duration, t =>
			{
				transform.localScale = Vector3.LerpUnclamped(from, to, t);
			}, UiEase.OutCubic));
		}
	}
}
