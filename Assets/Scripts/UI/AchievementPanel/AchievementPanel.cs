using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class AchievementPanelData : IUIData { }

	/// <summary>
	/// 成就面板：进度总览、成就列表、选中详情与奖励领取。
	/// 纯显示层——状态判定与发奖全部委托 AchievementManager，面板只维护当前选中项。
	/// 列表按「待领取 → 未完成 → 已领取」排序，把可操作项顶到最前。
	/// 列表项为运行时克隆体，子节点只能按名解析。
	/// </summary>
	public partial class AchievementPanel : UIPanel
	{
		/// <summary>选中标记子节点名（预制体约定）</summary>
		private const string SelectedNodeName = "Selected";

		/// <summary>选中成就 ID；默认取第一个可领取项</summary>
		private string mSelectedId;

		/// <summary>运行时列表项（关闭时销毁）</summary>
		private readonly List<GameObject> mItems = new();

		/// <summary>成就ID → 运行时列表项，用于快速刷新选中标记</summary>
		private readonly Dictionary<string, GameObject> mItemMap = new();

		protected override void OnInit(IUIData data = null)
		{
			mData = data as AchievementPanelData ?? new AchievementPanelData();

			if (mCloseButton != null)
			{
				mCloseButton.onClick.RemoveAllListeners();
				mCloseButton.onClick.AddListener(CloseSelf);
			}
			if (mClaimButton != null)
			{
				mClaimButton.onClick.RemoveAllListeners();
				mClaimButton.onClick.AddListener(OnClaimClicked);
			}
			// 未绑定时退化：取列表首个预置子节点作为母版
			if (mItemTemplate == null && mListRoot != null && mListRoot.childCount > 0)
				mItemTemplate = mListRoot.GetChild(0).gameObject;
			if (mItemTemplate != null) mItemTemplate.SetActive(false);

			if (mListRoot == null) LogKit.Warning("[AchievementPanel] 未绑定 ListRoot，无法显示成就列表");
		}

		protected override void OnOpen(IUIData data = null)
		{
			Rebuild();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);

		protected override void OnClose()
		{
			ClearItems();
		}

		// ===== 列表构建 =====

		private void Rebuild()
		{
			ClearItems();
			var mgr = AchievementManager.Instance;
			if (mgr == null) return;

		if (mProgressText != null)
				mProgressText.text = $"已完成 {mgr.GetUnlockedCount()} / {mgr.Configs.Count}";
			if (mPointsText != null)
				mPointsText.text = $"成就点数：{mgr.TotalPoints}";

			// 排序：待领取(已完成未领) → 未完成(按分类) → 已领取(已完成已领)
			var sorted = new List<AchievementConfig>(mgr.Configs);
			sorted.Sort((a, b) =>
			{
				int ga = SortGroup(mgr, a);
				int gb = SortGroup(mgr, b);
				if (ga != gb) return ga.CompareTo(gb);
				// 同组（未完成）内按分类排序
				return a.category.CompareTo(b.category);
			});

		mSelectedId = null;
			foreach (var cfg in sorted)
			{
				if (mgr.CanClaim(cfg.achievementId))
				{
					mSelectedId = cfg.achievementId;
					break;
				}
			}
			if (mSelectedId == null && sorted.Count > 0)
				mSelectedId = sorted[0].achievementId;

		foreach (var cfg in sorted)
			{
				var go = CreateItem(cfg);
				mItems.Add(go);
			}

			RefreshSelectionVisual();
			RefreshDetail();

			// 切换分类会重建条目：补挂按钮悬停缩放（已挂过的跳过）
			UiHoverInstaller.Install(gameObject);
		}

		/// <summary>排序分组：0=待领取（已完成未领） 1=未完成 2=已领取（已完成已领）</summary>
		private static int SortGroup(AchievementManager mgr, AchievementConfig cfg)
		{
			if (!mgr.IsUnlocked(cfg.achievementId))
				return 1; // 未完成 → 中间，按分类排
			return mgr.IsClaimed(cfg.achievementId) ? 2 : 0; // 已领 → 最下；未领 → 最上
		}

		/// <summary>实例化一个列表项（模板；根 Button 点击选中）</summary>
		private GameObject CreateItem(AchievementConfig cfg)
		{
			var go = mItemTemplate != null
				? Instantiate(mItemTemplate, mListRoot)
				: CreateDefaultItem();
			go.name = "AchievementItem_" + cfg.achievementId;
			go.SetActive(true);

			var progress = AchievementManager.Instance.GetProgress(cfg.achievementId);
			bool unlocked = progress != null && progress.isUnlocked;
			bool secret = cfg.isSecret && !unlocked;

			// 子节点按名解析
			SetIcon(go.transform, "Icon", secret ? "" : cfg.iconPath);
			SetText(go.transform, "Name", secret ? "???" : cfg.title);
			SetText(go.transform, "Desc", secret ? cfg.secretDescription : cfg.description);

		var btn = go.GetComponent<Button>();
			if (btn != null)
			{
				btn.onClick.RemoveAllListeners();
				btn.onClick.AddListener(() => OnItemClicked(cfg.achievementId));
			}

			// 选中标记：新建时先隐藏，由 RefreshSelectionVisual 统一决定显示与否
			SetSelectedMark(go, false);

			mItemMap[cfg.achievementId] = go;
			return go;
		}

		/// <summary>点击列表项：选中并刷新详情</summary>
		private void OnItemClicked(string achievementId)
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			mSelectedId = achievementId;
			RefreshSelectionVisual();
			RefreshDetail();
		}

		/// <summary>刷新列表项选中标记：仅当前选中项的 Selected 子节点显示</summary>
		private void RefreshSelectionVisual()
		{
			foreach (var kv in mItemMap)
			{
				if (kv.Value == null) continue;
				SetSelectedMark(kv.Value, kv.Key == mSelectedId);
			}
		}

		/// <summary>设置单个列表项的 Selected 子节点显隐（节点不存在则忽略）</summary>
		private static void SetSelectedMark(GameObject item, bool selected)
		{
			if (item == null) return;
			var mark = FindChild(item.transform, SelectedNodeName);
			if (mark != null && mark.gameObject.activeSelf != selected)
				mark.gameObject.SetActive(selected);
		}

		// ===== 详情 =====

		/// <summary>刷新选中项的 Detail 详情 + 领取按钮状态</summary>
		private void RefreshDetail()
		{
			var mgr = AchievementManager.Instance;
			if (mgr == null || string.IsNullOrEmpty(mSelectedId)) return;

			var cfg = mgr.GetConfig(mSelectedId);
			var progress = mgr.GetProgress(mSelectedId);
			if (cfg == null) return;

			bool unlocked = progress != null && progress.isUnlocked;
			bool claimed = mgr.IsClaimed(mSelectedId);
			bool secret = cfg.isSecret && !unlocked;

			SetImage(mDetailIcon, secret ? "" : cfg.iconPath);
			if (mDetailName != null) mDetailName.text = secret ? "???" : cfg.title;
			if (mDetailDesc != null) mDetailDesc.text = secret ? cfg.secretDescription : BuildDescText(cfg);
			if (mDetailProgress != null) mDetailProgress.text = BuildProgressText(cfg, progress);
			if (mDetailReward != null) mDetailReward.text = BuildRewardsText(cfg);
			if (mDetailState != null)
			{
				mDetailState.text = !unlocked ? "未达成"
					: claimed ? "已领取"
					: "可领取";
				mDetailState.color = !unlocked ? Color.gray
					: claimed ? new Color(0.6f, 0.85f, 0.6f)
					: new Color(1f, 0.8f, 0.2f);
			}

		if (mClaimButton != null)
				mClaimButton.interactable = mgr.CanClaim(mSelectedId);
		}

		/// <summary>点击领取：发奖 + 刷新详情/列表（保持选中）</summary>
		private void OnClaimClicked()
		{
			if (string.IsNullOrEmpty(mSelectedId)) return;

			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			if (AchievementManager.Instance.ClaimReward(mSelectedId))
			{
				RefreshDetail();
				if (mPointsText != null)
					mPointsText.text = $"成就点数：{AchievementManager.Instance.TotalPoints}";
			}
		}

		// ===== 文本/图标构建 =====

		/// <summary>成就要求说明：拼接各条件的目标描述</summary>
		private static string BuildDescText(AchievementConfig cfg)
		{
			if (cfg.conditions == null || cfg.conditions.Count == 0) return cfg.description;
			var parts = new List<string>();
			foreach (var c in cfg.conditions)
			{
				parts.Add(ConditionDesc(c.type, EffectiveTarget(cfg, c)));
			}
			return string.Join("；", parts);
		}

		/// <summary>进度文本：当前/目标（多条件逐行）</summary>
		private static string BuildProgressText(AchievementConfig cfg, AchievementSave progress)
		{
			if (cfg.conditions == null || cfg.conditions.Count == 0) return "";
			var lines = new List<string>();
			for (int i = 0; i < cfg.conditions.Count; i++)
			{
				int current = progress != null && i < progress.conditionCurrent.Count ? progress.conditionCurrent[i] : 0;
				int target = EffectiveTarget(cfg, cfg.conditions[i]);
				lines.Add($"{Mathf.Min(current, target)}/{target}");
			}
			return string.Join("  ", lines);
		}

		/// <summary>奖励说明</summary>
		private static string BuildRewardsText(AchievementConfig cfg)
		{
			if (cfg.rewards == null || cfg.rewards.Count == 0) return "无奖励";
			var parts = new List<string>();
			foreach (var r in cfg.rewards)
				parts.Add(RewardText(r));
			return string.Join("，", parts);
		}

		private static string RewardText(AchievementReward r)
		{
			switch (r.type)
			{
				case AchievementRewardType.Gold: return $"金币 ×{r.amount}";
				case AchievementRewardType.Ingredient:
					var ing = GameDataMgr.Instance.GetIngredient(r.rewardId);
					return $"{(ing != null ? ing.ingredientName : r.rewardId)} ×{r.amount}";
				case AchievementRewardType.RecipeUnlock: return "解锁菜谱";
				case AchievementRewardType.CharacterUnlock: return "解锁角色";
				case AchievementRewardType.WeaponUnlock: return "解锁武器";
				default: return "";
			}
		}

		/// <summary>条件类型 → 中文描述（含目标值）</summary>
		private static string ConditionDesc(AchievementConditionType type, int target)
		{
			switch (type)
			{
				case AchievementConditionType.TotalKills: return $"累计击杀 {target} 个敌人";
				case AchievementConditionType.WaveReached: return $"到达第 {target} 波";
				case AchievementConditionType.TotalCooking: return $"累计烹饪 {target} 次";
				case AchievementConditionType.PerfectCooking: return $"完美判定 {target} 次";
				case AchievementConditionType.BattlefieldCookingTriggered: return $"战场炒菜触发 {target} 次";
				case AchievementConditionType.BossKills: return $"击败 {target} 个Boss";
				case AchievementConditionType.RecipeCollectionCount: return $"图鉴收集 {target} 道菜品";
				case AchievementConditionType.TotalGoldEarned: return $"累计获得 {target} 金币";
				case AchievementConditionType.GamesPlayed: return $"游玩 {target} 局";
				case AchievementConditionType.MaxComboChain: return $"历史最高连招达到 {target}";
				default: return $"达成目标 {target}";
			}
		}

		private static int EffectiveTarget(AchievementConfig cfg, AchievementCondition cond)
		{
			if (cond.type == AchievementConditionType.RecipeCollectionCount && cond.targetValue >= 999)
				return RecipeAtlasManager.HasInstance ? RecipeAtlasManager.Instance.TotalCount : 0;
			return cond.targetValue;
		}

		// ===== 兜底列表项（模板缺失时） =====

		private GameObject CreateDefaultItem()
		{
			var go = new GameObject("AchievementItem", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var rt = (RectTransform)go.transform;
			rt.SetParent(mListRoot, false);
			rt.sizeDelta = new Vector2(880f, 96f);
			go.GetComponent<Image>().color = new Color(0.2f, 0.24f, 0.32f);

			// Icon 占位（无模板时省略实际图标，只留 Name/Desc）
			CreateText(go.transform, "Name", TextAnchor.MiddleLeft, new Vector2(20f, 26f), 26);
			CreateText(go.transform, "Desc", TextAnchor.MiddleLeft, new Vector2(20f, -22f), 18);
			return go;
		}

		// ===== 通用小工具 =====

		/// <summary>设置图标（Image.sprite）；path 空则隐藏</summary>
		private static void SetIcon(Transform parent, string name, string path)
		{
			var img = FindChild(parent, name)?.GetComponent<Image>();
			if (img == null) return;
			if (string.IsNullOrEmpty(path))
			{
				img.gameObject.SetActive(false);
				return;
			}
			var sprite = Resources.Load<Sprite>(path);
			if (sprite == null)
			{
				img.gameObject.SetActive(false);
				return;
			}
			img.sprite = sprite;
			img.gameObject.SetActive(true);
		}

		private static void SetImage(Image img, string path)
		{
			if (img == null) return;
			if (string.IsNullOrEmpty(path))
			{
				img.gameObject.SetActive(false);
				return;
			}
			var sprite = Resources.Load<Sprite>(path);
			if (sprite == null)
			{
				img.gameObject.SetActive(false);
				return;
			}
			img.sprite = sprite;
			img.gameObject.SetActive(true);
		}

		private static void SetText(Transform parent, string name, string text)
		{
			var t = FindChild(parent, name)?.GetComponent<Text>();
			if (t != null) t.text = text;
		}

		private static void CreateText(Transform parent, string name, TextAnchor align, Vector2 offset, int fontSize)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = new Vector2(offset.x, 0f);
			rt.offsetMax = new Vector2(offset.x, 0f);

			var text = go.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = fontSize;
			text.alignment = align;
			text.color = Color.white;
			text.raycastTarget = false;
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

		private void ClearItems()
		{
			foreach (var go in mItems)
				if (go != null) Destroy(go);
			mItems.Clear();
			mItemMap.Clear();
		}

		private void CloseSelf()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			UIKit.ClosePanel<AchievementPanel>();
		}
	}
}
