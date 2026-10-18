using UnityEngine;
using UnityEngine.UI;

namespace GameUI
{
	/// <summary>
	/// 给一个面板 / 列表下的所有可点击控件批量挂 <see cref="UiHoverScale"/>（已挂过的跳过）。
	/// 接入点：<see cref="UiPanelTween.PlayIn"/> 里已自动调用（覆盖所有面板），
	/// 动态重建的列表（如 ShopPanel 商品网格）在重建后自行调用一次 <c>UiHoverInstaller.Install(root)</c>。
	/// </summary>
	public static class UiHoverInstaller
	{
		/// <summary>命中这些名字的节点不缩放（点击遮罩、纯背景按钮）</summary>
		private static readonly string[] k_skipNameKeywords =
		{
			"mask", "block", "overlay", "backdrop", "dim", "遮罩", "background", "bg"
		};

		/// <summary>递归安装（包含未激活子节点，面板还没露脸时也能一次装好）</summary>
		public static int Install(GameObject root)
		{
			if (root == null) return 0;

			var selectables = root.GetComponentsInChildren<Selectable>(true);
			int installed = 0;
			for (int i = 0; i < selectables.Length; i++)
			{
				var selectable = selectables[i];
				if (selectable == null) continue;
				if (!ShouldInstall(selectable)) continue;

				if (selectable.GetComponent<UiHoverScale>() == null)
					selectable.gameObject.AddComponent<UiHoverScale>();
				installed++;
			}
			return installed;
		}

		/// <summary>单个控件安装（外部代码手动创建按钮后调用）</summary>
		public static UiHoverScale Ensure(Selectable selectable)
		{
			if (selectable == null) return null;
			var hover = selectable.GetComponent<UiHoverScale>();
			return hover != null ? hover : selectable.gameObject.AddComponent<UiHoverScale>();
		}

		private static bool ShouldInstall(Selectable selectable)
		{
			// 滚动条 / 滑条本身就是拖拽控件，缩放会破坏手感
			if (selectable is Scrollbar || selectable is Slider) return false;

			// 没有 targetGraphic 的控件收不到指针事件（点击区域在别处）
			if (selectable.targetGraphic == null) return false;

			return ShouldScale(selectable.transform);
		}

		private static bool ShouldScale(Transform target)
		{
			string lower = target.name.ToLowerInvariant();
			for (int i = 0; i < k_skipNameKeywords.Length; i++)
			{
				if (lower.Contains(k_skipNameKeywords[i])) return false;
			}

			var rect = target as RectTransform;
			if (rect == null) return true;

			// 铺满父级的点击遮罩（锚点四边贴合、无偏移）：放大只会把遮罩推出屏幕边缘
			bool fullStretch = rect.anchorMin == Vector2.zero && rect.anchorMax == Vector2.one
				&& rect.offsetMin.sqrMagnitude < 0.01f && rect.offsetMax.sqrMagnitude < 0.01f;
			return !fullStretch;
		}
	}
}
