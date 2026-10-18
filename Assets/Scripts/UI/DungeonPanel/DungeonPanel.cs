using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class DungeonPanelData : IUIData
	{
	}

	/// <summary>
	/// 副本选择面板：副本列表、选中详情与进入战前准备的入口。
	/// 纯显示层——选关状态读写委托 MetaManager，关卡配置取自 GameDataMgr，面板只维护选中项。
	/// 节点结构由预制体约定；母版缺失时按同构布局用代码构建列表项。
	/// </summary>
	public partial class DungeonPanel : UIPanel
	{
		private readonly List<GameObject> mItems = new();
		private DungeonConfig mSelected;             // 选中副本（null = 未选中）

		protected override void OnInit(IUIData data = null)
		{
			mData = data as DungeonPanelData ?? new DungeonPanelData();
			ResolveRefs();
			BindButtons();
			SetDetailVisible(false);
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as DungeonPanelData ?? mData;
			if (mTitleText != null) mTitleText.text = "副本选择";
			RebuildList();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		protected override void OnClose()
		{
			ClearItems();
			mSelected = null;
		}

		// ===== 引用解析 =====

		private void ResolveRefs()
		{
			if (mTitleText == null) mTitleText = FindComponent<Text>("TitleText");
			if (mConfirmButton == null) mConfirmButton = FindComponent<Button>("ConfirmButton");
			if (mBackButton == null) mBackButton = FindComponent<Button>("BackButton");

			if (mListContent == null)
			{
				var group = FindChild(transform, "DungeonGroup");
				if (group != null)
				{
					var viewport = FindChild(group, "Viewport");
					var content = viewport != null ? FindChild(viewport, "Content") : null;
					mListContent = content ?? group;
				}
			}
			if (mItemTemplate == null && mListContent != null)
			{
				var tpl = FindChild(mListContent, "DungeonItemTemplate");
				// 名称未约定时退化为首个子节点：兼容嵌套在 Content 下的模板
				if (tpl == null && mListContent.childCount > 0)
					tpl = mListContent.GetChild(0);
				if (tpl != null) mItemTemplate = tpl.gameObject;
			}
			// 母版仅作克隆源：运行时隐藏，不参与列表显示（实例克隆后单独激活）
			if (mItemTemplate != null) mItemTemplate.SetActive(false);

			if (mDetailGroup == null)
			{
				var detail = FindChild(transform, "DetailGroup");
				if (detail != null) mDetailGroup = detail.gameObject;
			}
			// DetailGroup 内图标节点名也是 DungeonIcon
			if (mDetailIcon == null) mDetailIcon = FindComponent<Image>("DungeonIcon");
			if (mDetailNameText == null) mDetailNameText = FindComponent<Text>("NameText");
			if (mDetailInfoText == null) mDetailInfoText = FindComponent<Text>("InfoText");
			if (mEnemyOverviewText == null) mEnemyOverviewText = FindComponent<Text>("EnemyOverviewText");
			if (mTipText == null) mTipText = FindComponent<Text>("TipText");

			if (mListContent == null) LogKit.Warning("[DungeonPanel] 未找到 DungeonGroup/Content，无法显示副本列表");
		}

		private void BindButtons()
		{
			if (mConfirmButton != null)
			{
				mConfirmButton.onClick.RemoveAllListeners();
				mConfirmButton.onClick.AddListener(OnConfirm);
			}
			if (mBackButton != null)
			{
				mBackButton.onClick.RemoveAllListeners();
				mBackButton.onClick.AddListener(OnBack);
			}
		}

		// ===== 列表构建 =====

		private void RebuildList()
		{
			ClearItems();
			if (mListContent == null)
			{
				LogKit.Warning("[DungeonPanel] 缺少列表 Content，无法构建副本列表");
				return;
			}

			var dungeons = GameDataMgr.Instance.Dungeons?.Dungeons;
			if (dungeons == null || dungeons.Count == 0)
			{
				LogKit.Warning("[DungeonPanel] 副本配置为空（Resources/Data/DungeonConfig.json）");
				return;
			}

			string selectedId = MetaManager.Instance.SelectedDungeonId;
			foreach (var cfg in dungeons)
			{
				if (cfg == null || string.IsNullOrEmpty(cfg.dungeonId)) continue;
				bool unlocked = MetaManager.Instance.IsDungeonUnlocked(cfg.dungeonId);
				var item = CreateItem(cfg, unlocked, cfg.dungeonId == selectedId);
				mItems.Add(item);
			}

		if (mSelected == null)
			{
				foreach (var cfg in dungeons)
				{
					if (cfg != null && cfg.dungeonId == selectedId && MetaManager.Instance.IsDungeonUnlocked(cfg.dungeonId))
					{
						SelectDungeon(cfg, false);
						break;
					}
				}
			}
			else if (mSelected != null)
			{
				UpdateDetail();
			}

			// 列表重建后补挂按钮悬停缩放
			UiHoverInstaller.Install(gameObject);
		}

		private GameObject CreateItem(DungeonConfig cfg, bool unlocked, bool selected)
		{
			GameObject go = mItemTemplate != null
				? Instantiate(mItemTemplate, mListContent)
				: CreateDefaultItem();

			go.name = "DungeonItem_" + cfg.dungeonId;
			go.SetActive(true);

		var icon = FindChild(go.transform, "DungeonIcon")?.GetComponent<Image>();
			if (icon != null)
			{
				var spr = !string.IsNullOrEmpty(cfg.iconPath) ? Resources.Load<Sprite>(cfg.iconPath) : null;
				icon.gameObject.SetActive(unlocked && spr != null);
				if (spr != null) icon.sprite = spr;
			}

		var nameText = FindChild(go.transform, "NameText")?.GetComponent<Text>();
			if (nameText != null) nameText.text = cfg.dungeonName;
			var infoText = FindChild(go.transform, "InfoText")?.GetComponent<Text>();
			if (infoText != null) infoText.text = cfg.info;

		var selectedMask = FindChild(go.transform, "SelectedMask");
			if (selectedMask != null) selectedMask.gameObject.SetActive(selected);
			var lockMask = FindChild(go.transform, "LockMask");
			if (lockMask != null) lockMask.gameObject.SetActive(!unlocked);

		var button = go.GetComponent<Button>();
			if (button != null)
			{
				button.interactable = unlocked;
				button.onClick.RemoveAllListeners();
				DungeonConfig dungeon = cfg;
				button.onClick.AddListener(() => SelectDungeon(dungeon, true));
			}

			return go;
		}

		private GameObject CreateDefaultItem()
		{
			var go = new GameObject("DungeonItem", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var rt = (RectTransform)go.transform;
			rt.SetParent(mListContent, false);
			rt.sizeDelta = new Vector2(320f, 90f);
			go.GetComponent<Image>().color = new Color(0.25f, 0.32f, 0.45f);

			var nameGo = new GameObject("NameText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var nameRt = nameGo.GetComponent<RectTransform>();
			nameRt.SetParent(rt, false);
			nameRt.anchorMin = Vector2.zero;
			nameRt.anchorMax = Vector2.one;
			nameRt.offsetMin = Vector2.zero;
			nameRt.offsetMax = Vector2.zero;
			var text = nameGo.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 26;
			text.alignment = TextAnchor.MiddleCenter;
			text.color = Color.white;
			return go;
		}

		// ===== 选中 / 详情 =====

		private void SelectDungeon(DungeonConfig cfg, bool playClick)
		{
			if (cfg == null || !MetaManager.Instance.SelectDungeon(cfg.dungeonId)) return;
			mSelected = cfg;
			SetDetailVisible(true);
			UpdateDetail();
			RebuildList(); // 刷新选中遮罩
			if (playClick) PlayClick();
		}

		private void UpdateDetail()
		{
			if (mSelected == null) return;
			var cfg = mSelected;

			if (mDetailIcon != null)
			{
				var spr = !string.IsNullOrEmpty(cfg.iconPath) ? Resources.Load<Sprite>(cfg.iconPath) : null;
				mDetailIcon.sprite = spr;
				mDetailIcon.color = spr != null ? Color.white : new Color(1f, 1f, 1f, 0.15f);
			}
			if (mDetailNameText != null) mDetailNameText.text = cfg.dungeonName;
			if (mDetailInfoText != null) mDetailInfoText.text = cfg.info;
			if (mEnemyOverviewText != null) mEnemyOverviewText.text = cfg.enemyOverview;
			if (mTipText != null) mTipText.text = cfg.tip;
		}

		/// <summary>DetailGroup 显隐（未选中副本前隐藏）</summary>
		private void SetDetailVisible(bool visible)
		{
			if (mDetailGroup != null && mDetailGroup.activeSelf != visible)
				mDetailGroup.SetActive(visible);
		}

		// ===== 交互 =====

		private void OnConfirm()
		{
			PlayClick();
			UIKit.ClosePanel<DungeonPanel>();
			UIKit.OpenPanel<PreparationPanel>(UILevel.Pop);
		}

		private void OnBack()
		{
			PlayClick();
			UIKit.ClosePanel<DungeonPanel>();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// ===== 工具 =====

		private void ClearItems()
		{
			foreach (var go in mItems)
				if (go != null) Destroy(go);
			mItems.Clear();
		}

		private T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
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
