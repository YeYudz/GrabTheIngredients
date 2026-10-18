using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class InventoryPanelData : IUIData
	{
	}
	/// <summary>
	/// 背包面板：菜品实例的筛选、展示与合成。
	/// 定位为纯显示层——所有数据读写委托 MetaManager，面板不持有业务状态，
	/// 使存档与 UI 共享同一数据源，避免双写不一致。
	/// 合成采用两段式提交：先扣材料、确认后才入库，关闭面板时回补，避免异常退出丢道具。
	/// 列表每次刷新全量重建：格子由母版克隆，选中/已装备仅切换预制体节点显隐。
	/// </summary>
	public partial class InventoryPanel : UIPanel
	{
		private readonly List<GameObject> mDynamicItems = new();
		private bool mUIBuilt;

		/// <summary>星级筛选：0 = 全部，1~5 = 对应星级</summary>
		private int mStarFilter;

		/// <summary>合成台已选材料；上限 combineRequiredCount，须同星同种</summary>
		private readonly List<DishInstance> mCraftSelection = new();

		/// <summary>待确认的合成结果（材料已扣，确认后才入库）</summary>
		private string mPendingRecipeId;
		private int mPendingResultStar;

		/// <summary>格子模板缓存；无本地母版时回退 Resources/Prefabs/UI/InventoryGrid</summary>
		private GameObject mGridTemplateCache;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as InventoryPanelData ?? new InventoryPanelData();
			EnsureUIBuilt();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as InventoryPanelData ?? mData;
			EnsureUIBuilt();
			RefreshList();
			RefreshCraftUI();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		protected override void OnClose()
		{
			// 未确认即关闭：回补为原星级材料，保证两段式提交不丢道具
			if (!string.IsNullOrEmpty(mPendingRecipeId))
			{
				if (MetaManager.HasInstance)
					MetaManager.Instance.AddDish(mPendingRecipeId, mPendingResultStar - 1,
						GameDataMgr.Instance.GetCraftRules().combineRequiredCount);
				mPendingRecipeId = null;
				mPendingResultStar = 0;
			}

			foreach (var go in mDynamicItems)
				if (go != null) Destroy(go);
			mDynamicItems.Clear();
			mCraftSelection.Clear();
			mGridTemplateCache = null;
		}

		// ===== UI 构建：绑定缺失时动态补齐 =====

		private void EnsureUIBuilt()
		{
			if (mUIBuilt) return;
			var canvas = UIRoot.Instance?.Canvas;
			if (canvas == null) return;

			var root = transform as RectTransform;
			if (root == null) root = gameObject.AddComponent<RectTransform>();
			root.SetParent(canvas.transform, false);
			root.anchorMin = Vector2.zero;
			root.anchorMax = Vector2.one;
			root.offsetMin = Vector2.zero;
			root.offsetMax = Vector2.zero;
			var bg = GetComponent<Image>();
			if (bg == null) bg = gameObject.AddComponent<Image>();
			bg.color = new Color(0.08f, 0.08f, 0.1f, 0.95f);

			EnsureGridLayout(mListRoot);

			EnsureStarToggleGroup(root);

			EnsureCraftBench(root);

			if (mBackButton != null)
			{
				mBackButton.onClick.RemoveAllListeners();
				mBackButton.onClick.AddListener(OnBack);
			}

			mUIBuilt = true;
		}

		/// <summary>补齐网格布局组件；幂等，已存在则保留既有配置</summary>
		private static void EnsureGridLayout(RectTransform rt)
		{
			if (rt.GetComponent<GridLayoutGroup>() != null) return;
			var grid = rt.gameObject.AddComponent<GridLayoutGroup>();
			grid.cellSize = new Vector2(170f, 84f);
			grid.spacing = new Vector2(8f, 8f);
			grid.padding = new RectOffset(10, 10, 10, 10);
			grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
			grid.constraintCount = 4;
			grid.childAlignment = TextAnchor.UpperLeft;
		}

		/// <summary>星级分类；子节点顺序约定 全部/★1…★5，索引即筛选值</summary>
		private void EnsureStarToggleGroup(RectTransform root)
		{
			var toggles = mStarToggleGroup.GetComponentsInChildren<Toggle>(true);
			for (int i = 0; i < toggles.Length; i++)
			{
				int filter = i;
				toggles[i].onValueChanged.RemoveAllListeners();
				toggles[i].onValueChanged.AddListener(on => { if (on) { mStarFilter = filter; RefreshList(); } });
			}
		}

		/// <summary>合成台：槽位 + 自动添加 + 合成 + 出品栏；按名称补齐缺失节点</summary>
		private void EnsureCraftBench(RectTransform root)
		{
			// 已绑定时按名称补齐缺失槽位，不重建既有节点
			for (int i = 0; i < 3; i++)
			{
				if (mCraftSlotsRoot.Find($"Slot_{i}") == null)
				{
					var slot = CreateSlot(mCraftSlotsRoot, $"Slot_{i}", new Vector2(-190f + i * 190f, 0f));
					mDynamicItems.Add(slot);
				}
			}

			if (mConfirmButton != null)
			{
				mConfirmButton.onClick.RemoveAllListeners();
				mConfirmButton.onClick.AddListener(OnConfirmProduction);
			}
			if (mProductionIcon != null) mProductionIcon.gameObject.SetActive(false);

			if (mAutoAddButton != null)
			{
				mAutoAddButton.onClick.RemoveAllListeners();
				mAutoAddButton.onClick.AddListener(OnAutoAdd);
			}
			if (mCraftButton != null)
			{
				mCraftButton.onClick.RemoveAllListeners();
				mCraftButton.onClick.AddListener(OnCraft);
			}
		}

		private static GameObject CreateSlot(Transform parent, string name, Vector2 pos)
		{
			var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var rt = go.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			rt.anchorMin = new Vector2(0.5f, 0.5f);
			rt.anchorMax = new Vector2(0.5f, 0.5f);
			rt.pivot = new Vector2(0.5f, 0.5f);
			rt.sizeDelta = new Vector2(180f, 84f);
			rt.anchoredPosition = pos;
			go.GetComponent<Image>().color = new Color(0.25f, 0.27f, 0.34f, 0.95f);

			var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var textRt = textGo.GetComponent<RectTransform>();
			textRt.SetParent(rt, false);
			textRt.anchorMin = Vector2.zero;
			textRt.anchorMax = Vector2.one;
			textRt.offsetMin = Vector2.zero;
			textRt.offsetMax = Vector2.zero;
			var text = textGo.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 18;
			text.alignment = TextAnchor.MiddleCenter;
			text.color = new Color(0.7f, 0.7f, 0.75f);
			text.text = "空";
			return go;
		}

		// ===== 格子模板 =====

		/// <summary>格子模板：优先容器内本地母版，缺失则回退 Resources 预制体</summary>
		private GameObject GetGridTemplate()
		{
			if (mGridTemplateCache == null)
			{
				// 优先按约定名 "Grid" 取本地母版
				var local = mListRoot != null ? mListRoot.Find("Grid") : null;

				// 名称未约定时退化为首个非动态子节点：兼容嵌套在 Content 下的模板
				if (local == null && mListRoot != null)
				{
					for (int i = 0; i < mListRoot.childCount; i++)
					{
						var child = mListRoot.GetChild(i);
						if (!mDynamicItems.Contains(child.gameObject))
						{
							local = child;
							break;
						}
					}
				}

				if (local != null)
				{
					mGridTemplateCache = local.gameObject;
					local.gameObject.SetActive(false); // 母版仅作克隆源
				}
				else
				{
					mGridTemplateCache = Resources.Load<GameObject>("Prefabs/UI/InventoryGrid");
				}
			}
			return mGridTemplateCache;
		}

		// ===== 列表刷新：筛选 + 排序 + 实例化 =====

		private void RefreshList()
		{
			ClearListChildren(mListRoot);

			if (MetaManager.HasInstance && mCoinText != null)
				mCoinText.text = $"钱币: {MetaManager.Instance.Coins}";

			if (!MetaManager.HasInstance) return;

			var dishes = MetaManager.Instance.GetDishInventory();
			if (dishes == null || dishes.Count == 0)
			{
				CreateEmptyHint(mListRoot, "  暂无成品菜");
				return;
			}

			// 已装备判定：新档按实例 ID 精确匹配，旧档无实例 ID 者回退 菜+星级 组合
			var equippedInstanceIds = new HashSet<string>();
			var equippedCombos = new HashSet<string>();
			if (RosterManager.HasInstance)
			{
				foreach (var e in RosterManager.Instance.GetEquippedDishes())
				{
					if (!string.IsNullOrEmpty(e.InstanceId))
						equippedInstanceIds.Add(e.InstanceId);
					else
						equippedCombos.Add(e.RecipeId + "_" + e.Star);
				}
			}

						if (mStarFilter > 0)
				dishes.RemoveAll(d => d == null || d.star != mStarFilter);
			dishes.RemoveAll(d => d == null);

			// 排序复用 SortRules，与战前准备口径一致：已装备 > 星级降序 > 同类相邻
			dishes.Sort((a, b) => SortRules.CompareItem(
				IsEquipped(a, equippedInstanceIds, equippedCombos), a.star, a.recipeId,
				IsEquipped(b, equippedInstanceIds, equippedCombos), b.star, b.recipeId));

						foreach (var d in dishes)
			{
				var recipe = GameDataMgr.Instance.GetRecipe(d.recipeId);
				if (recipe == null) continue;

				bool equipped = IsEquipped(d, equippedInstanceIds, equippedCombos);
				bool selected = mCraftSelection.Exists(s => s.instanceId == d.instanceId);
				int count = MetaManager.Instance.GetDishCount(d.recipeId, d.star);

				CreateDishCell(d, recipe, equipped, selected, count);
			}

			if (mListRoot.childCount == 0)
				CreateEmptyHint(mListRoot, "  该星级暂无成品菜");

			// 列表重建后补挂按钮悬停缩放
			UiHoverInstaller.Install(gameObject);
		}

		/// <summary>
		/// 已装备判定：有 InstanceId 者精确匹配；旧档缺失 InstanceId 时回退 菜+星级 组合（兼容历史存档）
		/// </summary>
		private static bool IsEquipped(DishInstance d, HashSet<string> instanceIds, HashSet<string> combos)
		{
			if (d == null) return false;
			if (!string.IsNullOrEmpty(d.instanceId) && instanceIds.Contains(d.instanceId)) return true;
			if (string.IsNullOrEmpty(d.instanceId) && combos.Contains(d.recipeId + "_" + d.star)) return true;
			return false;
		}

		/// <summary>实例化菜品格子：母版克隆 + 数据填充；状态标识仅切换预制体节点显隐</summary>
		private void CreateDishCell(DishInstance d, RecipeConfig recipe, bool equipped, bool selected, int count)
		{
			GameObject go;
			var template = GetGridTemplate();
			if (template != null)
			{
				go = Instantiate(template, mListRoot, false);
				go.SetActive(true);
			}
			else
			{
				// 无母版时兜底创建；自带 Button 保证仍可点击
				go = new GameObject("DishCell", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
				var img = go.GetComponent<Image>();
				img.color = new Color(0.24f, 0.26f, 0.33f, 0.95f);
				go.GetComponent<Button>().targetGraphic = img;
			}

			// 节点名约定：NameText = 菜名 / StarText = 星级，均按稀有度着色
			var nameTextTransform = go.transform.Find("NameText");
			var nameText = nameTextTransform?.GetComponent<Text>();
			if (nameText != null)
			{
				nameText.text = recipe.recipeName;          // 星级不再拼进菜名
				nameText.color = DishRarity.GetColor(d.star);
				DisableRaycast(nameTextTransform);
			}

			// 星级单列，格式 "★n"
			var starTextTransform = go.transform.Find("StarText");
			var starText = starTextTransform?.GetComponent<Text>();
			if (starText != null)
			{
				starText.text = $"★{d.star}";
				starText.color = DishRarity.GetColor(d.star);
				DisableRaycast(starTextTransform);
			}

			// 路径为空或加载失败时隐藏 Icon 节点
			var iconTransform = go.transform.Find("Icon");
			var icon = iconTransform?.GetComponent<Image>();
			if (icon != null)
			{
				var sprite = string.IsNullOrEmpty(recipe.iconPath) ? null : Resources.Load<Sprite>(recipe.iconPath);
				icon.gameObject.SetActive(sprite != null);
				if (sprite != null)
				{
					icon.sprite = sprite;
					icon.color = Color.white;
				}
				DisableRaycast(iconTransform);
			}

			// 标识由预制体约定，此处只切显隐；子节点关闭射线避免拦截格子点击
			var selectedMark = go.transform.Find("Selected");
			if (selectedMark != null)
			{
				selectedMark.gameObject.SetActive(selected);
				DisableRaycast(selectedMark);
			}
			var equippedMark = go.transform.Find("Equipped");
			if (equippedMark != null)
			{
				equippedMark.gameObject.SetActive(equipped);
				DisableRaycast(equippedMark);
			}

			// 捕获格子自身，便于原地切换选中标识而不重建列表
			var btn = go.GetComponent<Button>();
			if (btn != null)
			{
				btn.onClick.RemoveAllListeners();
				string instanceId = d.instanceId;
				var cellTransform = go.transform;
				btn.onClick.AddListener(() => OnCellClick(instanceId, equipped, cellTransform));
			}

			// 长按与单击语义正交，共用同一格子
			var longPress = go.GetComponent<LongPressTrigger>();
			if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
			longPress.onLongPress = () => OpenDishDetail(recipe, d.star);

			mDynamicItems.Add(go);
		}

		/// <summary>
		/// 长按格子：打开成品菜详情弹窗（单击仍为加入合成台，两者不冲突）。
		/// 统一走 ItemDetailFactory.ForDish，与图鉴/商店/背包/装配另一份口径保持一致
		/// （只展示该菜品给予的属性数值加成）。
		/// </summary>
		private void OpenDishDetail(RecipeConfig recipe, int star)
		{
			if (recipe == null) return;

			var data = ItemDetailFactory.ForDish(recipe.id, star);
			if (data == null) return;

			UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
		}

		/// <summary>点击格子：加入/移出合成槽位（已装备忽略）；只切换被点击格子的选中标识，不重建列表</summary>
		private void OnCellClick(string instanceId, bool equipped, Transform cellRoot)
		{
			if (equipped)
			{
				LogKit.Warning("[InventoryPanel] 已装备的菜品不可作为合成材料");
				return;
			}

			var selectedMark = cellRoot != null ? cellRoot.Find("Selected") : null;

			// 已在槽位 → 取消选中
			int idx = mCraftSelection.FindIndex(s => s.instanceId == instanceId);
			if (idx >= 0)
			{
				mCraftSelection.RemoveAt(idx);
				if (selectedMark != null) selectedMark.gameObject.SetActive(false);
				RefreshCraftUI();
				return;
			}

			// 槽满 combineRequiredCount 个
			int required = GameDataMgr.Instance.GetCraftRules().combineRequiredCount;
			if (mCraftSelection.Count >= required)
			{
				LogKit.Warning($"[InventoryPanel] 合成台已满（最多 {required} 个）");
				return;
			}

			// 必须同星同种（槽位里已有选中时）
			if (mCraftSelection.Count > 0)
			{
				var first = mCraftSelection[0];
				var d = FindDish(instanceId);
				if (d == null || d.recipeId != first.recipeId || d.star != first.star)
				{
					LogKit.Warning("[InventoryPanel] 只能选中 3 个同星同种的菜品");
					return;
				}
			}

			var inst = FindDish(instanceId);
			if (inst == null) return;
			mCraftSelection.Add(inst);
			if (selectedMark != null) selectedMark.gameObject.SetActive(true);
			PlayClick();
			RefreshCraftUI();
		}

		/// <summary>自动添加：选第一个满足"同星同种 ≥combineRequiredCount 且非装备可用"的组合；满星（≥combineMaxStar）无法再合成，跳过</summary>
		private void OnAutoAdd()
		{
			if (!MetaManager.HasInstance) return;
			var dishes = MetaManager.Instance.GetDishInventory();
			int required = GameDataMgr.Instance.GetCraftRules().combineRequiredCount;
			int maxStar = GameDataMgr.Instance.GetCraftRules().combineMaxStar;

			// 已装备判断：优先实例ID精确匹配，旧档回退组合
			var equippedInstanceIds = new HashSet<string>();
			var equippedCombos = new HashSet<string>();
			if (RosterManager.HasInstance)
			{
				foreach (var e in RosterManager.Instance.GetEquippedDishes())
				{
					if (!string.IsNullOrEmpty(e.InstanceId))
						equippedInstanceIds.Add(e.InstanceId);
					else
						equippedCombos.Add(e.RecipeId + "_" + e.Star);
				}
			}

			var groups = new List<(string recipeId, int star, List<DishInstance> available)>();
			foreach (var d in dishes)
			{
				if (d == null || d.star >= maxStar) continue; // 已装备实例跳过；满星无法再合成，跳过
				if (IsEquipped(d, equippedInstanceIds, equippedCombos)) continue;
				var group = groups.Find(g => g.recipeId == d.recipeId && g.star == d.star);
				if (group.available == null)
				{
					group = (d.recipeId, d.star, new List<DishInstance>());
					groups.Add(group);
				}
				group.available.Add(d);
			}

			// 排序（与列表一致），找第一个可用 ≥required 的组合
			groups.Sort((a, b) => SortRules.CompareItem(false, a.star, a.recipeId, false, b.star, b.recipeId));
			foreach (var g in groups)
			{
				if (g.available.Count >= required)
				{
					mCraftSelection.Clear();
					for (int i = 0; i < required; i++) mCraftSelection.Add(g.available[i]);
					HideTip();
					PlayClick();
					RefreshList();
					RefreshCraftUI();
					return;
				}
			}

			ShowTip($"自动添加失败：没有满足 {required} 个同星同种的菜品");
		}

		/// <summary>合成：校验 → 消耗槽位 combineRequiredCount 个同星同种 → 结果预览到出品 Icon（确认后才入库）</summary>
		private void OnCraft()
		{
			if (!IsCraftSelectionValid())
			{
				LogKit.Warning("[InventoryPanel] 需要同星同种（且非满星）的菜品才能合成");
				return;
			}

			var first = mCraftSelection[0];
			int required = GameDataMgr.Instance.GetCraftRules().combineRequiredCount;

			// 消耗材料（combineRequiredCount 个同星同种）
			if (!MetaManager.Instance.TryConsumeDish(first.recipeId, first.star, required))
			{
				LogKit.Warning("[InventoryPanel] 消耗材料失败");
				return;
			}

			// 记录待确认结果，显示出品 Icon（预览），清空槽位
			mPendingRecipeId = first.recipeId;
			mPendingResultStar = first.star + 1;
			mCraftSelection.Clear();
			PlayClick();
			ShowProductionIcon();
			HideTip();
			RefreshList();
			RefreshCraftUI();
		}

		/// <summary>确认出品：新菜入库 + 关闭出品 Icon</summary>
		private void OnConfirmProduction()
		{
			if (string.IsNullOrEmpty(mPendingRecipeId)) return;

			MetaManager.Instance.AddDish(mPendingRecipeId, mPendingResultStar, 1);

			var recipe = GameDataMgr.Instance.GetRecipe(mPendingRecipeId);
			LogKit.Log($"[InventoryPanel] 合成入库：{(recipe != null ? recipe.recipeName : mPendingRecipeId)} ★{mPendingResultStar}");

			mPendingRecipeId = null;
			mPendingResultStar = 0;
			HideProductionIcon();
			PlayClick();
			RefreshList();
			RefreshCraftUI();
		}

		/// <summary>显示出品 Icon（合成结果预览）：立绘 + 菜名 + 星级（均按星级着色）</summary>
		private void ShowProductionIcon()
		{
			if (mProductionIcon == null) return;
			var recipe = GameDataMgr.Instance.GetRecipe(mPendingRecipeId);
			var rarityColor = DishRarity.GetColor(mPendingResultStar);

			// NameText：只显示菜名，按星级着色（优先绑定字段，未绑定时回退查找子物体）
			var nameText = mProductionNameText != null
				? mProductionNameText
				: mProductionIcon.Find("NameText")?.GetComponent<Text>();
			if (nameText != null)
			{
				nameText.text = recipe != null ? recipe.recipeName : mPendingRecipeId;
				nameText.color = rarityColor;
			}

			// StarText：星级专用文本，格式 "★n"，按星级着色（优先绑定字段，未绑定时回退查找子物体）
			var starText = mProductionStarText != null
				? mProductionStarText
				: mProductionIcon.Find("StarText")?.GetComponent<Text>();
			if (starText != null)
			{
				starText.text = $"★{mPendingResultStar}";
				starText.color = rarityColor;
			}

			// 立绘：合成获得菜品的资源图直接赋给 ProductionIcon 自身 Image（无图时保留底色）
			var iconImage = mProductionIcon.GetComponent<Image>();
			var sprite = recipe == null || string.IsNullOrEmpty(recipe.iconPath)
				? null
				: Resources.Load<Sprite>(recipe.iconPath);
			if (iconImage != null && sprite != null)
			{
				iconImage.sprite = sprite;
				iconImage.color = Color.white;
			}

			mProductionIcon.gameObject.SetActive(true);
		}

		/// <summary>隐藏出品 Icon</summary>
		private void HideProductionIcon()
		{
			if (mProductionIcon != null) mProductionIcon.gameObject.SetActive(false);
		}

		/// <summary>显示提示（仅自动添加失败）</summary>
		private void ShowTip(string msg)
		{
			if (mTipText == null) return;
			mTipText.text = msg;
			mTipText.gameObject.SetActive(true);
		}

		private void HideTip()
		{
			if (mTipText != null) mTipText.gameObject.SetActive(false);
		}

		/// <summary>刷新合成台：槽位文本（显示选中的菜名，空位显示"空"）+ 合成按钮可用状态</summary>
		private void RefreshCraftUI()
		{
			if (mCraftSlotsRoot == null) return;
			for (int i = 0; i < 3; i++)
			{
				var slot = mCraftSlotsRoot.Find($"Slot_{i}");
				if (slot == null) continue;

				// 优先使用绑定字段，未绑定时回退查找 Slot_i/Text
				var text = mSlotTexts != null && i < mSlotTexts.Length && mSlotTexts[i] != null
					? mSlotTexts[i]
					: slot.Find("Text")?.GetComponent<Text>();

				string iconPath = null;
				if (i < mCraftSelection.Count)
				{
					var d = mCraftSelection[i];
					var recipe = GameDataMgr.Instance.GetRecipe(d.recipeId);
					if (text != null)
					{
						text.text = (recipe != null ? recipe.recipeName : d.recipeId) + $" ★{d.star}";
						text.color = new Color(1f, 0.9f, 0.6f);
					}
					iconPath = recipe?.iconPath;
				}
				else if (text != null)
				{
					text.text = "空";
					text.color = new Color(0.7f, 0.7f, 0.75f);
				}

				// 槽位图标：选中物品的立绘（无物品/无图标时隐藏）
				SetSlotIcon(slot, iconPath);
			}

			// 合成按钮：仅当 3 个同星同种且非满星时可用
			bool valid = IsCraftSelectionValid();
			if (mCraftButton != null) mCraftButton.interactable = valid;
		}

		/// <summary>合成选择是否有效：恰好 N 个（combineRequiredCount）、同星同种、且非满星（combineMaxStar）</summary>
		private bool IsCraftSelectionValid()
		{
			var rules = GameDataMgr.Instance.GetCraftRules();
			if (mCraftSelection.Count != rules.combineRequiredCount) return false;
			var first = mCraftSelection[0];
			if (first == null || first.star >= rules.combineMaxStar) return false;
			for (int i = 1; i < mCraftSelection.Count; i++)
			{
				var s = mCraftSelection[i];
				if (s == null || s.recipeId != first.recipeId || s.star != first.star) return false;
			}
			return true;
		}

		/// <summary>按实例 id 查菜品</summary>
		private DishInstance FindDish(string instanceId)
		{
			if (!MetaManager.HasInstance) return null;
			foreach (var d in MetaManager.Instance.GetDishInventory())
				if (d != null && d.instanceId == instanceId) return d;
			return null;
		}

		private void CreateEmptyHint(RectTransform parent, string msg)
		{
			var textGo = new GameObject("EmptyHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = textGo.GetComponent<RectTransform>();
			rt.SetParent(parent, false);
			rt.anchorMin = new Vector2(0f, 1f);
			rt.anchorMax = new Vector2(1f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.sizeDelta = new Vector2(0f, 60f);
			var text = textGo.GetComponent<Text>();
			SetupText(text, 22, TextAnchor.MiddleLeft, new Color(0.75f, 0.75f, 0.8f));
			text.text = msg;
			mDynamicItems.Add(textGo);
		}

		private void OnBack()
		{
			PlayClick();
			CloseSelf();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// ===== 通用控件工具 =====

		private static void SetupText(Text t, int size, TextAnchor anchor, Color color)
		{
			t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			t.fontSize = size;
			t.alignment = anchor;
			t.color = color;
		}

		/// <summary>关闭子节点射线检测，确保只有根按钮接收点击</summary>
		private static void DisableRaycast(Transform t)
		{
			if (t == null) return;
			foreach (var g in t.GetComponentsInChildren<Graphic>(true))
				g.raycastTarget = false;
		}

		/// <summary>槽位图标：设置 Slot 下 Icon 子节点；无图标/缺节点时隐藏</summary>
		private static void SetSlotIcon(Transform slot, string iconPath)
		{
			if (slot == null) return;
			var icon = slot.Find("Icon")?.GetComponent<Image>();
			if (icon == null) return;

			var sprite = string.IsNullOrEmpty(iconPath) ? null : Resources.Load<Sprite>(iconPath);
			icon.gameObject.SetActive(sprite != null);
			if (sprite != null)
			{
				icon.sprite = sprite;
				icon.color = Color.white;
			}
			icon.raycastTarget = false;
		}

		private void ClearListChildren(RectTransform listRoot)
		{
			if (listRoot == null) return;
			for (int i = listRoot.childCount - 1; i >= 0; i--)
			{
				var child = listRoot.GetChild(i).gameObject;
				// 只销毁运行时登记的动态格子；预制体内预置的嵌套母版只隐藏不销毁，
				// 否则母版一旦被销毁，后续克隆全部失败
				if (mDynamicItems.Contains(child))
				{
					mDynamicItems.Remove(child);
					child.SetActive(false);   // 先失活，Destroy 延迟到帧末执行，避免期间仍拦截点击
					Destroy(child);
				}
				else
				{
					child.SetActive(false);
				}
			}
		}
	}
}
