using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameUI
{
	/// <summary>
	/// 滑动条拖拽组件：把「指针在值域区中的位置」换算为滑动条的值。
	/// 自建原因：原生 Slider 只在自身 RectTransform 内响应按下，且没有松手事件——
	/// 这两点无法支撑「拖拽区大于轨道」与「拖动中只预览、松手才落盘」。
	/// 换算规则与 Unity Slider 完全一致，故可与原生 Slider 共存于同一节点而不冲突。
	/// 用法：挂在滑动条节点或其子节点上，令需响应的区域具备 raycastTarget 图元。
	/// </summary>
	[DisallowMultipleComponent]
	[AddComponentMenu("GameUI/Drag Slider")]
	public sealed class DragSlider : MonoBehaviour,
		IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler
	{
		[Tooltip("目标滑动条（留空 = 自身，其次父级上找）")]
		[SerializeField] private Slider m_slider;

		[Tooltip("值域区：这一段的长度对应 minValue ~ maxValue（留空 = 滑动条的手柄容器，再次填充容器）")]
		[SerializeField] private RectTransform m_valueRange;

		[Tooltip("滚轮每格的变化量（归一化，0 = 关闭滚轮微调）")]
		[SerializeField] private float m_scrollStep = 0.05f;

		/// <summary>值变化（拖动中每帧都可能触发；**不要**在这里落盘，用 <see cref="onValueCommitted"/>)</summary>
		public event Action<float> onValueChanged;

		/// <summary>一次操作结束（松开指针 / 滚一格滚轮；用于落盘这类一次性动作）</summary>
		public event Action<float> onValueCommitted;

		/// <summary>是否正在被拖拽（拖动中 = true，松手置回）</summary>
		public bool IsDragging { get; private set; }

		/// <summary>按下点相对手柄中心的偏移：抓着手柄拖时"手在哪手柄在哪"，不会跳到指针中心</summary>
		private Vector2 m_grabOffset;

		/// <summary>目标滑动条（按需自动解析，避免预制体没绑就报空）</summary>
		public Slider Target
		{
			get
			{
				if (m_slider == null) m_slider = GetComponent<Slider>();
				if (m_slider == null) m_slider = GetComponentInParent<Slider>();
				return m_slider;
			}
		}

		/// <summary>值域区（手柄容器优先 —— 与 Unity Slider 的拖拽换算取同一个 rect）</summary>
		public RectTransform ValueRange
		{
			get
			{
				if (m_valueRange != null) return m_valueRange;

				var slider = Target;
				if (slider != null)
				{
					if (slider.handleRect != null && slider.handleRect.parent is RectTransform handleArea)
						return m_valueRange = handleArea;
					if (slider.fillRect != null && slider.fillRect.parent is RectTransform fillArea)
						return m_valueRange = fillArea;
					return m_valueRange = (RectTransform)slider.transform;
				}
				return null;
			}
		}

		/// <summary>运行时装配（预制体里没挂本组件时，由面板代码补挂并指定参数）</summary>
		public void Initialize(Slider slider, float scrollStep = 0.05f, RectTransform valueRange = null)
		{
			m_slider = slider;
			m_scrollStep = scrollStep;
			if (valueRange != null) m_valueRange = valueRange;
		}

		// ===== 指针交互 =====

		public void OnPointerDown(PointerEventData eventData)
		{
			if (!MayDrag(eventData)) return;

			IsDragging = true;
			m_grabOffset = Vector2.zero;

			var slider = Target;
			var handle = slider != null ? slider.handleRect : null;

			if (handle != null &&
				RectTransformUtility.RectangleContainsScreenPoint(handle, eventData.pointerPressRaycast.screenPosition, eventData.enterEventCamera))
			{
				// 按在手柄上 → 记住抓取点，拖动时保持相对位置（与 Unity 同规则，拖起来不"跳"）
				RectTransformUtility.ScreenPointToLocalPointInRectangle(
					handle, eventData.pointerPressRaycast.screenPosition, eventData.pressEventCamera, out m_grabOffset);
			}
			else
			{
				// 按在别处 → 直接把值跳到指针位置
				ApplyPointer(eventData);
			}
		}

		public void OnDrag(PointerEventData eventData)
		{
			if (!MayDrag(eventData)) return;
			IsDragging = true;
			ApplyPointer(eventData);
		}

		public void OnPointerUp(PointerEventData eventData)
		{
			if (!IsDragging) return;

			IsDragging = false;
			m_grabOffset = Vector2.zero;

			var slider = Target;
			onValueCommitted?.Invoke(slider != null ? slider.value : 0f);
		}

		public void OnScroll(PointerEventData eventData)
		{
			var slider = Target;
			if (slider == null || !slider.IsInteractable() || m_scrollStep <= 0f) return;

			float step = Mathf.Sign(eventData.scrollDelta.y) * m_scrollStep;
			float value = Mathf.Clamp01(slider.normalizedValue + step);
			if (Mathf.Approximately(slider.normalizedValue, value)) return;

			slider.normalizedValue = value;
			onValueChanged?.Invoke(slider.value);
			onValueCommitted?.Invoke(slider.value);   // 滚轮是"离散一下"，直接算一次提交
		}

		// ===== 换算（与 Unity Slider.UpdateDrag 同规则） =====

		private void ApplyPointer(PointerEventData eventData)
		{
			var slider = Target;
			if (slider == null) return;

			var range = ValueRange;
			if (range == null) return;

			int axis = IsVertical(slider.direction) ? 1 : 0;
			var rect = range.rect;
			if (rect.size[axis] <= 0.001f) return;

			if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(range, eventData.position, eventData.pressEventCamera, out var local))
				return;

			local -= rect.position;   // 换成"相对值域区左下角"的坐标

			float normalized = Mathf.Clamp01((local - m_grabOffset)[axis] / rect.size[axis]);
			if (IsReverse(slider.direction)) normalized = 1f - normalized;

			float before = slider.value;
			slider.normalizedValue = normalized;   // 交给 Slider 做 min/max 换算与取整
			if (!Mathf.Approximately(before, slider.value))
				onValueChanged?.Invoke(slider.value);
		}

		private bool MayDrag(PointerEventData eventData)
		{
			var slider = Target;
			return slider != null
				&& slider.IsActive()
				&& slider.IsInteractable()
				&& eventData.button == PointerEventData.InputButton.Left;
		}

		private static bool IsVertical(Slider.Direction direction)
			=> direction == Slider.Direction.BottomToTop || direction == Slider.Direction.TopToBottom;

		/// <summary>RightToLeft / TopToBottom 是"反向"轴：坐标从左/下变大，值反而变小</summary>
		private static bool IsReverse(Slider.Direction direction)
			=> direction == Slider.Direction.RightToLeft || direction == Slider.Direction.TopToBottom;
	}
}
