using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class MetaUpgradePanelData : IUIData
	{
	}

	/// <summary>
	/// 永久升级面板：升级项的列表展示与购买入口。
	/// 纯显示层——数据读写全部委托 MetaManager，面板不持有业务状态，与存档共享同一数据源。
	/// 列表项优先克隆预制体母版，缺失时按同构布局用代码重建，使最小预制体只需根节点与本组件。
	/// </summary>
	public partial class MetaUpgradePanel : UIPanel
	{
		private readonly List<GameObject> mDynamicRows = new();

		protected override void OnInit(IUIData data = null)
		{
			mData = data as MetaUpgradePanelData ?? new MetaUpgradePanelData();
			ResolveRefs();
			BindBack();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as MetaUpgradePanelData ?? mData;
			EnsureLayout();
			RefreshList();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		protected override void OnClose()
		{
			ClearRows();
		}

		// ===== 引用解析 =====

		private void ResolveRefs()
		{
			if (mTitleText == null) mTitleText = FindComponent<Text>("TitleText");
			if (mCoinText == null) mCoinText = FindComponent<Text>("CoinText");
			if (mBackButton == null) mBackButton = FindComponent<Button>("BackButton");

			if (mListContent == null)
			{
				var content = FindChild(transform, "Content");
				if (content != null)
				{
					mListContent = content;
				}
				else
				{
					var listGroup = FindChild(transform, "ListGroup");
					if (listGroup != null)
					{
						var viewport = FindChild(listGroup, "Viewport");
						mListContent = viewport != null
							? (FindChild(viewport, "Content") ?? viewport)
							: listGroup;
					}
				}
			}

			if (mRowTemplate == null && mListContent != null)
			{
				var tpl = FindChild(mListContent, "MetaUpgradeRow");
				if (tpl == null && mListContent.childCount > 0)
					tpl = mListContent.GetChild(0);   // 兜底：Content 下首个预置子节点即母版
				if (tpl != null) mRowTemplate = tpl.gameObject;
			}
			// 母版仅作克隆源：运行时隐藏，不参与列表显示
			if (mRowTemplate != null) mRowTemplate.SetActive(false);
		}

		/// <summary>预制体节点缺失时的兜底：按同构布局重建</summary>
		private void EnsureLayout()
		{
			if (mListContent != null) return;
			BuildLayout();
			BindBack();
		}

		private void BindBack()
		{
			if (mBackButton == null) return;
			mBackButton.onClick.RemoveAllListeners();
			mBackButton.onClick.AddListener(() =>
			{
				PlayClick();
				CloseSelf();
			});
		}

		// ===== 列表刷新 =====

		private void RefreshList()
		{
			if (mCoinText != null && MetaManager.HasInstance)
				mCoinText.text = $"钱币：{MetaManager.Instance.Coins}";

			ClearRows();

			var entries = GameDataMgr.Instance.GetMetaUpgrade()?.Entries;
			if (!MetaManager.HasInstance || entries == null || entries.Count == 0)
			{
				CreateEmptyHint("  暂无可升级项");
				return;
			}

			foreach (var e in entries)
			{
				if (e == null || string.IsNullOrEmpty(e.id)) continue;
				CreateRow(e);
			}

			// 升级后列表重建：补挂按钮悬停缩放
			UiHoverInstaller.Install(gameObject);
		}

		/// <summary>创建一条升级项（母版可用则克隆，否则代码构建）</summary>
		private void CreateRow(MetaUpgradeEntry entry)
		{
			GameObject row = CreateRowObject();
			row.name = "Row_" + entry.id;
			row.SetActive(true);

			// 保证行高（母版可能未设置 LayoutElement）
			var le = row.GetComponent<LayoutElement>();
			if (le == null) le = row.AddComponent<LayoutElement>();
			le.preferredHeight = 110f;
			le.minHeight = 110f;

			var meta = MetaManager.Instance;
			int level = meta.GetUpgradeLevel(entry.id);
			int cost = meta.GetUpgradeCost(entry.id);
			bool maxed = cost < 0;
			bool affordable = !maxed && meta.Coins >= cost;

			var nameText = FindRowText(row.transform, "NameText");
			if (nameText != null) nameText.text = entry.name;

			var descText = FindRowText(row.transform, "DescText");
			if (descText != null) descText.text = $"{entry.desc}{FormatValue(entry, level)}/级";

			var levelText = FindRowText(row.transform, "LevelText");
			if (levelText != null) levelText.text = $"Lv {level}/{entry.maxLevel}";

			var costText = FindRowText(row.transform, "CostText");
			if (costText != null) costText.text = maxed ? "已满级" : $"{cost} 币";

			var btn = FindRowButton(row.transform, "UpgradeButton");
			if (btn != null)
			{
				string id = entry.id;
				btn.onClick.RemoveAllListeners();
				btn.onClick.AddListener(() => OnUpgradeClicked(id));
				btn.interactable = affordable;

				var label = btn.GetComponentInChildren<Text>();
				if (label != null) label.text = maxed ? "已满级" : (affordable ? "升级" : "钱币不足");
			}

			mDynamicRows.Add(row);
		}

		private GameObject CreateRowObject()
		{
			if (mRowTemplate != null)
			{
				var clone = Instantiate(mRowTemplate, mListContent, false);
				if (FindChild(clone.transform, "NameText") != null) return clone;
				Destroy(clone);   // 母版缺关键子节点 → 退化为代码构建
			}
			return BuildRowObject();
		}

		private void OnUpgradeClicked(string id)
		{
			if (!MetaManager.HasInstance) return;
			if (MetaManager.Instance.TryUpgrade(id))
			{
				PlayClick();
				RefreshList();
			}
		}

		/// <summary>当前「下一级」增量展示（第 L+1 级增量 = valuePerLevel × (1 + valueGrowth × L)）</summary>
		private static string FormatValue(MetaUpgradeEntry entry, int level)
		{
			float v = entry.valuePerLevel * (1f + entry.valueGrowth * level);
			return IsPercentAttr(entry.attrKey) ? $"+{v * 100f:F1}%" : $"+{v:0.##}";
		}

		/// <summary>比例型属性（0~1 展示为百分比）</summary>
		private static bool IsPercentAttr(string attrKey)
		{
			return attrKey == "DodgeChance" || attrKey == "CritChance"
				|| attrKey == "PhysDamageBonus" || attrKey == "ElemDamageBonus";
		}

		// ===== 空提示 / 清理 =====

		private void CreateEmptyHint(string msg)
		{
			if (mListContent == null) return;
			var text = CreateText(mListContent, "EmptyHint", msg, 24, new Color(0.75f, 0.75f, 0.8f), TextAnchor.MiddleCenter);
			var rt = text.rectTransform;
			rt.anchorMin = new Vector2(0f, 1f);
			rt.anchorMax = new Vector2(1f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.sizeDelta = new Vector2(0f, 60f);
			rt.anchoredPosition = Vector2.zero;

			var le = text.gameObject.AddComponent<LayoutElement>();
			le.preferredHeight = 60f;
			le.minHeight = 60f;

			mDynamicRows.Add(text.gameObject);
		}

		/// <summary>清理列表：仅销毁运行时创建的条目，母版/预置节点只隐藏（模板保护）</summary>
		private void ClearRows()
		{
			if (mListContent == null) return;
			for (int i = mListContent.childCount - 1; i >= 0; i--)
			{
				var child = mListContent.GetChild(i).gameObject;
				if (mDynamicRows.Contains(child))
				{
					mDynamicRows.Remove(child);
					Destroy(child);
				}
				else
				{
					child.SetActive(false);
				}
			}
			mDynamicRows.Clear();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// ===== 代码构建布局 =====

		private void BuildLayout()
		{
			// 全屏半透明遮罩
			var backdropGo = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var backdropRt = backdropGo.GetComponent<RectTransform>();
			backdropRt.SetParent(transform, false);
			backdropRt.anchorMin = Vector2.zero;
			backdropRt.anchorMax = Vector2.one;
			backdropRt.offsetMin = Vector2.zero;
			backdropRt.offsetMax = Vector2.zero;
			backdropGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

			// 面板背板（居中）
			var boardGo = new GameObject("Board", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var boardRt = boardGo.GetComponent<RectTransform>();
			boardRt.SetParent(transform, false);
			boardRt.anchorMin = new Vector2(0.5f, 0.5f);
			boardRt.anchorMax = new Vector2(0.5f, 0.5f);
			boardRt.pivot = new Vector2(0.5f, 0.5f);
			boardRt.sizeDelta = new Vector2(1100f, 760f);
			boardRt.anchoredPosition = Vector2.zero;
			boardGo.GetComponent<Image>().color = new Color(0.13f, 0.15f, 0.2f, 0.98f);

			Transform board = boardRt;

		var titleText = CreateText(board, "TitleText", "永久升级", 40, Color.white, TextAnchor.MiddleCenter);
			var titleRt = titleText.rectTransform;
			titleRt.anchorMin = new Vector2(0.5f, 1f);
			titleRt.anchorMax = new Vector2(0.5f, 1f);
			titleRt.pivot = new Vector2(0.5f, 1f);
			titleRt.sizeDelta = new Vector2(600f, 60f);
			titleRt.anchoredPosition = new Vector2(0f, -40f);
			mTitleText = titleText;

		var coinText = CreateText(board, "CoinText", "", 28, new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleRight);
			var coinRt = coinText.rectTransform;
			coinRt.anchorMin = new Vector2(1f, 1f);
			coinRt.anchorMax = new Vector2(1f, 1f);
			coinRt.pivot = new Vector2(1f, 1f);
			coinRt.sizeDelta = new Vector2(380f, 50f);
			coinRt.anchoredPosition = new Vector2(-32f, -46f);
			mCoinText = coinText;

		var backBtn = CreateButton(board, "BackButton", "返回", new Color(0.28f, 0.32f, 0.4f), 26);
			var backRt = backBtn.GetComponent<RectTransform>();
			backRt.anchorMin = new Vector2(0.5f, 0f);
			backRt.anchorMax = new Vector2(0.5f, 0f);
			backRt.pivot = new Vector2(0.5f, 0f);
			backRt.sizeDelta = new Vector2(220f, 64f);
			backRt.anchoredPosition = new Vector2(0f, 28f);
			mBackButton = backBtn;

		var listGo = new GameObject("ListGroup", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
			var listRt = listGo.GetComponent<RectTransform>();
			listRt.SetParent(board, false);
			listRt.anchorMin = Vector2.zero;
			listRt.anchorMax = Vector2.one;
			listRt.offsetMin = new Vector2(40f, 116f);   // left / bottom
			listRt.offsetMax = new Vector2(-40f, -110f); // right / top
			listGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);

			var scroll = listGo.GetComponent<ScrollRect>();
			scroll.horizontal = false;
			scroll.vertical = true;
			scroll.movementType = ScrollRect.MovementType.Clamped;
			scroll.scrollSensitivity = 30f;

		var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var viewportRt = viewportGo.GetComponent<RectTransform>();
			viewportRt.SetParent(listRt, false);
			viewportRt.anchorMin = Vector2.zero;
			viewportRt.anchorMax = Vector2.one;
			viewportRt.offsetMin = Vector2.zero;
			viewportRt.offsetMax = Vector2.zero;
			viewportGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);
			viewportGo.AddComponent<RectMask2D>();

		var contentGo = new GameObject("Content", typeof(RectTransform));
			var contentRt = contentGo.GetComponent<RectTransform>();
			contentRt.SetParent(viewportRt, false);
			contentRt.anchorMin = new Vector2(0f, 1f);
			contentRt.anchorMax = new Vector2(1f, 1f);
			contentRt.pivot = new Vector2(0.5f, 1f);
			contentRt.sizeDelta = Vector2.zero;
			contentRt.anchoredPosition = Vector2.zero;

			var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
			vlg.spacing = 14f;
			vlg.padding = new RectOffset(12, 12, 12, 12);
			vlg.childAlignment = TextAnchor.UpperCenter;
			vlg.childControlWidth = true;
			vlg.childControlHeight = true;
			vlg.childForceExpandWidth = true;
			vlg.childForceExpandHeight = false;

			var fitter = contentGo.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			scroll.viewport = viewportRt;
			scroll.content = contentRt;

			mListContent = contentGo.transform;
		}

		/// <summary>母版缺失时的兜底：代码构建单行</summary>
		private GameObject BuildRowObject()
		{
			var rowGo = new GameObject("MetaUpgradeRow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var rowRt = rowGo.GetComponent<RectTransform>();
			rowRt.SetParent(mListContent, false);
			rowGo.GetComponent<Image>().color = new Color(0.22f, 0.24f, 0.3f, 0.9f);

		var nameText = CreateText(rowRt, "NameText", "", 30, Color.white, TextAnchor.MiddleLeft);
			var nameRt = nameText.rectTransform;
			nameRt.anchorMin = new Vector2(0f, 1f);
			nameRt.anchorMax = new Vector2(0f, 1f);
			nameRt.pivot = new Vector2(0f, 1f);
			nameRt.sizeDelta = new Vector2(520f, 44f);
			nameRt.anchoredPosition = new Vector2(24f, -14f);

		var descText = CreateText(rowRt, "DescText", "", 22, new Color(0.8f, 0.8f, 0.85f), TextAnchor.MiddleLeft);
			var descRt = descText.rectTransform;
			descRt.anchorMin = new Vector2(0f, 1f);
			descRt.anchorMax = new Vector2(0f, 1f);
			descRt.pivot = new Vector2(0f, 1f);
			descRt.sizeDelta = new Vector2(620f, 40f);
			descRt.anchoredPosition = new Vector2(24f, -60f);

		var levelText = CreateText(rowRt, "LevelText", "", 24, new Color(0.7f, 0.85f, 1f), TextAnchor.MiddleCenter);
			var levelRt = levelText.rectTransform;
			levelRt.anchorMin = new Vector2(1f, 0.5f);
			levelRt.anchorMax = new Vector2(1f, 0.5f);
			levelRt.pivot = new Vector2(1f, 0.5f);
			levelRt.sizeDelta = new Vector2(170f, 50f);
			levelRt.anchoredPosition = new Vector2(-330f, 0f);

		var costText = CreateText(rowRt, "CostText", "", 24, new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleCenter);
			var costRt = costText.rectTransform;
			costRt.anchorMin = new Vector2(1f, 0.5f);
			costRt.anchorMax = new Vector2(1f, 0.5f);
			costRt.pivot = new Vector2(1f, 0.5f);
			costRt.sizeDelta = new Vector2(150f, 50f);
			costRt.anchoredPosition = new Vector2(-200f, 0f);

		var btn = CreateButton(rowRt, "UpgradeButton", "升级", new Color(0.3f, 0.55f, 0.35f), 26);
			var btnRt = btn.GetComponent<RectTransform>();
			btnRt.anchorMin = new Vector2(1f, 0.5f);
			btnRt.anchorMax = new Vector2(1f, 0.5f);
			btnRt.pivot = new Vector2(1f, 0.5f);
			btnRt.sizeDelta = new Vector2(160f, 64f);
			btnRt.anchoredPosition = new Vector2(-24f, 0f);

			return rowGo;
		}

		// ===== UI 工具 =====

		private static Text CreateText(Transform parent, string name, string text, int fontSize, Color color, TextAnchor anchor)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			var t = go.GetComponent<Text>();
			t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			t.fontSize = fontSize;
			t.color = color;
			t.alignment = anchor;
			t.text = text;
			t.horizontalOverflow = HorizontalWrapMode.Overflow;
			t.verticalOverflow = VerticalWrapMode.Overflow;
			return t;
		}

		private static Button CreateButton(Transform parent, string name, string label, Color bg, int fontSize)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			var img = go.GetComponent<Image>();
			img.color = bg;
			var btn = go.GetComponent<Button>();
			btn.targetGraphic = img;

			var text = CreateText(rt, "Text", label, fontSize, Color.white, TextAnchor.MiddleCenter);
			var textRt = text.rectTransform;
			textRt.anchorMin = Vector2.zero;
			textRt.anchorMax = Vector2.one;
			textRt.offsetMin = Vector2.zero;
			textRt.offsetMax = Vector2.zero;
			return btn;
		}

		private T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
		}

		private static Text FindRowText(Transform row, string nodeName)
		{
			var node = FindChild(row, nodeName);
			return node != null ? node.GetComponent<Text>() : null;
		}

		private static Button FindRowButton(Transform row, string nodeName)
		{
			var node = FindChild(row, nodeName);
			return node != null ? node.GetComponent<Button>() : null;
		}

		/// <summary>深度优先按名称查找子节点（不包含自身）</summary>
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
	}
}
