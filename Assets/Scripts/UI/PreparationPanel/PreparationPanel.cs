using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class PreparationPanelData : IUIData
	{
	}

	/// <summary>
	/// 战前准备面板：角色/武器/菜品/技能的槽位装配，以及开始游戏。
	/// 定位为纯显示层——读写全部委托 RosterManager / MetaManager，面板不持有业务状态。
	/// 编辑走工作副本：三类槽位各维护恒 3 元素的副本，确定后才落库，取消即自然丢弃。
	/// 四个分类共用一个网格容器，切换时清空动态克隆并按当前分类重建；母版只作克隆源，不参与显示。
	/// </summary>
	public partial class PreparationPanel : UIPanel
	{
		/// <summary>当前展开的分类；None = 不展开任何 List</summary>
		private enum PanelList { None, Character, Weapon, Dish, Skill }

		private PanelList mCurrentList = PanelList.None;

		/// <summary>槽位编辑模式：-1 = 整体编辑（多选），0-2 = 通过 DishButton/SkillButton 切入替换对应槽位</summary>
		private int mPendingSlot = -1;

		// 3 槽位工作副本：恒 3 个元素，null/default 表示空槽，确定后才写入存档
		private string mSelectedCharacterId;
		private readonly List<string> mSelectedWeapons = new(); // 武器 3 槽位（位置语义：取消保留空位，新增填首个空槽）
		private readonly List<EquippedDish> mSelectedDishes = new(); // 菜品 3 槽位（同上）
		private readonly List<string> mSelectedSkills = new();       // 技能 3 槽位（同上）

		private readonly List<GameObject> mDynamicOptions = new();
		private GameObject mGridTemplateCache; // 公共 Grid 模板缓存（Resources/Prefabs/UI/PreparationGrid）

		// ===== 生命周期 =====

		protected override void OnInit(IUIData data = null)
		{
			mData = data as PreparationPanelData ?? new PreparationPanelData();
			BindEvents();
			ResolveUnifiedContent();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as PreparationPanelData ?? mData;
			// 旧档迁移：为无实例ID的装配项补绑具体实例（保证实例级自洽）
			if (RosterManager.HasInstance) RosterManager.Instance.EnsureEquippedDishInstances();
			// 技能槽自愈：剔除已不在可选池的技能，否则会显示并真的装备上列表里没有的技能
			if (RosterManager.HasInstance) RosterManager.Instance.SanitizeSkillIds();
			ResetView();
			RefreshSlotButtons();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		protected override void OnClose()
		{
			ClearDynamicOptions();
		}

		// ===== 事件绑定 =====

		private void BindEvents()
		{
			if (mCloseButton != null)
			{
				mCloseButton.onClick.RemoveAllListeners();
				mCloseButton.onClick.AddListener(OnCloseButton);
			}
			if (mStartButton != null)
			{
				mStartButton.onClick.RemoveAllListeners();
				mStartButton.onClick.AddListener(OnStartGame);
			}
			if (mSureButton != null)
			{
				mSureButton.onClick.RemoveAllListeners();
				mSureButton.onClick.AddListener(ConfirmCurrentList);
			}

			// 允许全部不选中：默认态就是不展开任何 List
			if (mCharacterToggle != null && mCharacterToggle.group != null)
				mCharacterToggle.group.allowSwitchOff = true;

			BindToggle(mCharacterToggle, PanelList.Character);
			BindToggle(mWeaponToggle, PanelList.Weapon);
			BindToggle(mDishToggle, PanelList.Dish);
			BindToggle(mSkillToggle, PanelList.Skill);

			BindButton(mCharacterButton, PanelList.Character);

			BindSlotButtons(mWeaponRoot, "WeaponButton_", PanelList.Weapon);
			BindSlotButtons(mDishRoot, "DishButton_", PanelList.Dish);
			// 被动为角色固有，不在此展示，故可选技能槽只有 2 个
			BindSlotButtons(mSkillRoot, "SkillButton_", PanelList.Skill, RosterManager.MaxSelectableSkills);
		}

		private void BindToggle(Toggle toggle, PanelList list)
		{
			if (toggle == null) return;
			toggle.onValueChanged.RemoveAllListeners();
			toggle.onValueChanged.AddListener(on =>
			{
				if (on) ShowList(list, -1);
				else if (mCurrentList == list) HideAllLists();
			});
		}

		private void BindButton(Button button, PanelList list)
		{
			if (button == null) return;
			button.onClick.RemoveAllListeners();
			button.onClick.AddListener(() => { ShowList(list, -1); PlayClick(); });
		}

		/// <summary>绑定 WeaponRoot/DishRoot/SkillRoot 下的 *_0..N 槽位按钮（count 默认 3；技能为 2）</summary>
		private void BindSlotButtons(RectTransform root, string prefix, PanelList list, int count = 3)
		{
			if (root == null) return;
			for (int i = 0; i < count; i++)
			{
				var child = root.Find(prefix + i);
				if (child == null) continue;
				var btn = child.GetComponent<Button>();
				if (btn == null) continue;

				int slot = i; // 捕获
				btn.onClick.RemoveAllListeners();
				btn.onClick.AddListener(() =>
				{
					ShowList(list, slot);
					PlayClick();
				});
			}
		}

		// ===== List 显示切换 =====

		/// <summary>显示指定分类到统一网格容器，slot=-1 整体编辑，0-2 槽位替换</summary>
		private void ShowList(PanelList list, int slot)
		{
			HideAllLists();
			mCurrentList = list;
			mPendingSlot = slot;

			if (mUnifiedContent == null) return;

			mUnifiedContent.gameObject.SetActive(true);
			if (mSureButton != null) mSureButton.gameObject.SetActive(true);
			InitSelectionForCurrentList();
			BuildList(list);
		}

		/// <summary>
		/// 槽位工作副本每次展开都从存档重建，保证显示与真实装备一致；编辑只改副本，确定时才写入 RosterManager
		/// </summary>
		private void InitSelectionForCurrentList()
		{
			switch (mCurrentList)
			{
				case PanelList.Weapon:
					FillSlots(mSelectedWeapons, RosterManager.HasInstance ? RosterManager.Instance.GetStoredWeaponIds() : null);
					break;
				case PanelList.Dish:
					FillDishSlots(mSelectedDishes, RosterManager.HasInstance ? RosterManager.Instance.GetEquippedDishes() : null);
					break;
				case PanelList.Skill:
					FillSkillSlots(mSelectedSkills, RosterManager.HasInstance ? RosterManager.Instance.GetStoredSkillIds() : null);
					break;
			}
		}

		/// <summary>用存档槽位填充 3 槽位数组（长度不足补 null 空槽）</summary>
		private static void FillSlots(List<string> slots, List<string> stored)
		{
			slots.Clear();
			for (int i = 0; i < RosterManager.MaxEquippedDishes; i++)
				slots.Add(stored != null && i < stored.Count ? stored[i] : null);
		}

		/// <summary>用存档菜品填充 3 槽位数组（长度不足补 default 空槽）</summary>
		private static void FillDishSlots(List<EquippedDish> slots, List<EquippedDish> stored)
		{
			slots.Clear();
			for (int i = 0; i < RosterManager.MaxEquippedDishes; i++)
				slots.Add(stored != null && i < stored.Count ? stored[i] : default);
		}

		/// <summary>用存档的可选技能填充槽位数组（长度不足补 null 空槽）</summary>
		private static void FillSkillSlots(List<string> slots, List<string> stored)
		{
			slots.Clear();
			for (int i = 0; i < RosterManager.MaxSelectableSkills; i++)
				slots.Add(stored != null && i < stored.Count ? stored[i] : null);
		}

		private void HideAllLists()
		{
			if (mUnifiedContent != null) mUnifiedContent.gameObject.SetActive(false);
			if (mSureButton != null) mSureButton.gameObject.SetActive(false);
			mCurrentList = PanelList.None;
			mPendingSlot = -1;
			ClearDynamicOptions();
		}

		/// <summary>打开面板时复位：无 List、Toggle 全部不选中</summary>
		private void ResetView()
		{
			HideAllLists();
			SetToggle(mCharacterToggle, false);
			SetToggle(mWeaponToggle, false);
			SetToggle(mDishToggle, false);
			SetToggle(mSkillToggle, false);
		}

		private static void SetToggle(Toggle toggle, bool on)
		{
			if (toggle == null) return;
			// 用无通知版本改 isOn，避免回调再次触发 List 显隐逻辑
			if (toggle.isOn != on)
			{
				toggle.SetIsOnWithoutNotify(on);
			}
		}

		/// <summary>
		/// 解析统一网格容器：Inspector 绑定优先，未绑定时按候选节点名在面板内深查；
		/// 随后隐藏 Content 下的 PreparationGrid 母版——母版只作克隆源，不能当成一条数据显示
		/// </summary>
		private void ResolveUnifiedContent()
		{
			if (mUnifiedContent == null)
			{
				// 工作台搭建约定的候选节点名（按优先级）
				string[] candidates = { "UnifiedContent", "PreparationContent", "Content" };
				foreach (string nodeName in candidates)
				{
					var found = FindDeep(transform, nodeName) as RectTransform;
					if (found != null)
					{
						mUnifiedContent = found;
						break;
					}
				}
			}

			if (mUnifiedContent == null)
			{
				LogKit.Warning("[PreparationPanel] 未绑定统一网格容器，无法显示可选项");
				return;
			}

			// Content 下非动态子节点 = 母版 → 隐藏保留（动态克隆体由 CreateOption 单独激活）
			for (int i = 0; i < mUnifiedContent.childCount; i++)
			{
				var child = mUnifiedContent.GetChild(i).gameObject;
				if (!mDynamicOptions.Contains(child)) child.SetActive(false);
			}
		}

		// ===== 列表构建 =====

		private void BuildList(PanelList list)
		{
			ClearDynamicOptions();

			switch (list)
			{
				case PanelList.Character: BuildCharacterOptions(); break;
				case PanelList.Weapon: BuildWeaponOptions(); break;
				case PanelList.Dish: BuildDishOptions(); break;
				case PanelList.Skill: BuildSkillOptions(); break;
			}

			// 切换分类会重建选项按钮：补挂悬停缩放
			UiHoverInstaller.Install(gameObject);
		}

		private void BuildCharacterOptions()
		{
			if (mUnifiedContent == null || !RosterManager.HasInstance) return;
			var roster = RosterManager.Instance;
			var owned = MetaManager.Instance.Data.ownedCharacters;
			if (owned == null || owned.Count == 0) return;

			mSelectedCharacterId = string.IsNullOrEmpty(mSelectedCharacterId)
				? roster.GetCurrentCharacterId() : mSelectedCharacterId;

			for (int i = 0; i < owned.Count; i++)
			{
				var cfg = GameDataMgr.Instance.GetCharacter(owned[i]);
				if (cfg == null) continue;
				string id = cfg.id;
				bool selected = id == mSelectedCharacterId;
				CreateOption(mUnifiedContent, (selected ? "▶ " : "   ") + cfg.characterName, selected, cfg.iconPath, () =>
				{
					mSelectedCharacterId = id;
					BuildList(PanelList.Character);
					PlayClick();
				}, onLongPress: () => OpenItemDetail(PanelList.Character, id));
			}
		}

		private void BuildWeaponOptions()
		{
			if (mUnifiedContent == null || !RosterManager.HasInstance) return;

			// 从武器库存构建（实例化；按 weaponId 去重，同型号只出一格）
			var inventory = MetaManager.HasInstance ? MetaManager.Instance.GetWeaponInventory() : new List<WeaponInstance>();
			if (inventory == null || inventory.Count == 0)
			{
				CreateOption(mUnifiedContent, "  暂无武器", false, null, null);
				return;
			}

			var equippedIds = RosterManager.Instance.GetStoredWeaponIds();

			// 去重 by weaponId，收集 (配置, 已装备)
			var seen = new HashSet<string>();
			var entries = new List<(WeaponConfig cfg, bool equipped)>();
			foreach (var inst in inventory)
			{
				if (inst == null || string.IsNullOrEmpty(inst.weaponId)) continue;
				if (!seen.Add(inst.weaponId)) continue;
				var cfg = GameDataMgr.Instance.GetWeapon(inst.weaponId);
				if (cfg == null) continue;

				entries.Add((cfg, equippedIds.Contains(inst.weaponId)));
			}

			// 统一排序：已装备 > 优先级(武器无星级=0) > 同类相邻
			entries.Sort((a, b) => SortRules.CompareItem(a.equipped, 0, a.cfg.id, b.equipped, 0, b.cfg.id));

			foreach (var entry in entries)
			{
				var w = entry.cfg;
				// 高亮：整体=出现在任一槽位；槽位编辑=仅编辑槽的物品
				bool selected = mPendingSlot >= 0
					? mSelectedWeapons[mPendingSlot] == w.id
					: mSelectedWeapons.Contains(w.id);
				// NameText 只放武器名（数量/攻击方式等不再拼进名称，细节走长按详情）
				CreateOption(mUnifiedContent, (selected ? "▶ " : "   ") + w.weaponName, selected, w.iconPath, () =>
				{
					ToggleWeaponSelection(w.id);
					BuildList(PanelList.Weapon);
					PlayClick();
				}, onLongPress: () => OpenItemDetail(PanelList.Weapon, w.id));
			}
		}

		/// <summary>
		/// 武器选中切换（槽位位置语义，工作副本，点确定才生效）：
		///   槽位编辑：点当前项=清空该槽；点另一槽的物品=互换；点未装备=替换该槽
		///   整体编辑：点已选=取消（保留槽位位置）；点未选=填入第一个空槽；无空槽=忽略并警告
		/// </summary>
		private void ToggleWeaponSelection(string weaponId)
		{
			int idx = mSelectedWeapons.IndexOf(weaponId);
			if (mPendingSlot >= 0)
			{
				if (idx == mPendingSlot)
					mSelectedWeapons[mPendingSlot] = null;
				else if (idx >= 0)
				{
					string tmp = mSelectedWeapons[mPendingSlot];
					mSelectedWeapons[mPendingSlot] = mSelectedWeapons[idx];
					mSelectedWeapons[idx] = tmp;
				}
				else
					mSelectedWeapons[mPendingSlot] = weaponId;
			}
			else
			{
				if (idx >= 0)
					mSelectedWeapons[idx] = null;
				else
				{
					int empty = mSelectedWeapons.FindIndex(string.IsNullOrEmpty);
					if (empty < 0)
					{
						LogKit.Warning("[PreparationPanel] 槽位已满，请先取消一个已选物品");
						return;
					}
					mSelectedWeapons[empty] = weaponId;
				}
			}
		}

		private void BuildDishOptions()
		{
			if (mUnifiedContent == null || !MetaManager.HasInstance) return;
			var dishes = MetaManager.Instance.GetDishInventory();
			if (dishes == null || dishes.Count == 0)
			{
				CreateOption(mUnifiedContent, "  暂无成品菜", false, null, null);
				return;
			}

			// 按 (菜+星级) 去重：每个组合一个选项（同菜多实例不重复显示，数量不再展示）
			var seen = new HashSet<string>();
			for (int i = 0; i < dishes.Count; i++)
			{
				var d = dishes[i];
				if (d == null) continue;
				string key = d.recipeId + "_" + d.star;
				if (!seen.Add(key)) continue;

				var recipe = GameDataMgr.Instance.GetRecipe(d.recipeId);
				if (recipe == null) continue;

				// 高亮：整体=出现在任一槽位；槽位编辑=仅编辑槽的物品
				bool selected = mPendingSlot >= 0
					? mSelectedDishes[mPendingSlot].RecipeId == d.recipeId && mSelectedDishes[mPendingSlot].Star == d.star
					: mSelectedDishes.Exists(e => e.RecipeId == d.recipeId && e.Star == d.star);
				// NameText 只放菜名（统一纯白）；星级交给 StarText（"★n" + 稀有度色）；数量不显示
				CreateOption(mUnifiedContent, (selected ? "▶ " : "   ") + recipe.recipeName, selected, recipe.iconPath, () =>
				{
					ToggleDishSelection(new EquippedDish { RecipeId = d.recipeId, Star = d.star });
					BuildList(PanelList.Dish);
					PlayClick();
				}, onLongPress: () => OpenItemDetail(PanelList.Dish, d.recipeId, d.star), star: d.star);
			}
		}

		/// <summary>菜品选中切换（槽位位置语义，同武器；新增槽位时自动绑定一个具体实例）</summary>
		private void ToggleDishSelection(EquippedDish dish)
		{
			int idx = mSelectedDishes.FindIndex(e => e.RecipeId == dish.RecipeId && e.Star == dish.Star);
			if (mPendingSlot >= 0)
			{
				if (idx == mPendingSlot)
					mSelectedDishes[mPendingSlot] = default;
				else if (idx >= 0)
				{
					var tmp = mSelectedDishes[mPendingSlot];
					mSelectedDishes[mPendingSlot] = mSelectedDishes[idx];
					mSelectedDishes[idx] = tmp;
				}
				else
					mSelectedDishes[mPendingSlot] = BindInstance(dish);
			}
			else
			{
				if (idx >= 0)
					mSelectedDishes[idx] = default;
				else
				{
					int empty = mSelectedDishes.FindIndex(e => string.IsNullOrEmpty(e.RecipeId));
					if (empty < 0)
					{
						LogKit.Warning("[PreparationPanel] 槽位已满，请先取消一个已选物品");
						return;
					}
					mSelectedDishes[empty] = BindInstance(dish);
				}
			}
		}

		/// <summary>为装配菜绑定一个该组合下未被当前装配占用的具体实例（实例级自洽）</summary>
		private EquippedDish BindInstance(EquippedDish dish)
		{
			if (!string.IsNullOrEmpty(dish.InstanceId)) return dish;
			if (!MetaManager.HasInstance) return dish;

			// 当前已占用的实例（工作副本槽位）
			var taken = new HashSet<string>();
			foreach (var e in mSelectedDishes)
				if (!string.IsNullOrEmpty(e.InstanceId)) taken.Add(e.InstanceId);

			var inventory = MetaManager.Instance.GetDishInventory();
			foreach (var inst in inventory)
			{
				if (inst == null || inst.recipeId != dish.RecipeId || inst.star != dish.Star) continue;
				if (taken.Contains(inst.instanceId)) continue;
				dish.InstanceId = inst.instanceId;
				break;
			}
			return dish;
		}

		private void BuildSkillOptions()
		{
			if (mUnifiedContent == null || !RosterManager.HasInstance) return;
			var roster = RosterManager.Instance;

			// 可选池 = 共有池 ∪ 已解锁技能（商店购买 / 角色附赠）（RosterManager 统一计算）
			// 角色限制：专属技能只有归属角色能用，别的角色的专属技能不进本角色的池
			var pool = roster.GetSelectableSkillPool();

			if (pool.Count == 0)
			{
				CreateOption(mUnifiedContent, "  该角色无可搭配技能", false, null, null);
				return;
			}

			for (int i = 0; i < pool.Count; i++)
			{
				string id = pool[i];
				var cfg = GameDataMgr.Instance.GetActiveSkill(id);
				if (cfg == null) continue;

				bool selected = mPendingSlot >= 0
					? mSelectedSkills[mPendingSlot] == id
					: mSelectedSkills.Contains(id);
				CreateOption(mUnifiedContent, (selected ? "▶ " : "   ") + cfg.skillName, selected, cfg.iconPath, () =>
				{
					ToggleSkillSelection(id);
					BuildList(PanelList.Skill);
					PlayClick();
				}, onLongPress: () => OpenItemDetail(PanelList.Skill, id));
			}
		}

		/// <summary>技能选中切换（槽位位置语义，同武器）</summary>
		private void ToggleSkillSelection(string skillId)
		{
			int idx = mSelectedSkills.IndexOf(skillId);
			if (mPendingSlot >= 0)
			{
				if (idx == mPendingSlot)
					mSelectedSkills[mPendingSlot] = null;
				else if (idx >= 0)
				{
					string tmp = mSelectedSkills[mPendingSlot];
					mSelectedSkills[mPendingSlot] = mSelectedSkills[idx];
					mSelectedSkills[idx] = tmp;
				}
				else
					mSelectedSkills[mPendingSlot] = skillId;
			}
			else
			{
				if (idx >= 0)
					mSelectedSkills[idx] = null;
				else
				{
					int empty = mSelectedSkills.FindIndex(string.IsNullOrEmpty);
					if (empty < 0)
					{
						LogKit.Warning("[PreparationPanel] 槽位已满，请先取消一个已选物品");
						return;
					}
					mSelectedSkills[empty] = skillId;
				}
			}
		}

		// ===== 确认逻辑 =====

		/// <summary>共用确定按钮：按当前显示的 List 执行对应确认</summary>
		private void ConfirmCurrentList()
		{
			switch (mCurrentList)
			{
				case PanelList.Character: ConfirmCharacter(); break;
				case PanelList.Weapon: ConfirmWeapon(); break;
				case PanelList.Dish: ConfirmDish(); break;
				case PanelList.Skill: ConfirmSkill(); break;
				default:
					// 无 List 显示时点击确定无效
					break;
			}
		}

		private void ConfirmCharacter()
		{
			if (RosterManager.HasInstance && !string.IsNullOrEmpty(mSelectedCharacterId))
			{
				RosterManager.Instance.SetCharacter(mSelectedCharacterId);
				mSelectedSkills.Clear(); // 角色切换后技能库可能变化，清空待选
			}
			FinishConfirm();
		}

		private void ConfirmWeapon()
		{
			if (RosterManager.HasInstance)
			{
				// 整体/槽位统一：直接写 3 槽位工作副本（保留中间空位，仅清理尾部空槽）
				var current = new List<string>(mSelectedWeapons);
				TrimEmptyStringSlots(current);
				RosterManager.Instance.SetWeaponIds(current);
			}
			FinishConfirm();
		}

		private void ConfirmDish()
		{
			if (RosterManager.HasInstance)
			{
				var current = new List<EquippedDish>(mSelectedDishes);
				TrimEmptyDishSlots(current);
				RosterManager.Instance.SetEquippedDishes(current);
			}
			FinishConfirm();
		}

		private void ConfirmSkill()
		{
			if (RosterManager.HasInstance)
			{
				var current = new List<string>(mSelectedSkills);
				TrimEmptyStringSlots(current);
				RosterManager.Instance.SetSkillIds(current);
			}
			FinishConfirm();
		}

		/// <summary>去除尾部空槽位（保持 List 紧凑，空槽以缺省表示）</summary>
		private static void TrimEmptyDishSlots(List<EquippedDish> dishes)
		{
			for (int i = dishes.Count - 1; i >= 0; i--)
			{
				if (string.IsNullOrEmpty(dishes[i].RecipeId))
					dishes.RemoveAt(i);
				else
					break; // 只清理尾部
			}
		}

		private static void TrimEmptyStringSlots(List<string> slots)
		{
			for (int i = slots.Count - 1; i >= 0; i--)
			{
				if (string.IsNullOrEmpty(slots[i]))
					slots.RemoveAt(i);
				else
					break;
			}
		}

		/// <summary>确认完成：隐藏 List、清空选中、刷新槽位按钮</summary>
		private void FinishConfirm()
		{
			PlayClick();
			HideAllLists();
			mSelectedCharacterId = null;
			mSelectedWeapons.Clear();
			mSelectedDishes.Clear();
			mSelectedSkills.Clear();
			ResetView();
			RefreshSlotButtons();
		}

		// ===== 槽位按钮显示（WeaponButton_0-2 / DishButton_0-2 / SkillButton_0-2） =====

		private void RefreshSlotButtons()
		{
			RefreshCharacterButton();
			RefreshWeaponSlots();
			RefreshDishSlots();
			RefreshSkillSlots();
		}

		/// <summary>CharacterButton 图标：显示当前所选角色的立绘</summary>
		private void RefreshCharacterButton()
		{
			if (mCharacterButton == null) return;
			string iconPath = null;
			if (RosterManager.HasInstance)
			{
				var id = RosterManager.Instance.GetCurrentCharacterId();
				if (!string.IsNullOrEmpty(id))
				{
					var cfg = GameDataMgr.Instance.GetCharacter(id);
					iconPath = cfg?.iconPath;
				}
			}
			SetSlotIcon(mCharacterButton.transform, iconPath);
		}

		/// <summary>槽位按钮图标：设置 Button 下 Icon 子节点；无图标/缺节点时隐藏</summary>
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

		/// <summary>
		/// 给槽位按钮写文本：名称进 "Text" 子节点；星级（仅菜品）进 "StarText"，格式 "★n" 并按星级着色，
		/// 空槽与非菜品分类整体隐藏该节点。
		/// ⚠ 不能用 GetComponentInChildren&lt;Text&gt;() 取名称节点——StarText 同为 Text，层级顺序不可靠。
		/// </summary>
		private static void SetSlotLabel(Transform slot, string label, int star = 0)
		{
			if (slot == null) return;

			var textTransform = slot.Find("Text");
			var text = textTransform != null ? textTransform.GetComponent<Text>() : slot.GetComponentInChildren<Text>();
			if (text != null)
			{
				text.text = label;
				text.raycastTarget = false;
			}

			var starTransform = slot.Find("StarText");
			if (starTransform == null) return;

			bool hasStar = star > 0;
			starTransform.gameObject.SetActive(hasStar);
			if (!hasStar) return;

			var starText = starTransform.GetComponent<Text>();
			if (starText != null)
			{
				starText.text = $"★{star}";
				starText.color = DishRarity.GetColor(star);
				starText.raycastTarget = false;
			}
		}

		private void RefreshWeaponSlots()
		{
			if (mWeaponRoot == null) return;
			var ids = RosterManager.HasInstance ? RosterManager.Instance.GetStoredWeaponIds() : new List<string>();

			for (int i = 0; i < 3; i++)
			{
				var child = mWeaponRoot.Find("WeaponButton_" + i);
				if (child == null) continue;

				string label = "武器" + (i + 1);
				string iconPath = null;
				if (i < ids.Count && !string.IsNullOrEmpty(ids[i]))
				{
					var cfg = GameDataMgr.Instance.GetWeapon(ids[i]);
					label = cfg != null ? cfg.weaponName : ids[i];
					iconPath = cfg?.iconPath;
				}
				SetSlotLabel(child, label); // 武器无星级
				SetSlotIcon(child, iconPath);
			}
		}

		private void RefreshDishSlots()
		{
			if (mDishRoot == null) return;
			var equipped = RosterManager.HasInstance ? RosterManager.Instance.GetEquippedDishes() : new List<EquippedDish>();

			for (int i = 0; i < 3; i++)
			{
				var child = mDishRoot.Find("DishButton_" + i);
				if (child == null) continue;

				string label = "菜品" + (i + 1); // 空槽占位文案（此时 StarText 隐藏）
				string iconPath = null;
				int star = 0;
				if (i < equipped.Count && !string.IsNullOrEmpty(equipped[i].RecipeId))
				{
					var recipe = GameDataMgr.Instance.GetRecipe(equipped[i].RecipeId);
					label = recipe != null ? recipe.recipeName : equipped[i].RecipeId;
					iconPath = recipe?.iconPath;
					star = equipped[i].Star;
				}
				SetSlotLabel(child, label, star); // 星级写在 StarText，不拼进名称
				SetSlotIcon(child, iconPath);
			}
		}

		private void RefreshSkillSlots()
		{
			if (mSkillRoot == null) return;
			var skills = RosterManager.HasInstance ? RosterManager.Instance.GetStoredSkillIds() : new List<string>();

			// SkillButton_0/1 = 2 个可选技能槽（被动为角色固有，不在此展示）
			for (int i = 0; i < RosterManager.MaxSelectableSkills; i++)
			{
				var child = mSkillRoot.Find("SkillButton_" + i);
				if (child == null) continue;

				string label = "技能" + (i + 1);
				string iconPath = null;
				if (i < skills.Count && !string.IsNullOrEmpty(skills[i]))
				{
					var cfg = GameDataMgr.Instance.GetActiveSkill(skills[i]);
					label = cfg != null ? cfg.skillName : skills[i];
					iconPath = cfg?.iconPath;
				}
				SetSlotLabel(child, label); // 技能无星级
				SetSlotIcon(child, iconPath);
			}
		}

		// ===== 顶部按钮 =====

		private void OnStartGame()
		{
			PlayClick();
			// 保存战前配置（记住上次准备内容），然后进入游戏
			if (RosterManager.HasInstance) RosterManager.Instance.SavePreparation();
			EventKit.Type.Send(new GameEvents.GameStart());
		}

		private void OnCloseButton()
		{
			PlayClick();
			// 关闭准备面板即返回主菜单
			CloseSelf();
			// 确保主菜单面板可见（Pop 层关闭后恢复主菜单显示）
			UIKit.ShowPanel<MainMenuPanel>();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// ===== UI 工具 =====

		/// <summary>
		/// 在 List 容器内生成一个 Grid 选项（克隆模板 + 数据赋值）
		/// 模板来源：优先 List 内名为 "Grid" 的子物体，否则 Resources/Prefabs/UI/PreparationGrid.prefab
		/// 模板结构约定：根(Image+Button) → 子物体 Icon(Image) / NameText(Text 只放名称，统一纯白) /
		///              StarText(Text 星级专用，格式 "★n"，仅菜品显示) / Selected(Image 高亮覆盖)
		/// </summary>
		/// <param name="label">NameText 文本：只放物品名称，不拼星级与数量</param>
		/// <param name="star">菜品星级：>0 时写入 StarText（"★n" + 稀有度色），=0（非菜品/空态）整节点隐藏</param>
		private void CreateOption(RectTransform list, string label, bool highlighted, string iconPath, UnityEngine.Events.UnityAction onClick, System.Action onLongPress = null, int star = 0)
		{
			if (list == null) return;

			var template = FindGridTemplate(list);
			if (template == null)
			{
				LogKit.Warning("[PreparationPanel] 找不到 Grid 模板，跳过生成选项");
				return;
			}

			var go = Instantiate(template, list, false);
			go.name = "Grid_Item";
			go.SetActive(true);

			// ===== 数据赋值 =====
			// NameText：只写名称（星级/数量已剥离给 StarText），全分类统一纯白色
			var nameTransform = go.transform.Find("NameText");
			var text = nameTransform?.GetComponent<Text>();
			if (text != null)
			{
				text.text = label;
				text.color = Color.white;
				text.raycastTarget = false; // 文本不拦截点击/长按
			}

			// StarText：星级专用文本，格式 "★1" / "★2" / "★n"（非菜品 star=0 → 整节点隐藏，不留空文本）
			var starTransform = go.transform.Find("StarText");
			var starText = starTransform?.GetComponent<Text>();
			if (starText != null)
			{
				bool hasStar = star > 0;
				starTransform.gameObject.SetActive(hasStar);
				if (hasStar)
				{
					starText.text = $"★{star}";
					starText.color = DishRarity.GetColor(star);
					starText.raycastTarget = false;
				}
			}

			var iconTransform = go.transform.Find("Icon");
			var icon = iconTransform?.GetComponent<Image>();
			if (icon != null)
			{
				var spr = string.IsNullOrEmpty(iconPath) ? null : Resources.Load<Sprite>(iconPath);
				icon.gameObject.SetActive(spr != null);
				if (spr != null)
				{
					icon.sprite = spr;
					icon.color = Color.white;
				}
				icon.raycastTarget = false;
			}

			var selected = go.transform.Find("Selected")?.gameObject;
			if (selected != null) selected.SetActive(highlighted);

			var bg = go.GetComponent<Image>();
			if (bg != null)
			{
				bg.color = highlighted
					? new Color(0.3f, 0.55f, 0.35f, 0.9f)
					: new Color(0.22f, 0.24f, 0.3f, 0.9f);
			}

			if (onClick != null)
			{
				var btn = go.GetComponent<Button>();
				if (btn != null)
				{
					btn.onClick.RemoveAllListeners();
					btn.onClick.AddListener(onClick);
				}
			}

			// 长按：打开物品详情弹窗（单击语义保持不变，两者不冲突）
			if (onLongPress != null)
			{
				var longPress = go.GetComponent<LongPressTrigger>();
				if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
				longPress.onLongPress = onLongPress;
			}

			mDynamicOptions.Add(go);
		}

		// ===== 物品详情弹窗（长按触发） =====

		/// <summary>打开物品详情弹窗（长按触发；按分类经 ItemDetailFactory 读取配置填充）</summary>
		private void OpenItemDetail(PanelList list, string id, int star = 0)
		{
			ItemDetailPanelData data = null;
			switch (list)
			{
				case PanelList.Character: data = ItemDetailFactory.ForCharacter(id); break;
				case PanelList.Weapon: data = ItemDetailFactory.ForWeapon(id); break;
				case PanelList.Dish: data = ItemDetailFactory.ForDish(id, star); break;
				case PanelList.Skill: data = ItemDetailFactory.ForSkill(id); break;
			}
			if (data == null) return;
			UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
		}

		/// <summary>
		/// 查找 Grid 模板：优先级 = Inspector 拖拽的 mGridTemplate → Content 内名为 "Grid" 的子物体
		/// → Content 内首个非动态子节点 → Resources 公共模板
		/// </summary>
		private GameObject FindGridTemplate(RectTransform list)
		{
			// 手动拖拽优先（最稳妥，不依赖层级命名）
			if (mGridTemplate != null) return mGridTemplate;

			var inList = list.Find("Grid");

			// 名称未约定时退化为首个非动态子节点：跳过已生成的克隆，兼容嵌套模板
			if (inList == null)
			{
				for (int i = 0; i < list.childCount; i++)
				{
					var child = list.GetChild(i);
					if (!mDynamicOptions.Contains(child.gameObject))
					{
						inList = child;
						break;
					}
				}
			}

			if (inList != null) return inList.gameObject;

			if (mGridTemplateCache == null)
				mGridTemplateCache = Resources.Load<GameObject>(AssetKeys.PANEL_PREFIX + "PreparationGrid");
			return mGridTemplateCache;
		}

		/// <summary>按名字深度查找子节点（广度优先，层级较深/路径不稳时使用）</summary>
		private static Transform FindDeep(Transform root, string nodeName)
		{
			var pending = new Queue<Transform>();
			pending.Enqueue(root);

			while (pending.Count > 0)
			{
				var current = pending.Dequeue();
				for (int i = 0; i < current.childCount; i++)
				{
					var child = current.GetChild(i);
					if (child.name == nodeName) return child;
					pending.Enqueue(child);
				}
			}
			return null;
		}

		private void ClearDynamicOptions()
		{
			foreach (var go in mDynamicOptions)
				if (go != null) Destroy(go);
			mDynamicOptions.Clear();
		}
	}
}
