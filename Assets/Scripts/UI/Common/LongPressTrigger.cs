using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameUI
{
	/// <summary>
	/// UGUI 长按触发器：按住 m_holdSeconds 秒后触发长按回调，并抑制同物体上 Button 的单击。
	/// - 未达时长时，单击行为与原来完全一致（不影响"加入合成台 / 装配选中"等既有语义）
	/// - 触发长按后，本帧起把 Button 置为不可交互并在指针抬起后恢复，从而吞掉随后的 OnPointerClick
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class LongPressTrigger : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
	{
		[Tooltip("触发长按所需的按住时长（秒，使用非缩放时间）")]
		[SerializeField] private float m_holdSeconds = 0.45f;

		private Button m_button;
		private bool m_pressing;
		private bool m_fired;
		private bool m_restorePending;
		private float m_downTime;

		/// <summary>长按触发回调（由绑定方赋值）。</summary>
		public System.Action onLongPress;

		private void Awake()
		{
			m_button = GetComponent<Button>();
		}

		private void OnDisable()
		{
			m_pressing = false;
			m_fired = false;
			m_restorePending = false;
			if (m_button != null) m_button.interactable = true;
		}

		public void OnPointerDown(PointerEventData eventData)
		{
			if (eventData.button != PointerEventData.InputButton.Left) return;

			// 按钮本身不可交互（如已装备项）时不参与长按
			if (m_button != null && !m_button.interactable)
			{
				m_pressing = false;
				return;
			}

			m_pressing = true;
			m_fired = false;
			m_downTime = Time.unscaledTime;
		}

		public void OnPointerUp(PointerEventData eventData)
		{
			m_pressing = false;
			// 已触发长按：把恢复推迟到 LateUpdate，确保先吞掉本帧的 OnPointerClick
			if (m_fired) m_restorePending = true;
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			// 指针移出格子：取消未触发的长按
			m_pressing = false;
			if (m_fired) m_restorePending = true;
		}

		private void Update()
		{
			if (!m_pressing || m_fired) return;
			if (Time.unscaledTime - m_downTime < m_holdSeconds) return;

			m_fired = true;
			m_pressing = false;

			// 抑制 Button 单击
			if (m_button != null) m_button.interactable = false;

			onLongPress?.Invoke();
		}

		private void LateUpdate()
		{
			if (!m_restorePending) return;

			m_restorePending = false;
			m_fired = false;
			if (m_button != null) m_button.interactable = true;
		}
	}
}
